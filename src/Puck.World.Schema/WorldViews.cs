namespace Puck.World;

/// <summary>One slot of a <see cref="WorldViewLayout"/> — a normalized rect (origin top-left, Y down) plus what fills it.
/// A slot whose <see cref="Camera"/> and <see cref="Instance"/> are both <see langword="null"/> shows the seat that owns
/// this slot (the next joined seat in slot order); a named camera renders that authored view into the rect; a named graph
/// instance (a <see cref="WorldViewGraph"/> row) shows its selected image output in the rect, placed over the SDF world by
/// the render graph's root — a slot names at most one of the two (the validator refuses both authored together).</summary>
/// <param name="X">The rect's left edge, normalized [0, 1]. Default 0.</param>
/// <param name="Y">The rect's top edge, normalized [0, 1]. Default 0.</param>
/// <param name="Width">The rect's width, normalized (0, 1]. Default 1, so a slot that authors no rect fills the
/// window.</param>
/// <param name="Height">The rect's height, normalized (0, 1]. Default 1.</param>
/// <param name="Camera">The authored camera name filling this slot, or <see langword="null"/> for the seat that owns it
/// (or the instance named by <see cref="Instance"/>).</param>
/// <param name="Instance">The <c>views.graphs</c> row name whose output fills this slot, or <see langword="null"/> for
/// a seat or camera slot.</param>
[method: System.Text.Json.Serialization.JsonConstructor]
public readonly record struct WorldViewSlot(float X = 0f, float Y = 0f, float Width = 1f, float Height = 1f, string? Camera = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Instance = null) {
    /// <summary>Creates a full-window slot, matching an authored slot with omitted rectangle fields.</summary>
    public WorldViewSlot() : this(X: 0f, Y: 0f, Width: 1f, Height: 1f, Camera: null, Instance: null) { }
}
/// <summary>How often a <see cref="WorldViewGraph"/> instance refreshes: exactly one of <see cref="Divisor"/> and
/// <see cref="Hertz"/>. Presentation only; nothing here reaches simulation state.</summary>
/// <param name="Divisor">The instance renders at most once every this many presented frames, or <see langword="null"/>
/// when <see cref="Hertz"/> states the rate.</param>
/// <param name="Hertz">The most renders a second, or <see langword="null"/> when <see cref="Divisor"/> states the
/// rate.</param>
public sealed record WorldViewGraphRefresh(
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Divisor = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Hertz = null);
/// <summary>One external version of a graph instance's document bound to another instance's output.</summary>
/// <param name="Resource">The external version of the instance's graph document the binding fills.</param>
/// <param name="Instance">The <c>views.graphs</c> row whose output fills it. Naming the row's own instance reads its
/// previous frame.</param>
/// <param name="PreviousFrame">Whether the read takes the producer's previous completed frame, which lets two instances
/// show each other. Default <see langword="false"/>: the producer renders first in the same frame.</param>
public sealed record WorldViewGraphInput(string Resource, string Instance, bool PreviousFrame = false);
/// <summary>One named instance of a frame graph: a view rendered by a <c>puck.render.graph.v1</c> document, such as a
/// pane, a game camera shown on a screen, or a nested world, or an engine package's producer such as the SDF world. A
/// <see cref="WorldViewSlot.Instance"/> shows it in a layout, and the <c>pipeline.*</c> console verbs address it by
/// name. The row carries authored intent; planning, scheduling, compilation and resources belong to the
/// renderer.</summary>
/// <param name="Name">The instance's stable name (a <c>SafeName</c>, unique within the section), which another row's
/// input, a layout slot and a capture name.</param>
/// <param name="Source">Where the instance's graph comes from, or <see langword="null"/> for a row that names a
/// <see cref="Package"/>: a <c>puck.render.graph.v1</c> graph document, a one-off shader source file, which reads as a
/// one-pass graph, or a <c>puck.shader.package.v1</c> package directory, which loads with its source tree gone. It
/// resolves beside the document that authors it (<see cref="WorldDocumentPaths"/>), like every relative path a
/// document authors.</param>
/// <param name="Package">The engine package whose producer renders the instance, such as <c>sdf.world</c>, or an
/// uploaded producer's source package, <c>source.&lt;producer id&gt;</c>, opened from <see cref="Settings"/> and converted
/// once a frame at most however many instances read it, or <see langword="null"/> for a row that names a
/// <see cref="Source"/>. A package instance reads no input and takes no time scale, output or override.</param>
/// <param name="Camera">The authored camera the instance renders from, feeding the frame block's
/// <c>cameraPosition</c>, <c>cameraTarget</c>, <c>cameraUp</c> and <c>cameraFov</c>, or <see langword="null"/>, which
/// leaves <c>cameraFov</c> zero so a graph keeps its own pointer orbit.</param>
/// <param name="Refresh">How often it refreshes, or <see langword="null"/> for every frame something visible reads
/// it.</param>
/// <param name="Inputs">The external versions of its graph bound to other instances' outputs, or
/// <see langword="null"/> for none.</param>
/// <param name="TimeScale">The instance clock's rate multiplier — presentation only, never simulation state. Default 1;
/// 0 freezes the clock.</param>
/// <param name="Output">The image version the instance shows, or <see langword="null"/> for the source's first
/// declared output.</param>
/// <param name="Overrides">This instance's parameter overrides, keyed by pass name; each value is that pass's config
/// object keyed by field. A field absent here keeps the default the shared source declares, so two instances of one
/// source differ only in what they override. The source's config schema binds them when a boot, a
/// <c>world.load</c> or <c>world.reload</c>, a commit, or an upsert names them (the server reads the source, and
/// refuses a value by name), and again when a graph installs (the host binds them into its passes).
/// <see langword="null"/> overrides nothing.</param>
/// <param name="Parameters">This instance's bound parameters, keyed by pass name and then by config field: a number,
/// or a <c>state.&lt;row&gt;[.&lt;key&gt;][.$target]</c> token naming a Fixed or Int cell whose value the pass reads
/// through its state mirror slot, eased by default. A field a row names both here and in <see cref="Overrides"/> is
/// refused by name, and a binding that does not resolve leaves the field at its source's default.
/// <see langword="null"/> binds nothing.</param>
/// <param name="Settings">A source package row's producer settings, which its producer opens the image with and its
/// shape validates, as a screen's <c>producer</c> source's settings are, or <see langword="null"/> for the producer's
/// defaults. Only a row naming a source package (<c>source.&lt;producer id&gt;</c>) takes settings.</param>
public sealed record WorldViewGraph(string Name,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Source = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Package = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Camera = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldViewGraphRefresh? Refresh = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldViewGraphInput>? Inputs = null,
    float TimeScale = 1f,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Output = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Overrides = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Settings = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, IReadOnlyDictionary<string, BindableScalar>>? Parameters = null);
/// <summary>One pass the render graph composition synthesizes runs over the composed frame: a post-process package pass
/// of the root graph (<c>WorldViewGraphs.MainInstance</c>), written as a <c>puck.render.graph.v1</c> document's
/// <c>packages</c> row is, less its ports. The rows run in order after every view and pane is placed and before the
/// overlay, each reading the frame the pass before it wrote and writing the frame the next reads, so a row names no
/// input or output. Presentation only; nothing here reaches simulation state.</summary>
/// <param name="Name">The pass's name in the root graph (a <c>SafeName</c>, unique within the section and distinct from
/// every <c>views.graphs</c> row, whose place pass the root names after the row), which a probe parameter's
/// <c>post</c> target names.</param>
/// <param name="Package">The post-process package the pass runs, a render graph package id such as
/// <c>sdf.film-grain</c>, checked against the host's catalog at document load.</param>
/// <param name="Config">The package's config values, each absent field at its default, or <see langword="null"/> for
/// every default. The graph compiler binds them against the package's schema when the root graph is composed, and a
/// boot refuses a value that does not bind, naming the row.</param>
public sealed record WorldViewPostPass(string Name, string Package,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] System.Text.Json.JsonElement? Config = null);
/// <summary>The presentation's price ceilings: the pass-pixels the graph scheduler holds the instances the display does
/// not show directly to, and the bytes the <c>views.graphs</c> rows' bound parameters may owe their passes
/// (<see cref="WorldBindingCost"/>).</summary>
/// <param name="PassPixelsPerFrame">The pass-pixels (passes times rendered pixels) those instances may spend in one
/// presented frame; the stalest due instance is admitted first and the rest read their latest completed output. 0 sets
/// no ceiling.</param>
/// <param name="BytesPerTick">The bytes every bound parameter together may owe its pass on a tick that moves its row,
/// or 0 for no ceiling. A document whose bindings exceed it is refused, naming the graph and the binding that crosses
/// it.</param>
/// <param name="BytesPerFrame">The bytes every bound parameter together may owe its pass on each presented frame
/// between ticks, or 0 for no ceiling, refused the same way.</param>
public sealed record WorldViewGraphBudget(long PassPixelsPerFrame = 0, long BytesPerTick = 0, long BytesPerFrame = 0);
/// <summary>One named window composition — an ordered list of <see cref="WorldViewSlot"/>s plus a transition envelope,
/// selected for a given session shape by its <see cref="SeatCount"/> (0 = the catch-all for any joined-seat count). The
/// data-side replacement for a compiled layout <c>switch</c>: an author can see it, change it, and add arrangements.</summary>
/// <param name="Name">The layout's stable name (the <c>view.override layout</c> override handle; unique within the section).</param>
/// <param name="Slots">The slots, in order (a null-camera slot binds the next joined seat).</param>
/// <param name="SeatCount">The joined-seat count this layout composes for, or 0 for the catch-all. Default 0.</param>
/// <param name="TransitionSeconds">How long the ease into this composition takes when it becomes active. Default 0,
/// a cut.</param>
/// <param name="TransitionRenderScale">The render scale (0, 1] applied to every slot mid-transition (a soft dip that
/// sharpens on settle). Default 1, no dip.</param>
public sealed record WorldViewLayout(string Name, IReadOnlyList<WorldViewSlot> Slots, int SeatCount = 0,
    float TransitionSeconds = 0f, float TransitionRenderScale = 1f) {
    private readonly IReadOnlyList<WorldViewSlot> m_slots = (Slots ?? []);

    /// <summary>Gets the slots, in order. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="WorldHudPanel.Elements"/>'s does.</summary>
    public IReadOnlyList<WorldViewSlot> Slots {
        get => m_slots;
        init => m_slots = (value ?? []);
    }
}
/// <summary>The <c>views</c> document section — the seat framing every seat wakes on plus the authored named layouts. A
/// REQUIRED section every document carries; an empty layout list falls the composer through to the built-in seat
/// ladder.</summary>
/// <summary>The authored structure of live seat-camera control.</summary>
/// <param name="YawReference">What the camera yaw is relative to.</param>
/// <param name="MinPitch">The minimum live pitch offset in radians.</param>
/// <param name="MaxPitch">The maximum live pitch offset in radians.</param>
/// <param name="Follow">The follow camera: with no look input the camera yaw eases in behind the body's heading;
/// any look input (a deflected look stick, a held orbit/steer) is free-look and the follow yields for as long as
/// it lasts. Optional; absent is a still camera that goes only where look input sends it. Needs
/// <see cref="WorldSeatYawReference.World"/> — a body-relative yaw already rides the body.</param>
public sealed record WorldSeatViewControl(WorldSeatYawReference YawReference, float MinPitch, float MaxPitch, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldSeatFollow? Follow = null);
/// <summary>The follow camera's shape.</summary>
/// <param name="Rate">The exponential rate (per second) the camera yaw closes on the heading — about 63% of the
/// remaining angle per <c>1/rate</c> seconds; larger is a stiffer follow.</param>
/// <param name="WhileIdle">Whether the follow also runs while the body has no movement input. <see langword="false"/>
/// (the default) is the classic feel: after a free-look the camera stays where you left it until you move.</param>
public sealed record WorldSeatFollow(float Rate, bool WhileIdle = false);
/// <summary>What a seat camera's yaw is relative to.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldSeatYawReference>))]
public enum WorldSeatYawReference : byte {
    World,
    Body,
}
/// <param name="Layouts">The authored named layouts (empty = the built-in ladder).</param>
/// <param name="SeatRigRaw">The authored chase framing, or <see langword="null"/> for a document that seats no
/// body; <see cref="SeatRig"/> is what a reader resolves through.</param>
/// <param name="SeatControlRaw">The authored constraints for live seat camera input, or <see langword="null"/>
/// for a document that seats no body; <see cref="SeatControl"/> is what a reader resolves through.</param>
/// <param name="CameraRig">The program a seat's view resolves through while its published mode state targets
/// <see cref="WorldSeatModeState.CameraTarget"/> — <see langword="null"/> for a world that authors no
/// camera-targeting mode state. Resolved through the ordinary <c>Puck.World.Client.WorldCameraRigCompiler</c> pipeline
/// against whichever body the seat currently perceives from (the possessed camera body — see
/// <c>Puck.World.Server.WorldEngagement</c>), exactly like <see cref="SeatRig"/> resolves against the seat's own
/// avatar; no bespoke per-frame integrator reads this field.</param>
/// <param name="ShaderToolchain">The directory holding the graph compiler's <c>dxc</c>, or <see langword="null"/> to
/// resolve it by bare name through the ordinary executable search path — never an environment variable.</param>
/// <param name="Graphs">The authored <c>views.graphs</c> frame-graph instances (empty = none declared).</param>
/// <param name="Root">The <c>views.graphs</c> row the display shows and captures read, or <see langword="null"/> for
/// the render graph composition synthesizes from the document around <c>WorldViewGraphs.WorldInstance</c> and
/// <c>WorldViewGraphs.MainInstance</c>. A world that names its root authors its whole render graph, the
/// <c>sdf.world</c> package row included.</param>
/// <param name="GraphBudget">The graph scheduler's price ceiling, or <see langword="null"/> for none.</param>
/// <param name="Post">The post-process passes the synthesized root graph runs over the composed frame, in order, before
/// the overlay, or <see langword="null"/> for none. A world that names <see cref="Root"/> authors its whole graph and
/// names none.</param>
public sealed record WorldViewDefaults(IReadOnlyList<WorldViewLayout>? Layouts = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("seatRig"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldCameraProgram? SeatRigRaw = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("seatControl"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldSeatViewControl? SeatControlRaw = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldCameraProgram? CameraRig = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ShaderToolchain = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldViewGraph>? Graphs = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldViewGraphBudget? GraphBudget = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Root = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldViewPostPass>? Post = null) {
    private readonly IReadOnlyList<WorldViewLayout> m_layouts = (Layouts ?? []);

    /// <summary>Gets the placeholder an UNAUTHORED <c>views</c> section resolves to — an empty program, holding the
    /// property non-null between parse and validation. The engine carries no camera policy of its own: the standard
    /// chase framing is AUTHORED, in <c>Assets/worlds/standard.world.json</c>, and a world inherits it by naming that
    /// document as its basis. A document whose census implies a body is refused for authoring no <c>views</c>
    /// (<c>WorldDefinitionValidator</c>), so nothing ever composes a seat view from this. Control feel is not here
    /// either: it is per-seat, on <see cref="WorldPlayerDefaults.SeatLook"/>.</summary>
    public static WorldViewDefaults Absent { get; } = new(
        SeatRigRaw: new WorldCameraProgram(
            Name: "absent",
            Version: WorldCameraProgram.CurrentVersion,
            Operations: [
                new WorldCameraProgramOp.Orbit(
                    Distance: 0.01f,
                    Yaw: new BindableScalar(literal: 0f),
                    Pitch: new BindableScalar(literal: 0f)
                ),
                new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 0f)),
            ]
        ),
        SeatControlRaw: new WorldSeatViewControl(
            MaxPitch: 0f,
            MinPitch: 0f,
            YawReference: WorldSeatYawReference.World
        ),
        Layouts: []
    );
    /// <summary>Gets the chase framing every seat's view resolves through by default: the authored rig, or
    /// <see cref="Absent"/>'s where the document authors none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public WorldCameraProgram SeatRig => (SeatRigRaw ?? Absent.SeatRigRaw!);
    /// <summary>Gets the structural constraints for live seat camera input: the authored control, or
    /// <see cref="Absent"/>'s where the document authors none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public WorldSeatViewControl SeatControl => (SeatControlRaw ?? Absent.SeatControlRaw!);
    /// <summary>Gets the authored named layouts. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="WorldHudPanel.Elements"/>'s does.</summary>
    public IReadOnlyList<WorldViewLayout> Layouts {
        get => m_layouts;
        init => m_layouts = (value ?? []);
    }
}
