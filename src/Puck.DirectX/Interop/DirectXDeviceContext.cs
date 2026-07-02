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
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXDeviceContext : IDirectXDeviceContext, IGpuDeviceContext, IDisposable {
    private readonly long m_adapterLuid;
    private readonly IDirectXDeviceApi m_deviceApi;
    private readonly Func<long>? m_adapterLuidProvider;
    private nint m_commandQueue;
    private DirectXDevice? m_device;
    private bool m_disposed;
    private nint m_idleFence;
    private nint m_infoQueue;
    private HANDLE m_idleFenceEvent;
    private ulong m_idleFenceValue;

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
    /// <exception cref="ArgumentNullException"><paramref name="deviceApi"/> is <see langword="null"/>.</exception>
    public DirectXDeviceContext(long adapterLuid, IDirectXDeviceApi deviceApi, DirectXFeatureLevel minimumFeatureLevel) {
        ArgumentNullException.ThrowIfNull(deviceApi);

        m_adapterLuid = adapterLuid;
        m_deviceApi = deviceApi;
        FeatureLevel = minimumFeatureLevel;
    }

    /// <summary>Initializes a new instance whose adapter LUID is resolved lazily on first use.</summary>
    /// <param name="adapterLuidProvider">Resolves the adapter LUID to create the device on (zero for the default adapter); invoked once, on first use.</param>
    /// <param name="deviceApi">The device API used to create the device on a specific adapter.</param>
    /// <param name="minimumFeatureLevel">The minimum Direct3D feature level the device must support.</param>
    /// <exception cref="ArgumentNullException"><paramref name="adapterLuidProvider"/> or <paramref name="deviceApi"/> is <see langword="null"/>.</exception>
    public DirectXDeviceContext(Func<long> adapterLuidProvider, IDirectXDeviceApi deviceApi, DirectXFeatureLevel minimumFeatureLevel) {
        ArgumentNullException.ThrowIfNull(adapterLuidProvider);
        ArgumentNullException.ThrowIfNull(deviceApi);

        m_adapterLuidProvider = adapterLuidProvider;
        m_deviceApi = deviceApi;
        FeatureLevel = minimumFeatureLevel;
    }

    /// <inheritdoc />
    public nint DeviceHandle {
        get {
            EnsureCreated();

            return m_device!.Handle;
        }
    }
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
    /// <inheritdoc />
    public DirectXFeatureLevel FeatureLevel { get; }
    /// <inheritdoc />
    public bool IsInitialized => (!m_disposed && (m_device is not null));

    private void EnsureCreated() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_device is not null) {
            return;
        }

        // Enable the Direct3D 12 debug layer BEFORE device creation so it validates this device and feeds the
        // [d3d12-debug] drain. OPT-IN via PUCK_D3D12_DEBUG: on some configurations (observed on a Windows 11
        // build 26200 / RTX 4070 with a mismatched Graphics Tools layer) EnableDebugLayer poisons the process
        // so the very next D3D12CreateDevice fails with DXGI_ERROR_DEVICE_RESET (0x887A0007) — and the layer
        // cannot be turned off once enabled in a process, so there is no in-process recovery. Defaulting off
        // keeps device creation working everywhere; set PUCK_D3D12_DEBUG=1 to get the validation drain on
        // machines where the layer is healthy.
        if (Environment.GetEnvironmentVariable("PUCK_D3D12_DEBUG") is not null) {
            void* debugInterface;
            var debugIid = ID3D12Debug.IID_Guid;

            if (PInvoke.D3D12GetDebugInterface(riid: in debugIid, ppvDebug: &debugInterface).Succeeded) {
                ((ID3D12Debug*)debugInterface)->EnableDebugLayer();
                _ = ((IUnknown*)debugInterface)->Release();
            }
        }

        var adapterLuid = (m_adapterLuidProvider?.Invoke() ?? m_adapterLuid);

        m_device = ((0 != adapterLuid)
            ? m_deviceApi.CreateDevice(
                adapterLuid: adapterLuid,
                minimumFeatureLevel: FeatureLevel
            )
            : CreateDefaultDevice(minimumFeatureLevel: FeatureLevel));

        // The info queue (present only when the debug layer loaded) lets DrainDebugMessages surface validation
        // messages to the console instead of only OutputDebugString.
        void* infoQueuePtr;
        var infoQueueIid = ID3D12InfoQueue.IID_Guid;

        if (((IUnknown*)m_device.Handle)->QueryInterface(in infoQueueIid, out infoQueuePtr).Succeeded) {
            m_infoQueue = (nint)infoQueuePtr;
        }

        var queueDesc = new D3D12_COMMAND_QUEUE_DESC {
            Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
        };

        ((ID3D12Device*)m_device.Handle)->CreateCommandQueue(
            pDesc: in queueDesc,
            riid: ID3D12CommandQueue.IID_Guid,
            ppCommandQueue: out var commandQueue
        );
        m_commandQueue = (nint)commandQueue;

        ((ID3D12Device*)m_device.Handle)->CreateFence(
            InitialValue: 0,
            Flags: default,
            riid: ID3D12Fence.IID_Guid,
            ppFence: out var idleFence
        );
        m_idleFence = (nint)idleFence;
        m_idleFenceValue = 1;
        m_idleFenceEvent = PInvoke.CreateEvent(
            lpEventAttributes: (SECURITY_ATTRIBUTES*)null,
            bManualReset: false,
            bInitialState: false,
            lpName: default(PCWSTR)
        );

        if (m_idleFenceEvent.IsNull) {
            throw new DirectXException(
                operation: "CreateEventW",
                result: Marshal.GetHRForLastWin32Error()
            );
        }
    }
    private static DirectXDevice CreateDefaultDevice(DirectXFeatureLevel minimumFeatureLevel) {
        void* device;
        var deviceIid = ID3D12Device.IID_Guid;

        PInvoke.D3D12CreateDevice(
            pAdapter: null,
            MinimumFeatureLevel: (D3D_FEATURE_LEVEL)minimumFeatureLevel,
            riid: deviceIid,
            ppDevice: &device
        ).ThrowIfFailed(operation: "D3D12CreateDevice");

        return new DirectXDevice(
            deviceHandle: (nint)device,
            featureLevel: minimumFeatureLevel
        );
    }

    /// <inheritdoc/>
    public void WaitIdle() {
        EnsureCreated();

        var fence = (ID3D12Fence*)m_idleFence;
        var value = m_idleFenceValue;

        ((ID3D12CommandQueue*)m_commandQueue)->Signal(fence, value);
        m_idleFenceValue++;

        if (fence->GetCompletedValue() < value) {
            fence->SetEventOnCompletion(value, m_idleFenceEvent);
            _ = PInvoke.WaitForSingleObject(hHandle: m_idleFenceEvent, dwMilliseconds: uint.MaxValue);
        }

        DrainDebugMessages();
    }

    // Surface any Direct3D 12 debug-layer messages accumulated since the last drain to the console, then clear them.
    // A no-op when the debug layer / info queue is unavailable.
    private void DrainDebugMessages() {
        if (0 == m_infoQueue) {
            return;
        }

        var infoQueue = (ID3D12InfoQueue*)m_infoQueue;
        var count = infoQueue->GetNumStoredMessages();

        for (var index = 0UL; (index < count); index++) {
            nuint length = 0;

            // First call (null message) returns the byte length the message + its description need.
            infoQueue->GetMessage(MessageIndex: index, pMessage: null, pMessageByteLength: &length);

            if (0 == length) {
                continue;
            }

            var buffer = new byte[(int)length];

            fixed (byte* pointer = buffer) {
                var message = (D3D12_MESSAGE*)pointer;

                infoQueue->GetMessage(MessageIndex: index, pMessage: message, pMessageByteLength: &length);

                var description = new string(
                    value: (sbyte*)message->pDescription,
                    startIndex: 0,
                    length: (int)((message->DescriptionByteLength > 0) ? (message->DescriptionByteLength - 1) : 0)
                );

                Console.Error.WriteLine(value: $"[d3d12-debug] {message->Severity}: {description}");
            }
        }

        infoQueue->ClearStoredMessages();
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

        m_device?.Dispose();
        m_device = null;
        m_idleFenceValue = 1;

        // m_device is now null, so this rebuilds a fresh device + queue + fence + event.
        EnsureCreated();
    }

    /// <summary>Releases the command queue and the owned device. Safe to call more than once.</summary>
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

        if (0 != m_infoQueue) {
            _ = ((IUnknown*)m_infoQueue)->Release();
            m_infoQueue = 0;
        }

        m_device?.Dispose();
        m_device = null;
    }
}
