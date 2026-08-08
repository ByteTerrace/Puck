using System.Text;

namespace Puck.World.Protocol;

/// <summary>The operation a foreign document asks an owning document to apply to one durable slot.</summary>
public enum WorldDocumentWriteKind : byte {
    /// <summary>Replace the slot.</summary>
    Set,
    /// <summary>Add the operand to the slot.</summary>
    Add,
}

/// <summary>The <see cref="WorldDocumentWriteKind"/> operations a <see cref="WorldGrant.WriteMask"/> row admits on the
/// CROSS-DOCUMENT durable-state write-back channel — the mask <c>Server.WorldOwnedWorlds.Decide</c> checks a
/// <see cref="WorldDocumentSubmission"/> against before an owning world applies a foreign document's operation to one
/// of its own state rows. Legal only on a <see cref="WorldCapability.Mutate"/> row whose subject is a concrete
/// <see cref="GrantSubjectKind.State"/>: that door speaks operations (replace vs. accumulate), never mutation kinds.
/// <para><b>Deliberately NOT <see cref="MutationKindMask"/>.</b> The two masks share a bitset SHAPE and nothing else
/// — this one's bit 0 is <see cref="WorldDocumentWriteKind.Set"/>, that one's bit 0 is <c>UpsertKit</c>. They were a
/// single <c>ulong</c> field once, read under whichever vocabulary the carrying grant's subject kind implied;
/// splitting them into two types is what makes the confusion a compile error instead of a silent mis-authorization.
/// They no longer even share a WIDTH: this lane stays 64 bits because
/// <see cref="WorldDocumentWriteKind"/> is a small closed vocabulary, while
/// <see cref="MutationKindMask"/> outgrew 64 and widened. Neither width constrains the other.</para></summary>
/// <param name="Bits">The raw 64-bit lane, one bit per <see cref="WorldDocumentWriteKind"/> member.</param>
public readonly record struct DocumentWriteMask(ulong Bits) {
    /// <summary>Gets the empty mask — admits no operation. The grant door refuses a row that would resolve to exactly
    /// this (an admitted-but-inert bit set is a grant that lies).</summary>
    public static DocumentWriteMask Empty { get; } = new(Bits: 0UL);

    /// <summary>Gets every declared operation — the ceiling a row's authored mask is bounded against.</summary>
    public static DocumentWriteMask All { get; } = new(Bits: ((1UL << (int)WorldDocumentWriteKind.Set) | (1UL << (int)WorldDocumentWriteKind.Add)));

    /// <summary>Gets whether this mask admits no operation at all.</summary>
    public bool IsEmpty => (Bits == 0UL);

    /// <summary>Determines whether <paramref name="kind"/> is admitted.</summary>
    /// <param name="kind">The operation.</param>
    /// <returns><see langword="true"/> when the operation's bit is set.</returns>
    public bool Contains(WorldDocumentWriteKind kind) => ((Bits & (1UL << (int)kind)) != 0UL);

    /// <summary>Returns the mask with <paramref name="kind"/> additionally admitted.</summary>
    /// <param name="kind">The operation to add.</param>
    /// <returns>The widened mask.</returns>
    public DocumentWriteMask With(WorldDocumentWriteKind kind) => new(Bits: (Bits | (1UL << (int)kind)));

    /// <summary>Returns the intersection with <paramref name="other"/> — the operations BOTH masks admit.</summary>
    /// <param name="other">The mask to intersect with.</param>
    /// <returns>The intersection.</returns>
    public DocumentWriteMask Meet(DocumentWriteMask other) => new(Bits: (Bits & other.Bits));

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

            _ = builder.Append(value: (builder.Length == 0) ? string.Empty : ",").Append(value: kind.ToString());
        }

        return ((builder.Length == 0) ? "<none>" : builder.ToString());
    }

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

        foreach (var name in text.Split(separator: ',', options: StringSplitOptions.None)) {
            if (!Enum.TryParse<WorldDocumentWriteKind>(value: name, ignoreCase: true, result: out var kind) || !Enum.IsDefined(value: kind)) {
                unknown = name;

                return false;
            }

            mask = mask.With(kind: kind);
        }

        return true;
    }
}

/// <summary>One tick-stamped foreign durable-state submission — the ONE door both a numeric operand and a text
/// operand cross, submitter-agnostic. The door's contract (grants + <see cref="DocumentWriteMask"/>) never varies;
/// only who submits does: today the sim itself, per-tick, for numeric <c>Counter</c>/<c>Timer</c> outputs
/// (<c>Server.WorldServer.Step</c>); tomorrow a player-initiated text delivery (the whisper transport). Extending
/// this ONE shape — rather than adding a sibling text-submission door — is deliberate: two admission doors drift,
/// the predicate must stay one (see <c>Server.WorldOwnedWorlds.Decide</c>'s remarks).</summary>
/// <param name="SourceDocumentId">The asking document.</param>
/// <param name="OwnerDocumentId">The owning document.</param>
/// <param name="Tick">The source tick.</param>
/// <param name="Slot">The state row name.</param>
/// <param name="Kind">The requested operation. A <see cref="Text"/> submission admits only
/// <see cref="WorldDocumentWriteKind.Set"/> — <see cref="WorldDocumentWriteKind.Add"/> refuses by name at the door
/// (no concatenation-by-stealth), regardless of what the recipient's write mask admits.</param>
/// <param name="StorageKind">The durable slot's numeric representation. Ignored when <see cref="Text"/> is set — the
/// SAME asymmetry <see cref="WorldStateCell"/>'s own <c>Value</c>/<c>Text</c> pair carries: a string cannot ride a
/// numeric lane by any honest encoding, so a text submission carries its operand in the second field rather than
/// reusing the first.</param>
/// <param name="Value">The raw numeric operand. Ignored when <see cref="Text"/> is set.</param>
/// <param name="Text">The text operand for a submission against a <see cref="CellKind.Text"/> slot row, or
/// <see langword="null"/> for a numeric submission. Capped at the SAME
/// <see cref="WorldStateCapacity.MaxTextValueLength"/> refusal every other text-cell write door enforces.</param>
public readonly record struct WorldDocumentSubmission(string SourceDocumentId, string OwnerDocumentId, ulong Tick, string Slot, WorldDocumentWriteKind Kind, ActionStateKind StorageKind, long Value, string? Text = null);

/// <summary>The owning authority's visible submission verdict.</summary>
/// <param name="Submission">The request.</param>
/// <param name="Accepted">Whether it applied.</param>
/// <param name="Reason">Why it applied or was refused.</param>
public readonly record struct WorldDocumentSubmissionReceipt(WorldDocumentSubmission Submission, bool Accepted, string Reason);
