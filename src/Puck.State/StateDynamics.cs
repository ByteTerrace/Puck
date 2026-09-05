namespace Puck.State;

/// <summary>
/// A <see cref="StateCell"/>/<see cref="StateRow"/>'s second-order easing trait: the STORED value stays the
/// TRUTH (what rules, gates, and comparands read), while a read through <c>Puck.World.WorldStateReader.TryReadEased</c>
/// computes a second-order follower's current sample from <see cref="Y0"/>/<see cref="V0"/> at <see cref="EpochTick"/>,
/// chasing the stored value as its target — the closed-form counterpart to <see cref="StateAdvance"/>'s linear
/// accumulation, no per-tick work either. An explicit write REBASES: the trait's <see cref="Y0"/>/<see cref="V0"/>
/// become the eased value and velocity computed AT the write's own tick, and <see cref="EpochTick"/> becomes that tick
/// — the same rebase discipline <see cref="StateAdvance"/>'s own write rule follows, so a retune never jumps.
/// </summary>
/// <param name="Row">The referenced <c>dynamics</c> row name; must resolve.</param>
/// <param name="Y0">The follower's position at <see cref="EpochTick"/> as raw <c>FixedQ4816</c> bits, independent
/// of the carrying row's stored-value kind. Keeping the continuous state fixed-native preserves sub-unit phase when
/// an integer target is rebased.</param>
/// <param name="V0">The follower's velocity at <see cref="EpochTick"/>, per second, as raw
/// <c>FixedQ4816</c> bits. A sub-unit response kick therefore survives an integer-row rebase.</param>
/// <param name="EpochTick">The server tick <see cref="Y0"/>/<see cref="V0"/> were captured at.</param>
public sealed record StateDynamics(string Row, long Y0, long V0, long EpochTick = 0);
