namespace Puck.Demo.Configuration;

/// <summary>
/// A scenario — a repeatable creator-mode review session, bound from the <c>Scenario</c> configuration section. A
/// scenario is DEMO TOOLING, deliberately separate from the gated <c>puck.run.v1</c> run document (that is the engine
/// contract; this is greenfield): it describes which creation to load into creator mode and a capture plan of camera
/// shots to write out. The <c>--scenario &lt;name-or-path&gt;</c> flag loads a scenario JSON as a configuration source;
/// <c>--scenario-set Key=Value</c> overrides any field per run without editing the file (e.g.
/// <c>--scenario-set Scenario:Creation=lantern-fish</c>).
///
/// The capture harness is SETTLE-THEN-CAPTURE and COMPLETION-DRIVEN (never frame-number-keyed): each shot applies its
/// pose, settles for <see cref="SettleSeconds"/> of wall-clock AND a few produced frames, then arms the capture and
/// waits for the PNG to be written before advancing. When the last shot is written the run requests a graceful
/// shutdown; <see cref="ExitAfterSeconds"/> is only a SAFETY NET (generous, so slow frames never race the schedule).
/// Wall-clock influences ONLY when a capture is armed — never a rendered value: the camera pose is verbatim and each
/// shot's content/animation time is PINNED deterministically to <see cref="StartTime"/> + <c>i</c>·<see cref="TimeStep"/>,
/// so two runs at different fps produce byte-identical PNGs.
/// </summary>
public sealed class ScenarioOptions {
    /// <summary>The configuration section these options bind from.</summary>
    public const string Section = "Scenario";

    /// <summary>Whether a scenario is active (the composition root sets this when <c>--scenario</c> resolved a file).
    /// Bound from config too, so a scenario JSON can carry <c>"Active": true</c>, but the flag path is authoritative.</summary>
    public bool Active { get; init; }

    /// <summary>The creation to review: a name resolved under <c>./creations/</c>, or a direct path (the flagships live
    /// at <c>docs/examples/creations/*.creation.json</c>). Loaded into creator mode at boot — the harness equivalent of
    /// the <c>creator</c> + <c>creator.load</c> console verbs.</summary>
    public string? Creation { get; init; }

    /// <summary>The SAFETY-NET auto-exit, in seconds — a scenario run is COMPLETION-DRIVEN (it exits when the last shot
    /// is written), so this exists only so a wedged run can't hang forever. It must be generous enough that it never
    /// races the settle-then-capture schedule; if it DOES fire before completion, the harness prints a loud stderr line
    /// reporting how many shots were captured. 0 or less leaves the CLI <c>--exit-after-seconds</c> (or its default) in
    /// place; a scenario JSON should set a comfortable ceiling (the committed review uses 120).</summary>
    public int ExitAfterSeconds { get; init; }

    /// <summary>How long (wall-clock seconds) each shot's pose is held to SETTLE before its capture is armed — long
    /// enough that the pose and any content state are unambiguously in effect even under GPU contention. Combined with
    /// a small minimum produced-frame count (the harness holds the pose for at least a few frames too). Wall-clock here
    /// gates only WHEN the capture arms; it never reaches a rendered value. Default 0.5.</summary>
    public double SettleSeconds { get; init; } = 0.5;

    /// <summary>The content/animation time (seconds) the FIRST shot renders at — the deterministic clock fed to the
    /// rendered frame in place of the wall-clock accumulator, so a time-animated creation looks identical every run.
    /// Shot <c>i</c> renders at <c>StartTime + i·</c><see cref="TimeStep"/>. Default 0.</summary>
    public double StartTime { get; init; }

    /// <summary>The per-shot content-time advance (seconds) — shot <c>i</c> renders at <see cref="StartTime"/> +
    /// <c>i·TimeStep</c>. 0 (the default) pins EVERY shot to the same instant (a still turntable of one animation
    /// frame); a nonzero step walks a time-animated creation forward deterministically across the shots.</summary>
    public double TimeStep { get; init; }

    /// <summary>The backdrop the workpiece is reviewed against (defaults to <see cref="ScenarioBackdrop.Studio"/> for
    /// the canned review turntable, but a scenario may set <see cref="ScenarioBackdrop.Room"/> to shoot the creation in
    /// its arcade-room context instead). Studio drops the room/cabinet/shelf content and lights the scene flat and
    /// bright so the palette reads TRUE; room keeps the dim overworld mood. Only the scenario harness ever engages
    /// studio — the plain demo and the live <c>capture</c> console verb stay room-framed.</summary>
    public ScenarioBackdrop Backdrop { get; init; } = ScenarioBackdrop.Studio;

    /// <summary>The capture plan — where the shots are written and how the camera moves.</summary>
    public ScenarioCaptureOptions Capture { get; init; } = new();
}

/// <summary>How a <see cref="ScenarioOptions"/> frames the workpiece — bound from <c>Scenario:Backdrop</c> by name
/// (<c>"studio"</c> / <c>"room"</c>).</summary>
public enum ScenarioBackdrop {
    /// <summary>A neutral, flat-lit studio: the creation alone against the dark neutral backdrop, no room, cabinets,
    /// walls, or shelf clutter, evenly lit so colors read true — the default for a creation review.</summary>
    Studio,

    /// <summary>The creation shot inside the dim arcade room — cabinets, walls, and the diegetic CRT glow behind it
    /// (the in-context framing).</summary>
    Room,
}

/// <summary>The capture plan of a <see cref="ScenarioOptions"/>: the output location plus either an explicit list of
/// <see cref="ScenarioShotOptions"/> or an <see cref="ScenarioOrbitOptions"/> expanded into evenly-spaced shots. Shots
/// no longer carry produced-frame numbers — the harness is settle-then-capture (see <see cref="ScenarioOptions"/>), so
/// a shot is just a camera pose in plan order.</summary>
public sealed class ScenarioCaptureOptions {
    /// <summary>The output directory the numbered PNGs are written under (created if missing).</summary>
    public string Directory { get; init; } = "artifacts/scenario";

    /// <summary>The file-name stem; each shot writes <c>{Prefix}-{index:000}.png</c> in shot order.</summary>
    public string Prefix { get; init; } = "shot";

    /// <summary>An explicit shot list (takes precedence over <see cref="Orbit"/> when non-empty).</summary>
    public IReadOnlyList<ScenarioShotOptions> Shots { get; init; } = [];

    /// <summary>An orbit spec expanded into <see cref="ScenarioOrbitOptions.Count"/> evenly-spaced yaws when
    /// <see cref="Shots"/> is empty.</summary>
    public ScenarioOrbitOptions? Orbit { get; init; }
}

/// <summary>One explicit scenario shot: the verbatim camera pose. Poses are SCALAR floats (never a serialized
/// <c>Vector3</c>) so binding is trivial and the STJ field-vs-property zeroing trap never applies — an omitted target
/// (<see langword="null"/> X/Y/Z) falls back to the creation's workbench center. Shots capture in plan order; the
/// harness settles each pose before arming its capture (no frame numbers).</summary>
public sealed class ScenarioShotOptions {
    /// <summary>The orbit yaw in radians (ignored when <see cref="Sprite"/> is true).</summary>
    public float Yaw { get; init; }

    /// <summary>The orbit pitch in radians (ignored when <see cref="Sprite"/> is true).</summary>
    public float Pitch { get; init; } = 0.5f;

    /// <summary>The camera distance from the target.</summary>
    public float Distance { get; init; } = 6.5f;

    /// <summary>An explicit orbit-target X (world/render-relative), or null to use the creation's center.</summary>
    public float? TargetX { get; init; }

    /// <summary>An explicit orbit-target Y, or null to use the creation's center.</summary>
    public float? TargetY { get; init; }

    /// <summary>An explicit orbit-target Z, or null to use the creation's center.</summary>
    public float? TargetZ { get; init; }

    /// <summary>Whether to lock the head-on SPRITE framing (+Z, zero pitch) instead of orbiting — the
    /// what-you-see-is-what-bakes shot.</summary>
    public bool Sprite { get; init; }
}

/// <summary>An orbit spec expanded into <see cref="Count"/> evenly-spaced yaw shots (0…2π) — the canned 8-shot review
/// turntable. No frame numbers: the harness settles each yaw before capturing (see <see cref="ScenarioOptions"/>).</summary>
public sealed class ScenarioOrbitOptions {
    /// <summary>The number of shots (evenly-spaced yaws around a full turn).</summary>
    public int Count { get; init; } = 8;

    /// <summary>The camera distance for every orbit shot.</summary>
    public float Distance { get; init; } = 6.5f;

    /// <summary>The camera pitch (radians) for every orbit shot.</summary>
    public float Pitch { get; init; } = 0.5f;

    /// <summary>An explicit orbit-target X, or null to use the creation's center.</summary>
    public float? TargetX { get; init; }

    /// <summary>An explicit orbit-target Y, or null to use the creation's center.</summary>
    public float? TargetY { get; init; }

    /// <summary>An explicit orbit-target Z, or null to use the creation's center.</summary>
    public float? TargetZ { get; init; }
}
