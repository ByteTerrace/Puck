using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
namespace Puck.Post;

/// <summary>The shared per-run context handed to every <see cref="IPostStage"/>: the application service provider
/// (through which a stage resolves the neutral GPU services), the shared GPU device the offscreen host published, the
/// lazily-created LUID-matched Direct3D 12 device the cross-backend tier shares, and the directory stages write their
/// artifacts to.</summary>
internal sealed class PostContext : IDisposable {
    private readonly IGpuDeviceContext? m_gpuDevice;
    private readonly IServiceProvider m_services;
    private PostDirectXDevice? m_directXDevice;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="PostContext"/> class.</summary>
    /// <param name="services">The application service provider.</param>
    /// <param name="gpuDevice">The shared GPU device the host published, or <see langword="null"/> when none is available.</param>
    /// <param name="artifactsDirectory">The directory stages write artifacts to.</param>
    public PostContext(IServiceProvider services, IGpuDeviceContext? gpuDevice, string artifactsDirectory) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(artifactsDirectory);

        m_gpuDevice = gpuDevice;
        m_services = services;
        ArtifactsDirectory = artifactsDirectory;
    }

    /// <summary>The directory stages write artifacts to.</summary>
    public string ArtifactsDirectory { get; }

    /// <summary>Resolves a required service from the application provider.</summary>
    /// <typeparam name="TService">The service type.</typeparam>
    /// <returns>The resolved service.</returns>
    public TService Resolve<TService>() where TService : notnull =>
        m_services.GetRequiredService<TService>();

    /// <summary>Returns the shared GPU device the host published, or throws when none is available — a GPU stage cannot
    /// run without one.</summary>
    /// <returns>The shared GPU device context.</returns>
    /// <exception cref="InvalidOperationException">No GPU device was published by the host.</exception>
    public IGpuDeviceContext RequireGpuDevice() =>
        (m_gpuDevice ?? throw new InvalidOperationException(message: "No GPU device context was published by the host; a GPU stage cannot run."));

    /// <summary>Returns the shared LUID-matched Direct3D 12 device for the cross-backend tier, creating it on the
    /// first call. Shared across Tier C with an explicit reset between stages: every acquire waits BOTH devices idle,
    /// so one stage's in-flight work can never alias the next stage's shared-handle imports. (The per-stage-device
    /// fallback from the plan's risk list applies if shared-handle or descriptor-pool leaks ever appear.)</summary>
    /// <returns>The Direct3D 12 device bundle.</returns>
    [SupportedOSPlatform("windows10.0.10240")]
    public PostDirectXDevice RequireDirectXDevice() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        m_directXDevice ??= new PostDirectXDevice(hostProvider: m_services);
        m_directXDevice.DeviceContext.WaitIdle();
        m_gpuDevice?.WaitIdle();

        return m_directXDevice;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        // The device can only have been created on Windows (RequireDirectXDevice is platform-gated).
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            m_directXDevice?.Dispose();
        }
    }
}
