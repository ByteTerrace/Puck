using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Pacing;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>
/// The headless boot shape's outermost host loop — a composition root's <c>host.presentation: none</c> /
/// <c>--headless</c> run-time twin. No window, no GPU device, no swapchain, no audio device: it paces the SAME
/// <see cref="FixedStepPump"/> the windowed <see cref="LauncherWindowHostedService"/> drives, off a high-resolution
/// waitable-timer wait instead of a present cadence — wall clock paces ordinary runs and never enters simulation
/// state (<see cref="TickClock"/> converts the sampled delta to engine ticks exactly, same as windowed). An offline
/// run may instead request <see cref="LauncherOptions.Unpaced"/>, which feeds one exact fixed step per loop. The
/// console pump (stdin → <see cref="CommandRegistry"/>) and every registered
/// <see cref="ISnapshotInputCapture"/> contribution run
/// every iteration exactly like the windowed loop, so a headless session is scriptable over stdin/stdout identically
/// and a frame-serviced input source (a probes host's track playback) lands in the tape.
/// </summary>
public sealed class HeadlessTickHostedService : BackgroundService {
    private readonly IHostApplicationLifetime m_applicationLifetime;
    private readonly BufferedConsoleOutput m_bufferedOutput;
    private readonly IInputClock m_inputClock;
    private readonly StandardInputBacklog m_inputBacklog;
    private readonly InputRouter? m_inputRouter;
    private readonly ILogger<HeadlessTickHostedService> m_logger;
    private readonly LauncherOptions m_options;
    private readonly IPrecisionWaiter? m_precisionWaiter;
    private readonly CommandRegistry m_registry;
    private readonly IFixedStepSimulation? m_simulation;
    private readonly ISnapshotInputCapture[] m_snapshotInputCaptures;
    private readonly TerminalControl m_terminal;
    private readonly TextCommandSource m_textSource;

    public HeadlessTickHostedService(
        IHostApplicationLifetime applicationLifetime,
        BufferedConsoleOutput bufferedOutput,
        IInputClock inputClock,
        ILogger<HeadlessTickHostedService> logger,
        LauncherOptions options,
        IEnumerable<InputRouter> inputRouters,
        IEnumerable<IFixedStepSimulation> simulations,
        IEnumerable<IPrecisionWaiter> precisionWaiters,
        IEnumerable<ISnapshotInputCapture> snapshotInputCaptures,
        CommandRegistry registry,
        TextCommandSource textSource,
        TerminalControl terminal,
        StandardInputBacklog inputBacklog
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(bufferedOutput);
        ArgumentNullException.ThrowIfNull(inputClock);
        ArgumentNullException.ThrowIfNull(inputRouters);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(precisionWaiters);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(snapshotInputCaptures);
        ArgumentNullException.ThrowIfNull(textSource);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(inputBacklog);

        m_applicationLifetime = applicationLifetime;
        m_bufferedOutput = bufferedOutput;
        m_inputClock = inputClock;
        m_inputRouter = LauncherHostLoop.SingleOrDefault(
            items: inputRouters,
            name: nameof(InputRouter),
            hostDescription: "headless host"
        );
        m_logger = logger;
        m_options = options;
        m_precisionWaiter = precisionWaiters.FirstOrDefault();
        m_registry = registry;
        m_snapshotInputCaptures = [.. snapshotInputCaptures];
        m_textSource = textSource;
        m_simulation = LauncherHostLoop.SingleOrDefault(
            items: simulations,
            name: nameof(IFixedStepSimulation),
            hostDescription: "headless host"
        );
        m_terminal = terminal;
        m_inputBacklog = inputBacklog;

        if ((m_simulation is null) != (m_inputRouter is null)) {
            throw new InvalidOperationException(message: "A fixed-step simulation and its InputRouter must be registered together. Use AddFixedStepSimulation<TSimulation>().");
        }

        // The console text door's OWN sink — bound to the Console principal when the router built it, so wiring it
        // here cannot choose what a submitted line acts as (mirrors LauncherWindowHostedService's own constructor
        // wiring; FixedStepPump below re-asserts the SAME mapping, which is idempotent).
        m_registry.RouteSimulationTo(sink: m_inputRouter?.ConsoleTextSink);
    }

    private void RunHeadlessLoop(CancellationToken stoppingToken) {
        Exception? fault = null;

        try {
            if (m_logger.IsEnabled(logLevel: LogLevel.Information)) {
                m_logger.LogInformation(message: "Headless boot: no window, no GPU device, no swapchain, no audio device — the authoritative server, console, and tape only.");
            }

            var clock = TickClock.Start();
            // Mirrors LauncherWindowHostedService's own null-simulation tolerance: a composition root that registers
            // no fixed-step sim still runs the console pump alone.
            var pump = FixedStepPump.CreateHosted(
                holdsClock: false,
                inputBacklog: m_inputBacklog,
                inputClock: m_inputClock,
                inputRouter: m_inputRouter,
                output: m_bufferedOutput,
                registry: m_registry,
                simulation: m_simulation,
                terminal: m_terminal,
                textSource: m_textSource
            );
            var frequency = Stopwatch.Frequency;
            var maxFrameTicks = (EngineTicks.PerSecond / 4UL);
            // The registered simulation declares its own rate; DefaultUpdateRate is the null-simulation fallback
            // (console pump alone) and the fallback while the registered simulation reports 0 (an authored
            // simulation.rateHz durable stop) — the loop's own pacing (this wait grid, and the pump's calling
            // cadence below) is presentation-adjacent host pacing, never sim state, and must never depend on a rate
            // that can legitimately be zero (EngineTicks.PerRate refuses zero outright). The registered simulation
            // still gates whether it actually steps internally (WorldSimulation.ShouldStepBoot); this value only
            // decides how often Advance is called, so a stopped world's console keeps answering.
            //
            // Resolved per iteration, not once before the loop: a live world.load can swap in a differently-rated
            // document mid-session, and this boot shape must adopt the new cadence — both the step width handed to
            // Advance and the wall-clock wait grid below — the next iteration rather than keep pacing at a stale
            // rate.
            var spinThreshold = LauncherHostLoop.SpinThreshold(frequency: frequency);
            var hostFrame = 0UL;
            var nextDeadline = Stopwatch.GetTimestamp();
            var exitAfterTimestamp = ((m_options.ExitAfter is { } exitAfter)
                ? (nextDeadline + ((long)(exitAfter.TotalSeconds * frequency)))
                : (long?)null
            );

            while (!stoppingToken.IsCancellationRequested) {
                m_textSource.Collect();

                // Flush the command pump's buffered result echoes ONCE, right after the drain that produced them (see
                // BufferedConsoleOutput) — the windowed loop's own precedent.
                m_bufferedOutput.Flush();

                if (
                    (exitAfterTimestamp is { } deadline) &&
                    (Stopwatch.GetTimestamp() >= deadline)
                ) {
                    m_terminal.RequestExit();
                }

                if (m_terminal.TryConsumeExit()) {
                    break;
                }

                // The per-host-frame snapshot contributions (a probes host's track/axis servicing, for one), in the
                // same place the windowed loop services them: after the text drain, before the due ticks apply.
                for (var captureIndex = 0; (captureIndex < m_snapshotInputCaptures.Length); captureIndex++) {
                    m_snapshotInputCaptures[captureIndex].CaptureFrame(frameKey: hostFrame);
                }

                hostFrame++;

                // Re-resolved every iteration — see ResolveRatePerSecond's own remarks above.
                var ratePerSecond = LauncherHostLoop.ResolveRatePerSecond(simulation: m_simulation);
                var stepTicks = EngineTicks.PerRate(ratePerSecond: ratePerSecond);
                // An offline schedule already pins every input and observation to the simulation's integer tick
                // grid. Feeding exactly one step here preserves the ordinary FixedStepPump/InputRouter path while
                // removing wall time from the run; no synthetic clock or alternate simulation loop is introduced.
                var deltaTicks = (m_options.Unpaced
                    ? stepTicks
                    : clock.Sample()
                );
                // The wall-clock pacing grid for ONE fixed step — presentation-adjacent only (paces the wait, never
                // enters sim state); the TickClock sample above is what actually measures elapsed time for the
                // accumulator.
                var period = (frequency / ((long)ratePerSecond));

                pump?.Advance(
                    deltaTicks: deltaTicks,
                    maxFrameTicks: maxFrameTicks,
                    stepTicks: stepTicks
                );

                // Simulation-routed console handlers run while snapshots are applied above. Flush their real results
                // in this iteration rather than leaving them buffered until the next tick.
                m_bufferedOutput.Flush();

                if (m_options.Unpaced) {
                    continue;
                }

                nextDeadline += period;

                var nowTimestamp = Stopwatch.GetTimestamp();

                // CATCH-UP: fell more than a whole slot behind (a scripted burst, a stalled thread) — re-origin the
                // grid at now instead of accumulating debt as a scheduling storm of steps. The FIXED-STEP ACCUMULATOR
                // (inside FixedStepPump) is what actually absorbs the jitter this introduces: whatever real time
                // elapses before the next Advance call converts to the right whole number of steps regardless of
                // when this wait actually woke up.
                if ((nowTimestamp - nextDeadline) > period) {
                    nextDeadline = nowTimestamp;
                } else {
                    LauncherHostLoop.WaitUntil(
                        deadlineTimestamp: nextDeadline,
                        frequency: frequency,
                        precisionWaiter: m_precisionWaiter,
                        spinThreshold: spinThreshold
                    );
                }
            }

            m_logger.LogInformation(message: "Headless run ending; shutting the host down.");
        } catch (Exception exception) {
            fault = exception;

            throw;
        } finally {
            try {
                // Flush any buffered echo tail before teardown so the final lines a scripted run emits (e.g. right
                // before an --exit-after shutdown, or the frame a quit/exit verb lands) are never lost.
                LauncherHostRun.RunTeardown(
                    fault,
                    m_logger,
                    ("flush output", m_bufferedOutput.Flush)
                );
            } finally {
                m_applicationLifetime.StopApplication();
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => LauncherHostLoop.RunPump(
        name: "Puck.Launcher Headless Tick Pump",
        pump: () => RunHeadlessLoop(stoppingToken: stoppingToken)
    );

}
