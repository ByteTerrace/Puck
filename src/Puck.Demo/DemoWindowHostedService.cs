using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Demo.Commands;
using Puck.Demo.Rendering;
using Puck.Demo.Scene;
using Puck.Platform;
using Puck.Vulkan;

namespace Puck.Demo;

internal sealed class DemoWindowHostedService : BackgroundService {
    private const int PollIntervalMilliseconds = 16;

    private readonly IHostApplicationLifetime m_applicationLifetime;
    private readonly IDemoExitSignal m_exitSignal;
    private readonly ILogger<DemoWindowHostedService> m_logger;
    private readonly VulkanRenderer m_renderer;
    private readonly DemoScene m_scene;
    private readonly SdfViewRenderer m_sdfRenderer;
    private readonly DemoShell m_shell;
    private readonly INativeWindowFactory m_windowFactory;

    public DemoWindowHostedService(
        IHostApplicationLifetime applicationLifetime,
        IDemoExitSignal exitSignal,
        ILogger<DemoWindowHostedService> logger,
        VulkanRenderer renderer,
        DemoScene scene,
        SdfViewRenderer sdfRenderer,
        DemoShell shell,
        INativeWindowFactory windowFactory
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(exitSignal);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sdfRenderer);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(windowFactory);

        m_applicationLifetime = applicationLifetime;
        m_exitSignal = exitSignal;
        m_logger = logger;
        m_renderer = renderer;
        m_scene = scene;
        m_sdfRenderer = sdfRenderer;
        m_shell = shell;
        m_windowFactory = windowFactory;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        // Native windows and Vulkan presentation are single-thread affine: both the message
        // pump and the renderer must live on the thread that created the window, and that work
        // must not block host startup. So the loop owns a dedicated thread and bridges back to
        // the host via IHostApplicationLifetime.
        var pumpThread = new Thread(start: () => RunWindowLoop(stoppingToken: stoppingToken)) {
            IsBackground = true,
            Name = "Puck.Demo Window Pump",
        };

        pumpThread.Start();

        return Task.CompletedTask;
    }

    private void RunWindowLoop(CancellationToken stoppingToken) {
        try {
            using var window = m_windowFactory.Create();

            try {
                m_renderer.Initialize(window: window);
                m_sdfRenderer.Initialize();

                m_logger.LogInformation(
                    "Opened native window \"{Title}\" ({Width}x{Height}) with the Vulkan SDF renderer.",
                    window.Title,
                    window.Width,
                    window.Height
                );
                window.Show();

                while (
                    window.IsOpen &&
                    !stoppingToken.IsCancellationRequested
                ) {
                    window.PollEvents();

                    // Clear last frame's transient command values before sources re-assert them.
                    m_shell.BeginFrame();

                    while (window.TryDequeueInput(inputEvent: out var inputEvent)) {
                        m_shell.Enqueue(packet: inputEvent);
                    }

                    m_shell.Update();

                    // Re-upload only when the scene's program actually changed (first frame, scene switch).
                    if (m_scene.ConsumeProgramChanged()) {
                        m_sdfRenderer.UploadProgram(program: m_scene.Program);
                    }

                    m_sdfRenderer.Render(scene: m_scene);

                    if (m_exitSignal.TryConsumeExit()) {
                        window.Close();
                        window.PollEvents();
                        break;
                    }

                    Thread.Sleep(millisecondsTimeout: PollIntervalMilliseconds);
                }

                if (window.IsOpen) {
                    window.Close();
                    window.PollEvents();
                }

                m_logger.LogInformation("Native window closed; shutting the host down.");
            } finally {
                // Tear down Vulkan (waits for device idle) BEFORE the window — its surface
                // references the native window handle. The SDF renderer's GPU resources depend on the
                // device, so dispose it before the renderer that owns the device.
                m_sdfRenderer.Dispose();
                m_renderer.Dispose();
            }
        } finally {
            m_applicationLifetime.StopApplication();
        }
    }
}
