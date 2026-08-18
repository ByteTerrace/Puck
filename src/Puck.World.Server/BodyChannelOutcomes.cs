using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The outcome of a <see cref="WorldBody.Stop"/> panic verb: how many device-held channels it released and
/// how many in-flight timed presses (<c>player.press</c> holds) it cancelled — <c>player.stop</c>'s synchronous
/// read-back, so its echo reports what actually happened instead of a fixed template.</summary>
/// <param name="ReleasedHeldChannels">Count of non-zero ordinals in the dropped device-held image.</param>
/// <param name="ClearedTimedPresses">Count of in-flight timed presses (role and composition ordinals alike)
/// cancelled.</param>
public readonly record struct StopOutcome(int ReleasedHeldChannels, int ClearedTimedPresses);
/// <summary>Which cap, if any, decided a timed <c>player.press</c>'s effective hold — <see cref="WorldBody.PressChannel(int, FixedQ4816, float, FixedQ4816)"/>'s
/// synchronous read-back discriminator, so the console echo can name the true binder instead of guessing from the
/// effective value's magnitude.</summary>
public enum PressHoldCapKind : byte {
    /// <summary>The effective hold equals the request — nothing capped it.</summary>
    None,

    /// <summary>The request was non-positive (or NaN) and was ignored outright — no timed press was created, and
    /// any in-flight hold on the ordinal was left untouched.</summary>
    Ignored,

    /// <summary>The deciding Drive grant row's <c>hold:</c> ceiling (<see cref="WorldGrant.DefaultHoldSeconds"/>
    /// absent an authored row) is what bound the request — it is authored strictly below the engine backstop, so it
    /// is doing real narrowing of its own.</summary>
    GrantBudget,

    /// <summary>The <see cref="WorldBody.MaxActionHoldSeconds"/> engine backstop is what bound the request — the
    /// grant permits up to the backstop with no narrowing of its own, and the raw request still exceeded it.</summary>
    EngineCeiling,
}
/// <summary>The outcome of a timed <c>player.press</c> — the effective hold after both caps apply, and which one
/// (if either) actually bound it. <c>player.press</c>'s synchronous read-back, so its echo can report the true
/// result instead of echoing the requested duration as though it were honored.</summary>
/// <param name="EffectiveHoldSeconds">The hold actually applied, in sim seconds.</param>
/// <param name="CapKind">Which cap decided it.</param>
public readonly record struct PressOutcome(FixedQ4816 EffectiveHoldSeconds, PressHoldCapKind CapKind);
