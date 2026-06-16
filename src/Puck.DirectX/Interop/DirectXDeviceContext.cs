using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// Owns a Direct3D 12 device and a direct command queue, and exposes them as an
/// <see cref="IDirectXDeviceContext"/>. This is the Direct3D 12 analog of the Vulkan renderer that owns and
/// publishes the shared device chain: a host creates one and publishes it through the capability seam so every
/// DirectX node in its subtree resolves — and shares — the same device.
/// <para>
/// The device is created lazily on first use, on the adapter identified by <c>adapterLuid</c> (so it can be
/// matched to another backend's GPU for resource sharing) — falling back to the default adapter when the LUID
/// is zero. Deferring lets the caller supply a LUID that is only known once the other backend's device exists.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXDeviceContext : IDirectXDeviceContext, IDisposable {
    private readonly long m_adapterLuid;
    private readonly IDirectXDeviceApi m_deviceApi;
    private readonly Func<long>? m_adapterLuidProvider;
    private nint m_commandQueue;
    private DirectXDevice? m_device;
    private bool m_disposed;

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

        var adapterLuid = (m_adapterLuidProvider?.Invoke() ?? m_adapterLuid);

        m_device = ((0 != adapterLuid)
            ? m_deviceApi.CreateDevice(
                adapterLuid: adapterLuid,
                minimumFeatureLevel: FeatureLevel
            )
            : CreateDefaultDevice(minimumFeatureLevel: FeatureLevel));

        var queueDesc = new D3D12_COMMAND_QUEUE_DESC {
            Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
        };

        ((ID3D12Device*)m_device.Handle)->CreateCommandQueue(
            pDesc: in queueDesc,
            riid: ID3D12CommandQueue.IID_Guid,
            ppCommandQueue: out var commandQueue
        );
        m_commandQueue = (nint)commandQueue;
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

    /// <summary>Releases the command queue and the owned device. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (0 != m_commandQueue) {
            _ = ((IUnknown*)m_commandQueue)->Release();
            m_commandQueue = 0;
        }

        m_device?.Dispose();
        m_device = null;
    }
}
