namespace Puck.HumbleGamingBrick;

/// <summary>The credit a suspended <see cref="SerialLinkSession"/> hands back so a later reconnect resumes the exact
/// pacing it severed at. Each machine's credit is its instruction overshoot — the T-cycles it had already run past its
/// cumulative link target when the cable was suspended — which a naive reconnect (re-anchoring targets at the current
/// instant) would discard, running that many extra cycles per machine and diverging the trace by construction. It is
/// host state, never emulated state; a live link that snapshots its members serializes it beside them (see
/// <see cref="SerialLinkSession.PacingCredits"/>), because the pair's trajectory is not reproducible without it.</summary>
/// <param name="FirstCredit">The first machine's instruction-overshoot credit at suspend.</param>
/// <param name="SecondCredit">The second machine's instruction-overshoot credit at suspend.</param>
public readonly record struct SerialLinkResumeToken(
    ulong FirstCredit,
    ulong SecondCredit
);
