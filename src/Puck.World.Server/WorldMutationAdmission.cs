using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Which gate of <see cref="WorldServer.TryAdmitMutation"/> decided a mutation's admission. One member per
/// distinct way the one admission predicate can answer, so a caller maps a decided rule onto its own refusal
/// vocabulary (the addon seam's <c>Addons.AddonMutateRefusal</c>, the apply path's loud stderr line) instead of
/// re-deciding the rule for itself.</summary>
public enum WorldMutationAdmissionRule : byte {
    /// <summary>Every gate cleared — the mutation may proceed (and its dispatch has been charged, when metered).</summary>
    Admitted,
    /// <summary>Admitted structurally, without consulting the grant table at all — the acting principal is
    /// <see cref="WorldPrincipal.World"/>, the world's own authored program (a rule's effects, a kit's generate
    /// effect). Distinct from <see cref="Admitted"/> on purpose: a read-back must be able to say the table was never
    /// asked, rather than implying a row was found.</summary>
    Structural,
    /// <summary>Denied — the principal holds no Mutate over the mutation's own document section.</summary>
    SectionDenied,
    /// <summary>Denied — the principal holds no Mutate over the mutation's own document section AND none over the
    /// concrete row the mutation names (<c>creation:&lt;id&gt;</c>/<c>placement:&lt;id&gt;</c>). Distinct from
    /// <see cref="SectionDenied"/> so the refusal can name the row the caller reached for, which is the whole
    /// diagnostic for a row-scoped grantee that addressed someone else's row.</summary>
    RowScopedDenied,
    /// <summary>Denied — the deciding Mutate/section row carries a kind mask that does not admit this mutation kind.</summary>
    MaskedKind,
    /// <summary>Denied — a row-scoped (state) mutation whose principal holds no Edit over the concrete row.</summary>
    RowDenied,
    /// <summary>Denied — the deciding Edit/state row carries a kind mask that does not admit this mutation kind.</summary>
    RowMaskedKind,
    /// <summary>Denied — an untrusted principal's Mutate row carries no recorded dispatch budget. Unreachable by
    /// construction (<c>WorldGrants.Conflicts</c> requires one on every untrusted Mutate row before it can be added),
    /// so this is an authority-table inconsistency rather than a quota event — it refuses rather than dispatching
    /// unmetered.</summary>
    MissingBudget,
    /// <summary>Denied — the untrusted principal's per-tick dispatch budget for this section is spent.</summary>
    BudgetExhausted,
}
/// <summary>The decided outcome of <see cref="WorldServer.TryAdmitMutation"/> — which rule fired and the row-level
/// evidence behind it, produced inside the decision so a caller's narration can never disagree with the door (the
/// same posture <see cref="GrantVerdict"/> carries for a bare capability check).</summary>
/// <param name="Rule">Which gate decided.</param>
/// <param name="Verdict">The capability verdict the deciding gate produced (the Mutate hold, or the Edit hold for the
/// two row-scoped rules).</param>
/// <param name="Subject">The subject the deciding gate checked — the mutation's section, its concrete state row, or,
/// for <see cref="WorldMutationAdmissionRule.RowScopedDenied"/>, the concrete creations/placements row it names.</param>
/// <param name="DecidingSubject">The subject of the row that actually decided a mask or budget gate
/// (<c>ConcreteHold</c> beats <c>WildcardHold</c>, and a row-scoped hold decides in place of the section), which is
/// the queried subject except when a wildcard row decided; for
/// <see cref="WorldMutationAdmissionRule.RowScopedDenied"/> it is the section hold that was also missing.</param>
/// <param name="Mask">The deciding row's kind mask, meaningful only for the two mask rules.</param>
/// <param name="Budget">The row's recorded per-tick dispatch budget, meaningful only for
/// <see cref="WorldMutationAdmissionRule.BudgetExhausted"/>.</param>
public readonly record struct WorldMutationAdmission(
    WorldMutationAdmissionRule Rule,
    GrantVerdict Verdict,
    GrantSubject Subject,
    GrantSubject DecidingSubject,
    MutationKindMask Mask,
    ushort Budget
) {
    /// <summary>Gets a value indicating whether every gate cleared.</summary>
    public bool IsAdmitted => (Rule is WorldMutationAdmissionRule.Admitted or WorldMutationAdmissionRule.Structural);

    /// <summary>Describes the human-legible predicate fragment naming WHY this admission refused — written to read directly
    /// after a principal's own <c>Describe()</c> ("<c>addon:x cannot mutate section:hud (…)</c>"), so both doors
    /// print the same sentence for the same decision.</summary>
    /// <returns>The refusal fragment, or <c>"admitted"</c>.</returns>
    public string Describe() => Rule switch {
        WorldMutationAdmissionRule.Admitted => "admitted",
        WorldMutationAdmissionRule.Structural => "admitted structurally — the world's own authored program is not an actor, so the grant table is never consulted for it (see WorldPrincipal.World)",
        WorldMutationAdmissionRule.SectionDenied => $"cannot mutate {Subject.Describe()} ({Verdict.DescribeDenial()})",
        WorldMutationAdmissionRule.RowScopedDenied => $"cannot mutate {Subject.Describe()} ({Verdict.DescribeDenial()}) — it holds neither that row nor {DecidingSubject.Describe()}; a section grant admits every row, a row grant admits only its own",
        WorldMutationAdmissionRule.MaskedKind => $"cannot mutate {Subject.Describe()} (mask on mutate {DecidingSubject.Describe()} admits {Mask.Describe()})",
        WorldMutationAdmissionRule.RowDenied => $"cannot edit {Subject.Describe()} ({Verdict.DescribeDenial()})",
        WorldMutationAdmissionRule.RowMaskedKind => $"cannot edit {Subject.Describe()} (mask on edit {DecidingSubject.Describe()} admits {Mask.Describe()})",
        WorldMutationAdmissionRule.MissingBudget => $"holds mutate over {Subject.Describe()} with no recorded dispatch budget — an authority-table inconsistency (unreachable by construction)",
        WorldMutationAdmissionRule.BudgetExhausted => $"exceeded its mutate/{Subject.Describe()} dispatch budget ({Budget}/tick)",
        _ => "?",
    };
}
