using System.Text.Json.Serialization;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>
/// The world's storage host-section defaults — the per-user cloud endpoint, an explicit user-id override, and the
/// direct-to-account discovery endpoint — authored as data so durable configuration lives in the world file (never a
/// <c>PUCK_*</c> env var; World has no such surface). An endpoint plus a resolved identity wires the owned-world sync
/// engine (<c>storage.push</c> / <c>storage.pull</c>); anything less leaves the catalog local-only. A
/// <c>--storage-uri</c> / <c>--user-id</c> / <c>--storage-discovery-uri</c> CLI reflection overrides each at boot.
/// <c>storage.status</c> echoes the resolved values.
/// </summary>
/// <param name="Endpoint">The per-user blob endpoint (a URI, e.g. <c>https://blob.byteterrace.com</c>), or
/// <see langword="null"/> for none. Validated as an absolute URI when present. Feeds
/// <c>WorldStorageSyncHandle</c>'s target construction; a URI here is edge-shaped (platform-managed containers), a
/// connection-string override (CLI-only — see the validator) is raw-shaped.</param>
/// <param name="UserId">An explicit user-id override (an Entra <c>oid</c> Guid string for a dev box or agent), or
/// <see langword="null"/> to decline identity (local-only). Fed to the identity resolver's explicit-override source.</param>
/// <param name="DiscoveryEndpoint">The direct-to-account connection container listing uses when <see cref="Endpoint"/>
/// resolves to an edge-shaped target — the platform edge cannot serve List at all (see
/// <c>AzureBlobObjectStorageTarget.DirectEndpoint</c>'s remarks), so an edge-shaped target with this
/// <see langword="null"/> refuses discovery by name instead of a request the edge cannot answer. Validated as an
/// absolute URI when present; a connection-string override (CLI-only — see the validator) is for the dev/emulator
/// shape. Ignored when <see cref="Endpoint"/> is raw-shaped (a raw target lists directly, like it reads and
/// writes).</param>
// Every member is optional and null-meaningful — see None below, which is all three absent. The explicit defaults are
// what tell the loader so, now that a parameter without one is required of the document.
public sealed record WorldStorageDefaults(string? Endpoint = null, string? UserId = null, string? DiscoveryEndpoint = null) {
    /// <summary>Gets the built-in default: no endpoint, no user-id, no discovery endpoint (cloud unwired, identity
    /// declined — local-only).</summary>
    public static WorldStorageDefaults None { get; } = new WorldStorageDefaults(
        DiscoveryEndpoint: null,
        Endpoint: null,
        UserId: null
    );
}
/// <summary>
/// World-varying editor/authoring policy values, authored as data rather than compile-time constants. Two
/// consumption classes share this one row (whole-row mutable like every other section — never split into two
/// sections for a consumption nuance that consumers already handle honestly):
/// <list type="bullet">
/// <item><description><b>Boot-consumed</b> (<see cref="AuthoringHeadroomScreens"/>,
/// <see cref="AuthoringHeadroomPlacements"/>): read exactly once, at
/// <c>Client.WorldSceneEmitter</c> construction, into the frozen render-envelope capacity floor (the probe's
/// worst-case word/instance reservation). The one honest exception: a live edit to these capacity-floor fields is
/// journaled but the running session's floor cannot retroactively grow — it applies at the next boot (the validator
/// still gates the new value against engine caps immediately, so a bad authored value never reaches a boot).</description></item>
/// <item><description><b>Live-consumed</b> (<see cref="MinPlacementScale"/>, <see cref="MaxPlacementScale"/>,
/// <see cref="CandidateRadius"/>, <see cref="CandidateCap"/>, <see cref="WorkbenchFraction"/>,
/// <see cref="PreviewDeadlineFrames"/>): read fresh from the delivered definition at each use site (a candidate
/// gather, a layout resolve, a drag-freeze tick) — a mutation takes effect at the very next tick/frame, no restart.
/// </description></item>
/// </list>
/// </summary>
/// <param name="AuthoringHeadroomScreens">Boot-consumed. The extra screen slots the probe reserves, bounded by the
/// engine's <see cref="Puck.SignedDistance.SdfProgramBuilder.MaxScreenSurfaces"/> ceiling.</param>
/// <param name="AuthoringHeadroomPlacements">Boot-consumed. The placement rows of headroom the probe reserves beyond
/// the boot placements (see <c>Client.WorldPlacementStamper.StaticStampInstances</c>).</param>
/// <param name="MinPlacementScale">Live-consumed. The placement uniform-scale envelope's floor — a pure validator
/// bound, revalidated on every placement mutation.</param>
/// <param name="MaxPlacementScale">Live-consumed. The placement uniform-scale envelope's ceiling — also the worst-case
/// scale <c>Client.WorldStampPool</c>'s probe bound-radius reads (bound radius is spatial-cull metadata,
/// never a word-capacity term, so re-reading it live every build cannot desync the frozen capacity floor).</param>
/// <param name="CandidateRadius">Live-consumed. The proximity-candidate radius (world units) around a seat's editor
/// focus point — cycling never walks the whole world (the explicit candidate policy).</param>
/// <param name="CandidateCap">Live-consumed. The candidate-count cap: at most this many nearest in-radius rows enter
/// the cycle ring.</param>
/// <param name="WorkbenchFraction">Live-consumed. The full-height fraction a sole editing seat's viewport takes when
/// 2+ seats are joined (the remaining width splits as a live rail among the playing seats) — read fresh each captured
/// frame by <c>Client.WorldFrameSource.LayoutRegion(int, int, int, float)</c>.</param>
/// <param name="PreviewDeadlineFrames">Live-consumed. The drag preview channel's missing-response fallback: a
/// released overlay with no definition delivery after this many produced frames drops honestly.</param>
/// <param name="DerivedFaceScreens">Boot-consumed. The derived screen slots the binder reserves at boot for creation
/// faces (a face declared by a placement's creation, lit by a feed), registered at
/// <c>[<c>Client.WorldCreationFacets.DerivedFaceBase</c>, DerivedFaceBase + this)</c>. Bounded so the range
/// stays inside the engine screen table.</param>
public sealed record WorldAuthoringDefaults(
    int AuthoringHeadroomScreens,
    int AuthoringHeadroomPlacements,
    float MinPlacementScale,
    float MaxPlacementScale,
    float CandidateRadius,
    int CandidateCap,
    float WorkbenchFraction,
    int PreviewDeadlineFrames,
    int DerivedFaceScreens
) {
    /// <summary>Gets the built-in default authoring policy.</summary>
    public static WorldAuthoringDefaults Default { get; } = new WorldAuthoringDefaults(
        AuthoringHeadroomPlacements: 8,
        AuthoringHeadroomScreens: 4,
        CandidateCap: 16,
        CandidateRadius: 32f,
        DerivedFaceScreens: 4,
        MaxPlacementScale: 5.0f,
        MinPlacementScale: 0.2f,
        PreviewDeadlineFrames: 12,
        WorkbenchFraction: 0.70f
    );
}
/// <summary>Which graphics backend a world prefers. <see cref="Auto"/> — the default — picks the OS-appropriate backend,
/// so a shared world document is portable across an OS boundary; an explicit preference the running OS cannot satisfy
/// degrades loudly (a document author preference) or hard-exits (a CLI operator assertion) rather than silently
/// mispresenting.</summary>
public enum WorldBackendPreference : byte {
    /// <summary>Pick the OS-appropriate backend at boot — Direct3D 12 on Windows 10+, Vulkan elsewhere.</summary>
    Auto,

    /// <summary>Prefer Direct3D 12.</summary>
    DirectX,

    /// <summary>Prefer Vulkan.</summary>
    Vulkan,
}
/// <summary>
/// The world's simulation rate — how many fixed steps the authoritative server advances per second. It is simulation
/// state, unlike <see cref="WorldHostDefaults"/> (presentation-only, never simulation state): the rate is
/// simulation input (rule 4) — it is what <c>Puck.Hosting.EngineTicks.PerRate</c> turns into the exact fixed-point
/// step width every kit tuning, motion program, and physics constant is authored against, so two worlds authoring
/// different rates are two different, equally deterministic simulations, never a presentation preference.
/// </summary>
/// <param name="RateHz">The simulation rate in Hz. Zero is a legal, distinct rate: a resident, non-stepping
/// world — a static diorama the authoritative server never advances a fixed step for, though it still applies
/// ordered submissions (mutations, session requests, connects/disconnects) through the administrative drain, so a
/// rate-0 world can accept the very write that revives it. At rate 0, a simulation-tick duration authored as a
/// positive value means never — not zero and not "already expired" — since there is no tick mapping for a world
/// that never advances (see <see cref="CompiledTickDuration"/>, <see cref="WorldDefinition.PopulationReconnectGraceTicks"/>).
/// A positive rate must be a divisor of <see cref="Puck.Maths.FixedTickConversion.TicksPerSecond"/> (50400)
/// exactly, so <c>Puck.Hosting.EngineTicks.PerRate</c> always derives a whole engine-tick step width — never
/// truncated, never remainder-carried (<see cref="WorldDefinitionValidator"/> refuses a non-divisor, naming the
/// nearest valid rates; a negative rate is refused outright, at any magnitude). 45 and 90 Hz — Steam Deck OLED's
/// two refresh rates — both divide 50400 exactly (1120 and 560 engine ticks per step). Defaults to
/// <see cref="DefaultRateHz"/> (240), the fixed rate every world ran at before this section existed, so a world
/// authoring no <c>simulation</c> section boots byte-identically to before.
/// <para><b>The derived-floor seam.</b> This record is deliberately the one place a follow-on validation pass adds
/// the physics floor (from body size/speed), the interactivity floor (from input latency), the substep-derived
/// contact clamp (<c>contactHertz &lt;= RateHz * n / 8</c> at substep count <c>n</c> — it coincides with
/// <c>RateHz / 4</c> only at <c>n</c> = 2), and the representable band — none of which is built yet. The clamp's
/// <c>n</c> is a solver parameter, so its validator arrives with the solver landing that introduces it. A derived
/// floor belongs here, beside the rate it constrains, never as a second section.</para></param>
public sealed record WorldSimulationDefaults(
    int RateHz = WorldSimulationDefaults.DefaultRateHz
) {
    /// <summary>The simulation rate every world ran at before this section existed (Hz) — the fallback
    /// <see cref="WorldDefinition.SimulationRateHz"/> uses for a world authoring no <see cref="WorldDefinition.Simulation"/>
    /// section.</summary>
    public const int DefaultRateHz = 240;
}
/// <summary>
/// How the world boots its presentation shell — the closed vocabulary <see cref="WorldHostDefaults.Presentation"/> and
/// the <c>--headless</c> CLI reflection resolve to (see <c>Puck.World.WorldHostSettings.Headless</c>). Deciding this
/// before any other registration is the boot-shape split's own precondition: <see cref="None"/> composes
/// <c>AddWorldAuthoritativeCore</c> alone (no GPU device, no swapchain, no window), <see cref="Windowed"/> composes it
/// plus <c>AddWorldPresentation</c>.
/// </summary>
[JsonConverter(typeof(StrictEnumConverter<WorldHostPresentation>))]
public enum WorldHostPresentation : byte {
    /// <summary>Boot a native window, GPU device, and swapchain — World's original, still-default shape.</summary>
    Windowed,

    /// <summary>Boot the authoritative server, console, and tape only — no window, no GPU device, no swapchain, no
    /// audio device. Every presentation-only console verb (<c>world.fps</c>/<c>.gpu</c>/<c>render*</c>/<c>view*</c>/
    /// <c>.screenshot</c>, <c>screen.*</c>, audio, editor) refuses as unknown — the honest reflection of the composed
    /// set, not a special-cased denial.</summary>
    None,
}
/// <summary>
/// The world's host defaults — how the world asks to be presented, independent of what it contains. presentation-only
/// throughout (never simulation state). Two consumption classes share this one row, named per field:
/// <list type="bullet">
/// <item><description><b>boot-only</b> (<see cref="Presentation"/>, <see cref="Backend"/>, <see cref="Width"/>,
/// <see cref="Height"/>, <see cref="SurfaceFormat"/>, <see cref="Fullscreen"/>, <see cref="PresentMode"/>,
/// <see cref="ExitAfterSeconds"/>, <see cref="RayQuery"/>, <see cref="Genlock"/>): read once at composition; a live
/// edit is journaled and validated immediately but takes effect next boot.</description></item>
/// <item><description><b>Boot-default with a live lever</b> (<see cref="TargetHertz"/> via <c>world.target</c>,
/// <see cref="Timing"/> via <c>world.timing</c>): the value the session wakes on;
/// <c>Puck.World.WorldSessionCapture</c> folds the live values back at <c>world.save</c>.</description></item>
/// </list>
/// <see cref="Default"/> reproduces World's current boot exactly.
/// </summary>
/// <param name="Presentation">Which boot shape the world composes — see <see cref="WorldHostPresentation"/>. Defaults
/// to <see cref="WorldHostPresentation.Windowed"/>, so every world authored before this field existed boots
/// byte-identically; the <c>--headless</c> CLI flag reflects <see cref="WorldHostPresentation.None"/> for a single run
/// without editing the document.</param>
/// <param name="Backend">The preferred graphics backend (<see cref="WorldBackendPreference.Auto"/> is OS-portable), or
/// <see langword="null"/> when <paramref name="BackendDraw"/> draws it — omitting both reads as
/// <see cref="WorldBackendPreference.Auto"/>.</param>
/// <param name="BackendDraw">The backend choice's authored-randomness facet, or <see langword="null"/> for an ordinary
/// literal <paramref name="Backend"/>. A boot-only site (<see cref="WorldDrawSites.HostBackend"/>): the resolver draws
/// it once at composition, writes the settled preference into <paramref name="Backend"/>, clears this facet, and
/// narrates the settlement on stderr — the only surface that can say the backend was drawn at all, since a settled
/// field is indistinguishable from an authored one thereafter.
/// <para>Its natural spelling is a weighted text source over the backend tokens (<c>auto</c>/<c>directx</c>/
/// <c>vulkan</c> — a one-context Markov table with <c>bound</c> 1, the degenerate flat weighted draw), parsed through
/// <see cref="WorldHostTokens.ParseBackend"/> at settle. A token naming no backend refuses by name. Drawing the name
/// rather than an ordinal is deliberate: an ordinal draw over an enum silently re-points itself the day a member is
/// inserted, and reads at the authoring site as a number nothing explains.</para>
/// <para>Declared together with <paramref name="Backend"/> it is refused by name — this record is a class, so
/// presence is honestly observable here, unlike <see cref="WorldPopulationDefaults.CapacityDraw"/>'s struct-typed
/// site.</para></param>
/// <param name="Width">The window client width in pixels.</param>
/// <param name="Height">The window client height in pixels.</param>
/// <param name="SurfaceFormat">The swapchain surface format (<see cref="SurfaceFormat.Unknown"/> is rejected by the validator).</param>
/// <param name="Fullscreen">Whether the window enters borderless fullscreen when first shown.</param>
/// <param name="PresentMode">The swapchain presentation algorithm.</param>
/// <param name="TargetHertz">The boot present-pacing target in Hz; <c>0</c> selects automatic display pacing. The
/// <c>world.target</c> live lever owns "now" thereafter.</param>
/// <param name="ExitAfterSeconds">Seconds before the world auto-exits; <c>0</c> runs until the window is closed.</param>
/// <param name="RayQuery">Whether the SDF renderer may use the ray-query hardware path.</param>
/// <param name="Timing">Whether GPU per-pass timing boots armed; the <c>world.timing</c> live lever owns it thereafter.</param>
/// <param name="Genlock">The external-clock election policy, consumed at boot by the clock registry (which tolerates an
/// unknown source id): <see langword="null"/> for the launcher's automatic election, or a non-whitespace source id /
/// <c>off</c>. Shape-only validation (null or non-whitespace); the registry, not the validator, interprets the id.</param>
/// <param name="Listen">The TCP listen endpoint (<c>host:port</c>) the authoritative host binds for remote peer
/// admission, or <see langword="null"/> to stay loopback-only (no socket ever opens). Durable configuration per the
/// unification contract — the <c>--listen</c> CLI flag reflects it for a single run without editing the document.
/// Shape-only validation (null or a non-whitespace <c>host:port</c> pair); <c>Server.WorldTcpHost</c> is what actually
/// parses and binds it.</param>
/// <param name="Authority">The TCP endpoint at which this world's authority is reached when another world resolves
/// it as a destination, or <see langword="null"/> when the authority is colocated with the resolver. Colocation
/// short-circuits the authority transport; it does not select a separate transfer path.</param>
public sealed record WorldHostDefaults(
    WorldHostPresentation Presentation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBackendPreference? Backend,
    int Width,
    int Height,
    SurfaceFormat SurfaceFormat,
    bool Fullscreen,
    PresentMode PresentMode,
    double TargetHertz,
    int ExitAfterSeconds,
    bool RayQuery,
    bool Timing,
    string? Genlock,
    string? Listen,
    string? Authority = null,
    // OPTIONAL — the authored-randomness facet over Backend above (see the param docs). XOR-BY-PRESENCE against it:
    // WorldHostDefaults is a CLASS, so a null Backend is honestly distinguishable from an authored one and declaring
    // both is refused BY NAME. (WorldPopulationDefaults.CapacityDraw's site cannot do this — see its own remarks.)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldDraw? BackendDraw = null
) {
    /// <summary>Gets the built-in host defaults — reproducing World's current hardcoded boot exactly (windowed, 1280×800,
    /// auto backend, immediate present, automatic display pacing, R8G8B8A8 surface, ray-query on, timing off, no
    /// auto-exit, no listener).</summary>
    public static WorldHostDefaults Default { get; } = new WorldHostDefaults(
        Presentation: WorldHostPresentation.Windowed,
        Backend: WorldBackendPreference.Auto,
        Width: 1280,
        Height: 800,
        SurfaceFormat: SurfaceFormat.R8G8B8A8Unorm,
        Fullscreen: false,
        PresentMode: PresentMode.Immediate,
        TargetHertz: 0.0,
        ExitAfterSeconds: 0,
        RayQuery: true,
        Timing: false,
        Genlock: null,
        Listen: null,
        Authority: null
    );
}
/// <summary>
/// A world's self-update OPERATIONAL configuration — the deployment-facet fields <c>Puck.Launcher.AddSelfUpdate</c>
/// needs from a document field, matching <see cref="WorldHostDefaults"/>'s own posture for fields carrying no
/// simulation-state weight. The SECURITY-CRITICAL trust anchor and the durable replay high-water mark are never
/// document fields — a synced <c>puck.world.def.v1</c> a user's own storage container could rewrite is not a trust
/// anchor; the composition root compiles the anchor in as a constant.
/// </summary>
/// <param name="Channel">The release channel this install tracks (e.g. <c>stable</c>, <c>beta</c>). Null = the app's own default channel.</param>
/// <param name="CacheRoot">The on-disk root staged versions and update state live under. Null = the app's own default.</param>
/// <param name="CheckIntervalSeconds">Seconds between automatic <c>update.check</c> attempts. Null = the app's own
/// default; <c>0</c> disables automatic checking (a manual <c>update.check</c> still works).</param>
/// <param name="KeepVersions">The number of most-recent staged versions to retain beyond the current one. Null = the app's own default.</param>
public sealed record WorldUpdateDefaults(
    string? Channel = null,
    string? CacheRoot = null,
    int? CheckIntervalSeconds = null,
    int? KeepVersions = null
) {
    /// <summary>Gets the built-in update defaults — every field unauthored, so a world declaring no update section runs its app's own hardcoded defaults.</summary>
    public static WorldUpdateDefaults Default { get; } = new WorldUpdateDefaults();
}
