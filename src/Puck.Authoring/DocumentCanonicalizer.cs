using System.Text.Json;

namespace Puck.Authoring;

/// <summary>One structural rule a document violates, collected by a family canonicalizer's <c>Validate</c> rather than
/// thrown per-violation, so a caller sees every problem in one pass instead of fixing a document one exception at a
/// time. Every document family reports through this one type.</summary>
/// <param name="Path">A dotted/indexed pointer to the offending member (e.g. <c>"patterns[2][0].note"</c>,
/// <c>"order[3]"</c>).</param>
/// <param name="Message">A human-readable description of the violation.</param>
public readonly record struct DocumentValidationError(string Path, string Message) {
    /// <inheritdoc/>
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>
/// Thrown by a family canonicalizer's <c>ValidateOrThrow</c>/<c>Canonicalize</c> when a document fails validation —
/// an absent or foreign schema, or a structural invariant the document must hold before it is safe to normalize,
/// hash, and embed. A file-backed store wraps this in an <see cref="InvalidDataException"/> (as its
/// <see cref="Exception.InnerException"/>) before it leaves the store, so it flows through the repo's existing
/// malformed-input catch convention (<c>Puck.Commands.CommandArgs.IsMalformedInput</c>) at every file-load-verb call
/// site with zero changes there, while a caller that talks to the canonicalizer directly sees this type — and its
/// structured <see cref="Errors"/> — undecorated.
/// </summary>
public sealed class DocumentValidationException : Exception {
    /// <summary>Gets every violation found in the validation pass that raised this exception (never empty).</summary>
    public IReadOnlyList<DocumentValidationError> Errors { get; }

    /// <summary>Initializes the exception from one or more validation errors.</summary>
    /// <param name="errors">The violations found (must be non-empty — an empty list never fails validation).</param>
    /// <param name="source">An optional source label (a file path or save handle) prefixed onto the message.</param>
    public DocumentValidationException(IReadOnlyList<DocumentValidationError> errors, string? source = null)
        : base(message: DocumentCanonicalizer.FormatErrors(errors: errors, source: source)) => Errors = errors;
}

/// <summary>
/// The canonical bytes of a validated, normalized document and their identity hash — the exact payload a caller
/// should persist, embed, or pin. THIS pair is the identity contract every family's inline-canonical row pins: two
/// documents hash equal if and only if their canonical bytes are equal, and a consumer embedding a document MUST
/// obtain both <c>doc</c> and <c>hash</c> from the SAME result — never hash a document by any other route and never
/// accept a hash the pipeline did not itself compute.
/// </summary>
/// <typeparam name="TDocument">The document family's record type.</typeparam>
/// <param name="Document">The validated, normalized document <see cref="Bytes"/> was serialized from.</param>
/// <param name="Bytes">The canonical UTF-8 JSON bytes: deterministic member order (declared record order, via the
/// shared <see cref="DocumentJsonOptions.Shared"/> instance) and deterministic formatting — for a given normalized
/// document these bytes never vary across calls, processes, or machines.</param>
/// <param name="Hash">The SHA-256 hex64 digest of <see cref="Bytes"/>, in
/// <see cref="Puck.Assets.ContentAddressedStore.ComputeHash"/>'s format (lowercase hex, no <c>sha256/</c> prefix) —
/// prefix it with <c>"sha256/"</c> to use directly as a <see cref="Puck.Assets.ContentAddressedStore"/> ref
/// target.</param>
public record CanonicalDocument<TDocument>(TDocument Document, byte[] Bytes, string Hash);

/// <summary>
/// The document-neutral canonicalize+hash core every family canonicalizer rides (<see cref="CreationCanonicalizer"/>,
/// <c>AudioCanonicalizer</c>, <c>SynthPatchCanonicalizer</c>): one serializer configuration produces the
/// canonical bytes, one hash routine produces the identity, one schema rule gates recognition, and one extensions rule
/// guards the unknown-member bag — so no family can fork the mechanism. A family adapter owns its CONTENT rules
/// (which members are structural invariants, what normalization self-heals) and calls down here for everything
/// document-neutral; the strict pipeline shape (Validate → Normalize → Canonicalize, never silently relabeling an
/// absent/foreign schema) is the standard each adapter implements against this core.
/// </summary>
public static class DocumentCanonicalizer {
    /// <summary>Serializes an already-validated, already-normalized document to canonical UTF-8 bytes (deterministic
    /// member order and formatting via the ONE <see cref="DocumentJsonOptions.Shared"/> instance) and hashes them.
    /// Family adapters call this LAST — after their own validate + normalize — so the bytes always describe a
    /// document its family vouches for.</summary>
    /// <typeparam name="TDocument">The document family's record type.</typeparam>
    /// <param name="document">The validated, normalized document.</param>
    /// <returns>The document plus its canonical bytes and hash.</returns>
    public static CanonicalDocument<TDocument> Canonicalize<TDocument>(TDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(value: document, options: DocumentJsonOptions.Shared);

        return new CanonicalDocument<TDocument>(Bytes: bytes, Document: document, Hash: Puck.Assets.ContentAddressedStore.ComputeHash(content: bytes));
    }

    /// <summary>The ONE strict-schema rule: an exact ordinal match against the family's recognized tag, or a
    /// standard violation message (an absent schema reads <c>"(absent)"</c> — never silently relabeled). A family's
    /// <c>Validate</c> short-circuits to this one violation, since no other check has a defined meaning against an
    /// unrecognized document shape.</summary>
    /// <param name="declared">The document's declared schema tag (null/empty = absent).</param>
    /// <param name="recognized">The family's current schema tag.</param>
    /// <returns>The violation message for the <c>"schema"</c> path, or null when the schema is recognized.</returns>
    public static string? SchemaViolationMessage(string? declared, string recognized) {
        ArgumentException.ThrowIfNullOrEmpty(recognized);

        if (string.Equals(a: declared, b: recognized, comparisonType: StringComparison.Ordinal)) {
            return null;
        }

        var schemaLabel = ((declared is { Length: > 0 } schema) ? schema : "(absent)");

        return $"declares '{schemaLabel}', not the recognized '{recognized}'.";
    }

    /// <summary>The ONE extensions rule: an unknown-member bag entry whose key shadows a KNOWN document member is not
    /// a real extension — it is a member the serializer failed to bind (a typo'd casing, a type mismatch) that would
    /// otherwise round-trip as silent data rot. Reports one violation per shadowing key through
    /// <paramref name="addError"/> (path <c>"extensions.&lt;key&gt;"</c>).</summary>
    /// <param name="extensions">The document's <c>[JsonExtensionData]</c> bag (null/empty = nothing to check).</param>
    /// <param name="knownMemberNames">The family's known wire member names (case-insensitive).</param>
    /// <param name="addError">Receives each violation as (path, message).</param>
    public static void ValidateExtensions(IDictionary<string, JsonElement>? extensions, IReadOnlySet<string> knownMemberNames, Action<string, string> addError) {
        ArgumentNullException.ThrowIfNull(knownMemberNames);
        ArgumentNullException.ThrowIfNull(addError);

        if (extensions is not { Count: > 0 }) {
            return;
        }

        foreach (var key in extensions.Keys) {
            if (knownMemberNames.Contains(item: key)) {
                addError(arg1: $"extensions.{key}", arg2: $"'{key}' shadows a known document member — not a real extension.");
            }
        }
    }

    /// <summary>Formats a validation error list into the ONE exception-message shape every family's validation
    /// exception carries (<c>'source' failed validation: path: message; path: message</c>).</summary>
    /// <typeparam name="TError">The error record type (its <see cref="object.ToString"/> renders one violation).</typeparam>
    /// <param name="errors">The violations (never empty on a real failure).</param>
    /// <param name="source">An optional source label (a file path or save handle) prefixed onto the message.</param>
    /// <returns>The formatted message.</returns>
    public static string FormatErrors<TError>(IReadOnlyList<TError> errors, string? source) {
        ArgumentNullException.ThrowIfNull(errors);

        var joined = string.Join(separator: "; ", values: errors);

        return ((source is { Length: > 0 }) ? $"'{source}' failed validation: {joined}" : joined);
    }
}
