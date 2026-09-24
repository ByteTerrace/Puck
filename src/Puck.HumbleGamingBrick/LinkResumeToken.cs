namespace Puck.HumbleGamingBrick;

/// <summary>The pacing credits a <see cref="LinkSession{TPort}"/> hands back so a later reconnect or a coupled restore
/// resumes the exact pacing it captured. Each machine's credit is its instruction overshoot — the T-cycles it had
/// already run past its cumulative link target — which a naive reconnect (re-anchoring targets at the current instant)
/// would discard, running that many extra cycles per machine and diverging the trace by construction. It is host
/// state, never emulated state; a live link that snapshots its members serializes it beside them (see
/// <see cref="LinkSession{TPort}.PacingCredits"/>), because the pair's trajectory is not reproducible without it.</summary>
/// <param name="FirstCredit">The first machine's instruction-overshoot credit, in T-cycles.</param>
/// <param name="SecondCredit">The second machine's instruction-overshoot credit, in T-cycles.</param>
public readonly record struct LinkResumeToken(
    ulong FirstCredit,
    ulong SecondCredit
);
