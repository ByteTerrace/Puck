using System.Numerics;

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
public sealed record WorldSeatViewControl(WorldSeatYawReference YawReference, float MinPitch, float MaxPitch);

/// <summary>What a seat camera's yaw is relative to.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldSeatYawReference>))]
public enum WorldSeatYawReference : byte {
    World,
    Body,
}

/// <param name="SeatRig">The chase framing every seat's view resolves through (the non-editing default).</param>
/// <param name="SeatControl">The structural constraints/reference for live seat camera input.</param>
/// <param name="Layouts">The authored named layouts (empty = the built-in ladder).</param>
public sealed record WorldViewDefaults(WorldCameraRig SeatRig, WorldSeatViewControl SeatControl, IReadOnlyList<WorldViewLayout> Layouts) {
    /// <summary>The engine's default vertical field of view (55 degrees), mirroring
    /// <c>Puck.SdfVm.Views.OrbitRig.DefaultFieldOfViewRadians</c> — every concrete <c>ISdfCameraRig</c> shares this one
    /// value, so it is pinned here rather than read from Puck.SdfVm, which Puck.World.Data must not reference.</summary>
    private const float EngineDefaultFieldOfViewRadians = (55f * (float.Pi / 180f));

    private readonly IReadOnlyList<WorldViewLayout> m_layouts = (Layouts ?? []);

    /// <summary>Gets the authored named layouts. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="WorldMotionModel.Grounded.Response"/>'s does.</summary>
    public IReadOnlyList<WorldViewLayout> Layouts {
        get => m_layouts;
        init => m_layouts = (value ?? []);
    }

    /// <summary>Gets the built-in defaults: the engine chase framing every seat wakes on — the same orbit numbers and
    /// rig-level smoothing `play`/`jump` author, and NO authored layouts (an empty list means the built-in seat ladder
    /// composes the window). Control feel is NOT here: it is per-seat, on
    /// <see cref="WorldPlayerDefaults.SeatLook"/>.</summary>
    public static WorldViewDefaults Default { get; } = new(
        SeatRig: new WorldCameraRig(
            Motion: new WorldCameraMotion.Orbit(Distance: 5.4626001f, Yaw: 0f, Pitch: 0.4145069f, PivotOffset: Vector3.Zero),
            Aim: new WorldCameraAim.Anchor(Offset: new(x: 0f, y: 1f, z: 0f), WorldAxes: false),
            Lens: new WorldCameraLens(FieldOfViewRadians: EngineDefaultFieldOfViewRadians),
            SmoothRate: 6f
        ),
        SeatControl: new WorldSeatViewControl(YawReference: WorldSeatYawReference.World, MinPitch: -0.35f, MaxPitch: 1.2f),
        Layouts: []
    );
}
