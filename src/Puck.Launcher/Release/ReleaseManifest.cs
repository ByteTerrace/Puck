using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.Launcher.Release;

/// <summary>One file inside a release payload: its path relative to the payload root, its content hash in
/// <see cref="Puck.Assets.ContentAddressedStore"/> form (<c>sha256/&lt;hex64&gt;</c>), and its byte size.</summary>
/// <param name="Path">The file's path relative to the payload root, forward-slash separated, never absolute and
/// never carrying a <c>..</c> segment.</param>
/// <param name="Hash">The file's content hash, as <c>sha256/&lt;hex64&gt;</c>.</param>
/// <param name="Size">The file's exact byte size.</param>
public sealed record ReleasePayloadFile(string Path, string Hash, long Size);
/// <summary>One RID's complete file set for a release, plus an optional single-archive fallback for a transport
/// that cannot fetch individual files.</summary>
/// <param name="Rid">The .NET runtime identifier this payload targets (e.g. <c>win-x64</c>, <c>linux-x64</c>).</param>
/// <param name="Files">Every file the RID's install directory contains, by content hash.</param>
/// <param name="Archive">An optional single-archive form of the same payload.</param>
public sealed record ReleasePayload(string Rid, IReadOnlyList<ReleasePayloadFile> Files, ReleasePayloadFile? Archive = null);
/// <summary>The staged-rollout fraction a release is exposed to, evaluated per install via
/// <c>ReleaseRolloutBucket.IsIncluded</c> — never free-text; the client honors exactly one fixed bucketing
/// function.</summary>
/// <param name="Percent">The inclusive percentage of installs admitted, 0 through 100.</param>
public sealed record ReleaseRollout(int Percent);
/// <summary>
/// The <c>sequence</c>-route bearer claim over a release manifest's canonical bytes (the manifest with this field
/// itself held <see langword="null"/>) — the wire form of an ordinary Puck.Attestation claim, carried
/// as the codec's own transport bytes rather than a second, hand-rolled mirror of
/// <see cref="Puck.Attestation.AttestationHeader"/>. <see cref="Chain"/> holds the claim's two key-binding
/// attestations under a <see cref="Puck.Attestation.AttestationTrustMode.Vouches"/> trust anchor, or is empty under
/// a <see cref="Puck.Attestation.AttestationTrustMode.SignsDirectly"/> one.
/// </summary>
/// <param name="Claim">The base64 encoding of the codec's <c>EncodeAttestation</c> output for the claim.</param>
/// <param name="Chain">The base64-encoded key-binding attestations beneath the claim, in root-to-subject order — 0 or exactly 2 entries.</param>
public sealed record ReleaseSignature(string Claim, IReadOnlyList<string> Chain);
/// <summary>
/// The <c>puck.release.v1</c> document: what one build of a Launcher-based program's release looks like — its
/// version, its per-RID file manifest, its staged-rollout fraction, and the signed claim that lets a client verify
/// it without contacting the issuer. Code and content ride separate channels: this document names binaries only,
/// never a world or library asset.
/// </summary>
/// <param name="Schema">The document version tag (<see cref="CurrentSchema"/>).</param>
/// <param name="App">The application id this release belongs to (e.g. <c>puck.world</c>).</param>
/// <param name="Channel">The release channel (e.g. <c>stable</c>, <c>beta</c>) — an author-chosen string, not a closed enum.</param>
/// <param name="Version">The release's semantic version, including build metadata.</param>
/// <param name="StateGeneration">Bumps only when this release writes a durable data shape an older binary of the
/// same app cannot safely read back. A rollback target older than the currently-installed generation is refused.</param>
/// <param name="MinimumSupported">The oldest version this release still runs for; a client below it refuses to run
/// rather than degrade. Null means no floor is authored.</param>
/// <param name="Payloads">Every RID's file manifest this release ships.</param>
/// <param name="Rollout">The staged-rollout fraction.</param>
/// <param name="Revoked">Version strings refused outright regardless of rollout bucket.</param>
/// <param name="Notes">Short release notes text (null = none).</param>
/// <param name="Signature">The issuing claim over this manifest's unsigned canonical bytes, or <see langword="null"/> for an unsigned draft (a dry-run publish, or a canary's throwaway-chain fixture before signing).</param>
public sealed record ReleaseManifest(
    string? Schema,
    string App,
    string Channel,
    string Version,
    int StateGeneration,
    string? MinimumSupported,
    IReadOnlyList<ReleasePayload> Payloads,
    ReleaseRollout Rollout,
    IReadOnlyList<string>? Revoked,
    string? Notes,
    ReleaseSignature? Signature
) {
    /// <summary>The version tag every saved document carries.</summary>
    public const string CurrentSchema = "puck.release.v1";

    /// <summary>Gets or sets the unknown members preserved across a round-trip. Null when the document carries none.
    /// A settable (not <c>init</c>) accessor is required: System.Text.Json appends to it during deserialization.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}
/// <summary>
/// THE strict validate → normalize → canonicalize boundary every <see cref="ReleaseManifest"/> crosses before it is
/// trusted, staged, or signed — the release family's adapter over <see cref="DocumentCanonicalizer"/>.
/// </summary>
public static class ReleaseCanonicalizer {
    private static readonly HashSet<string> KnownMemberNames = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "schema", "app", "channel", "version", "stateGeneration", "minimumSupported",
        "payloads", "rollout", "revoked", "notes", "signature",
    };

    /// <summary>Validates a document's schema and structural invariants in one pass — every violation is collected
    /// rather than throwing on the first. An absent or foreign <see cref="ReleaseManifest.Schema"/> short-circuits
    /// to that one violation.</summary>
    /// <param name="document">The document to validate, as deserialized — not yet normalized.</param>
    /// <returns>Every violation found; empty when the document is a valid <c>puck.release.v1</c> value.</returns>
    public static IReadOnlyList<DocumentValidationError> Validate(ReleaseManifest document) {
        ArgumentNullException.ThrowIfNull(document);

        if (DocumentCanonicalizer.SchemaViolationMessage(declared: document.Schema, recognized: ReleaseManifest.CurrentSchema) is { } schemaViolation) {
            return [new DocumentValidationError(Message: schemaViolation, Path: "schema")];
        }

        var errors = new List<DocumentValidationError>();

        if (string.IsNullOrWhiteSpace(value: document.App)) {
            errors.Add(item: new(Message: "an app id is required.", Path: "app"));
        }

        if (string.IsNullOrWhiteSpace(value: document.Channel)) {
            errors.Add(item: new(Message: "a channel is required.", Path: "channel"));
        }

        if (string.IsNullOrWhiteSpace(value: document.Version)) {
            errors.Add(item: new(Message: "a version is required.", Path: "version"));
        }

        if (document.StateGeneration < 0) {
            errors.Add(item: new(Message: "must not be negative.", Path: "stateGeneration"));
        }

        if ((document.Payloads is null) || (document.Payloads.Count == 0)) {
            errors.Add(item: new(Message: "at least one payload is required.", Path: "payloads"));
        } else {
            var seenRids = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var i = 0; (i < document.Payloads.Count); i++) {
                ValidatePayload(errors: errors, index: i, payload: document.Payloads[i], seenRids: seenRids);
            }
        }

        if (document.Rollout is null) {
            errors.Add(item: new(Message: "a rollout is required.", Path: "rollout"));
        } else if ((document.Rollout.Percent < 0) || (document.Rollout.Percent > 100)) {
            errors.Add(item: new(Message: $"must be 0..100, was {document.Rollout.Percent}.", Path: "rollout.percent"));
        }

        for (var i = 0; (i < (document.Revoked?.Count ?? 0)); i++) {
            if (string.IsNullOrWhiteSpace(value: document.Revoked![i])) {
                errors.Add(item: new(Message: "a revoked version entry may not be empty.", Path: $"revoked[{i}]"));
            }
        }

        DocumentCanonicalizer.ValidateExtensions(
            addError: (path, message) => errors.Add(item: new(Message: message, Path: path)),
            extensions: document.Extensions,
            knownMemberNames: KnownMemberNames
        );

        return errors;
    }
    /// <summary>Runs <see cref="Validate"/> and throws when it finds anything.</summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="source">An optional source label (a file path or save handle) for the exception message.</param>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a structural invariant.</exception>
    public static void ValidateOrThrow(ReleaseManifest document, string? source = null) =>
        DocumentCanonicalizer.ThrowIfInvalid(errors: Validate(document: document), source: source);
    /// <summary>Normalizes an already-schema-valid document: sorts payloads by RID and each payload's files by path
    /// (ordinal), sorts and dedupes <see cref="ReleaseManifest.Revoked"/>, and trims notes — so an authored document
    /// canonicalizes independent of authoring order. Idempotent. Does NOT itself validate; <see cref="Canonicalize"/>
    /// always validates first.</summary>
    /// <param name="document">The document to normalize.</param>
    /// <returns>The normalized document.</returns>
    public static ReleaseManifest Normalize(ReleaseManifest document) {
        ArgumentNullException.ThrowIfNull(document);

        var payloads = document.Payloads
            .Select(selector: payload => payload with {
                Files = payload.Files.OrderBy(keySelector: file => file.Path, comparer: StringComparer.Ordinal).ToList(),
            })
            .OrderBy(keySelector: payload => payload.Rid, comparer: StringComparer.Ordinal)
            .ToList();
        var revoked = (document.Revoked ?? [])
            .Distinct()
            .OrderBy(keySelector: version => version, comparer: StringComparer.Ordinal)
            .ToList();

        return (document with {
            Notes = (string.IsNullOrWhiteSpace(value: document.Notes) ? null : document.Notes.Trim()),
            Payloads = payloads,
            Revoked = revoked,
            Schema = ReleaseManifest.CurrentSchema,
        });
    }
    /// <summary>THE full pipeline: validates schema + structural invariants (throwing on either), normalizes the
    /// self-heal, then serializes to canonical UTF-8 bytes and hashes them through
    /// <see cref="DocumentCanonicalizer.Canonicalize"/>.</summary>
    /// <param name="document">The document to canonicalize.</param>
    /// <param name="source">An optional source label for a validation-failure message.</param>
    /// <returns>The validated, normalized document plus its canonical bytes and hash.</returns>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a structural invariant.</exception>
    public static CanonicalDocument<ReleaseManifest> Canonicalize(ReleaseManifest document, string? source = null) {
        ValidateOrThrow(document: document, source: source);

        return DocumentCanonicalizer.Canonicalize(document: Normalize(document: document));
    }

    private static void ValidatePayload(List<DocumentValidationError> errors, int index, ReleasePayload payload, HashSet<string> seenRids) {
        var path = $"payloads[{index}]";

        if (string.IsNullOrWhiteSpace(value: payload.Rid)) {
            errors.Add(item: new(Message: "a rid is required.", Path: $"{path}.rid"));
        } else if (!seenRids.Add(item: payload.Rid)) {
            errors.Add(item: new(Message: $"rid '{payload.Rid}' is declared more than once.", Path: $"{path}.rid"));
        }

        if ((payload.Files is null) || (payload.Files.Count == 0)) {
            errors.Add(item: new(Message: "at least one file is required.", Path: $"{path}.files"));

            return;
        }

        var seenPaths = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var i = 0; (i < payload.Files.Count); i++) {
            ValidateFile(errors: errors, file: payload.Files[i], path: $"{path}.files[{i}]", seenPaths: seenPaths);
        }

        if (payload.Archive is { } archive) {
            ValidateFile(errors: errors, file: archive, path: $"{path}.archive", seenPaths: null);
        }
    }
    private static void ValidateFile(List<DocumentValidationError> errors, ReleasePayloadFile file, string path, HashSet<string>? seenPaths) {
        if (string.IsNullOrWhiteSpace(value: file.Path) ||
            file.Path.StartsWith(value: '/') ||
            file.Path.Contains(comparisonType: StringComparison.Ordinal, value: "..")
        ) {
            errors.Add(item: new(Message: "a file path must be a non-empty relative path with no '..' segment.", Path: $"{path}.path"));
        } else if ((seenPaths is not null) && !seenPaths.Add(item: file.Path)) {
            errors.Add(item: new(Message: $"path '{file.Path}' is declared more than once.", Path: $"{path}.path"));
        }

        if (!IsWellFormedContentHash(hash: file.Hash)) {
            errors.Add(item: new(Message: $"'{file.Hash}' is not a well-formed sha256/<hex64> content hash.", Path: $"{path}.hash"));
        }

        if (file.Size < 0) {
            errors.Add(item: new(Message: "must not be negative.", Path: $"{path}.size"));
        }
    }
    private static bool IsWellFormedContentHash(string hash) {
        const string Prefix = "sha256/";

        return (
            hash.StartsWith(comparisonType: StringComparison.Ordinal, value: Prefix) &&
            (hash.Length == (Prefix.Length + 64)) &&
            hash.AsSpan(start: Prefix.Length).ToArray().All(predicate: Uri.IsHexDigit)
        );
    }
}
