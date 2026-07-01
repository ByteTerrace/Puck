using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Post;

/// <summary>The root render node that hosts the POST battery. On its first <see cref="ProduceFrame"/> — once the
/// offscreen host has brought the device up — it runs the battery, records the aggregate exit code, writes the report,
/// and asks the terminal to exit. It never presents (always returns a default surface), so the window the launcher
/// opens flashes once and closes; the verdict is the process exit code.</summary>
internal sealed class PostBatteryNode : IRenderNode {
    private readonly string m_artifactsDirectory;
    private readonly PostBattery m_battery;
    private readonly NodeDescriptor m_descriptor = new(Name: "post-battery", SurfaceId: SurfaceId.New());
    private readonly PostRunResult m_runResult;
    private readonly IServiceProvider m_services;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="PostBatteryNode"/> class.</summary>
    /// <param name="battery">The battery to run on the first frame.</param>
    /// <param name="runResult">The shared carrier the aggregate exit code is written to.</param>
    /// <param name="services">The application service provider (handed to stages through the context).</param>
    /// <param name="artifactsDirectory">The directory the battery and its stages write artifacts to.</param>
    public PostBatteryNode(PostBattery battery, PostRunResult runResult, IServiceProvider services, string artifactsDirectory) {
        ArgumentNullException.ThrowIfNull(battery);
        ArgumentNullException.ThrowIfNull(runResult);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(artifactsDirectory);

        m_artifactsDirectory = artifactsDirectory;
        m_battery = battery;
        m_runResult = runResult;
        m_services = services;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_done) {
            return default;
        }

        m_done = true;

        try {
            // The shared GPU device is an inherited host capability (published once the offscreen host brought it up);
            // Tier A ignores it, the GPU tiers require it.
            _ = context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice);

            using var postContext = new PostContext(services: m_services, gpuDevice: gpuDevice, artifactsDirectory: m_artifactsDirectory);
            var report = m_battery.Run(context: postContext);

            report.Write(artifactsDirectory: m_artifactsDirectory);
            m_runResult.ExitCode = report.ExitCode;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"POST infra-fail | the battery could not run | {exception.Message}");
            m_runResult.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }
}
