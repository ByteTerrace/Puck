using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>The host seam the demo's <c>creator</c>/<c>tracker</c> console commands drive: it flips the in-engine SDF
/// authoring mode and the in-engine music tracker on the live overworld root. Presentation/authoring only — neither
/// mode's state ever touches the deterministic sim. Every tracker member below is PRIMITIVE-typed (bool/string) on
/// purpose: the overworld render node that implements this sits AT its analyzer coupling ceiling, so the tracker's own
/// types (<c>Tracker.TrackerModeState</c> and friends) must never appear in its signature — they live behind
/// <see cref="Forge.ForgeCommands"/>'s static forwarders instead (the same facade pattern the Solitaire/Poker cart
/// wiring already uses to stay under the same budget).</summary>
internal interface ICreatorModeHost {
    /// <summary>Whether creator mode is currently active.</summary>
    bool CreatorModeActive { get; }

    /// <summary>The live overworld frame source (the creator objects' composition point), when the root is ready —
    /// the <c>creator.*</c> console verbs reach the authored scene through it.</summary>
    OverworldFrameSource? CreatorFrameSource { get; }

    /// <summary>The WASM-addon runtime seam the <c>addon</c> console verbs drive (list / enable / disable), or
    /// <see langword="null"/> when the run declares no addons — available from the first frame (built by the graph
    /// builder, not lazily like the frame source).</summary>
    IAddonControlHost? AddonControl { get; }

    /// <summary>Toggles creator mode and returns the new state (false if the root is not yet ready).</summary>
    bool ToggleCreatorMode();

    /// <summary>Queues a FORGE of the current creator creation into a cart on the next frame (the live GPU device is
    /// there). The <paramref name="subject"/> word chooses which cart the SAME creation forges: <c>avatar</c> (default)
    /// — the walker overworld <c>.gbc</c> written to disk under <c>./forged-avatars</c>; <c>scene</c> — the
    /// SDF-art creature cart forged + hot-swapped into the nearest cabinet in-session. Returns a status line.</summary>
    /// <param name="subject">The forge subject: <c>avatar</c> (default) or <c>scene</c>.</param>
    string RequestCreatorForge(string subject);

    /// <summary>Queues a FORGE of the tracker's working tune into a JUKEBOX cart on the next frame, then installs it into
    /// the nearest cabinet (the tune subject's in-session hot-swap — GPU-free). Returns a status line.</summary>
    string RequestTuneForge();

    /// <summary>Whether tracker mode is currently active.</summary>
    bool TrackerModeActive { get; }

    /// <summary>Toggles world-sculpt mode (the creating slot's third takeover — mutually exclusive with the creator
    /// and tracker modes) and returns the narration line. The <c>world.*</c> verbs reach the sculpted scene through
    /// <see cref="CreatorFrameSource"/>, every authoring surface's composition point.</summary>
    string ToggleWorldSculptMode();

    /// <summary>Toggles tracker mode and returns the new state.</summary>
    bool ToggleTrackerMode();

    /// <summary>Whether the fullscreen SDF-debug mode is currently active.</summary>
    bool SdfDebugModeActive { get; }

    /// <summary>Toggles the fullscreen SDF-debug mode (the single-shape debug tool; mutually exclusive with
    /// creator/world-sculpt/tracker) and returns the narration line. The <c>sdf.*</c> verbs reach the debug scene
    /// through <see cref="CreatorFrameSource"/>, every authoring surface's composition point.</summary>
    string ToggleSdfDebugMode();

    /// <summary>Reads the previous frame's per-pass GPU times for <c>sdf.info</c> — a passthrough of the producer's
    /// <c>SdfEngineNode.TryReadPassTimings</c> that fills <paramref name="passMilliseconds"/> (one entry per
    /// <c>SdfWorldEngine.PassTimingLabels</c>, in order) and the whole-frame span, so the node names no engine type it
    /// does not already. False when the producer is absent or timing is off (arm it live via the gpu.timing switch /
    /// the world.timing verb, or the run-doc <c>host.timing</c> field).</summary>
    /// <param name="passMilliseconds">Receives each render pass's milliseconds; size it to <c>SdfWorldEngine.PassTimingCount</c>.</param>
    /// <param name="passCount">The number of pass entries written (0 when unavailable).</param>
    /// <param name="frame">The whole-frame milliseconds.</param>
    bool TryReadSdfPassTimings(Span<double> passMilliseconds, out int passCount, out double frame);

    /// <summary>Reads the cadence gate's per-span diagnostics for <c>sdf.info</c> — a passthrough of the producer's
    /// <c>SdfEngineNode.TryReadCadenceDiagnostics</c>, mirroring <see cref="TryReadSdfPassTimings"/>. False when the
    /// producer is absent.</summary>
    /// <param name="diagnostics">Receives the latest diagnostics.</param>
    bool TryReadSdfCadenceDiagnostics(out SdfCadenceDiagnostics diagnostics);

    /// <summary>Starts or stops the headless preview of the working tune. Returns a status line for the console.</summary>
    /// <param name="play"><see langword="true"/> to (re)start the preview, <see langword="false"/> to stop it.</param>
    string RequestTrackerPreview(bool play);

    /// <summary>Whether the fullscreen native AGB (ARM7TDMI) debug scene is currently active.</summary>
    bool AgbDebugModeActive { get; }

    /// <summary>Toggles the fullscreen native AGB debug scene (the FIFTH creating-slot takeover, mutually exclusive with
    /// creator/world-sculpt/tracker/sdf-debug) and returns the narration. On enter it boots <paramref name="romPath"/>
    /// (or the pending native/<c>--rom</c> cartridge, else a built-in micro-ROM) through the DI-held
    /// <c>AgbDebugService</c>; the <c>agb.*</c> execution-control verbs drive that service directly.</summary>
    /// <param name="romPath">An optional explicit cartridge ROM path.</param>
    string ToggleAgbDebugMode(string? romPath);

    /// <summary>Live device swap by EXPLICIT console index — the <c>mode &lt;i&gt; &lt;dmg|cgb|agb&gt;</c> console verb's
    /// parity with the proximity Bricks-page chord. Both land on the identical snapshot-preserving <c>ChangeModel</c>
    /// costume change (the game keeps running, no reboot). String-typed so the console module stays primitive; an
    /// unrecognized model word parses to CGB. Returns a status line.</summary>
    /// <param name="consoleIndex">The target console index.</param>
    /// <param name="model">The desired costume: <c>dmg</c> | <c>cgb</c> | <c>agb</c>.</param>
    string SwapConsoleModel(int consoleIndex, string model);
}
