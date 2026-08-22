namespace Puck.World;

/// <summary>One slot of a <see cref="WorldViewLayout"/> — a normalized rect (origin top-left, Y down) plus what fills it.
/// A slot whose <see cref="Camera"/> is <see langword="null"/> shows the seat that owns this slot (the next joined seat
/// in slot order); a named camera renders that authored view into the rect.</summary>
/// <param name="X">The rect's left edge, normalized [0, 1].</param>
/// <param name="Y">The rect's top edge, normalized [0, 1].</param>
/// <param name="Width">The rect's width, normalized (0, 1].</param>
/// <param name="Height">The rect's height, normalized (0, 1].</param>
/// <param name="Camera">The authored camera name filling this slot, or <see langword="null"/> for the seat that owns it.</param>
public readonly record struct WorldViewSlot(float X, float Y, float Width, float Height, string? Camera);
/// <summary>One named window composition — an ordered list of <see cref="WorldViewSlot"/>s plus a transition envelope,
/// selected for a given session shape by its <see cref="SeatCount"/> (0 = the catch-all for any joined-seat count). The
/// data-side replacement for a compiled layout <c>switch</c>: an author can see it, change it, and add arrangements.</summary>
/// <param name="Name">The layout's stable name (the <c>view.override layout</c> override handle; unique within the section).</param>
/// <param name="SeatCount">The joined-seat count this layout composes for, or 0 for the catch-all.</param>
/// <param name="Slots">The slots, in order (a null-camera slot binds the next joined seat).</param>
/// <param name="TransitionSeconds">How long the ease into this composition takes when it becomes active.</param>
/// <param name="TransitionRenderScale">The render scale (0, 1] applied to every slot mid-transition (a soft dip that
/// sharpens on settle), the compiled director's <c>0.5f</c> now authored per layout.</param>
public sealed record WorldViewLayout(string Name, int SeatCount, IReadOnlyList<WorldViewSlot> Slots,
    float TransitionSeconds, float TransitionRenderScale) {
    private readonly IReadOnlyList<WorldViewSlot> m_slots = (Slots ?? []);

    /// <summary>Gets the slots, in order. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="WorldMotionModel.Grounded.Response"/>'s does.</summary>
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
/// <param name="SeatRig">The chase framing every seat's view resolves through by default.</param>
/// <param name="SeatControl">The structural constraints/reference for live seat camera input.</param>
/// <param name="Layouts">The authored named layouts (empty = the built-in ladder).</param>
/// <param name="CameraRig">The program a seat's view resolves through while its published mode state targets
/// <see cref="WorldSeatModeState.CameraTarget"/> — <see langword="null"/> for a world that authors no
/// camera-targeting mode state. Resolved through the ordinary <c>Puck.World.Client.WorldCameraRigCompiler</c> pipeline
/// against whichever body the seat currently perceives from (the possessed camera body — see
/// <c>Puck.World.Server.WorldEngagement</c>), exactly like <see cref="SeatRig"/> resolves against the seat's own
/// avatar; no bespoke per-frame integrator reads this field.</param>
public sealed record WorldViewDefaults(WorldCameraProgram SeatRig, WorldSeatViewControl SeatControl, IReadOnlyList<WorldViewLayout> Layouts,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldCameraProgram? CameraRig = null) {
    private readonly IReadOnlyList<WorldViewLayout> m_layouts = (Layouts ?? []);

    /// <summary>Gets the placeholder an UNAUTHORED <c>views</c> section resolves to — an empty program, holding the
    /// property non-null between parse and validation. The engine carries no camera policy of its own: the standard
    /// chase framing is AUTHORED, in <c>Assets/worlds/standard.world.json</c>, and a world inherits it by naming that
    /// document as its basis. A document whose census implies a body is refused for authoring no <c>views</c>
    /// (<c>WorldDefinitionValidator</c>), so nothing ever composes a seat view from this. Control feel is not here
    /// either: it is per-seat, on <see cref="WorldPlayerDefaults.SeatLook"/>.</summary>
    public static WorldViewDefaults Absent { get; } = new(
        SeatRig: new WorldCameraProgram(
            Name: "absent",
            Version: WorldCameraProgram.CurrentVersion,
            Operations: [
                new WorldCameraProgramOp.Orbit(Distance: 0.01f, Yaw: new BindableScalar(literal: 0f), Pitch: new BindableScalar(literal: 0f)),
                new WorldCameraProgramOp.Fov(new BindableScalar(literal: 0f)),
            ]
        ),
        SeatControl: new WorldSeatViewControl(
            MaxPitch: 0f,
            MinPitch: 0f,
            YawReference: WorldSeatYawReference.World
        ),
        Layouts: []
    );
    /// <summary>Gets the authored named layouts. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="WorldMotionModel.Grounded.Response"/>'s does.</summary>
    public IReadOnlyList<WorldViewLayout> Layouts {
        get => m_layouts;
        init => m_layouts = (value ?? []);
    }
}
