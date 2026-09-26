using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Graphics.DirectX.Direct3D11;
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
/// image into the cross-device shared ring, and signals the consumer's shared fence (or, on a device that cannot open
/// it, waits on the CPU). All work stays on the dual-camera poll thread.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed unsafe class Win32D3D11CameraFrameConverter : IDisposable, IProbeKernelDevice {
    // The conversion kernels' file stem: Assets/Shaders/camera-conversion.hlsl compiles to one cs_5_0 DXBC file per
    // entry point at build (CompileDirect3D11Kernels), shipped beside the application.
    private const string KernelStem = "camera-conversion";
    // The L8 kernel's entry point, the one that reads no Conversion constants.
    private const string InfraredEntry = "infrared";

    private readonly int m_height;
    private readonly int m_width;
    private readonly ID3D11DeviceContext* m_context;
    private readonly ID3D11Device* m_device;
    private readonly ID3D11Device1* m_device1;

    private bool m_disposed;

    private readonly ID3D11Buffer* m_conversion;
    private readonly ID3D11Texture2D* m_input;
    private readonly ID3D11ShaderResourceView*[] m_inputViews;
    private readonly ID3D10Multithread* m_multithread;
    private readonly ID3D11Texture2D* m_output;
    private readonly ID3D11ShaderResourceView* m_outputSrv;
    private readonly ID3D11UnorderedAccessView* m_outputView;
    private readonly ID3D11Texture2D* m_previous;
    private readonly ID3D11ShaderResourceView* m_previousSrv;
    private readonly ID3D11UnorderedAccessView* m_previousView;
    private readonly ID3D11ComputeShader* m_shader;

    private Win32D3D11CompletionSignal? m_signal;
    private ID3D11Texture2D*[] m_targets = [];

    public Win32D3D11CameraFrameConverter(nint sourceTexture, long adapterLuid, int width, int height, string subtype, Win32CameraColorimetry colorimetry) {
        m_height = height;
        m_width = width;

        var source = ((ID3D11Texture2D*)sourceTexture);
        var description = default(D3D11_TEXTURE2D_DESC);

        source->GetDesc(pDesc: &description);
        var (requiredFormat, viewFormats, entry) = Kernel(subtype: subtype);
        // The infrared kernel reads no conversion, so an L8 stream's colorimetry is never resolved.
        var constants = ((entry == InfraredEntry)
            ? null
            : ConversionConstants(colorimetry: colorimetry));

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
        ID3D11Buffer* conversion = null;

        source->GetDevice(ppDevice: &device);

        try {
            var device1Iid = ID3D11Device1.IID_Guid;

            Win32D3D11.ThrowIfFailed(
                hr: ((IUnknown*)device)->QueryInterface(
                    ppvObject: out var device1Pointer,
                    riid: in device1Iid
                ),
                operation: "QueryInterface(ID3D11Device1)"
            );
            device1 = ((ID3D11Device1*)device1Pointer);
            device->GetImmediateContext(ppImmediateContext: &context);
            multithread = ProtectMultithreaded(device: device);
            ValidateAdapter(
                device: device,
                expectedLuid: adapterLuid
            );

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

            device->CreateTexture2D(
                pDesc: &inputDescription,
                pInitialData: null,
                ppTexture2D: &input
            );

            for (var index = 0; (index < viewFormats.Length); index++) {
                var inputViewDescription = new D3D11_SHADER_RESOURCE_VIEW_DESC {
                    Format = viewFormats[index],
                    ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2D,
                };

                inputViewDescription.Anonymous.Texture2D.MipLevels = 1;

                ID3D11ShaderResourceView* inputView = null;

                // CsWin32's generated COM projection throws on a failed HRESULT. Keep the postcondition explicit too:
                // a missing plane must refuse this converter so the graph reopens on the CPU tier.
                device->CreateShaderResourceView(
                    pDesc: &inputViewDescription,
                    pResource: ((ID3D11Resource*)input),
                    ppSRView: &inputView
                );
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

            device->CreateTexture2D(
                pDesc: &outputDescription,
                pInitialData: null,
                ppTexture2D: &output
            );
            device->CreateUnorderedAccessView(
                pDesc: null,
                pResource: ((ID3D11Resource*)output),
                ppUAView: &outputView
            );
            device->CreateShaderResourceView(
                pDesc: null,
                pResource: ((ID3D11Resource*)output),
                ppSRView: &outputSrv
            );
            // The previous frame's conversion, kept for kernels that read a strobing stream's unlit half beside the lit one.
            device->CreateTexture2D(
                pDesc: &outputDescription,
                pInitialData: null,
                ppTexture2D: &previous
            );
            device->CreateUnorderedAccessView(
                pDesc: null,
                pResource: ((ID3D11Resource*)previous),
                ppUAView: &previousView
            );
            device->CreateShaderResourceView(
                pDesc: null,
                pResource: ((ID3D11Resource*)previous),
                ppSRView: &previousSrv
            );
            shader = CreateShader(
                device: device,
                entry: entry
            );

            if (constants is not null) {
                conversion = CreateConversion(
                    constants: constants,
                    device: device
                );
            }
        } catch {
            Release(value: conversion);
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

        m_conversion = conversion;
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
    // must deliver, the shader-resource views over it (bound at t0, t1, … in order), and the entry point of the kernel
    // that unpacks it. A YUY2 view as R8G8B8A8 exposes each two-pixel macropixel as normalized Y0/U/Y1/V components at
    // half width; an NV12 texture answers an R8 view with its luma plane and an R8G8 view with its half-resolution
    // chroma plane.
    private static (DXGI_FORMAT Surface, DXGI_FORMAT[] Views, string Entry) Kernel(string subtype) => (subtype.ToUpperInvariant() switch {
        "YUY2" => (DXGI_FORMAT.DXGI_FORMAT_YUY2, [DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM], "packed"),
        "NV12" => (DXGI_FORMAT.DXGI_FORMAT_NV12, [DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM], "planar"),
        "L8" => (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, [DXGI_FORMAT.DXGI_FORMAT_R8_UNORM], InfraredEntry),
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
    /// <summary>Opens the consumer's shared targets and its shared fence; each <see cref="Convert"/> writes one target
    /// and signals the fence's next value.</summary>
    /// <param name="sharedTargetHandles">The consumer's RGBA8 shared textures, two or more.</param>
    /// <param name="sharedFenceHandle">The consumer's shared fence NT handle, or zero to keep the CPU wait.</param>
    /// <returns>How the conversions are ordered before the consumer's reads.</returns>
    /// <exception cref="InvalidOperationException">The targets are already attached, or a target is not an RGBA8
    /// texture of the converter's extent.</exception>
    public SharedFenceOrder AttachTargets(IReadOnlyList<nint> sharedTargetHandles, nint sharedFenceHandle) {
        if (IsStarted) {
            throw new InvalidOperationException(message: "camera converter targets are already attached");
        }

        var targets = new ID3D11Texture2D*[sharedTargetHandles.Count];

        try {
            for (var index = 0; (index < targets.Length); index++) {
                using var handle = new SafeFileHandle(
                    ownsHandle: false,
                    preexistingHandle: sharedTargetHandles[index]
                );

                m_device1->OpenSharedResource1(
                    hResource: handle,
                    ppResource: out var opened,
                    returnedInterface: ID3D11Texture2D.IID_Guid
                );
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

            m_signal = new Win32D3D11CompletionSignal(
                context: ((nint)m_context),
                device: ((nint)m_device),
                sharedFenceHandle: sharedFenceHandle
            );
            m_targets = targets;
        } catch {
            Release(values: targets);
            throw;
        }

        return m_signal.Order;
    }
    /// <summary>Converts a frame into a shared target and completes it for the consumer.</summary>
    /// <param name="sourceTexture">The native frame's <c>ID3D11Texture2D*</c>.</param>
    /// <param name="targetSlot">The target written.</param>
    /// <returns>The shared-fence value the write signals, or zero when it finished before the call returned.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetSlot"/> names no attached target.</exception>
    public ulong Convert(nint sourceTexture, int targetSlot) {
        if (((uint)targetSlot) >= ((uint)m_targets.Length)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(targetSlot));
        }

        // The context is the frame server's own immediate context; multithread protection serializes single calls
        // only, so the device critical section is held across the whole bind/dispatch/copy/signal sequence.
        m_multithread->Enter();

        try {
            Dispatch(
                sourceTexture: sourceTexture,
                target: m_outputView
            );
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

            return m_signal!.Complete();
        } finally {
            m_multithread->Leave();
        }
    }
    /// <summary>Converts a frame into the previous-frame texture only; no ring slot is written or published.</summary>
    public void ConvertPrevious(nint sourceTexture) {
        m_multithread->Enter();

        try {
            Dispatch(
                sourceTexture: sourceTexture,
                target: m_previousView
            );
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

        m_context->CSSetShader(
            NumClassInstances: 0,
            pComputeShader: m_shader,
            ppClassInstances: null
        );

        var conversion = m_conversion;

        if (conversion is not null) {
            m_context->CSSetConstantBuffers(
                NumBuffers: 1,
                StartSlot: 0,
                ppConstantBuffers: &conversion
            );
        }

        fixed (ID3D11ShaderResourceView** inputViews = m_inputViews) {
            m_context->CSSetShaderResources(
                NumViews: viewCount,
                StartSlot: 0,
                ppShaderResourceViews: inputViews
            );
        }

        m_context->CSSetUnorderedAccessViews(
            NumUAVs: 1,
            StartSlot: 0,
            pUAVInitialCounts: null,
            ppUnorderedAccessViews: &targetView
        );
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

        m_context->CSSetShaderResources(
            NumViews: viewCount,
            StartSlot: 0,
            ppShaderResourceViews: noInputs
        );
        m_context->CSSetUnorderedAccessViews(
            NumUAVs: 1,
            StartSlot: 0,
            pUAVInitialCounts: null,
            ppUnorderedAccessViews: &noTarget
        );

        if (conversion is not null) {
            ID3D11Buffer* noConversion = null;

            m_context->CSSetConstantBuffers(
                NumBuffers: 1,
                StartSlot: 0,
                ppConstantBuffers: &noConversion
            );
        }

        m_context->CSSetShader(
            NumClassInstances: 0,
            pComputeShader: null,
            ppClassInstances: null
        );
    }

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        Release(values: m_targets);
        m_signal?.Dispose();
        m_signal = null;
        Release(value: m_conversion);
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
    /// <summary>Returns the path of the build-compiled conversion kernel for a native subtype: the cs_5_0 DXBC a
    /// converter creates its shader from.</summary>
    /// <param name="subtype">The native transport subtype FOURCC: <c>YUY2</c>, <c>NV12</c> or <c>L8</c>.</param>
    /// <returns>The kernel's full path beside the application.</returns>
    /// <exception cref="NotSupportedException"><paramref name="subtype"/> has no GPU conversion kernel.</exception>
    public static string KernelPath(string subtype) => KernelPathOf(entry: Kernel(subtype: subtype).Entry);
    /// <summary>Packs the <c>Conversion</c> constants <c>camera-conversion.hlsl</c> reads for a colorimetry: luma
    /// offset and scale and chroma scale in code units, one padding float, the matrix's four coefficients (red from V,
    /// green from U, green from V, blue from U), and the chroma sample's offset on each axis, zero where cosited and
    /// one half where centered, then two padding floats.</summary>
    /// <param name="colorimetry">The stream's colorimetry metadata.</param>
    /// <returns>The twelve constants, in the order the constant buffer declares them.</returns>
    /// <exception cref="NotSupportedException"><paramref name="colorimetry"/> names a matrix, range or chroma siting the
    /// GPU conversion does not support.</exception>
    public static float[] ConversionConstants(Win32CameraColorimetry colorimetry) {
        var conversion = colorimetry.Resolve();
        var limited = (Win32YuvRange.Limited == conversion.Range);
        var bt709 = (Win32YuvMatrix.Bt709 == conversion.Matrix);
        float[] constants = [
            (limited ? 16f : 0f),
            (limited ? 219f : 255f),
            (limited ? 224f : 255f),
            0f,
            (bt709 ? 1.5748f : 1.402f),
            (bt709 ? 0.187324f : 0.344136f),
            (bt709 ? 0.468124f : 0.714136f),
            (bt709 ? 1.8556f : 1.772f),
            (conversion.ChromaHorizontallyCosited ? 0f : 0.5f),
            (conversion.ChromaVerticallyCosited ? 0f : 0.5f),
            0f,
            0f,
        ];

        return constants;
    }

    private static ID3D11Buffer* CreateConversion(ID3D11Device* device, float[] constants) {
        ID3D11Buffer* buffer = null;
        var description = new D3D11_BUFFER_DESC {
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            ByteWidth = checked((uint)(constants.Length * sizeof(float))),
            Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
        };

        fixed (float* data = constants) {
            var initialData = new D3D11_SUBRESOURCE_DATA { pSysMem = data };

            device->CreateBuffer(
                pDesc: &description,
                pInitialData: &initialData,
                ppBuffer: &buffer
            );
        }

        return ((buffer is null)
            ? throw new InvalidOperationException(message: "D3D11 camera conversion constant buffer creation returned no buffer")
            : buffer);
    }
    private static ID3D11ComputeShader* CreateShader(ID3D11Device* device, string entry) {
        var path = KernelPathOf(entry: entry);
        var bytecode = (File.Exists(path: path)
            ? File.ReadAllBytes(path: path)
            : throw new NotSupportedException(message: $"the camera conversion kernel '{entry}' has no precompiled bytecode at '{path}'; a build compiles it on Windows only"));
        ID3D11ComputeShader* shader = null;

        fixed (byte* code = bytecode) {
            device->CreateComputeShader(
                pShaderBytecode: code,
                BytecodeLength: ((nuint)bytecode.Length),
                pClassLinkage: null,
                ppComputeShader: &shader
            );
        }

        return ((shader is null)
            ? throw new InvalidOperationException(message: $"D3D11 camera conversion kernel '{entry}' creation returned no shader")
            : shader);
    }
    private static string KernelPathOf(string entry) => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "Assets",
        path3: "Shaders",
        path4: $"{KernelStem}.{entry}.dxbc"
    );
    private static ID3D10Multithread* ProtectMultithreaded(ID3D11Device* device) {
        var iid = ID3D10Multithread.IID_Guid;

        Win32D3D11.ThrowIfFailed(
            hr: ((IUnknown*)device)->QueryInterface(
                ppvObject: out var pointer,
                riid: in iid
            ),
            operation: "QueryInterface(ID3D10Multithread)"
        );
        var multithread = ((ID3D10Multithread*)pointer);

        _ = multithread->SetMultithreadProtected(bMTProtect: true);

        return multithread;
    }
    private static void ValidateAdapter(ID3D11Device* device, long expectedLuid) {
        var iid = IDXGIDevice.IID_Guid;

        Win32D3D11.ThrowIfFailed(
            hr: ((IUnknown*)device)->QueryInterface(
                ppvObject: out var pointer,
                riid: in iid
            ),
            operation: "QueryInterface(IDXGIDevice)"
        );
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
