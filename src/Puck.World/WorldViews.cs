using Puck.SdfVm.Views;

namespace Puck.World;

/// <summary>One slot of a <see cref="WorldViewLayout"/> — a normalized rect (origin top-left, Y down) plus what fills it.
/// A slot whose <see cref="Camera"/> is <see langword="null"/> shows the seat that owns this slot (the next joined seat
/// in slot order); a named camera renders that authored view into the rect.</summary>
/// <param name="X">The rect's left edge, normalized [0, 1].</param>
/// <param name="Y">The rect's top edge, normalized [0, 1].</param>
/// <param name="Width">The rect's width, normalized (0, 1].</param>
/// <param name="Height">The rect's height, normalized (0, 1].</param>
/// <param name="Camera">The authored camera name filling this slot, or <see langword="null"/> for the seat that owns it.</param>
internal readonly record struct WorldViewSlot(float X, float Y, float Width, float Height, string? Camera);

/// <summary>One named window composition — an ordered list of <see cref="WorldViewSlot"/>s plus a transition envelope,
/// selected for a given session shape by its <see cref="SeatCount"/> (0 = the catch-all for any joined-seat count). The
/// data-side replacement for a compiled layout <c>switch</c>: an author can see it, change it, and add arrangements.</summary>
/// <param name="Name">The layout's stable name (the <c>view.layout</c> override handle; unique within the section).</param>
/// <param name="SeatCount">The joined-seat count this layout composes for, or 0 for the catch-all.</param>
/// <param name="Slots">The slots, in order (a null-camera slot binds the next joined seat).</param>
/// <param name="TransitionSeconds">How long the ease into this composition takes when it becomes active.</param>
/// <param name="TransitionRenderScale">The render scale (0, 1] applied to every slot mid-transition (a soft dip that
/// sharpens on settle), the compiled director's <c>0.5f</c> now authored per layout.</param>
internal sealed record WorldViewLayout(string Name, int SeatCount, IReadOnlyList<WorldViewSlot> Slots,
    float TransitionSeconds, float TransitionRenderScale);

/// <summary>The <c>views</c> document section — the seat framing every seat wakes on plus the authored named layouts. A
/// nullable section (the plan-wide new-section idiom): <see langword="null"/> in JSON coalesces to <see cref="Default"/>,
/// whose empty layout list falls the composer through to the built-in seat ladder, reproducing today's window composition
/// byte-for-byte.</summary>
/// <param name="SeatRig">The chase framing every seat's view resolves through (the non-editing default).</param>
/// <param name="Layouts">The authored named layouts (default empty = the built-in ladder).</param>
internal sealed record WorldViewDefaults(WorldRig SeatRig, IReadOnlyList<WorldViewLayout> Layouts) {
    /// <summary>The built-in defaults: the engine chase framing every seat wakes on (<see cref="OrientedFollowRig"/>'s own
    /// field defaults, so the frozen default world renders identically), and NO authored layouts — an empty list means
    /// the built-in seat ladder composes the window exactly as it does today.</summary>
    public static WorldViewDefaults Default { get; } = new(
        SeatRig: new WorldRig.Chase(
            EyeOffset: new(x: 0f, y: 2.2f, z: 5f),
            TargetOffset: new(x: 0f, y: 1f, z: 0f),
            WorldAxes: false,
            SpreadPullback: 0f,
            FieldOfViewRadians: OrientedFollowRig.DefaultFieldOfViewRadians
        ),
        Layouts: []
    );
}
