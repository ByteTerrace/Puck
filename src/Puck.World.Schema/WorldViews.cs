namespace Puck.World;

/// <summary>One slot of a <see cref="WorldViewLayout"/> — a normalized rect (origin top-left, Y down) plus what fills it.
/// A slot whose <see cref="Camera"/> and <see cref="Pipeline"/> are both <see langword="null"/> shows the seat that owns
/// this slot (the next joined seat in slot order); a named camera renders that authored view into the rect; a named
/// pipeline (a <see cref="WorldViewPipeline"/> row) renders its selected image output into the rect — a
/// slot names at most one of the two (the validator refuses both authored together).</summary>
/// <param name="X">The rect's left edge, normalized [0, 1]. Default 0.</param>
/// <param name="Y">The rect's top edge, normalized [0, 1]. Default 0.</param>
/// <param name="Width">The rect's width, normalized (0, 1]. Default 1, so a slot that authors no rect fills the
/// window.</param>
/// <param name="Height">The rect's height, normalized (0, 1]. Default 1.</param>
/// <param name="Camera">The authored camera name filling this slot, or <see langword="null"/> for the seat that owns it
/// (or the pipeline named by <see cref="Pipeline"/>).</param>
/// <param name="Pipeline">The authored <c>views.pipelines</c> row name filling this slot with a compiled shader pipeline, or
/// <see langword="null"/> for an ordinary seat/camera slot. Mutually exclusive with <see cref="Camera"/>.</param>
[method: System.Text.Json.Serialization.JsonConstructor]
public readonly record struct WorldViewSlot(float X = 0f, float Y = 0f, float Width = 1f, float Height = 1f, string? Camera = null, string? Pipeline = null) {
    /// <summary>Creates a full-window slot, matching an authored slot with omitted rectangle fields.</summary>
    public WorldViewSlot() : this(X: 0f, Y: 0f, Width: 1f, Height: 1f, Camera: null, Pipeline: null) { }
}
/// <summary>One named shader-pipeline instance displayed by a <see cref="WorldViewSlot.Pipeline"/> slot.
/// Its source declares connected GPU passes, or is a single shader adapted into a one-pass pipeline.
/// The row carries authored intent; compilation, resource ownership, history, and presentation inputs belong
/// to the shader runtime and its host.</summary>
/// <param name="Name">The pipeline's stable name (a <c>SafeName</c>, unique within the section) — what a
/// <see cref="WorldViewSlot.Pipeline"/> names and a <c>pipeline.*</c> console verb addresses.</param>
/// <param name="Source">Where the pipeline comes from: a pipeline document, a one-off shader source file, or a
/// <c>puck.shader.package.v1</c> package directory, which loads with its source tree gone. It resolves beside the
/// document that authors it (<see cref="WorldDocumentPaths"/>), like every relative path a document authors.</param>
/// <param name="Camera">The authored camera feeding the pipeline frame block's <c>cameraPosition</c>,
/// <c>cameraTarget</c>, <c>cameraUp</c> and <c>cameraFov</c>, or <see langword="null"/>, which leaves <c>cameraFov</c>
/// zero so a pipeline keeps its own pointer orbit.</param>
/// <param name="TimeScale">The pipeline clock's rate multiplier — presentation only, never simulation state. Default 1;
/// 0 freezes the clock.</param>
/// <param name="Output">The image version the instance shows, or <see langword="null"/> for the source's first
/// declared output.</param>
/// <param name="Overrides">This instance's parameter overrides, keyed by pass name; each value is that pass's config
/// object keyed by field. A field absent here keeps the default the shared source declares, so two instances of one
/// source differ only in what they override. The source's config schema binds them when a boot, a
/// <c>world.load</c> or <c>world.reload</c>, a commit, or an upsert names them (the server reads the source, and
/// refuses a value by name), and again when a graph installs (the host binds them into its passes).
/// <see langword="null"/> overrides nothing.</param>
public sealed record WorldViewPipeline(string Name, string Source, string? Camera = null, float TimeScale = 1f,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Output = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Overrides = null);
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
/// pane, a game camera shown on a screen, or a nested world. The row carries authored intent; planning, scheduling and
/// resources belong to the renderer.</summary>
/// <param name="Name">The instance's stable name (a <c>SafeName</c>, unique within the section), which another row's
/// input names.</param>
/// <param name="Source">The graph document, resolved relative to the world document's own directory as a
/// <see cref="WorldViewPipeline.Source"/> is.</param>
/// <param name="Camera">The authored camera the instance renders from, or <see langword="null"/> for a graph that reads
/// no camera.</param>
/// <param name="Refresh">How often it refreshes, or <see langword="null"/> for every frame something visible reads
/// it.</param>
/// <param name="Inputs">The external versions of its graph bound to other instances' outputs, or
/// <see langword="null"/> for none.</param>
public sealed record WorldViewGraph(string Name, string Source,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Camera = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldViewGraphRefresh? Refresh = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldViewGraphInput>? Inputs = null);
/// <summary>The price ceiling the graph scheduler holds the instances the display does not show directly to.</summary>
/// <param name="PassPixelsPerFrame">The pass-pixels (passes times rendered pixels) those instances may spend in one
/// presented frame; the stalest due instance is admitted first and the rest read their latest completed output. 0 sets
/// no ceiling.</param>
public sealed record WorldViewGraphBudget(long PassPixelsPerFrame = 0);
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
/// <param name="Pipelines">The authored <c>views.pipelines</c> rows a <see cref="WorldViewSlot.Pipeline"/> may name (empty =
/// no pipelines declared).</param>
/// <param name="ShaderToolchain">The directory holding the pipeline compiler's <c>dxc</c>, or <see langword="null"/> to
/// resolve it by bare name through the ordinary executable search path — never an environment variable.</param>
/// <param name="Graphs">The authored <c>views.graphs</c> frame-graph instances (empty = none declared).</param>
/// <param name="GraphBudget">The graph scheduler's price ceiling, or <see langword="null"/> for none.</param>
public sealed record WorldViewDefaults(IReadOnlyList<WorldViewLayout>? Layouts = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("seatRig"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldCameraProgram? SeatRigRaw = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("seatControl"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldSeatViewControl? SeatControlRaw = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldCameraProgram? CameraRig = null,
    IReadOnlyList<WorldViewPipeline>? Pipelines = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ShaderToolchain = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldViewGraph>? Graphs = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldViewGraphBudget? GraphBudget = null) {
    private readonly IReadOnlyList<WorldViewLayout> m_layouts = (Layouts ?? []);
    private readonly IReadOnlyList<WorldViewPipeline> m_pipelines = (Pipelines ?? []);

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
    /// <summary>Gets the authored <c>views.pipelines</c> rows. The absence-coalesce lives in the accessor for the same
    /// reason <see cref="WorldHudPanel.Elements"/>'s does.</summary>
    public IReadOnlyList<WorldViewPipeline> Pipelines {
        get => m_pipelines;
        init => m_pipelines = (value ?? []);
    }
}
