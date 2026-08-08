namespace Puck.Scripting;

/// <summary>The addon ABI's authorization outcome wire values, carried in an <c>Answer</c> cell's <c>Verdict</c>
/// byte. Pinned independently of any consumer enum, the same way every other closed wire set in this file's
/// namespace is: the authorization outcome crosses as readable data rather than collapsing to a boolean, so a
/// guest can distinguish why a request was refused.</summary>
public enum AddonVerdict : byte {
    /// <summary>The cell's kind carries no verdict (<c>Tick</c>, <c>Observation</c>).</summary>
    None = 0,

    /// <summary>Allowed — a grant row names the subject itself.</summary>
    HeldConcrete = 1,

    /// <summary>Allowed — the wildcard row covers the subject.</summary>
    HeldWildcard = 2,

    /// <summary>Allowed — the caller is the subject's exclusive reserver.</summary>
    HeldAsReserver = 3,

    /// <summary>Denied — no grant row and no wildcard cover the subject.</summary>
    NoHold = 4,

    /// <summary>Denied — another principal exclusively reserves the subject.</summary>
    BeatenByReserver = 5,

    /// <summary>Denied — the requested mask attenuated against the granted mask to empty.</summary>
    AttenuatedToEmpty = 6,

    /// <summary>Denied — the named subject does not exist.</summary>
    NoSuchSubject = 7,

    /// <summary>Denied — a per-tick budget was spent.</summary>
    QuotaExhausted = 8,

    /// <summary>Denied — the handle's generation no longer matches the live table. Deliberately distinct from
    /// <see cref="NoHold"/>: withdrawn and never-granted are different states.</summary>
    StaleHandle = 9,

    /// <summary>Allowed and DONE — a <c>SubmitMutation</c> act cleared every dispatch-door stage and the mutation
    /// composed, revalidated, and swapped into the live document. The addon mutation seam's own positive outcome;
    /// distinct from the bare authority <see cref="HeldConcrete"/>/<see cref="HeldWildcard"/>/<see cref="HeldAsReserver"/>
    /// trio, which answer "may this happen" for a query/ask, never "this happened".</summary>
    Applied = 10,

    /// <summary>Denied — a <c>SubmitMutation</c> payload failed pointer safety (an out-of-bounds or overflowing
    /// <c>ptr</c>/<c>len</c>) or per-kind decode (invalid UTF-8/JSON, a duplicate or unknown member, a non-finite or
    /// wrongly-signed scalar, depth beyond the decoder's bound). The dispatch door's own budget is already spent by
    /// the time this fires — a malformed payload still costs its dispatch, so a guest cannot probe the decoder for
    /// free.</summary>
    MalformedPayload = 11,

    /// <summary>Denied — a <c>SubmitMutation</c> act named a length exceeding
    /// <see cref="AddonAbi.MaxMutationPayloadBytes"/>, refused BEFORE a single byte was read out of guest linear
    /// memory (the pointer-safety stage's own ceiling, distinct from <see cref="MalformedPayload"/>: this is a size
    /// refusal, never a content one).</summary>
    PayloadTooLarge = 12,

    /// <summary>Denied — a <c>SubmitMutation</c> act decoded to a WELL-FORMED mutation that the document-apply
    /// pipeline itself refused (the SAME compose→revalidate→swap gate a console-submitted mutation runs through —
    /// an unknown reference, a capacity ceiling, a malformed cross-row invariant). Distinct from every dispatch-door
    /// refusal above: this is the one outcome the door cannot predict from the wire alone, because it depends on
    /// the WHOLE document's state at apply time, not on the act's own shape or the grant table.</summary>
    Rejected = 13,
}

/// <summary>The addon ABI's pinned allowed/denied predicate over <see cref="AddonVerdict"/>, generated beside
/// the wire values in both languages so it cannot drift (see <c>AddonAbiRustPort</c>'s generated
/// <c>Verdict::is_allowed</c>).</summary>
public static class AddonVerdicts {
    /// <summary>Indicates whether <paramref name="verdict"/> represents an allowed outcome.</summary>
    /// <param name="verdict">The verdict to classify.</param>
    /// <returns><see langword="true"/> for <see cref="AddonVerdict.HeldConcrete"/>,
    /// <see cref="AddonVerdict.HeldWildcard"/>, <see cref="AddonVerdict.HeldAsReserver"/>, or
    /// <see cref="AddonVerdict.Applied"/>; otherwise <see langword="false"/>.</returns>
    public static bool IsAllowed(AddonVerdict verdict) {
        return (verdict is (AddonVerdict.HeldConcrete or AddonVerdict.HeldWildcard or AddonVerdict.HeldAsReserver or AddonVerdict.Applied));
    }
}
