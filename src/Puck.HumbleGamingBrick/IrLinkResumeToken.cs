namespace Puck.HumbleGamingBrick;

/// <summary>The credit a suspended <see cref="IrLinkSession"/> hands back so a later reconnect resumes the exact pacing it
/// severed at. Each machine's credit is its instruction overshoot — the T-cycles it had already run past its cumulative
/// link target when the cable was suspended — which a naive reconnect (re-anchoring targets at the current instant) would
/// discard, running that many extra cycles per machine and diverging the trace by construction. It is a plain host-owned
/// value, never emulated state and never serialized. Structurally identical to <see cref="SerialLinkResumeToken"/>: IR and
/// serial share the furthest-behind pair-stepping principle, so they share the credit shape — a shared idea, not shared
/// code.</summary>
/// <param name="FirstCredit">The first machine's instruction-overshoot credit at suspend.</param>
/// <param name="SecondCredit">The second machine's instruction-overshoot credit at suspend.</param>
public readonly record struct IrLinkResumeToken(
    ulong FirstCredit,
    ulong SecondCredit
);
