using Puck.Input.Devices;

namespace Puck.Demo.Tracker;

/// <summary>
/// The tracker mode's ENTIRE host-side footprint, composed as one object so <see cref="Overworld.OverworldRenderNode"/>
/// holds a single field for it (mirrors the node's own note in the creator-mode comments: "a mode class should own
/// its own state"). Owns the authored-document model (<see cref="TrackerScene"/>), the pad state machine
/// (<see cref="TrackerController"/>), and the headless preview player's lifecycle — entering/leaving the mode,
/// advancing pad input, and starting/stopping the preview all route through the small surface here instead of
/// growing the render node's own fields and methods. Unlike creator mode, tracker mode needs no SDF program, no
/// camera, and no diegetic screen: the console IS the display (see <see cref="TrackerScene.RenderRows"/>), so this
/// class has zero GPU/render coupling.
/// </summary>
internal sealed class TrackerModeState : IDisposable {
    private readonly TrackerController m_controller;
    private TrackerPreviewPlayer? m_preview;

    /// <summary>Initializes the mode state, resolving the on-screen dev console (when the host registered one) to
    /// narrate the pattern dump/status lines into — kept as an <see cref="IServiceProvider"/> lookup here (rather
    /// than a typed <c>DemoConsole</c> constructor parameter) so the render node that owns this object stays coupled
    /// to one more interface, not one more concrete console type — it is already at its analyzer coupling ceiling.</summary>
    /// <param name="services">The application services (resolves <c>DemoConsole</c> when registered).</param>
    public TrackerModeState(IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(services);

        Scene = new TrackerScene();
        m_controller = new TrackerController(narrate: ResolveNarrator(services: services), scene: Scene, setPreviewPlaying: SetPreviewPlaying);
    }

    // The pad's East button routes here (through the controller's setPreviewPlaying callback) so it has the exact
    // same effect as the tracker.play/tracker.stop console verbs — both go through StartPreview/StopPreviewRequest,
    // never just the scene's Playing flag, so the pad path actually hears the preview and not merely flips a bit.
    private string SetPreviewPlaying(bool play) => (play ? StartPreview() : StopPreviewRequest());

    // Prints to the on-screen dev console when the host registered one (DemoConsole.WriteLine both publishes to the
    // overlay AND echoes to the terminal); falls back to stderr-only, like creator mode's own narration, for a host
    // composed without one (e.g. a future headless/--validate-style composition).
    private static Action<string> ResolveNarrator(IServiceProvider services) {
        return ((services.GetService(serviceType: typeof(DemoConsole)) is DemoConsole console)
            ? console.WriteLine
            : static line => Console.Error.WriteLine(value: line));
    }

    /// <summary>The authored-document model the console verbs and the pad both edit.</summary>
    public TrackerScene Scene { get; }

    /// <summary>Whether tracker mode is currently active.</summary>
    public bool Active => Scene.Active;

    /// <summary>Enters or leaves tracker mode: resets pad edge-tracking, and leaving also tears down any running
    /// preview (a background preview surviving mode exit would be a silent, unkillable audio leak).</summary>
    /// <param name="active">The desired state.</param>
    public void SetActive(bool active) {
        if (Scene.Active == active) {
            return;
        }

        Scene.SetActive(active: active);
        m_controller.Reset();

        if (!active) {
            StopPreview();
        }
    }

    /// <summary>Advances one frame of pad input (see <see cref="TrackerController"/> for the binding map). Call only
    /// while <see cref="Active"/>.</summary>
    /// <param name="raw">The creating slot's raw pad state this frame.</param>
    public void AdvanceInput(in GamepadState raw) => m_controller.Advance(raw: in raw);

    /// <summary>Returns whether the pad's EXIT verb (North) fired since the last consume, clearing the latch.</summary>
    public bool ConsumeExitRequest() => m_controller.ConsumeExitRequest();

    /// <summary>Prints the current pattern dump — called once on ENTER so the console shows something before the
    /// first cursor move or edit, and after any cursor move/row change during <see cref="AdvanceInput"/>.</summary>
    public void NarrateRows() => m_controller.NarrateRows();

    /// <summary>Starts (or restarts) the headless preview from the CURRENT working document. Edits made while a
    /// preview was already running are picked up by tearing down the old player and building a fresh one from
    /// today's document — v1 keeps this simple (no live patching of a running preview).</summary>
    /// <returns>A status line for the console.</returns>
    public string StartPreview() {
        StopPreview();

        try {
            m_preview = TrackerPreviewPlayer.Start(document: Scene.Document);
        } catch (InvalidOperationException exception) {
            // A compile/verify failure (should not occur against a normalized document, but the preview must never
            // crash the demo over a malformed edit) reports itself instead of taking the run down.
            return $"[tracker.play: failed — {exception.Message}]";
        }

        if (!Scene.Playing) {
            Scene.TogglePlaying();
        }

        return $"[tracker.play: previewing \"{Scene.Document.Name}\"]";
    }

    /// <summary>Stops the preview (a no-op when nothing is playing).</summary>
    /// <returns>A status line for the console.</returns>
    public string StopPreviewRequest() {
        StopPreview();

        if (Scene.Playing) {
            Scene.TogglePlaying();
        }

        return "[tracker.stop: preview stopped]";
    }

    /// <summary>Steps the running preview by exactly one rendered frame (a no-op when nothing is playing) — call
    /// once per produced frame while <see cref="Active"/>.</summary>
    public void StepPreview() {
        m_preview?.StepOneFrame();
    }

    private void StopPreview() {
        m_preview?.Dispose();
        m_preview = null;
    }

    /// <inheritdoc/>
    public void Dispose() => StopPreview();
}
