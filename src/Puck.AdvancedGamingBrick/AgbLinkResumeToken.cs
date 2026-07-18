namespace Puck.AdvancedGamingBrick;

/// <summary>The credit and machine-identity binding a suspended <see cref="AgbLinkSession"/> hands back so a later
/// reconnect resumes the exact pacing it severed at, on the exact same consoles in the exact same order. Each
/// console's credit is its instruction overshoot — the master-clock cycles it had already run past its cumulative
/// link target when the cable was suspended — which a naive reconnect (re-anchoring targets at the current instant)
/// would discard, running that many extra cycles per console and diverging the trace by construction. Each console's
/// <see cref="AgbMachineIdentity"/> (the same format-version/BIOS/ROM stamp a snapshot carries) travels alongside its
/// credit, so the resume constructor can reject a reordered or substituted console rather than silently applying the
/// wrong credit to the wrong machine. A plain host-owned value, never emulated state and never serialized; both
/// arrays are defensively copied on the way in and exposed only as read-only spans, so a caller can never mutate a
/// live token's credits or identities out from under a pending resume.</summary>
public sealed class AgbLinkResumeToken {
    private readonly long[] m_credits;
    private readonly AgbMachineIdentity[] m_identities;

    internal AgbLinkResumeToken(long[] credits, AgbMachineIdentity[] identities) {
        m_credits = (long[])credits.Clone();
        m_identities = (AgbMachineIdentity[])identities.Clone();
    }

    /// <summary>Gets the number of consoles this token carries a credit and identity for.</summary>
    public int Count => m_credits.Length;

    /// <summary>Gets each console's instruction-overshoot credit at suspend, in session order.</summary>
    public ReadOnlySpan<long> Credits => m_credits;

    /// <summary>Gets each console's identity (format version / BIOS / ROM fingerprint) at suspend, in session
    /// order — the binding a resume validates the reconnecting consoles against.</summary>
    public ReadOnlySpan<AgbMachineIdentity> Identities => m_identities;
}
