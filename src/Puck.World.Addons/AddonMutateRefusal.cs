using Puck.Scripting;

namespace Puck.World.Addons;

/// <summary>The <c>addon.mutate</c> door's cataloged refusal reasons — one member per distinct way
/// <see cref="WorldAddonRuntime"/>'s <c>ResolveMutations</c> six-stage dispatch gate refuses a <c>SubmitMutation</c> act,
/// discovered by <c>world.refusals</c> the same DISCOVERED-NOT-HAND-KEPT way every other cataloged door is (see
/// <see cref="RefusalAttribute"/>'s own remarks). This enum is the World-side id a wire <see cref="AddonVerdict"/>
/// alone cannot carry: the wire value is a closed, cross-language ABI shape shared with the guest (renumbering it
/// is a re-key), while THIS enum exists purely so the door has a catalogable, human-legible reason per member —
/// <see cref="AddonMutateRefusals.ToVerdict"/> is the one-directional map from a decided reason onto the wire value
/// actually staged into the guest's reserved answer cell.</summary>
public enum AddonMutateRefusal : byte {
    /// <summary>Denied — the handle did not resolve to a live Mutate/section slot (revoked, re-sorted, or
    /// fabricated).</summary>
    [Refusal(door: "addon.mutate", condition: "the act's handle does not resolve to a live Mutate/section slot", kind: RefusalKind.Verdict)]
    StaleHandle,

    /// <summary>Denied — the addon's own manifest never requested Mutate over this section (requests ∧ grants).</summary>
    [Refusal(door: "addon.mutate", condition: "the addon's manifest never requested mutate over the resolved section", kind: RefusalKind.Verdict)]
    NotRequested,

    /// <summary>Denied — the DECIDING row's verb mask does not admit the named mutation-kind ordinal. A row carrying
    /// NO mask is a different case and never reaches this door: an absent mask is FULL REACH at the admission
    /// predicate, and the grant door refuses a maskless untrusted <c>Mutate</c>/<c>section:&lt;name&gt;</c> row
    /// outright, so an addon can never hold one.</summary>
    [Refusal(door: "addon.mutate", condition: "the deciding grant row's verb mask does not admit the named mutation-kind ordinal", kind: RefusalKind.Verdict)]
    MaskedKind,

    /// <summary>Denied — the grant row is held but carries no recorded dispatch budget (an authority-table
    /// inconsistency, unreachable by construction: the grant door requires one on every untrusted Mutate hold).</summary>
    [Refusal(door: "addon.mutate", condition: "the grant row carries no recorded dispatch budget (unreachable by construction)", kind: RefusalKind.Verdict)]
    MissingBudget,

    /// <summary>Denied — the per-(addon, section) dispatch budget is spent for this tick.</summary>
    [Refusal(door: "addon.mutate", condition: "the per-tick dispatch budget for this (addon, section) is spent", kind: RefusalKind.Verdict)]
    DispatchBudgetExhausted,

    /// <summary>Denied — the named payload length exceeds <see cref="AddonAbi.MaxMutationPayloadBytes"/>, refused
    /// before a single byte was read out of guest memory.</summary>
    [Refusal(door: "addon.mutate", condition: "the named payload length exceeds the single-payload byte ceiling", kind: RefusalKind.ProtocolFault)]
    PayloadTooLarge,

    /// <summary>Denied — this addon's running per-tick mutation-payload byte total would exceed
    /// <see cref="AddonAbi.MaxMutationBytesPerTickPerAddon"/>.</summary>
    [Refusal(door: "addon.mutate", condition: "the addon's per-tick mutation-payload byte total would exceed its own ceiling", kind: RefusalKind.Verdict)]
    AddonByteBudgetExhausted,

    /// <summary>Denied — the GLOBAL per-tick mutation-payload byte total (every addon summed) would exceed
    /// <see cref="AddonAbi.MaxMutationBytesPerTickAllAddons"/>.</summary>
    [Refusal(door: "addon.mutate", condition: "the global per-tick mutation-payload byte total (all addons summed) would exceed its ceiling", kind: RefusalKind.Verdict)]
    GlobalByteBudgetExhausted,

    /// <summary>Denied — the pointer-safety copy failed (an out-of-bounds or overflowing guest-memory range).</summary>
    [Refusal(door: "addon.mutate", condition: "the ptr/len range is out of bounds against the guest's actual linear memory", kind: RefusalKind.ProtocolFault)]
    PointerOutOfBounds,

    /// <summary>Denied — the copied payload failed per-kind decode (invalid JSON, a duplicate/unknown member, a
    /// non-finite or wrongly-signed scalar, or an ordinal this decoder has no entry for).</summary>
    [Refusal(door: "addon.mutate", condition: "the copied payload failed per-kind hand-walked JSON decode", kind: RefusalKind.ProtocolFault)]
    DecodeFailed,

    /// <summary>Denied — the decoded, well-formed mutation was itself refused by the document-apply pipeline (the
    /// SAME compose→revalidate→swap gate a console mutation runs through).</summary>
    [Refusal(door: "addon.mutate", condition: "the decoded mutation was refused by the document compose/revalidate/swap pipeline", kind: RefusalKind.Verdict)]
    ApplyRejected,
}
/// <summary>The one-directional map from a decided <see cref="AddonMutateRefusal"/> reason onto the wire
/// <see cref="AddonVerdict"/> actually staged into the guest's reserved answer cell.</summary>
public static class AddonMutateRefusals {
    /// <summary>Maps a decided refusal reason onto its pinned wire verdict. Total over
    /// <see cref="AddonMutateRefusal"/> — a new reason fails loudly here rather than crossing as garbage.</summary>
    /// <param name="reason">The decided refusal reason.</param>
    /// <returns>The wire verdict.</returns>
    public static AddonVerdict ToVerdict(AddonMutateRefusal reason) {
        return reason switch {
            AddonMutateRefusal.StaleHandle => AddonVerdict.StaleHandle,
            AddonMutateRefusal.NotRequested => AddonVerdict.AttenuatedToEmpty,
            AddonMutateRefusal.MaskedKind => AddonVerdict.AttenuatedToEmpty,
            AddonMutateRefusal.MissingBudget => AddonVerdict.NoHold,
            AddonMutateRefusal.DispatchBudgetExhausted => AddonVerdict.QuotaExhausted,
            AddonMutateRefusal.PayloadTooLarge => AddonVerdict.PayloadTooLarge,
            AddonMutateRefusal.AddonByteBudgetExhausted => AddonVerdict.QuotaExhausted,
            AddonMutateRefusal.GlobalByteBudgetExhausted => AddonVerdict.QuotaExhausted,
            AddonMutateRefusal.PointerOutOfBounds => AddonVerdict.MalformedPayload,
            AddonMutateRefusal.DecodeFailed => AddonVerdict.MalformedPayload,
            AddonMutateRefusal.ApplyRejected => AddonVerdict.Rejected,
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(reason),
            actualValue: reason,
            message: "unmapped addon.mutate refusal — the wire mapping must be extended deliberately, never defaulted"
        ),
        };
    }
}
