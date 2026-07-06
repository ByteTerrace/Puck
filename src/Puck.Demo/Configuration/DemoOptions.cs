namespace Puck.Demo.Configuration;

/// <summary>
/// The demo-side feature toggles and headless capture aids, bound from the <c>Demo</c> configuration section at the
/// composition root (JSON/scenario file &lt; environment &lt; command line). Historically each of these was a
/// <c>PUCK_*</c> environment variable read by <see cref="Environment.GetEnvironmentVariable(string)"/> scattered
/// across the overworld node, the frame source, and the console; they are now ONE typed options object, and the
/// legacy variable names keep working verbatim because a custom environment source maps every <c>PUCK_*</c> name
/// onto the matching key here (see <see cref="DemoConfiguration"/>). Presentation / headless-aid only — nothing here
/// feeds the deterministic simulation.
/// </summary>
public sealed class DemoOptions {
    /// <summary>The configuration section these options bind from.</summary>
    public const string Section = "Demo";

    /// <summary>A saved creation (name resolved under <c>./creations/</c>, or a direct path) loaded into the scene at
    /// boot; when set, the demo also opens straight into creator mode. Maps <c>PUCK_CREATOR_LOAD</c>.</summary>
    public string? CreatorLoad { get; init; }

    /// <summary>Opens straight into creator mode at boot (the headless authoring-view aid), even without a
    /// <see cref="CreatorLoad"/>. Maps <c>PUCK_OVERWORLD_CREATOR</c> (<c>=1</c>).</summary>
    public bool Creator { get; init; }

    /// <summary>Drives N scripted players apart for a bare-room capture (0 = live controllers only). Maps
    /// <c>PUCK_OVERWORLD_DEBUG_PLAYERS</c>.</summary>
    public int DebugPlayers { get; init; }

    /// <summary>The far spawn cell (both axes) — a determinism aid that spawns the room far from the origin. Maps
    /// <c>PUCK_OVERWORLD_CELL</c>.</summary>
    public long Cell { get; init; }

    /// <summary>Grabs the one-shot <c>--capture</c> after this many produced frames (the machines have booted + drawn
    /// by then), or -1 to leave the plain frame-0 capture. Maps <c>PUCK_OVERWORLD_CAPTURE_FRAME</c>.</summary>
    public int CaptureFrame { get; init; } = -1;

    /// <summary>Overrides the world-lens immersed boot's cart type (default 0 = the world-lens membrane; e.g. 3 boots
    /// the forged avatar cabinet). Maps <c>PUCK_OVERWORLD_CART</c>.</summary>
    public int Cart { get; init; }

    /// <summary>Comma-separated simulation ticks at which consoles 0,1,2… boot, so every layout stage can be captured
    /// without hardware. Maps <c>PUCK_OVERWORLD_DEBUG_BOOT</c>.</summary>
    public string? DebugBoot { get; init; }

    /// <summary>Fakes a linked (0,1) console pair so the sagging link cable can be captured headlessly. Maps
    /// <c>PUCK_LINK_CABLE_PROBE</c> (<c>=1</c>).</summary>
    public bool LinkCableProbe { get; init; }

    /// <summary>Comma-separated creation names/hashes spawned as companions at boot. Maps
    /// <c>PUCK_COMPANION_LOAD</c>.</summary>
    public string? CompanionLoad { get; init; }

    /// <summary>A headless <c>world.wire</c> applied at boot for feed captures — <c>&lt;screen&gt;:&lt;source&gt;</c>
    /// (e.g. <c>0:named:lure</c> shows the loaded fish's lure camera feed on cabinet screen 0), repeatable
    /// comma-separated. The console <c>world.wire</c> verb is the live path; this is its headless-capture twin (a
    /// keyboardless run has no console). Maps <c>PUCK_COMPANION_WIRE</c>.</summary>
    public string? CompanionWire { get; init; }

    /// <summary>A headless <c>companion.face</c> applied at boot — <c>&lt;index&gt;:&lt;feed|auto&gt;</c> (1-based,
    /// e.g. <c>1:lure</c> pins the first companion's face to the lure feed, <c>1:auto</c> resumes the hail-radius
    /// tune-in), repeatable comma-separated. The keyboardless twin of the console <c>companion.face</c> verb. Maps
    /// <c>PUCK_COMPANION_FACE</c>.</summary>
    public string? CompanionFace { get; init; }

    /// <summary>Runs the hands-off save→reload byte-compare of the live world at boot and prints a verdict. Maps
    /// <c>PUCK_WORLD_ROUNDTRIP</c> (<c>=1</c>).</summary>
    public bool WorldRoundtrip { get; init; }

    /// <summary>Opens the on-screen developer console at boot with a couple of seeded lines (a headless aid for
    /// verifying its rendering without a keyboard). Maps <c>PUCK_CONSOLE_OPEN</c>.</summary>
    public bool ConsoleOpen { get; init; }
}
