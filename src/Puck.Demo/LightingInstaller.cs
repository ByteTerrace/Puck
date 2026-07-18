using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Lighting;
using Puck.Input;
using Puck.Input.Lighting;
using Puck.Platform.Windows.Lighting;

namespace Puck.Demo;

/// <summary>
/// The demo's dynamic-lighting install: opens the keyboard HID LampArray (if one is present) and drives a
/// bind legend on it — the keyboard color-codes the player's controls. Presentation-only; nothing here touches
/// simulation state, so it is safe to tick from the render loop.
/// </summary>
/// <remarks>
/// Wire from <c>DemoHost</c> in three touches: construct once at startup, call <see cref="Tick"/> each rendered
/// frame with the frame delta, and <see cref="Dispose"/> at shutdown. When a keyboard command fires, call
/// <see cref="Flash"/> with its source to pulse that key. The category legend it paints is a sensible default
/// (movement WASD, camera arrows, interact E/F/Enter, console backtick, meta Escape/Tab); swap it for the demo's
/// real keyboard binds by editing <see cref="BuildDefaultLegend"/> or filling <see cref="Legend"/> directly.
/// </remarks>
public sealed class LightingInstaller : IDisposable {
    private readonly LightLegendState m_state = new();
    private readonly HashSet<string> m_pendingFlashes = new(comparer: StringComparer.Ordinal);
    // Guards m_pendingFlashes: Flash is called from the command/render thread as keyboard verbs fire, while the drain
    // runs on the ~30 Hz ticker thread — HashSet is not safe for concurrent add + enumerate/clear.
    private readonly object m_flashGate = new();
    private readonly ILampArrayDeviceSource? m_source;
    private readonly LightLegendDriver? m_driver;
    private readonly LightCelebration? m_celebration;

    /// <summary>Opens the lamp arrays and, if a keyboard is present, prepares a driver for it.</summary>
    public LightingInstaller() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        m_source = new Win32LampArrayDeviceSource();

        var keyboard = default(ILampArrayDevice);

        foreach (var device in m_source.Devices) {
            if (device.Kind == LampArrayKind.Keyboard) {
                keyboard = device;

                break;
            }
        }

        if (keyboard is not null) {
            m_driver = new LightLegendDriver(device: keyboard);
            m_celebration = new LightCelebration(device: keyboard);
        }
    }

    /// <summary>Gets whether a keyboard lamp array was found and is being driven.</summary>
    public bool HasKeyboard => (m_driver is not null);

    /// <summary>Gets the legend state, for a host that wants to author the bindings itself rather than the default.</summary>
    public LightLegendState Legend => m_state;

    /// <summary>Marks that a keyboard command fired this frame, so its key flashes. Safe to call when no keyboard is present.</summary>
    /// <param name="source">The neutral keyboard source string (from <see cref="InputSources.Keyboard"/>).</param>
    public void Flash(string source) {
        ArgumentNullException.ThrowIfNull(source);

        lock (m_flashGate) {
            _ = m_pendingFlashes.Add(item: source);
        }
    }

    /// <summary>
    /// Fires the score celebration on the keyboard: a tier-colored wave sweeps the board, breathes, and fades,
    /// then the legend repaints. Safe to call from any thread and a no-op without a keyboard — arming is a state
    /// write; all lamp writes happen on the ticker. Presentation only, and deliberately a little bit of joy: the
    /// bench's completion seam fires this, so finishing a scored run lights up the room.
    /// </summary>
    /// <param name="score">The score being celebrated.</param>
    /// <param name="referenceScore">The reference the score is graded against; defaults to the bench's 10000.</param>
    public void Celebrate(int score, int referenceScore = 10_000) {
        if ((m_celebration is null) || (m_driver is null)) {
            return;
        }

        // Arm only (a thread-safe volatile write); host control (m_driver.Start) is taken on the TICKER thread in Tick's
        // celebration branch, never here. Calling Start() from this (render/command) thread would race the ticker's own
        // first legend write and a concurrent feature-report write onto the same device.
        m_celebration.Begin(referenceScore: referenceScore, score: score);
    }

    /// <summary>Composes and pushes one frame of the legend, at the driver's throttled cadence. A no-op without a keyboard.</summary>
    /// <param name="elapsedSeconds">The render time since the previous call, in seconds.</param>
    public void Tick(double elapsedSeconds) {
        if (m_driver is null) {
            return;
        }

        // A playing celebration owns the board; when it finishes, the driver's dirty-cache is stale (the
        // celebration wrote directly), so mark it dirty and let the legend repaint in full next tick.
        if (m_celebration is { IsPlaying: true }) {
            // Take host control HERE, on the ticker thread, so the celebration's first lamp write can't race a Start()
            // from the render/command thread (Celebrate now only arms). Start() is idempotent.
            m_driver.Start();

            if (!m_celebration.Tick(elapsedSeconds: elapsedSeconds)) {
                m_driver.MarkDirty();
            }

            lock (m_flashGate) {
                m_pendingFlashes.Clear();
            }

            return;
        }

        BuildDefaultLegend();

        lock (m_flashGate) {
            foreach (var source in m_pendingFlashes) {
                m_state.Flash(source: source);
            }

            m_pendingFlashes.Clear();
        }

        m_driver.Tick(state: m_state, elapsedSeconds: elapsedSeconds);
    }

    // The default demo legend: the WoW-addon-flavored keyboard equivalents of the overworld's binds. Replace with
    // the demo's real keyboard binding table when one exists.
    private void BuildDefaultLegend() {
        m_state.Clear();

        m_state.Bind(source: InputSources.Keyboard.Letter(letter: 'w'), category: BindCategory.Movement);
        m_state.Bind(source: InputSources.Keyboard.Letter(letter: 'a'), category: BindCategory.Movement);
        m_state.Bind(source: InputSources.Keyboard.Letter(letter: 's'), category: BindCategory.Movement);
        m_state.Bind(source: InputSources.Keyboard.Letter(letter: 'd'), category: BindCategory.Movement);

        m_state.Bind(source: InputSources.Keyboard.ArrowUp, category: BindCategory.Camera);
        m_state.Bind(source: InputSources.Keyboard.ArrowDown, category: BindCategory.Camera);
        m_state.Bind(source: InputSources.Keyboard.ArrowLeft, category: BindCategory.Camera);
        m_state.Bind(source: InputSources.Keyboard.ArrowRight, category: BindCategory.Camera);

        m_state.Bind(source: InputSources.Keyboard.Letter(letter: 'e'), category: BindCategory.Interact);
        m_state.Bind(source: InputSources.Keyboard.Letter(letter: 'f'), category: BindCategory.Interact);
        m_state.Bind(source: InputSources.Keyboard.Enter, category: BindCategory.Interact);

        m_state.Bind(source: InputSources.Keyboard.Backtick, category: BindCategory.Console);
        m_state.Bind(source: InputSources.Keyboard.Escape, category: BindCategory.Meta);
        m_state.Bind(source: InputSources.Keyboard.Tab, category: BindCategory.Meta);
    }

    /// <summary>Restores every driven device to autonomous mode and releases the lamp arrays.</summary>
    public void Dispose() {
        m_driver?.Dispose();
        m_source?.Dispose();
    }
}

/// <summary>
/// Ticks the demo's dynamic-lighting legend at its own presentation cadence (~30 Hz), independent of the render
/// loop — the driver self-throttles and writes only dirty frames, so a plain timer is the whole transport. The
/// installer singleton is disposed by the container at shutdown, which restores the device to autonomous mode.
/// </summary>
/// <param name="lighting">The lighting install being driven.</param>
internal sealed class LightingTickService(LightingInstaller lighting) : BackgroundService {
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(period: TimeSpan.FromMilliseconds(value: 33.0));

        var previous = Stopwatch.GetTimestamp();

        try {
            while (await timer.WaitForNextTickAsync(cancellationToken: stoppingToken).ConfigureAwait(continueOnCapturedContext: false)) {
                var now = Stopwatch.GetTimestamp();

                lighting.Tick(elapsedSeconds: Stopwatch.GetElapsedTime(startingTimestamp: previous, endingTimestamp: now).TotalSeconds);
                previous = now;
            }
        } catch (OperationCanceledException) {
            // Host shutdown; the installer's disposal restores autonomous mode.
        }
    }
}
