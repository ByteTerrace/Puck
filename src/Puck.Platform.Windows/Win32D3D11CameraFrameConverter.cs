using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using WinRT;

namespace Puck.Platform.Windows;

/// <summary>Converts one native WinRT camera surface into consumer-owned RGBA shared targets without leaving the
/// camera frame server's Direct3D 11 device. YUY2 is unpacked and color-converted by a compute shader; L8 is expanded
/// to grayscale by a second shader. The source is first copied into a shader-readable texture because camera-driver
/// surfaces are not required to carry <c>D3D11_BIND_SHADER_RESOURCE</c>; the shader writes a private UAV, then one
/// GPU copy transfers the completed RGBA image into the cross-device shared ring. All work and completion waits stay
/// on the dual-camera poll thread.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed unsafe class Win32D3D11CameraFrameConverter : IDisposable {
    private const string ColorShader = """
        Texture2D<float4> Source : register(t0);
        RWTexture2D<float4> Target : register(u0);

        [numthreads(8, 8, 1)]
        void main(uint3 position : SV_DispatchThreadID) {
            uint width, height;
            Target.GetDimensions(width, height);
            if (position.x >= width || position.y >= height) return;

            float4 pair = Source.Load(int3(position.x >> 1, position.y, 0));
            float y = (((position.x & 1) == 0) ? pair.r : pair.b);
            float u = pair.g - 0.5;
            float v = pair.a - 0.5;
            y = max(0.0, ((y * 255.0) - 16.0) / 219.0);
            float3 rgb = saturate(float3(
                y + (1.596 * v),
                y - (0.392 * u) - (0.813 * v),
                y + (2.017 * u)
            ));
            Target[position.xy] = float4(rgb, 1.0);
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
    private readonly ID3D11ShaderResourceView* m_inputView;
    private readonly ID3D10Multithread* m_multithread;
    private readonly ID3D11Texture2D* m_output;
    private readonly ID3D11UnorderedAccessView* m_outputView;
    private readonly ID3D11Query* m_query;
    private readonly ID3D11ComputeShader* m_shader;

    private ID3D11Texture2D*[] m_targets = [];

    public Win32D3D11CameraFrameConverter(nint sourceTexture, long adapterLuid, int width, int height, string subtype) {
        m_height = height;
        m_width = width;

        var source = ((ID3D11Texture2D*)sourceTexture);
        var description = default(D3D11_TEXTURE2D_DESC);

        source->GetDesc(pDesc: &description);
        var (requiredFormat, viewFormat, shaderSource) = Kernel(subtype: subtype);

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
        ID3D11ShaderResourceView* inputView = null;
        ID3D11Texture2D* output = null;
        ID3D11UnorderedAccessView* outputView = null;
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

            var inputViewDescription = new D3D11_SHADER_RESOURCE_VIEW_DESC {
                Format = viewFormat,
                ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2D,
            };

            inputViewDescription.Anonymous.Texture2D.MipLevels = 1;
            device->CreateShaderResourceView(pResource: ((ID3D11Resource*)input), pDesc: &inputViewDescription, ppSRView: &inputView);

            var outputDescription = new D3D11_TEXTURE2D_DESC {
                Width = checked((uint)width),
                Height = checked((uint)height),
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
            };

            device->CreateTexture2D(pDesc: &outputDescription, pInitialData: null, ppTexture2D: &output);
            device->CreateUnorderedAccessView(pResource: ((ID3D11Resource*)output), pDesc: null, ppUAView: &outputView);
            shader = CompileShader(device: device, source: shaderSource);

            var queryDescription = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };

            device->CreateQuery(pQueryDesc: &queryDescription, ppQuery: &query);
        } catch {
            Release(value: query);
            Release(value: shader);
            Release(value: outputView);
            Release(value: output);
            Release(value: inputView);
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
        m_output = output;
        m_inputView = inputView;
        m_input = input;
        m_multithread = multithread;
        m_context = context;
        m_device1 = device1;
        m_device = device;
    }

    // Native transport subtype (the WinRT MediaFrameFormat.Subtype FOURCC) to the surface format the frame server
    // must deliver, the shader-resource view over it, and the kernel that unpacks it. A YUY2 view as R8G8B8A8 exposes
    // each two-pixel macropixel as normalized Y0/U/Y1/V components at half width.
    private static (DXGI_FORMAT Surface, DXGI_FORMAT View, string Shader) Kernel(string subtype) => (subtype.ToUpperInvariant() switch {
        "YUY2" => (DXGI_FORMAT.DXGI_FORMAT_YUY2, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, ColorShader),
        "L8" => (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, InfraredShader),
        _ => throw new NotSupportedException(message: $"no GPU conversion kernel for the native camera subtype '{subtype}'"),
    });

    public bool IsStarted => (m_targets.Length != 0);

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
            ConvertLocked(sourceTexture: sourceTexture, targetSlot: targetSlot);
        } finally {
            m_multithread->Leave();
        }
    }

    private void ConvertLocked(nint sourceTexture, int targetSlot) {
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

        var inputView = m_inputView;
        var targetView = m_outputView;

        m_context->CSSetShader(pComputeShader: m_shader, NumClassInstances: 0, ppClassInstances: null);
        m_context->CSSetShaderResources(StartSlot: 0, NumViews: 1, ppShaderResourceViews: &inputView);
        m_context->CSSetUnorderedAccessViews(StartSlot: 0, NumUAVs: 1, ppUnorderedAccessViews: &targetView, pUAVInitialCounts: null);
        m_context->Dispatch(
            ThreadGroupCountX: checked((uint)((m_width + 7) / 8)),
            ThreadGroupCountY: checked((uint)((m_height + 7) / 8)),
            ThreadGroupCountZ: 1
        );

        ID3D11ShaderResourceView* noInput = null;
        ID3D11UnorderedAccessView* noTarget = null;

        m_context->CSSetShaderResources(StartSlot: 0, NumViews: 1, ppShaderResourceViews: &noInput);
        m_context->CSSetUnorderedAccessViews(StartSlot: 0, NumUAVs: 1, ppUnorderedAccessViews: &noTarget, pUAVInitialCounts: null);
        m_context->CSSetShader(pComputeShader: null, NumClassInstances: 0, ppClassInstances: null);
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

        m_context->End(pAsync: ((ID3D11Asynchronous*)m_query));
        m_context->Flush();
        BOOL done = false;

        while (!done) {
            m_context->GetData(DataSize: ((uint)sizeof(BOOL)), GetDataFlags: 0, pAsync: ((ID3D11Asynchronous*)m_query), pData: &done);

            if (!done) {
                Thread.SpinWait(iterations: 64);
            }
        }
    }

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        Release(values: m_targets);
        Release(value: m_query);
        Release(value: m_shader);
        Release(value: m_outputView);
        Release(value: m_output);
        Release(value: m_inputView);
        Release(value: m_input);
        Release(value: m_multithread);
        Release(value: m_context);
        Release(value: m_device1);
        Release(value: m_device);
        m_targets = [];
    }

    private static ID3D11ComputeShader* CompileShader(ID3D11Device* device, string source) {
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

                    throw new COMException(message: $"camera conversion shader compilation failed: {message}", errorCode: result.Value);
                }
            }

            ID3D11ComputeShader* shader;

            device->CreateComputeShader(pShaderBytecode: code->GetBufferPointer(), BytecodeLength: code->GetBufferSize(), pClassLinkage: null, ppComputeShader: &shader);

            return shader;
        } finally {
            Release(value: errors);
            Release(value: code);
        }
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
