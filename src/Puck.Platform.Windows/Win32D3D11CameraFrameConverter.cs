using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using WinRT;

namespace Puck.Platform.Windows;

/// <summary>Converts one native WinRT camera surface into consumer-owned RGBA shared targets without leaving the
/// camera frame server's Direct3D 11 device. Packed YUY2 (the BRIO) and two-plane NV12 (the Surface's front camera)
/// color are unpacked and color-converted by a compute shader each; L8 is expanded to grayscale by a third. The source
/// is first copied into a shader-readable texture because camera-driver surfaces are not required to carry
/// <c>D3D11_BIND_SHADER_RESOURCE</c>; the shader writes a private UAV, then one GPU copy transfers the completed RGBA
/// image into the cross-device shared ring. All work and completion waits stay on the dual-camera poll thread.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed unsafe class Win32D3D11CameraFrameConverter : IDisposable, IProbeKernelDevice {
    private const string LimitedRangeMath = """
        float3 YuvToRgb(float y, float u, float v) {
            y = max(0.0, ((y * 255.0) - 16.0) / 219.0);
            u = ((u * 255.0) - 128.0) / 224.0;
            v = ((v * 255.0) - 128.0) / 224.0;

        """;
    private const string FullRangeMath = """
        float3 YuvToRgb(float y, float u, float v) {
            u = ((u * 255.0) - 128.0) / 255.0;
            v = ((v * 255.0) - 128.0) / 255.0;

        """;
    private const string Bt709MatrixMath = """
            return saturate(float3(
                y + (1.5748 * v),
                y - (0.187324 * u) - (0.468124 * v),
                y + (1.8556 * u)
            ));
        }

        """;
    private const string Bt601MatrixMath = """
            return saturate(float3(
                y + (1.402 * v),
                y - (0.344136 * u) - (0.714136 * v),
                y + (1.772 * u)
            ));
        }

        """;
    // YUY2 viewed as R8G8B8A8 at half width: each texel is one two-pixel macropixel, Y0/U/Y1/V.
    private const string PackedColorKernel = """
        Texture2D<float4> Source : register(t0);
        RWTexture2D<float4> Target : register(u0);

        [numthreads(8, 8, 1)]
        void main(uint3 position : SV_DispatchThreadID) {
            uint width, height;
            Target.GetDimensions(width, height);
            if (position.x >= width || position.y >= height) return;

            float4 pair = Source.Load(int3(position.x >> 1, position.y, 0));
            float y = (((position.x & 1) == 0) ? pair.r : pair.b);
            Target[position.xy] = float4(YuvToRgb(y, pair.g, pair.a), 1.0);
        }
        """;
    // NV12: a full-resolution luma plane (R8) and a half-resolution interleaved chroma plane (R8G8), each bound as its
    // own view over the one NV12 texture — Direct3D selects the plane from the view format.
    private const string PlanarColorKernel = """
        Texture2D<float> Luma : register(t0);
        Texture2D<float2> Chroma : register(t1);
        RWTexture2D<float4> Target : register(u0);

        [numthreads(8, 8, 1)]
        void main(uint3 position : SV_DispatchThreadID) {
            uint width, height;
            Target.GetDimensions(width, height);
            if (position.x >= width || position.y >= height) return;

            uint chromaWidth, chromaHeight;
            Chroma.GetDimensions(chromaWidth, chromaHeight);
            float2 chromaPosition = ((float2(position.xy) - ChromaOffset) * 0.5);
            int2 chromaBase = int2(floor(chromaPosition));
            float2 chromaBlend = frac(chromaPosition);
            int2 chromaMaximum = int2(chromaWidth - 1, chromaHeight - 1);
            int2 chroma00 = clamp(chromaBase, int2(0, 0), chromaMaximum);
            int2 chroma11 = clamp(chromaBase + 1, int2(0, 0), chromaMaximum);
            float2 top = lerp(
                Chroma.Load(int3(chroma00, 0)),
                Chroma.Load(int3(chroma11.x, chroma00.y, 0)),
                chromaBlend.x
            );
            float2 bottom = lerp(
                Chroma.Load(int3(chroma00.x, chroma11.y, 0)),
                Chroma.Load(int3(chroma11, 0)),
                chromaBlend.x
            );
            float y = Luma.Load(int3(position.xy, 0));
            float2 uv = lerp(top, bottom, chromaBlend.y);
            Target[position.xy] = float4(YuvToRgb(y, uv.x, uv.y), 1.0);
        }
        """;
    private const string InfraredShader = """
        Texture2D<float> Source : register(t0);
        RWTexture2D<float4> Target : register(u0);

        [numthreads(8, 8, 1)]
        void main(uint3 position : SV_DispatchThreadID) {
            uint width, height;
            Target.GetDimensions(width, height);
            if (position.x >= width || position.y >= height) return;

            float luminance = Source.Load(int3(position.xy, 0));
            Target[position.xy] = float4(luminance, luminance, luminance, 1.0);
        }
        """;

    private readonly int m_height;
    private readonly int m_width;
    private readonly ID3D11DeviceContext* m_context;
    private readonly ID3D11Device* m_device;
    private readonly ID3D11Device1* m_device1;

    private bool m_disposed;

    private readonly ID3D11Texture2D* m_input;
    private readonly ID3D11ShaderResourceView*[] m_inputViews;
    private readonly ID3D10Multithread* m_multithread;
    private readonly ID3D11Texture2D* m_output;
    private readonly ID3D11ShaderResourceView* m_outputSrv;
    private readonly ID3D11UnorderedAccessView* m_outputView;
    private readonly ID3D11Texture2D* m_previous;
    private readonly ID3D11ShaderResourceView* m_previousSrv;
    private readonly ID3D11UnorderedAccessView* m_previousView;
    private readonly ID3D11Query* m_query;
    private readonly ID3D11ComputeShader* m_shader;

    private ID3D11Texture2D*[] m_targets = [];

    public Win32D3D11CameraFrameConverter(nint sourceTexture, long adapterLuid, int width, int height, string subtype, Win32CameraColorimetry colorimetry) {
        m_height = height;
        m_width = width;

        var source = ((ID3D11Texture2D*)sourceTexture);
        var description = default(D3D11_TEXTURE2D_DESC);

        source->GetDesc(pDesc: &description);
        var (requiredFormat, viewFormats, shaderSource) = Kernel(colorimetry: colorimetry, subtype: subtype);

        if (
            (description.Width != width) ||
            (description.Height != height) ||
            (description.Format != requiredFormat) ||
            (description.SampleDesc.Count != 1)
        ) {
            throw new NotSupportedException(message: $"the native {subtype} GPU surface is {description.Width}x{description.Height} {description.Format}, expected {width}x{height} {requiredFormat}");
        }

        ID3D11Device* device = null;
        ID3D11Device1* device1 = null;
        ID3D11DeviceContext* context = null;
        ID3D10Multithread* multithread = null;
        ID3D11Texture2D* input = null;
        var inputViews = new ID3D11ShaderResourceView*[viewFormats.Length];
        ID3D11Texture2D* output = null;
        ID3D11ShaderResourceView* outputSrv = null;
        ID3D11UnorderedAccessView* outputView = null;
        ID3D11Texture2D* previous = null;
        ID3D11ShaderResourceView* previousSrv = null;
        ID3D11UnorderedAccessView* previousView = null;
        ID3D11ComputeShader* shader = null;
        ID3D11Query* query = null;

        source->GetDevice(ppDevice: &device);

        try {
            var device1Iid = ID3D11Device1.IID_Guid;

            Win32D3D11.ThrowIfFailed(hr: ((IUnknown*)device)->QueryInterface(ppvObject: out var device1Pointer, riid: in device1Iid), operation: "QueryInterface(ID3D11Device1)");
            device1 = ((ID3D11Device1*)device1Pointer);
            device->GetImmediateContext(ppImmediateContext: &context);
            multithread = ProtectMultithreaded(device: device);
            ValidateAdapter(device: device, expectedLuid: adapterLuid);

            var inputDescription = new D3D11_TEXTURE2D_DESC {
                Width = checked((uint)width),
                Height = checked((uint)height),
                MipLevels = 1,
                ArraySize = 1,
                Format = requiredFormat,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };

            device->CreateTexture2D(pDesc: &inputDescription, pInitialData: null, ppTexture2D: &input);

            for (var index = 0; (index < viewFormats.Length); index++) {
                var inputViewDescription = new D3D11_SHADER_RESOURCE_VIEW_DESC {
                    Format = viewFormats[index],
                    ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2D,
                };

                inputViewDescription.Anonymous.Texture2D.MipLevels = 1;

                ID3D11ShaderResourceView* inputView = null;

                // CsWin32's generated COM projection throws on a failed HRESULT. Keep the postcondition explicit too:
                // a missing plane must refuse this converter so the graph reopens on the CPU tier.
                device->CreateShaderResourceView(pDesc: &inputViewDescription, pResource: ((ID3D11Resource*)input), ppSRView: &inputView);
                if (inputView is null) {
                    throw new InvalidOperationException(message: $"D3D11 camera plane {index} view creation returned no view");
                }

                inputViews[index] = inputView;
            }

            var outputDescription = new D3D11_TEXTURE2D_DESC {
                Width = checked((uint)width),
                Height = checked((uint)height),
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                // Readable by the kernels a probe attaches to the graph, which sample the converted frames in place.
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };

            device->CreateTexture2D(pDesc: &outputDescription, pInitialData: null, ppTexture2D: &output);
            device->CreateUnorderedAccessView(pDesc: null, pResource: ((ID3D11Resource*)output), ppUAView: &outputView);
            device->CreateShaderResourceView(pDesc: null, pResource: ((ID3D11Resource*)output), ppSRView: &outputSrv);
            // The previous frame's conversion, kept for kernels that read a strobing stream's unlit half beside the lit one.
            device->CreateTexture2D(pDesc: &outputDescription, pInitialData: null, ppTexture2D: &previous);
            device->CreateUnorderedAccessView(pDesc: null, pResource: ((ID3D11Resource*)previous), ppUAView: &previousView);
            device->CreateShaderResourceView(pDesc: null, pResource: ((ID3D11Resource*)previous), ppSRView: &previousSrv);
            shader = CompileShader(device: device, source: shaderSource);

            var queryDescription = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };

            device->CreateQuery(pQueryDesc: &queryDescription, ppQuery: &query);
        } catch {
            Release(value: query);
            Release(value: shader);
            Release(value: previousView);
            Release(value: previousSrv);
            Release(value: previous);
            Release(value: outputView);
            Release(value: outputSrv);
            Release(value: output);
            Release(values: inputViews);
            Release(value: input);
            Release(value: multithread);
            Release(value: context);
            Release(value: device1);
            Release(value: device);
            throw;
        }

        m_query = query;
        m_shader = shader;
        m_outputView = outputView;
        m_outputSrv = outputSrv;
        m_output = output;
        m_previous = previous;
        m_previousSrv = previousSrv;
        m_previousView = previousView;
        m_inputViews = inputViews;
        m_input = input;
        m_multithread = multithread;
        m_context = context;
        m_device1 = device1;
        m_device = device;
    }

    // Native transport subtype (the WinRT MediaFrameFormat.Subtype FOURCC) to the surface format the frame server
    // must deliver, the shader-resource views over it (bound at t0, t1, … in order), and the kernel that unpacks it.
    // A YUY2 view as R8G8B8A8 exposes each two-pixel macropixel as normalized Y0/U/Y1/V components at half width; an
    // NV12 texture answers an R8 view with its luma plane and an R8G8 view with its half-resolution chroma plane.
    private static (DXGI_FORMAT Surface, DXGI_FORMAT[] Views, string Shader) Kernel(string subtype, Win32CameraColorimetry colorimetry) => (subtype.ToUpperInvariant() switch {
        "YUY2" => (DXGI_FORMAT.DXGI_FORMAT_YUY2, [DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM], (ColorMath(colorimetry: colorimetry) + PackedColorKernel)),
        "NV12" => (DXGI_FORMAT.DXGI_FORMAT_NV12, [DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM], PlanarColorShader(colorimetry: colorimetry)),
        "L8" => (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, [DXGI_FORMAT.DXGI_FORMAT_R8_UNORM], InfraredShader),
        _ => throw new NotSupportedException(message: $"no GPU conversion kernel for the native camera subtype '{subtype}'"),
    });

    public bool IsStarted => (m_targets.Length != 0);
    public int TargetCount => m_targets.Length;

    ID3D11DeviceContext* IProbeKernelDevice.Context => m_context;
    ID3D11Device* IProbeKernelDevice.Device => m_device;
    ID3D11Device1* IProbeKernelDevice.Device1 => m_device1;

    /// <summary>Gets the shader-resource view over the most recent conversion.</summary>
    public nint OutputView => ((nint)m_outputSrv);
    /// <summary>Gets the shader-resource view over the conversion kept by <see cref="ConvertPrevious"/>.</summary>
    public nint PreviousView => ((nint)m_previousSrv);

    /// <summary>Holds the device's critical section across a multi-call sequence on its immediate context.</summary>
    public void Enter() => m_multithread->Enter();
    public void Leave() => m_multithread->Leave();
    public void AttachTargets(IReadOnlyList<nint> sharedTargetHandles) {
        if (IsStarted) {
            throw new InvalidOperationException(message: "camera converter targets are already attached");
        }

        var targets = new ID3D11Texture2D*[sharedTargetHandles.Count];

        try {
            for (var index = 0; (index < targets.Length); index++) {
                using var handle = new SafeFileHandle(ownsHandle: false, preexistingHandle: sharedTargetHandles[index]);

                m_device1->OpenSharedResource1(hResource: handle, ppResource: out var opened, returnedInterface: ID3D11Texture2D.IID_Guid);
                targets[index] = ((ID3D11Texture2D*)opened);

                var description = default(D3D11_TEXTURE2D_DESC);

                targets[index]->GetDesc(pDesc: &description);

                if (
                    (description.Width != m_width) ||
                    (description.Height != m_height) ||
                    (description.Format != DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM)
                ) {
                    throw new InvalidOperationException(message: $"the shared camera target is {description.Width}x{description.Height} {description.Format} with {description.BindFlags}; expected an RGBA8 texture");
                }
            }

            m_targets = targets;
        } catch {
            Release(values: targets);
            throw;
        }
    }
    public void Convert(nint sourceTexture, int targetSlot) {
        if (((uint)targetSlot) >= ((uint)m_targets.Length)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(targetSlot));
        }

        // The context is the frame server's own immediate context; multithread protection serializes single calls
        // only, so the device critical section is held across the whole bind/dispatch/copy/wait sequence.
        m_multithread->Enter();

        try {
            Dispatch(sourceTexture: sourceTexture, target: m_outputView);
            m_context->CopySubresourceRegion(
                DstSubresource: 0,
                DstX: 0,
                DstY: 0,
                DstZ: 0,
                SrcSubresource: 0,
                pDstResource: ((ID3D11Resource*)m_targets[targetSlot]),
                pSrcBox: null,
                pSrcResource: ((ID3D11Resource*)m_output)
            );
            Win32D3D11.WaitForCompletion(context: m_context, query: m_query);
        } finally {
            m_multithread->Leave();
        }
    }
    /// <summary>Converts a frame into the previous-frame texture only; no ring slot is written or published.</summary>
    public void ConvertPrevious(nint sourceTexture) {
        m_multithread->Enter();

        try {
            Dispatch(sourceTexture: sourceTexture, target: m_previousView);
        } finally {
            m_multithread->Leave();
        }
    }

    private void Dispatch(nint sourceTexture, ID3D11UnorderedAccessView* target) {
        m_context->CopySubresourceRegion(
            DstSubresource: 0,
            DstX: 0,
            DstY: 0,
            DstZ: 0,
            SrcSubresource: 0,
            pDstResource: ((ID3D11Resource*)m_input),
            pSrcBox: null,
            pSrcResource: ((ID3D11Resource*)sourceTexture)
        );

        var viewCount = checked((uint)m_inputViews.Length);
        var targetView = target;

        m_context->CSSetShader(NumClassInstances: 0, pComputeShader: m_shader, ppClassInstances: null);

        fixed (ID3D11ShaderResourceView** inputViews = m_inputViews) {
            m_context->CSSetShaderResources(NumViews: viewCount, StartSlot: 0, ppShaderResourceViews: inputViews);
        }

        m_context->CSSetUnorderedAccessViews(NumUAVs: 1, StartSlot: 0, pUAVInitialCounts: null, ppUnorderedAccessViews: &targetView);
        m_context->Dispatch(
            ThreadGroupCountX: checked((uint)((m_width + 7) / 8)),
            ThreadGroupCountY: checked((uint)((m_height + 7) / 8)),
            ThreadGroupCountZ: 1
        );

        var noInputs = stackalloc ID3D11ShaderResourceView*[m_inputViews.Length];
        ID3D11UnorderedAccessView* noTarget = null;

        for (var index = 0; (index < m_inputViews.Length); index++) {
            noInputs[index] = null;
        }

        m_context->CSSetShaderResources(NumViews: viewCount, StartSlot: 0, ppShaderResourceViews: noInputs);
        m_context->CSSetUnorderedAccessViews(NumUAVs: 1, StartSlot: 0, pUAVInitialCounts: null, ppUnorderedAccessViews: &noTarget);
        m_context->CSSetShader(NumClassInstances: 0, pComputeShader: null, ppClassInstances: null);
    }

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        Release(values: m_targets);
        Release(value: m_query);
        Release(value: m_shader);
        Release(value: m_previousView);
        Release(value: m_previousSrv);
        Release(value: m_previous);
        Release(value: m_outputView);
        Release(value: m_outputSrv);
        Release(value: m_output);
        Release(values: m_inputViews);
        Release(value: m_input);
        Release(value: m_multithread);
        Release(value: m_context);
        Release(value: m_device1);
        Release(value: m_device);
        m_targets = [];
    }

    private static ID3D11ComputeShader* CompileShader(ID3D11Device* device, string source) {
        var code = CompileShaderBytecode(source: source);

        try {
            ID3D11ComputeShader* shader = null;

            device->CreateComputeShader(pShaderBytecode: code->GetBufferPointer(), BytecodeLength: code->GetBufferSize(), pClassLinkage: null, ppComputeShader: &shader);

            if (shader is null) {
                throw new InvalidOperationException(message: "D3D11 camera shader creation returned no shader");
            }

            return shader;
        } finally {
            Release(value: code);
        }
    }
    private static ID3DBlob* CompileShaderBytecode(string source) {
        var bytes = Encoding.UTF8.GetBytes(s: source);
        ID3DBlob* code = null;
        ID3DBlob* errors = null;

        try {
            fixed (byte* sourceBytes = bytes) {
                var result = PInvoke.D3DCompile(
                    pSrcData: sourceBytes,
                    SrcDataSize: ((nuint)bytes.Length),
                    pSourceName: "puck-camera.hlsl",
                    pDefines: null,
                    pInclude: null,
                    pEntrypoint: "main",
                    pTarget: "cs_5_0",
                    Flags1: 0,
                    Flags2: 0,
                    ppCode: &code,
                    ppErrorMsgs: &errors
                );

                if (result.Value < 0) {
                    var message = ((errors is null)
                        ? "unknown shader compiler error"
                        : Marshal.PtrToStringUTF8(((nint)errors->GetBufferPointer()), checked((int)errors->GetBufferSize()))
                    );

                    throw new COMException(errorCode: result.Value, message: $"camera conversion shader compilation failed: {message}");
                }
            }

            if (code is null) {
                throw new InvalidOperationException(message: "camera conversion shader compilation returned no bytecode");
            }

            return code;
        } catch {
            Release(value: code);
            throw;
        } finally {
            Release(value: errors);
        }
    }

    /// <summary>Composes the conversion shader for a native subtype under a colorimetry.</summary>
    public static string Shader(string subtype, Win32CameraColorimetry colorimetry) => Kernel(colorimetry: colorimetry, subtype: subtype).Shader;
    /// <summary>Compiles the conversion shader for a native subtype under a colorimetry, throwing on a compiler refusal.</summary>
    public static void ValidateShader(string subtype, Win32CameraColorimetry colorimetry) {
        var code = CompileShaderBytecode(source: Shader(colorimetry: colorimetry, subtype: subtype));

        Release(value: code);
    }

    private static string ColorMath(Win32CameraColorimetry colorimetry) {
        var conversion = colorimetry.Resolve();
        var range = ((Win32YuvRange.Limited == conversion.Range) ? LimitedRangeMath : FullRangeMath);
        var matrix = ((Win32YuvMatrix.Bt709 == conversion.Matrix) ? Bt709MatrixMath : Bt601MatrixMath);

        return (range + matrix);
    }
    private static string PlanarColorShader(Win32CameraColorimetry colorimetry) {
        var conversion = colorimetry.Resolve();
        var horizontalOffset = (conversion.ChromaHorizontallyCosited ? "0.0" : "0.5");
        var verticalOffset = (conversion.ChromaVerticallyCosited ? "0.0" : "0.5");

        return ((ColorMath(colorimetry: colorimetry) + $"static const float2 ChromaOffset = float2({horizontalOffset}, {verticalOffset});\n\n") + PlanarColorKernel);
    }
    private static ID3D10Multithread* ProtectMultithreaded(ID3D11Device* device) {
        var iid = ID3D10Multithread.IID_Guid;

        Win32D3D11.ThrowIfFailed(hr: ((IUnknown*)device)->QueryInterface(ppvObject: out var pointer, riid: in iid), operation: "QueryInterface(ID3D10Multithread)");
        var multithread = ((ID3D10Multithread*)pointer);

        _ = multithread->SetMultithreadProtected(bMTProtect: true);

        return multithread;
    }
    private static void ValidateAdapter(ID3D11Device* device, long expectedLuid) {
        var iid = IDXGIDevice.IID_Guid;

        Win32D3D11.ThrowIfFailed(hr: ((IUnknown*)device)->QueryInterface(ppvObject: out var pointer, riid: in iid), operation: "QueryInterface(IDXGIDevice)");
        var dxgiDevice = ((IDXGIDevice*)pointer);
        IDXGIAdapter* adapter = null;

        try {
            dxgiDevice->GetAdapter(pAdapter: &adapter);
            var description = adapter->GetDesc();
            var actualLuid = (((long)description.AdapterLuid.HighPart) << 32) | description.AdapterLuid.LowPart;

            if (actualLuid != expectedLuid) {
                throw new NotSupportedException(message: $"the camera GPU selected adapter 0x{actualLuid:X16}, but the renderer uses 0x{expectedLuid:X16}");
            }
        } finally {
            Release(value: adapter);
            Release(value: dxgiDevice);
        }
    }
    private static void Release<T>(T* value) where T : unmanaged {
        if (value is not null) {
            _ = ((IUnknown*)value)->Release();
        }
    }
    private static void Release<T>(T*[] values) where T : unmanaged {
        foreach (var value in values) {
            Release(value: value);
        }
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess {
        nint GetInterface(in Guid iid);
    }

    public static nint GetTexture(IDirect3DSurface surface, out object access) {
        var dxgiAccess = surface.As<IDirect3DDxgiInterfaceAccess>();

        access = dxgiAccess;

        return dxgiAccess.GetInterface(iid: ID3D11Texture2D.IID_Guid);
    }
}
