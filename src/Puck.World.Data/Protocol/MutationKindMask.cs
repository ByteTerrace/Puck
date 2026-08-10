using System.Text;

namespace Puck.World.Protocol;

/// <summary>The <see cref="WorldMutation"/> kind ordinals (see <see cref="MutationKindAttribute.Ordinal"/>,
/// <c>0..</c><see cref="WorldMutationKindCatalog.MaxOrdinal"/>) a <see cref="WorldGrant.KindMask"/> row admits —
/// the same <see cref="ChannelReachMask"/>-style closed bitset this codebase already uses for a per-ordinal
/// reach/consent grant payload, applied here to the mutation dispatch door instead of the channel fold.
/// A grant that holds <see cref="WorldCapability.Mutate"/> over a section (or <see cref="WorldCapability.Edit"/> over
/// a concrete state row) answers "may this principal touch the section/row at all"; the mask answers the narrower
/// "which kinds within it" — the same attenuation shape the addon wire already uses for capability requests
/// (<c>requested ∧ granted</c>): a bit this mask does not carry is a kind the row may never dispatch, no matter how
/// broad the underlying hold is.
/// <para><b>This type's ordinals are mutation-kind ordinals and nothing else.</b> Its sibling
/// <see cref="DocumentWriteMask"/> is a bitset over <see cref="WorldDocumentWriteKind"/> — a different vocabulary on
/// a different door (the cross-document durable-state write-back channel), which still rides a 64-bit lane; this one
/// no longer does. The two are distinct types so no call site can confuse them: handing one where the other is
/// expected does not compile.</para></summary>
/// <remarks>The lane is <see cref="UInt128"/> because the kind catalog outgrew 64: ordinals 0-63 filled it exactly,
/// and a 65th kind on a <c>ulong</c> lane does not overflow loudly — <c>1UL &lt;&lt; 64</c> masks the shift count to
/// <c>1UL &lt;&lt; 0</c> and silently admits <c>UpsertKit</c> instead. Widening was therefore the only way to add a
/// kind at all. Bits 0-63 keep their exact meanings and their exact wire positions; nothing was renumbered.</remarks>
/// <param name="Bits">The raw 128-bit lane, one bit per declared mutation-kind ordinal.</param>
public readonly record struct MutationKindMask(UInt128 Bits) {
    /// <summary>Gets the empty mask — admits no kind. The grant door refuses a row that would resolve to exactly this
    /// (an admitted-but-inert bit set is a grant that lies; see <c>Server.WorldGrants</c>'s own remarks for the
    /// ceiling/budget precedent this mirrors).</summary>
    public static MutationKindMask Empty { get; } = new(Bits: UInt128.Zero);

    /// <summary>Gets a value indicating whether this mask admits no kind at all.</summary>
    public bool IsEmpty => (Bits == UInt128.Zero);

    /// <summary>Determines whether <paramref name="ordinal"/> is admitted.</summary>
    /// <param name="ordinal">The declared mutation-kind ordinal.</param>
    /// <returns><see langword="true"/> when the ordinal's bit is set.</returns>
    public bool Contains(int ordinal) => ((Bits & Bit(ordinal: ordinal)) != UInt128.Zero);

    /// <summary>Returns the mask with <paramref name="ordinal"/> additionally admitted.</summary>
    /// <param name="ordinal">The declared mutation-kind ordinal to add.</param>
    /// <returns>The widened mask.</returns>
    public MutationKindMask With(int ordinal) => new(Bits: (Bits | Bit(ordinal: ordinal)));

    // The ONE place an ordinal becomes a bit. UInt128's shift masks its count to 0..127 exactly as ulong's masks to
    // 0..63, so an out-of-range ordinal would alias a real kind rather than throw — the precise failure that made a
    // 65th kind silently admit UpsertKit. An ordinal outside the lane therefore resolves to NO bit here, so it can
    // never be mistaken for an admitted one; the catalog refuses such an ordinal at Discover() time, and this is the
    // second line of defence rather than the first.
    private static UInt128 Bit(int ordinal) {
        return (((uint)ordinal < 128u) ? (UInt128.One << ordinal) : UInt128.Zero);
    }

    /// <summary>Returns the intersection with <paramref name="other"/> — the ordinals both masks admit. Used to bound a
    /// grant-authored mask against a section's own declared kind-set (<see cref="WorldMutationKindCatalog.KindsOf"/>):
    /// a bit this meet drops was never legitimately admissible in the first place, never a live attenuation the
    /// dispatch door re-derives per call.</summary>
    /// <param name="other">The mask to intersect with.</param>
    /// <returns>The intersection.</returns>
    public MutationKindMask Meet(MutationKindMask other) => new(Bits: (Bits & other.Bits));

    /// <summary>Describes the admitted kinds by their declared record names, comma-separated
    /// (<c>UpsertStateCell,RemoveStateCell</c>) — the same spelling <c>world.grant</c>'s own
    /// <c>verbs:&lt;name,…&gt;</c> token takes, so a read-back and the token that authored it never disagree, and a
    /// refusal can name the verb it denied rather than a hex lane nobody can decode by eye. A bit the catalog does
    /// not declare is skipped (it cannot be authored through the grant door, which refuses an inadmissible bit by
    /// name); an empty mask reads <c>&lt;none&gt;</c>.</summary>
    /// <returns>The comma-separated kind names.</returns>
    public string Describe() {
        var builder = new StringBuilder();

        foreach (var entry in WorldMutationKindCatalog.All()) {
            if (!Contains(ordinal: entry.Ordinal)) {
                continue;
            }

            _ = builder.Append(value: (builder.Length == 0) ? string.Empty : ",").Append(value: entry.Type.Name);
        }

        return ((builder.Length == 0) ? "<none>" : builder.ToString());
    }

    /// <summary>Parses the comma-separated kind-name form <see cref="Describe"/> writes — the same grammar
    /// <c>world.grant</c>'s <c>verbs:&lt;name,…&gt;</c> token takes, so a document-authored mask and a typed one
    /// canonicalize identically and neither can express a raw bit lane whose vocabulary is a guess. An unknown name
    /// refuses (naming it) rather than folding to nothing.</summary>
    /// <param name="text">The comma-separated kind names.</param>
    /// <param name="mask">The parsed mask, on success.</param>
    /// <param name="unknown">The first unrecognized name, on failure.</param>
    /// <returns><see langword="true"/> when every name resolved.</returns>
    public static bool TryParse(string? text, out MutationKindMask mask, out string unknown) {
        mask = Empty;
        unknown = string.Empty;

        if (string.IsNullOrEmpty(value: text)) {
            return false;
        }

        foreach (var name in text.Split(separator: ',', options: StringSplitOptions.None)) {
            var matched = false;

            foreach (var entry in WorldMutationKindCatalog.All()) {
                if (string.Equals(a: entry.Type.Name, b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    mask = mask.With(ordinal: entry.Ordinal);
                    matched = true;

                    break;
                }
            }

            if (!matched) {
                unknown = name;

                return false;
            }
        }

        return true;
    }
}
