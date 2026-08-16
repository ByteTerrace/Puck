using System.Text;

namespace Puck.World.Protocol;

/// <summary>The operation a foreign document asks an owning document to apply to one durable slot.</summary>
public enum WorldDocumentWriteKind : byte {
    /// <summary>Replace the slot.</summary>
    Set,
    /// <summary>Add the operand to the slot.</summary>
    Add,
}
/// <summary>The <see cref="WorldDocumentWriteKind"/> operations a <see cref="WorldGrant.WriteMask"/> row admits on
/// the cross-document durable-state write-back channel — the mask <c>Server.WorldOwnedWorlds.Decide</c> checks a
/// <c>WorldDocumentSubmission</c> against before an owning world applies a foreign document's operation to
/// one of its own state rows. Legal only on a <see cref="WorldCapability.Mutate"/> row whose subject is a concrete
/// <see cref="GrantSubjectKind.State"/>: that door speaks operations (replace vs. accumulate), never mutation kinds.
/// <para>Deliberately not <see cref="MutationKindMask"/>: the two masks share a bitset shape and nothing else — this
/// one's bit 0 is <see cref="WorldDocumentWriteKind.Set"/>, that one's bit 0 is <c>UpsertKit</c>. They no longer
/// even share a width: this lane stays 64 bits because <see cref="WorldDocumentWriteKind"/> is a small closed
/// vocabulary, while <see cref="MutationKindMask"/> outgrew 64 and widened. Neither width constrains the
/// other.</para></summary>
/// <param name="Bits">The raw 64-bit lane, one bit per <see cref="WorldDocumentWriteKind"/> member.</param>
public readonly record struct DocumentWriteMask(ulong Bits) {
    /// <summary>Gets the empty mask — admits no operation. The grant door refuses a row that would resolve to exactly
    /// this (an admitted-but-inert bit set is a grant that lies).</summary>
    public static DocumentWriteMask Empty { get; } = new(Bits: 0UL);
    /// <summary>Gets every declared operation — the ceiling a row's authored mask is bounded against.</summary>
    public static DocumentWriteMask All { get; } = new(Bits: (1UL << ((int)WorldDocumentWriteKind.Set)) | (1UL << ((int)WorldDocumentWriteKind.Add)));

    /// <summary>Gets whether this mask admits no operation at all.</summary>
    public bool IsEmpty => (Bits == 0UL);

    /// <summary>Determines whether <paramref name="kind"/> is admitted.</summary>
    /// <param name="kind">The operation.</param>
    /// <returns><see langword="true"/> when the operation's bit is set.</returns>
    public bool Contains(WorldDocumentWriteKind kind) => ((Bits & (1UL << ((int)kind))) != 0UL);
    /// <summary>Describes the admitted operations by NAME, comma-separated (<c>Set,Add</c>) — the spelling
    /// <c>world.grant</c>'s own <c>writes:&lt;name,…&gt;</c> token takes. An empty mask reads
    /// <c>&lt;none&gt;</c>.</summary>
    /// <returns>The comma-separated operation names.</returns>
    public string Describe() {
        var builder = new StringBuilder();

        foreach (var kind in Enum.GetValues<WorldDocumentWriteKind>()) {
            if (!Contains(kind: kind)) {
                continue;
            }

            _ = builder.Append(value: ((builder.Length == 0)
                ? string.Empty
                : ",")).Append(value: kind.ToString());
        }

        return ((builder.Length == 0)
            ? "<none>"
            : builder.ToString()
        );
    }
    /// <summary>Returns the intersection with <paramref name="other"/> — the operations BOTH masks admit.</summary>
    /// <param name="other">The mask to intersect with.</param>
    /// <returns>The intersection.</returns>
    public DocumentWriteMask Meet(DocumentWriteMask other) => new(Bits: Bits & other.Bits);
    /// <summary>Parses the comma-separated operation-name form <see cref="Describe"/> writes — the SAME grammar
    /// <c>world.grant</c>'s <c>writes:&lt;name,…&gt;</c> token takes. An unknown name refuses (naming it).</summary>
    /// <param name="text">The comma-separated operation names.</param>
    /// <param name="mask">The parsed mask, on success.</param>
    /// <param name="unknown">The first unrecognized name, on failure.</param>
    /// <returns><see langword="true"/> when every name resolved.</returns>
    public static bool TryParse(string? text, out DocumentWriteMask mask, out string unknown) {
        mask = Empty;
        unknown = string.Empty;

        if (string.IsNullOrEmpty(value: text)) {
            return false;
        }

        foreach (var name in text.Split(
            options: StringSplitOptions.None,
            separator: ','
        )) {
            if (
                !Enum.TryParse<WorldDocumentWriteKind>(
                ignoreCase: true,
                result: out var kind,
                value: name
            ) ||
                !Enum.IsDefined(value: kind)
            ) {
                unknown = name;

                return false;
            }

            mask = mask.With(kind: kind);
        }

        return true;
    }
    /// <summary>Returns the mask with <paramref name="kind"/> additionally admitted.</summary>
    /// <param name="kind">The operation to add.</param>
    /// <returns>The widened mask.</returns>
    public DocumentWriteMask With(WorldDocumentWriteKind kind) => new(Bits: Bits | (1UL << ((int)kind)));
}
