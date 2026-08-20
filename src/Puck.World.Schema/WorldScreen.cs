using Puck.Assets.Documents;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>
/// The signal carried by a <see cref="WorldScreen"/>'s lit face. A source declares which provider feeds a slot; the
/// engine resolves and samples it. The <c>$type</c> string is the JSON discriminator; a new source kind is a new
/// derived record plus its <see cref="JsonDerivedTypeAttribute"/> line.
/// </summary>
[JsonDerivedType(typeof(WorldScreenSource.None), typeDiscriminator: "none")]
[JsonDerivedType(typeof(WorldScreenSource.TestPattern), typeDiscriminator: "testPattern")]
[JsonDerivedType(typeof(WorldScreenSource.Machine), typeDiscriminator: "machine")]
[JsonDerivedType(typeof(WorldScreenSource.Camera), typeDiscriminator: "camera")]
[JsonDerivedType(typeof(WorldScreenSource.View), typeDiscriminator: "view")]
[JsonDerivedType(typeof(WorldScreenSource.Capture), typeDiscriminator: "capture")]
[JsonDerivedType(typeof(WorldScreenSource.Console), typeDiscriminator: "console")]
[JsonDerivedType(typeof(WorldScreenSource.Qr), typeDiscriminator: "qr")]
[JsonDerivedType(typeof(WorldScreenSource.Session), typeDiscriminator: "session")]
[JsonDerivedType(typeof(WorldScreenSource.Text), typeDiscriminator: "text")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldScreenSource {
    private WorldScreenSource() {
    }

    /// <summary>No provider is bound — the engine lights the slot with its procedural no-signal fallback (an animated
    /// test-card / striped no-signal look, never black).</summary>
    public sealed record None() : WorldScreenSource;
    /// <summary>The deterministic animated test pattern (<c>Puck.SdfVm.Views.TestPatternSource</c>), rendered
    /// from the world's sim tick (never the wall clock) into a CPU buffer and uploaded each frame.</summary>
    /// <param name="Width">The pattern framebuffer width in pixels.</param>
    /// <param name="Height">The pattern framebuffer height in pixels.</param>
    public sealed record TestPattern(int Width, int Height) : WorldScreenSource;
    /// <summary>An arbitrary deterministic machine's unresampled framebuffer — resolved against a registered
    /// <see cref="Puck.Abstractions.Machines.IScreenMachineEngine"/> by <paramref name="Engine"/> id. The world never
    /// names a concrete machine: the engine owns its <paramref name="Options"/> vocabulary (a GamingBrick reads a
    /// dmg/cgb/agb model + a dmgspeed pin).</summary>
    /// <param name="Engine">The screen-machine engine id (e.g. <c>gaming-brick</c>).</param>
    /// <param name="ContentPath">The content file (a cartridge ROM) the machine boots, or empty when the screen is
    /// unconfigured — the binder faults the slot gracefully (no crash, no-signal card) rather than booting.</param>
    /// <param name="Options">The engine-specific options string, or <see langword="null"/> for the engine's defaults.</param>
    /// <param name="Cable">This machine's cable port (see <see cref="WorldMachineCable"/>), or <see langword="null"/>
    /// for an unlinked machine. Legal only on a declared <c>screens</c> row's own source — a magazine entry or a
    /// placement face's source is refused one (validated), because a cable is a standing physical connection of the
    /// machine that owns the slot, not of whatever content happens to rotate through it. Omitted from the wire when
    /// null, so every machine source authored before cables existed round-trips unchanged.</param>
    public sealed record Machine(
        string Engine,
        string ContentPath,
        string? Options,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMachineCable? Cable = null
    ) : WorldScreenSource;
    /// <summary>The platform's default live camera feed, with an explicit preferred capture profile. The platform may
    /// negotiate a nearby extent; every screen sampling the same physical default device shares one session.</summary>
    /// <param name="Profile">The preferred capture extent and maximum upload cadence.</param>
    /// <param name="Controls">The authored device-control state (<see cref="WorldCameraControls"/>), or
    /// <see langword="null"/> to leave every control at its driver default. One physical device carries one control
    /// state, so the FIRST declared camera screen authoring this wins (matching the shared-session model); a later
    /// <c>UpsertScreen</c> mutation re-resolves and applies the change live. Omitted from the wire when null.</param>
    public sealed record Camera(WorldFeedProfile Profile,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCameraControls? Controls = null) : WorldScreenSource;
    /// <summary>A named view from the presentation view stack, such as a monitor showing another camera's output.</summary>
    /// <param name="CameraName">The registered view name this slot samples.</param>
    public sealed record View(string CameraName) : WorldScreenSource;
    /// <summary>A live compositor capture feed — a desktop window keyed by title, or a whole monitor keyed by index. The
    /// selector is the altitude of the primitive: <paramref name="MonitorIndex"/> null is window mode; non-null is
    /// whole-monitor mode (and <paramref name="WindowTitle"/> is unused).</summary>
    /// <param name="WindowTitle">The captured window's title (window mode; ignored when <paramref name="MonitorIndex"/> is set).</param>
    /// <param name="Profile">This capture consumer's output extent and maximum refresh cadence.</param>
    /// <param name="MonitorIndex">The 0-based monitor to capture whole (0 = primary), or <see langword="null"/> for window mode.</param>
    public sealed record Capture(string WindowTitle, WorldFeedProfile Profile, int? MonitorIndex = null) : WorldScreenSource;
    /// <summary>A screen showing the developer console as an object in the world — the diegetic half of the control plane
    /// the unification contract names ("the on-screen panel and process stdin"). The frame is CPU-composed into a
    /// CRT-styled framebuffer and pushed through <c>IGpuSurfaceUpload</c>, exactly as the ported console feed does;
    /// nothing about it is a render-graph node. Complementary to — never a duplicate of — <c>ConsoleTape</c>,
    /// which publishes the same content to the screen-space overlay. At most one <c>console</c> source may be live
    /// (declared) at a time; an unselected console entry sitting in a magazine is legal.</summary>
    /// <param name="Rows">Console text rows the framebuffer composes, 1..120. Sizes the CPU buffer.</param>
    /// <param name="Columns">Console text columns, 1..400.</param>
    /// <param name="Procedural">When true the slot shows the sibling generated pattern instead of console text — carried
    /// as a mode of this variant rather than as a seventh union case.</param>
    public sealed record Console(int Rows = 24, int Columns = 64, bool Procedural = false) : WorldScreenSource;
    /// <summary>An authorable QR code (ISO/IEC 18004) — the document names a payload string and the engine derives the
    /// scannable module grid (<see cref="Puck.Assets.Qr.QrEncoder"/>), rendered CPU-side into a static B8G8R8A8
    /// framebuffer and uploaded once, never re-derived from the tick like <see cref="TestPattern"/>. The driving case
    /// is a link one human hands another off an in-world screen. This record is the document-authored half only —
    /// nothing here mints a payload at runtime; <c>screen.source &lt;index&gt; qr</c> is the live-authoring twin, and <c>world.identify</c>
    /// is the one caller that mints its payload (the running world's own documentId and content-address pin) rather
    /// than being handed one.</summary>
    /// <param name="Payload">The encoded string, UTF-8 byte mode. Must fit within version
    /// <see cref="Puck.Assets.Qr.QrEncoder.MaxSupportedVersion"/> at <paramref name="EcLevel"/> — validation refuses an
    /// oversized payload by name (its byte count against the level's capacity), never truncates it.</param>
    /// <param name="EcLevel">The error-correction level: <c>L</c>, <c>M</c>, <c>Q</c>, or <c>H</c> (case-insensitive,
    /// parsed by <see cref="Puck.Assets.Qr.QrErrorCorrection.TryParse"/>). Defaults to <c>M</c>.</param>
    /// <param name="QuietZoneModules">The white quiet-zone border width in modules on every side. ISO/IEC 18004
    /// recommends at least 4; a smaller value authors a QR a real scanner may refuse to read (a borderless QR does not
    /// scan) — the document may still author it (validation only refuses a negative width), since a screen's physical
    /// framing sometimes supplies the margin itself.</param>
    public sealed record Qr(string Payload, string EcLevel = "M", int QuietZoneModules = 4) : WorldScreenSource;
    /// <summary>
    /// A live rendered view of another world, resolved through a <c>destinations</c> row (docs/vision.md,
    /// "Observation and display"). The face/screen resolves the same resolver-owned identity a
    /// portal crossing at the same door would land in (<see cref="Puck.World.WorldSessionResolver"/>), attaches an
    /// observation lease to the resolved instance's server, and mirrors just enough of its delivered
    /// definition/snapshots to render its static authored geometry through <paramref name="CameraName"/> (or the
    /// destination's default projection). It never re-derives durability/scope/generation itself — those are the
    /// destination row's own facts.
    /// </summary>
    /// <remarks>
    /// <para>The projection renders the destination's authored static placement geometry (terrain, structures) plus
    /// every mirrored-active body, each posed from the destination's own previous/current snapshot pair at
    /// <c>WorldSessionMirror.InterpolationAlpha</c> — the destination's clock, never the host's presentation alpha
    /// (<c>Client.WorldSessionSceneEmitter</c>). Creation text stays omitted until session delivery transports pinned
    /// font assets.</para>
    /// <para><b>Staged boundary — global scope only.</b> A <c>user</c>/<c>group</c>-scoped destination makes the
    /// resolved image viewer-dependent, and the shipped one-image-per-screen-index binding shows every viewer the
    /// same image — showing one viewer's world to everyone would be silently wrong, so a session face naming a
    /// non-global destination refuses at bind time by name rather than binding to an arbitrary viewer's resolution.
    /// Per-viewport binding is future work (docs/vision.md, "User/group-scoped destinations make images
    /// viewer-dependent").</para>
    /// </remarks>
    /// <param name="Destination">The <see cref="Puck.World.WorldDestination.Name"/> this face/screen observes. Must
    /// resolve to a declared <c>destinations</c> row — an undeclared name refuses at boot (validated, like a portal
    /// facet's own <c>destination</c>).</param>
    /// <param name="CameraName">The destination's own placeable-camera name to render through, or
    /// <see langword="null"/> for its default projection (its first declared camera, else a fixed overview derived
    /// from its spawn points). Wire name <c>camera</c> — plain <c>Camera</c> would collide with the sibling
    /// <see cref="WorldScreenSource.Camera"/> arm's own type name inside this enclosing record. Validated only as
    /// non-empty when present at author time — the destination's own definition is not joined at boot (references
    /// assert naming intent, not reachability), so an unknown camera name is refused loudly at bind time instead,
    /// once the destination is actually resolved, falling back to the default projection rather than refusing the
    /// whole bind. Ignored under <see cref="WorldScreenProjection.Window"/> (see <paramref name="Projection"/>).</param>
    /// <param name="Projection">How the destination render projects onto this face (see <see cref="WorldScreenProjection"/>).
    /// Default <see cref="WorldScreenProjection.Camera"/> — unauthored worlds and every session facet authored before
    /// this member existed render byte-identically. Optional and trailing (the same widen-without-moving-existing-members
    /// shape <paramref name="CameraName"/> itself already follows). <see cref="WorldScreenProjection.Window"/> requires
    /// this same face's <see cref="WorldPlacementFace.Portal"/> to author <see cref="WorldPortalArrival.Mapped"/> with a
    /// <see cref="WorldPlacementPortal.Counterpart"/> — refused by name otherwise (see <see cref="WorldDefinitionValidator"/>);
    /// a top-level <c>screens</c> row or magazine entry carries no face to pair with, so <c>window</c> is refused there
    /// unconditionally.</param>
    /// <param name="Resolution">The offscreen target's <c>[width, height]</c> in pixels, or <see langword="null"/> for
    /// the engine default (<c>Puck.SdfVm.Views.WorldSessionView.DefaultWidth</c> x <c>DefaultHeight</c> — today's
    /// 160x144 panel, unchanged for an unauthored facet). Each axis is validated within
    /// <c>1..WorldDefinitionValidator.MaxSurfaceDimension</c>. Omitted from the wire when null.</param>
    public sealed record Session(
        string Destination,
        [property: JsonPropertyName("camera"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CameraName = null,
        WorldScreenProjection Projection = WorldScreenProjection.Camera,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldScreenResolution? Resolution = null
    ) : WorldScreenSource;
    /// <summary>Authored reading text on the screen face, rendered through the engine's glyph-decal tier
    /// (<c>Puck.SdfVm.SdfWorldEngine.SetScreenDecal</c>): a fixed monospace cell grid sampled from the world's packed
    /// font atlas at shade time — the dense-text sibling of a creation's <c>textRuns</c>, which stamp marched
    /// <c>Glyph</c> geometry. Signs, plaques, books, and monitors author this; short sculptural lettering stays a
    /// text run. Requires the world to declare a text font catalog (<see cref="WorldDefinition.Text"/>); the decal
    /// bypasses the CRT image pipeline, so no image source competes with it on the slot.</summary>
    /// <param name="Lines">The text rows, top-down; each row maps onto one grid row of cells (row-major, one scalar
    /// per cell — no kerning or shaping on this tier).</param>
    /// <param name="Font">The text catalog font name; <see langword="null"/> selects the catalog's default font.</param>
    /// <param name="Columns">The cell grid's column count, or <see langword="null"/> to fit the widest line. The grid
    /// (columns x rows) is capped by the engine's per-screen decal cell budget
    /// (<c>Puck.SignedDistance.SdfScreenDecalLayout.MaxScreenDecalCells</c>).</param>
    /// <param name="Rows">The cell grid's row count, or <see langword="null"/> to fit the line count.</param>
    /// <param name="Foreground">The letter color as <c>#RRGGBB</c>, or <see langword="null"/> for white.</param>
    /// <param name="Background">The cell background color as <c>#RRGGBB</c>, or <see langword="null"/> for black.</param>
    public sealed record Text(
        IReadOnlyList<string> Lines,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Font = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Columns = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Rows = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Foreground = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Background = null
    ) : WorldScreenSource;
}
/// <summary>An ordered set of sources one screen may show, plus the entry its selector starts on — the cycle primitive. A
/// selection is a pointer into this list; changing it never changes how many screen slots exist, so a magazine costs no
/// render envelope. Entries are the same closed <see cref="WorldScreenSource"/> vocabulary the declared source uses, so a
/// screen may rotate a cartridge, the webcam, and a jumbotron view through one slot.</summary>
/// <param name="Entries">The ordered source list (at least one entry).</param>
/// <param name="Selected">The 0-based entry the selector starts on (what <c>screen.select</c> advances from), not what the
/// screen boots showing — a screen always wakes on its declared <c>Source</c> (the one-live-console ceiling depends on
/// this). Live selection drifts from this and is folded back by <c>world.save</c> (see <c>Puck.World.WorldSessionCapture</c>).</param>
/// <param name="Wrap">Whether advancing past the last entry returns to the first (the arcade cabinet's wrapping cycle);
/// when false the selector clamps at both ends.</param>
public sealed record WorldScreenMagazine(IReadOnlyList<WorldScreenSource> Entries, int Selected = 0, bool Wrap = true);
/// <summary>One machine's cable-port declaration — the machine-tier home of cable linking. A cable is the SET of
/// declared machine sources naming the same cable name, derived by <see cref="WorldDefinition.MachineCableGroups"/>;
/// no port ever points at another port, so reciprocity holds by construction and a one-port cable is a validation
/// refusal rather than a dangling half-pair.</summary>
/// <param name="Name">The cable's stable kebab-case name, shared by every plugged port.</param>
/// <param name="Position">This machine's 0-based place in cable order — contiguous across the cable's ports
/// (validated), and what decides the linking engine's deterministic player order.</param>
public sealed record WorldMachineCable(string Name, int Position);
/// <summary>A cable-linked group of screens whose machines advance as one interleaved unit — derived from the
/// declared machine sources' cable ports (<see cref="WorldDefinition.MachineCableGroups"/>), never authored as a row
/// of its own. The binder steps the group,
/// never its members individually, so the engine's deterministic interleave — not the host's frame order — decides who
/// runs when. Every member must resolve to a machine from the same engine, and that engine must implement
/// <c>IMachineLinkingEngine</c>; a group whose members do not currently satisfy that is reported dormant, never silently
/// dropped.</summary>
/// <param name="Name">The cable's stable kebab-case name.</param>
/// <param name="Screens">The engine screen indices in cable order (2 or more, no duplicates).</param>
public sealed record WorldMachineCableGroup(string Name, IReadOnlyList<int> Screens);
/// <summary>The authored control state for the shared physical camera — the standard UVC camera/image controls
/// (pan/tilt/zoom, exposure, focus, color). Every member is optional: an ABSENT member leaves that control at the
/// device's own driver default (automatic where the device supports it), and a PRESENT member drives the control
/// manually at that value. The device remains authoritative — a value outside the device's reported range is clamped
/// (and step-snapped) at apply, a control the device lacks is skipped, and <c>screen.camera</c> reads the resulting
/// live state (each control's device range, mode, and current value) back over the pipe. Removing a previously
/// authored member restores that control's driver default on the next apply. Ranges are device-specific by design, so
/// validation admits any integer rather than guessing one device's envelope.</summary>
/// <param name="Pan">Horizontal framing offset (digital pan on webcams).</param>
/// <param name="Tilt">Vertical framing offset (digital tilt on webcams).</param>
/// <param name="Zoom">Magnification — on sensor-cropping webcams a region-of-interest zoom (e.g. 100..500 = 1x..5x on
/// a Logitech BRIO), which pairs with <paramref name="Pan"/>/<paramref name="Tilt"/> to frame a region.</param>
/// <param name="Exposure">Manual exposure time, typically log2 seconds (e.g. -5 = 1/32 s); absent = auto exposure.</param>
/// <param name="Focus">Manual focus distance; absent = autofocus.</param>
/// <param name="Brightness">Image brightness offset.</param>
/// <param name="Contrast">Image contrast.</param>
/// <param name="Saturation">Color saturation (0 is grayscale on most devices).</param>
/// <param name="Sharpness">Edge sharpening strength.</param>
/// <param name="Gain">Sensor gain (ISO-like amplification).</param>
/// <param name="WhiteBalance">Manual white-balance color temperature in kelvin; absent = auto white balance.</param>
/// <param name="BacklightCompensation">Backlight compensation (devices commonly report a 0..1 toggle).</param>
public sealed record WorldCameraControls(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Pan = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Tilt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Zoom = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Exposure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Focus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Brightness = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Contrast = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Saturation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Sharpness = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Gain = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WhiteBalance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BacklightCompensation = null
);
/// <summary>A live screen feed's requested output policy. It belongs to the source declaration rather than the binder,
/// so two window captures can choose different extents and cadences. Camera extents are preferences because a physical
/// device remains authoritative for its negotiated format.</summary>
/// <param name="Width">Requested output width in pixels.</param>
/// <param name="Height">Requested output height in pixels.</param>
/// <param name="RefreshRateHz">Maximum pull/upload cadence; it must divide the engine time base exactly.</param>
public readonly record struct WorldFeedProfile(int Width, int Height, uint RefreshRateHz) {
    /// <summary>Gets the fallback used by runtime screen verbs that do not provide an authored source profile.</summary>
    public static WorldFeedProfile Default { get; } = new(
        Height: 240,
        RefreshRateHz: 30U,
        Width: 320
    );
}
/// <summary>The neutral pad element a <see cref="WorldKit.Pad"/> entry maps a channel onto. Named after the
/// engine-neutral <c>MachinePadState</c>'s own axis/button vocabulary; a pad entry picks exactly one.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldPadElement>))]
public enum WorldPadElement : byte {
    /// <summary>The left stick's X axis.</summary>
    LeftStickX,
    /// <summary>The left stick's Y axis.</summary>
    LeftStickY,
    /// <summary>The right stick's X axis.</summary>
    RightStickX,
    /// <summary>The right stick's Y axis.</summary>
    RightStickY,
    /// <summary>The left analog trigger.</summary>
    LeftTrigger,
    /// <summary>The right analog trigger.</summary>
    RightTrigger,
    /// <summary>The bottom face button.</summary>
    South,
    /// <summary>The right face button.</summary>
    East,
    /// <summary>The left face button.</summary>
    West,
    /// <summary>The top face button.</summary>
    North,
    /// <summary>The directional pad's up direction.</summary>
    DpadUp,
    /// <summary>The directional pad's down direction.</summary>
    DpadDown,
    /// <summary>The directional pad's left direction.</summary>
    DpadLeft,
    /// <summary>The directional pad's right direction.</summary>
    DpadRight,
    /// <summary>The left shoulder (bumper) button.</summary>
    LeftShoulder,
    /// <summary>The right shoulder (bumper) button.</summary>
    RightShoulder,
    /// <summary>The start/menu/plus button.</summary>
    Start,
    /// <summary>The back/select/view/minus button.</summary>
    Back,
}
/// <summary>The route policy a <see cref="WorldScreen"/> carries: whether a player may engage the screen, the activation
/// radius, whether engaging auto-boots the selected magazine entry, the world-event channels a gesture drives it
/// through, which channel ordinals a control application onto it reaches, and which kit's pad map gives those
/// channels their meaning at the machine. The optional members each default to the inert/baked choice: no auto-boot,
/// no gesture channel, every channel reached, and the engine's default pad map (the two movement roles to the left
/// stick — <c>MoveStrafe</c>/<c>MoveAdvance</c>, structural ordinals, never a channel name). The default names no
/// gameplay channel: a screen whose machine needs a face button (or any other element) must name a kit whose
/// <see cref="WorldKit.Pad"/> binds it.</summary>
/// <param name="Engageable">Whether a player may engage this screen.</param>
/// <param name="EngageRadius">The world-unit radius a player must be inside to engage (meaningful only when
/// <paramref name="Engageable"/>). Validated finite and non-negative.</param>
/// <param name="AutoInsert">When set, engaging the screen first boots the selected magazine entry (the "walk over, press
/// the button, the screen lights" gesture), so the interaction is one act rather than an insert then an engage.</param>
/// <param name="EngageChannel">The world-event channel whose arrival on a body engages this screen, or
/// <see langword="null"/> (the default) for a route that does not answer gestures. The author chooses this name freely;
/// the engine never special-cases a spelling. Omitted from the wire when null.</param>
/// <param name="CycleChannel">Same, for advancing the magazine selector. Omitted from the wire when null.</param>
/// <param name="Channels">The declared channel names an application onto this screen reaches — a masked-out channel
/// keeps flowing to the source body's own pose (when the own-body application is retained) but never reaches this
/// screen. <see langword="null"/> (the default) reaches every declared channel. Omitted from the wire when null.</param>
/// <param name="Kit">The <see cref="WorldKit.Name"/> whose <see cref="WorldKit.Pad"/> map an application onto this
/// screen wears, or <see langword="null"/> for the engine's default pad map. The named kit must carry a pad map —
/// refused by name otherwise. Omitted from the wire when null.</param>
public readonly record struct WorldScreenRoute(bool Engageable, float EngageRadius, bool AutoInsert = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EngageChannel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CycleChannel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Channels = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kit = null) {
    /// <summary>Gets a screen no player engages (the default for a passive display).</summary>
    public static WorldScreenRoute Passive { get; } = new WorldScreenRoute(
        Engageable: false,
        EngageRadius: 0f
    );
}
/// <summary>One diegetic screen in the world — a screen slab emitted by
/// <see cref="Puck.SignedDistance.SdfProgramBuilder"/> whose lit face
/// samples a bound source (or the procedural fallback when unbound). The frame (<see cref="Origin"/>/<see cref="Right"/>/
/// <see cref="Up"/> + <see cref="HalfWidth"/>/<see cref="HalfHeight"/>) is the sampled surface frame and must match the
/// slab's placement; the frame source bakes the geometry translate from it.</summary>
/// <param name="Index">The engine screen-surface index (0..<see cref="Puck.SignedDistance.SdfProgramBuilder.MaxScreenSurfaces"/>−1)
/// this slab declares — the key the source/light providers bind under.</param>
/// <param name="Origin">The front face's world-space center (the sampled surface origin); the geometry center sits one
/// <see cref="HalfDepth"/> behind it along the face normal.</param>
/// <param name="Right">The unit world axis the sampled U increases along (the slab's local +X in world space). Must be
/// orthogonal to <paramref name="Up"/>: the client derives the slab's orientation and UV frame from the pair while the
/// server's collider projects the half-extents onto it, so a skewed pair would render and collide as different
/// solids.</param>
/// <param name="Up">The unit world axis the sampled V increases against — V = 0 at the top (the slab's local +Y in
/// world space).</param>
/// <param name="HalfWidth">The face half-width (the slab's local X half-extent).</param>
/// <param name="HalfHeight">The face half-height (the slab's local Y half-extent).</param>
/// <param name="HalfDepth">The slab's local Z half-extent (its thickness behind the face).</param>
/// <param name="Round">The corner-rounding radius.</param>
/// <param name="Source">The signal the lit face carries.</param>
/// <param name="Route">The engage-route policy.</param>
/// <param name="Solid">The screen slab's solidity facet (a box collider derived from the slab's oriented frame +
/// <c>Margin</c> by <c>Server.WorldColliderSet</c>), or <see langword="null"/> for a decorative screen. Omitted from the
/// wire when null.</param>
/// <param name="Magazine">The per-screen source magazine (the cycle primitive), or <see langword="null"/> for a screen
/// with no magazine — nothing to cycle. Omitted from the wire when null — the whole-row <c>UpsertScreen</c>
/// carries it for free, so no new mutation kind is needed.</param>
public sealed record WorldScreen(
    int Index,
    DocumentVector3 Origin,
    DocumentVector3 Right,
    DocumentVector3 Up,
    float HalfWidth,
    float HalfHeight,
    float HalfDepth,
    float Round,
    WorldScreenSource Source,
    WorldScreenRoute Route,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSolid? Solid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldScreenMagazine? Magazine = null
);
