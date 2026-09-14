namespace Puck.State;

/// <summary>
/// A <see cref="StateCell"/>/<see cref="StateRow"/>'s second-order easing trait: the STORED value stays the
/// TRUTH (what rules, gates, and comparands read), while an eased read (<see cref="StateReader"/>)
/// computes a second-order follower's current sample from the carrying cell's own <see cref="StateCellClock.Y0"/>/
/// <see cref="StateCellClock.V0"/> at its <see cref="StateCellClock.EpochTick"/>, chasing the stored value as its
/// target — the closed-form counterpart to <see cref="StateAdvance"/>'s linear accumulation, no per-tick work
/// either. An explicit write REBASES: the cell's clock <see cref="StateCellClock.Y0"/>/<see cref="StateCellClock.V0"/>
/// become the eased value and velocity computed AT the write's own tick, and <see cref="StateCellClock.EpochTick"/>
/// becomes that tick — the same rebase discipline <see cref="StateAdvance"/>'s own write rule follows, so a retune
/// never jumps. A cell composed with this trait but no clock yet (never settled or explicitly written) reads its own
/// stored value as the follower's position, at rest — see <see cref="StateReader.TryEvaluateDynamics"/>.
/// </summary>
/// <param name="Row">The referenced <c>dynamics</c> row name; must resolve.</param>
public sealed record StateDynamics(string Row);
