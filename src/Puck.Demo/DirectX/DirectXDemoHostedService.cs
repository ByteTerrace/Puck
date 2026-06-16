using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Demo.Commands;
using Puck.Demo.Scene;
using Puck.DirectX;
using Puck.DirectX.Interop;
using Puck.Platform;

namespace Puck.Demo.DirectX;

/// <summary>
/// The Direct3D 12 backend's window loop, parallel to <see cref="DemoWindowHostedService"/>. It opens the
/// same native window, drives the shared command system + scene clock, and presents through a
/// <see cref="DirectXSwapChainRenderer"/>. This is Phase 1 of the D3D12 backend: it clears to an animated
/// color (proving device → queue → swap chain → present), before any SDF geometry is ported.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class DirectXDemoHostedService : BackgroundService {
    private const int PollIntervalMilliseconds = 16;

    private readonly IHostApplicationLifetime m_applicationLifetime;
    private readonly IDemoExitSignal m_exitSignal;
    private readonly ILogger<DirectXDemoHostedService> m_logger;
    private readonly DemoScene m_scene;
    private readonly DemoShell m_shell;
    private readonly INativeWindowFactory m_windowFactory;

    public DirectXDemoHostedService(
        IHostApplicationLifetime applicationLifetime,
        IDemoExitSignal exitSignal,
        ILogger<DirectXDemoHostedService> logger,
        DemoScene scene,
        DemoShell shell,
        INativeWindowFactory windowFactory
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(exitSignal);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(windowFactory);

        m_applicationLifetime = applicationLifetime;
        m_exitSignal = exitSignal;
        m_logger = logger;
        m_scene = scene;
        m_shell = shell;
        m_windowFactory = windowFactory;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        // Native windows and the D3D12 swap chain are single-thread affine: both the message pump and the
        // presenter must live on the thread that created the window, and that work must not block host
        // startup. So the loop owns a dedicated thread and bridges back via IHostApplicationLifetime.
        var pumpThread = new Thread(start: () => RunWindowLoop(stoppingToken: stoppingToken)) {
            IsBackground = true,
            Name = "Puck.Demo DirectX Window Pump",
        };

        pumpThread.Start();

        return Task.CompletedTask;
    }

    private void RunWindowLoop(CancellationToken stoppingToken) {
        try {
            using var window = m_windowFactory.Create();

            if (window is not INativeSurfaceSourceProvider surfaceSourceProvider) {
                throw new InvalidOperationException(message: "The DirectX backend requires a window that can provide a native surface binding.");
            }

            var binding = surfaceSourceProvider.CreateSurfaceBinding();

            if (
                (binding.DisplayKind != NativeDisplayKind.Win32) ||
                (binding.Win32 is not { } win32)
            ) {
                throw new InvalidOperationException(message: $"The DirectX backend requires a Win32 window; the native window reported display kind '{binding.DisplayKind}'.");
            }

            using var renderer = new DirectXSwapChainRenderer(
                height: window.Height,
                minimumFeatureLevel: DirectXFeatureLevel.Level110,
                width: window.Width,
                windowHandle: win32.WindowHandle
            );

            m_logger.LogInformation(
                "Opened native window \"{Title}\" ({Width}x{Height}) with the Direct3D 12 backend at {FeatureLevel}.",
                window.Title,
                window.Width,
                window.Height,
                renderer.FeatureLevel
            );
            window.Show();

            var clock = Stopwatch.StartNew();
            var resizeCount = window.ResizeCount;

            while (
                window.IsOpen &&
                !stoppingToken.IsCancellationRequested
            ) {
                window.PollEvents();
                m_shell.BeginFrame();

                while (window.TryDequeueInput(inputEvent: out var inputEvent)) {
                    m_shell.Enqueue(packet: inputEvent);
                }

                m_shell.Update();

                if (window.ResizeCount != resizeCount) {
                    resizeCount = window.ResizeCount;

                    renderer.Resize(
                        height: window.Height,
                        width: window.Width
                    );
                }

                var seconds = (float)clock.Elapsed.TotalSeconds;

                renderer.RenderClear(
                    alpha: 1.0f,
                    blue: (0.5f + (0.5f * MathF.Sin(x: ((seconds * 0.7f) + 4.18879f)))),
                    green: (0.5f + (0.5f * MathF.Sin(x: ((seconds * 0.7f) + 2.09439f)))),
                    red: (0.5f + (0.5f * MathF.Sin(x: (seconds * 0.7f))))
                );

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
            m_applicationLifetime.StopApplication();
        }
    }
}
