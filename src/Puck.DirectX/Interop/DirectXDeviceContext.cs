using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Security;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// Owns a Direct3D 12 device and a direct command queue, and exposes them as an
/// <see cref="IDirectXDeviceContext"/> and <see cref="IGpuDeviceContext"/>. This is the Direct3D 12 analog of
/// the Vulkan renderer that owns and publishes the shared device chain: a host creates one and publishes it
/// through the capability seam so every DirectX node in its subtree resolves — and shares — the same device.
/// <para>
/// The device is created lazily on first use, on the adapter identified by <c>adapterLuid</c> (so it can be
/// matched to another backend's GPU for resource sharing) — falling back to the default adapter when the LUID
/// is zero. Deferring lets the caller supply a LUID that is only known once the other backend's device exists.
/// A creation this host cannot satisfy raises <see cref="GpuDeviceUnavailableException"/> from whichever member
/// first needed the device.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXDeviceContext : IDirectXDeviceContext, IGpuDeviceContext, IGpuPipelineCache, IDisposable {
    private readonly long m_adapterLuid;
    private readonly IDirectXDeviceApi m_deviceApi;
    private readonly Func<long>? m_adapterLuidProvider;
    private readonly GpuPipelineCacheStore? m_pipelineCacheStore;
    private readonly GpuPipelineCacheWork? m_pipelineCacheWork;
    private readonly Lock m_dispatchSignatureLock = new();

    private nint m_commandQueue;
    private DirectXDevice? m_device;
    private nint m_dispatchSignature;
    private bool m_disposed;
    private nint m_idleFence;
    private nint m_infoQueue;
    private HANDLE m_idleFenceEvent;
    private GpuDeviceIdentity? m_identity;
    private ulong m_idleFenceValue;
    private GpuMemoryProfile m_memoryProfile;

    /// <summary>Initializes a new instance that creates its device on the default adapter at feature level 11.0.</summary>
    public DirectXDeviceContext()
        : this(
        adapterLuid: 0,
        deviceApi: new Apis.DirectXNativeDeviceApi(),
        minimumFeatureLevel: DirectXFeatureLevel.Level110
    ) {
    }
    /// <summary>Initializes a new instance bound to a fixed adapter LUID.</summary>
    /// <param name="adapterLuid">The adapter LUID to create the device on, or zero for the default adapter.</param>
    /// <param name="deviceApi">The device API used to create the device on a specific adapter.</param>
    /// <param name="minimumFeatureLevel">The minimum Direct3D feature level the device must support.</param>
    /// <param name="pipelineCacheWork">The backend's pipeline counts; when supplied, each device this context creates
    /// gets a <see cref="DirectXPipelineLibrary"/> that counts into it. <see langword="null"/> creates pipelines
    /// without a library.</param>
    /// <param name="pipelineCacheStore">Where the library lives on disk, or <see langword="null"/> to keep it in
    /// memory only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceApi"/> is <see langword="null"/>.</exception>
    public DirectXDeviceContext(long adapterLuid, IDirectXDeviceApi deviceApi, DirectXFeatureLevel minimumFeatureLevel, GpuPipelineCacheWork? pipelineCacheWork = null, GpuPipelineCacheStore? pipelineCacheStore = null) {
        ArgumentNullException.ThrowIfNull(deviceApi);

        m_adapterLuid = adapterLuid;
        m_deviceApi = deviceApi;
        m_pipelineCacheStore = pipelineCacheStore;
        m_pipelineCacheWork = pipelineCacheWork;
        FeatureLevel = minimumFeatureLevel;
    }
    /// <summary>Initializes a new instance whose adapter LUID is resolved lazily on first use.</summary>
    /// <param name="adapterLuidProvider">Resolves the adapter LUID to create the device on (zero for the default adapter); invoked once, on first use.</param>
    /// <param name="deviceApi">The device API used to create the device on a specific adapter.</param>
    /// <param name="minimumFeatureLevel">The minimum Direct3D feature level the device must support.</param>
    /// <param name="pipelineCacheWork">The backend's pipeline counts; when supplied, each device this context creates
    /// gets a <see cref="DirectXPipelineLibrary"/> that counts into it. <see langword="null"/> creates pipelines
    /// without a library.</param>
    /// <param name="pipelineCacheStore">Where the library lives on disk, or <see langword="null"/> to keep it in
    /// memory only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="adapterLuidProvider"/> or <paramref name="deviceApi"/> is <see langword="null"/>.</exception>
    public DirectXDeviceContext(Func<long> adapterLuidProvider, IDirectXDeviceApi deviceApi, DirectXFeatureLevel minimumFeatureLevel, GpuPipelineCacheWork? pipelineCacheWork = null, GpuPipelineCacheStore? pipelineCacheStore = null) {
        ArgumentNullException.ThrowIfNull(adapterLuidProvider);
        ArgumentNullException.ThrowIfNull(deviceApi);

        m_adapterLuidProvider = adapterLuidProvider;
        m_deviceApi = deviceApi;
        m_pipelineCacheStore = pipelineCacheStore;
        m_pipelineCacheWork = pipelineCacheWork;
        FeatureLevel = minimumFeatureLevel;
    }

    /// <inheritdoc />
    public long AdapterLuid {
        get {
            EnsureCreated();

            return m_deviceApi.GetAdapterLuid(deviceHandle: m_device!.Handle);
        }
    }
    /// <inheritdoc />
    public nint DeviceHandle {
        get {
            EnsureCreated();

            return m_device!.Handle;
        }
    }
    /// <summary>Gets the one-argument <c>DISPATCH</c> command signature <c>ExecuteIndirect</c> uses on this context's
    /// device, created on first use. It binds nothing, so it needs no root signature. It is a device child: released
    /// with the device on <see cref="Recreate"/> and <see cref="Dispose"/>, before the live-object report.</summary>
    public nint DispatchSignature {
        get {
            lock (m_dispatchSignatureLock) {
                if (0 != m_dispatchSignature) {
                    return m_dispatchSignature;
                }

                var device = ((ID3D12Device*)DeviceHandle);
                var argumentDesc = new D3D12_INDIRECT_ARGUMENT_DESC {
                    Type = D3D12_INDIRECT_ARGUMENT_TYPE.D3D12_INDIRECT_ARGUMENT_TYPE_DISPATCH,
                };
                var signatureDesc = new D3D12_COMMAND_SIGNATURE_DESC {
                    ByteStride = ((uint)sizeof(D3D12_DISPATCH_ARGUMENTS)),
                    NumArgumentDescs = 1,
                    pArgumentDescs = &argumentDesc,
                };
                void* signature;
                var signatureIid = ID3D12CommandSignature.IID_Guid;

                device->CreateCommandSignature(
                    pDesc: in signatureDesc,
                    pRootSignature: null,
                    ppvCommandSignature: &signature,
                    riid: in signatureIid
                );
                m_dispatchSignature = ((nint)signature);

                return m_dispatchSignature;
            }
        }
    }
    /// <inheritdoc />
    /// <remarks>Read from the adapter when the device is created; <see langword="null"/> before, and reading it never
    /// creates the device.</remarks>
    public GpuDeviceIdentity? Identity => m_identity;
    /// <inheritdoc />
    /// <remarks>Read from the device and its adapter when the device is created; the default profile before, and
    /// reading it never creates the device.</remarks>
    public GpuMemoryProfile MemoryProfile => m_memoryProfile;
    /// <inheritdoc />
    public nint CommandQueueHandle {
        get {
            EnsureCreated();

            return m_commandQueue;
        }
    }
    /// <inheritdoc />
    public DirectXDevice Device {
        get {
            EnsureCreated();

            return m_device!;
        }
    }
    /// <summary>Gets where debug-layer messages are written, one <c>[d3d12-debug]</c> line each: the drain's messages
    /// and, at teardown, every object the device still holds. <see langword="null"/> writes to the process's standard
    /// error at the time of writing, where a run's validation checks read them.</summary>
    public TextWriter? DebugOutput { get; init; }
    /// <summary>Gets whether the device is created with the Direct3D 12 debug layer, whose messages
    /// <see cref="DrainDebugMessages"/> surfaces and whose live-object report <see cref="Dispose"/> surfaces. Off by
    /// default: on some configurations the layer makes device creation fail, and it cannot be turned off once enabled
    /// in a process. The presenter registration sets it from <c>GpuDeviceOptions.DebugLayers</c>.</summary>
    public bool EnableDebugLayer { get; init; }
    /// <inheritdoc />
    public DirectXFeatureLevel FeatureLevel { get; }
    /// <summary>Gets whether the created device reports through the debug layer's info queue, so its messages and its
    /// live objects at teardown are surfaced; <see langword="false"/> before the device exists, and when the layer was
    /// not requested or could not load.</summary>
    public bool HasDebugLayer => (0 != m_infoQueue);
    /// <inheritdoc />
    public bool IsInitialized => (!m_disposed && (m_device is not null));
    /// <inheritdoc />
    public DirectXPipelineLibrary? PipelineLibrary { get; private set; }

    private void EnsureCreated() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_device is not null) {
            return;
        }

        // Enable the Direct3D 12 debug layer BEFORE device creation so it validates this device and feeds the
        // [d3d12-debug] drain. OPT-IN (EnableDebugLayer): on some configurations
        // (observed on a Windows 11 build 26200 / RTX 4070 with a mismatched Graphics Tools layer) EnableDebugLayer
        // poisons the process so the very next D3D12CreateDevice fails with DXGI_ERROR_DEVICE_RESET (0x887A0007) —
        // and the layer cannot be turned off once enabled in a process, so there is no in-process recovery.
        // Defaulting off keeps device creation working everywhere.
        if (EnableDebugLayer) {
            void* debugInterface;
            var debugIid = ID3D12Debug.IID_Guid;

            if (PInvoke.D3D12GetDebugInterface(
                ppvDebug: &debugInterface,
                riid: in debugIid
            ).Succeeded) {
                ((ID3D12Debug*)debugInterface)->EnableDebugLayer();
                _ = ((IUnknown*)debugInterface)->Release();
            }
        }

        // The device bring-up. No Direct3D 12 runtime, no adapter at the feature level, a device creation the driver
        // refuses, or a device below the Shader Model floor all mean this host has no usable Direct3D 12 device; a
        // removed device during creation stays DeviceLostException.
        try {
            var adapterLuid = (m_adapterLuidProvider?.Invoke() ?? m_adapterLuid);

            m_device = ((0 != adapterLuid)
                ? m_deviceApi.CreateDevice(
                    adapterLuid: adapterLuid,
                    minimumFeatureLevel: FeatureLevel
                )
                : CreateDefaultDevice(minimumFeatureLevel: FeatureLevel)
            );

            EnsureShaderModelFloor(deviceHandle: m_device.Handle);
            m_identity = m_deviceApi.GetDeviceIdentity(deviceHandle: m_device.Handle);
            m_memoryProfile = m_deviceApi.GetMemoryProfile(deviceHandle: m_device.Handle);
        } catch (Exception exception) when ((exception is DirectXException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)) {
            m_device?.Dispose();
            m_device = null;

            throw new GpuDeviceUnavailableException(
                backend: "directx",
                innerException: exception,
                reason: exception.Message
            );
        }

        // The info queue (present only when the debug layer loaded) lets DrainDebugMessages surface validation
        // messages to the console instead of only OutputDebugString.
        void* infoQueuePtr;
        var infoQueueIid = ID3D12InfoQueue.IID_Guid;

        if (((IUnknown*)m_device.Handle)->QueryInterface(
            ppvObject: out infoQueuePtr,
            riid: in infoQueueIid
        ).Succeeded) {
            m_infoQueue = ((nint)infoQueuePtr);
        }

        var queueDesc = new D3D12_COMMAND_QUEUE_DESC {
            Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
        };

        ((ID3D12Device*)m_device.Handle)->CreateCommandQueue(
            pDesc: in queueDesc,
            ppCommandQueue: out var commandQueue,
            riid: ID3D12CommandQueue.IID_Guid
        );
        m_commandQueue = ((nint)commandQueue);

        ((ID3D12Device*)m_device.Handle)->CreateFence(
            Flags: default,
            InitialValue: 0,
            ppFence: out var idleFence,
            riid: ID3D12Fence.IID_Guid
        );
        m_idleFence = ((nint)idleFence);
        m_idleFenceValue = 1;
        m_idleFenceEvent = PInvoke.CreateEvent(
            bInitialState: false,
            bManualReset: false,
            lpEventAttributes: ((SECURITY_ATTRIBUTES*)null),
            lpName: default(PCWSTR)
        );

        if (m_idleFenceEvent.IsNull) {
            throw new DirectXException(
                operation: "CreateEventW",
                result: Marshal.GetHRForLastWin32Error()
            );
        }

        if (m_pipelineCacheWork is not null) {
            // The file is named from the identity read above, the one read of the adapter and driver: a driver version
            // CheckInterfaceSupport would not report is zero there, and still names a file on disk.
            PipelineLibrary = DirectXPipelineLibrary.Create(
                deviceHandle: m_device.Handle,
                file: GpuPipelineCacheFile.Open(
                    identity: m_identity!,
                    store: m_pipelineCacheStore,
                    work: m_pipelineCacheWork
                )
            );
        }
    }
    // The Shader Model 6.6 device floor — the DXIL peer of the Vulkan SPIR-V 1.6 floor enforced in
    // VulkanPhysicalDeviceSelector. Puck's DXIL kernels are compiled at -T *_6_6, so a device below SM 6.6 would reject
    // them at pipeline creation; catch it here with a loud, named failure. CheckFeatureSupport clamps HighestShaderModel
    // to the driver's actual support, and a runtime too old to recognize 6.6 fails the query outright — both are below
    // the floor. All four supported GPUs clear SM 6.6 on current drivers (the RTX 4070 reaches 6.7/6.8).
    private static void EnsureShaderModelFloor(nint deviceHandle) {
        const D3D_SHADER_MODEL RequiredShaderModel = D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_6;

        var device = ((ID3D12Device*)deviceHandle);
        var shaderModel = new D3D12_FEATURE_DATA_SHADER_MODEL {
            HighestShaderModel = RequiredShaderModel,
        };
        var queried = false;

        // CsWin32's friendly CheckFeatureSupport overload throws on a failing HRESULT (E_INVALIDARG on a runtime that
        // does not recognize the requested model); treat any failure as "below the floor" and fall through to the throw.
        try {
            device->CheckFeatureSupport(
                Feature: D3D12_FEATURE.D3D12_FEATURE_SHADER_MODEL,
                FeatureSupportDataSize: ((uint)sizeof(D3D12_FEATURE_DATA_SHADER_MODEL)),
                pFeatureSupportData: &shaderModel
            );
            queried = true;
        } catch {
            // Swallow — handled by the floor check below.
        }

        if (
            queried &&
            (shaderModel.HighestShaderModel >= RequiredShaderModel)
        ) {
            return;
        }

        var reported = (queried
            ? $"{(((int)shaderModel.HighestShaderModel) >> 4)}.{((int)shaderModel.HighestShaderModel) & 0xF}"
            : "unknown (feature query failed)"
        );

        throw new InvalidOperationException(message:
            ((((string)$"Direct3D 12 device reports Shader Model {reported}, below the required 6.6 floor. Puck's DXIL kernels are compiled at Shader Model 6.6 and cannot load on this device. Puck supports exactly four GPUs — RTX 2070 ") +
            "(Turing), RTX 4070 (Ada), Steam Machine (AMD RDNA3), and Steam Deck (AMD RDNA2 Van Gogh) — all of which ") +
            "clear Shader Model 6.6 on current drivers; update your GPU driver or run on supported hardware."));
    }
    private static DirectXDevice CreateDefaultDevice(DirectXFeatureLevel minimumFeatureLevel) {
        void* device;
        var deviceIid = ID3D12Device.IID_Guid;

        PInvoke.D3D12CreateDevice(
            MinimumFeatureLevel: ((D3D_FEATURE_LEVEL)minimumFeatureLevel),
            pAdapter: null,
            ppDevice: &device,
            riid: deviceIid
        ).ThrowIfFailed(operation: "D3D12CreateDevice");

        return new DirectXDevice(
            deviceHandle: ((nint)device),
            featureLevel: minimumFeatureLevel
        );
    }

    /// <inheritdoc/>
    public void WaitIdle() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        // A device never created has run no work, so there is nothing to drain; draining must not create it.
        if (m_device is null) {
            return;
        }

        var fence = ((ID3D12Fence*)m_idleFence);
        var value = m_idleFenceValue;

        ((ID3D12CommandQueue*)m_commandQueue)->Signal(
            Value: value,
            pFence: fence
        );
        m_idleFenceValue++;

        if (fence->GetCompletedValue() < value) {
            fence->SetEventOnCompletion(
                Value: value,
                hEvent: m_idleFenceEvent
            );
            _ = PInvoke.WaitForSingleObject(
                dwMilliseconds: uint.MaxValue,
                hHandle: m_idleFenceEvent
            );
        }

        DrainDebugMessages();
    }
    /// <inheritdoc/>
    public void Persist() => PipelineLibrary?.Persist();
    /// <summary>Writes any Direct3D 12 debug-layer messages accumulated since the last drain to <see cref="DebugOutput"/>, then
    /// clears them. A no-op when the debug layer / info queue is unavailable (the default; opt in with
    /// <see cref="EnableDebugLayer"/>). Also called at the end of <see cref="WaitIdle"/>; callers that no longer drain the
    /// whole device every frame (e.g. a per-ring-slot presenter) call this directly to keep the same per-frame
    /// debug-message cadence.</summary>
    public void DrainDebugMessages() =>
        Drain(live: false);

    // Writes every stored message as one [d3d12-debug] line through DebugOutput, then clears them. A live-object report
    // is written as "live <description>" and leaves out the device's own entry: the device is still held here, by this
    // context, while it asks.
    private void Drain(bool live) {
        if (0 == m_infoQueue) {
            return;
        }

        var output = (DebugOutput ?? Console.Error);
        var infoQueue = ((ID3D12InfoQueue*)m_infoQueue);
        var count = infoQueue->GetNumStoredMessages();

        for (var index = 0UL; (index < count); index++) {
            nuint length = 0;

            // First call (null message) returns the byte length the message + its description need.
            infoQueue->GetMessage(
                MessageIndex: index,
                pMessage: null,
                pMessageByteLength: &length
            );

            if (0 == length) {
                continue;
            }

            var buffer = new byte[((int)length)];

            fixed (byte* pointer = buffer) {
                var message = ((D3D12_MESSAGE*)pointer);

                infoQueue->GetMessage(
                    MessageIndex: index,
                    pMessage: message,
                    pMessageByteLength: &length
                );

                // A pipeline library lookup that misses is the cache working as designed, counted as
                // gpu.pipeline-cache.misses; the layer warns on every one, so it is the one message not reported.
                if (
                    (message->ID == D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LOADPIPELINE_NAMENOTFOUND) ||
                    (live && (message->ID == D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LIVE_DEVICE))
                ) {
                    continue;
                }

                var description = new string(
                    length: ((int)((message->DescriptionByteLength > 0)
                    ? (message->DescriptionByteLength - 1)
                    : 0)),
                    startIndex: 0,
                    value: ((sbyte*)message->pDescription)
                );

                output.WriteLine(value: (live
                    ? $"[d3d12-debug] live {description}"
                    : $"[d3d12-debug] {message->Severity}: {description}"
                ));
            }
        }

        infoQueue->ClearStoredMessages();
    }
    // With the debug layer on, asks it for every object the device still holds once this context has released its own,
    // and writes each as a [d3d12-debug] live line: any one is a leak, and fails a debug-layer run like any other
    // message. Messages already stored are drained first, in their own form. Internal references are left out; the
    // device's own entry is dropped by the drain. A report the layer refuses is itself a [d3d12-debug] line, since the
    // run then cannot show that nothing leaked.
    private void ReleaseDispatchSignature() {
        lock (m_dispatchSignatureLock) {
            if (0 != m_dispatchSignature) {
                _ = ((IUnknown*)m_dispatchSignature)->Release();
                m_dispatchSignature = 0;
            }
        }
    }
    private void ReportLiveObjects() {
        if ((0 == m_infoQueue) || (m_device is null)) {
            return;
        }

        Drain(live: false);

        var debugDeviceIid = ID3D12DebugDevice.IID_Guid;
        var queried = ((IUnknown*)m_device.Handle)->QueryInterface(
            ppvObject: out var debugDevice,
            riid: in debugDeviceIid
        );

        if (queried.Failed) {
            (DebugOutput ?? Console.Error).WriteLine(value: $"[d3d12-debug] live objects not reported: QueryInterface(ID3D12DebugDevice) returned 0x{queried.Value:X8}");

            return;
        }

        try {
            ((ID3D12DebugDevice*)debugDevice)->ReportLiveDeviceObjects(Flags: D3D12_RLDO_FLAGS.D3D12_RLDO_DETAIL | D3D12_RLDO_FLAGS.D3D12_RLDO_IGNORE_INTERNAL);
        } catch (Exception exception) {
            (DebugOutput ?? Console.Error).WriteLine(value: $"[d3d12-debug] live objects not reported: ReportLiveDeviceObjects returned 0x{exception.HResult:X8}");
        } finally {
            _ = ((IUnknown*)debugDevice)->Release();
        }

        Drain(live: true);
    }

    /// <summary>Recreates the device, command queue, and idle fence IN PLACE after a device removal — preserving this
    /// instance's identity so the published <c>IGpuDeviceContext</c> capability (and every node that resolved it) stays
    /// valid; they rebuild their own device-derived resources. The old objects are released WITHOUT a GPU drain (the
    /// device is removed, so a Signal/wait would never complete; a COM Release on a removed device's objects is safe). The
    /// debug layer is NOT re-enabled here (it cannot be toggled per-process and can poison creation on some configs);
    /// <see cref="EnsureCreated"/> applies the same opt-in gate it always does.</summary>
    public void Recreate() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (0 != m_idleFence) {
            _ = ((IUnknown*)m_idleFence)->Release();
            m_idleFence = 0;
        }

        if (!m_idleFenceEvent.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_idleFenceEvent);
            m_idleFenceEvent = HANDLE.Null;
        }

        if (0 != m_commandQueue) {
            _ = ((IUnknown*)m_commandQueue)->Release();
            m_commandQueue = 0;
        }

        if (0 != m_infoQueue) {
            _ = ((IUnknown*)m_infoQueue)->Release();
            m_infoQueue = 0;
        }

        ReleaseDispatchSignature();
        // Serializing a removed device's library can fail; that is reported, and the file already on disk stays.
        PipelineLibrary?.Dispose();
        PipelineLibrary = null;
        m_device?.Dispose();
        m_device = null;
        m_idleFenceValue = 1;

        // m_device is now null, so this rebuilds a fresh device + queue + fence + event.
        EnsureCreated();
    }
    /// <summary>Releases the command queue and the owned device. With the debug layer on, every object the device still
    /// holds once the context has released its own is written as a <c>[d3d12-debug] live</c> line before the device is
    /// released. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        if (
            (0 != m_commandQueue) &&
            (0 != m_idleFence)
        ) {
            WaitIdle();
        }

        m_disposed = true;

        if (0 != m_idleFence) {
            _ = ((IUnknown*)m_idleFence)->Release();
            m_idleFence = 0;
        }

        if (!m_idleFenceEvent.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_idleFenceEvent);
            m_idleFenceEvent = HANDLE.Null;
        }

        if (0 != m_commandQueue) {
            _ = ((IUnknown*)m_commandQueue)->Release();
            m_commandQueue = 0;
        }

        ReleaseDispatchSignature();
        PipelineLibrary?.Dispose();
        PipelineLibrary = null;
        ReportLiveObjects();

        if (0 != m_infoQueue) {
            _ = ((IUnknown*)m_infoQueue)->Release();
            m_infoQueue = 0;
        }

        m_device?.Dispose();
        m_device = null;
    }
}
