using Puck.Assets.Documents;

namespace Puck.Launcher.Release;

/// <summary>
/// THE strict validate → normalize → canonicalize boundary every <see cref="OfficialManifest"/> crosses before it is
/// written or trusted — the official-tree family's adapter over <see cref="DocumentCanonicalizer"/>, mirroring
/// <see cref="ReleaseCanonicalizer"/>'s shape exactly.
/// </summary>
public static class OfficialCanonicalizer {
    private static readonly HashSet<string> KnownMemberNames = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "schema", "channel", "build", "worldSchemaBundle", "engine", "documents", "composed", "assets", "signature",
    };

    /// <summary>Validates a document's schema and structural invariants in one pass — every violation is collected
    /// rather than throwing on the first. An absent or foreign <see cref="OfficialManifest.Schema"/> short-circuits
    /// to that one violation.</summary>
    /// <param name="document">The document to validate, as constructed — not yet normalized.</param>
    /// <returns>Every violation found; empty when the document is a valid <c>puck.official.v1</c> value.</returns>
    public static IReadOnlyList<DocumentValidationError> Validate(OfficialManifest document) {
        ArgumentNullException.ThrowIfNull(document);

        if (DocumentCanonicalizer.SchemaViolationMessage(declared: document.Schema, recognized: OfficialManifest.CurrentSchema) is { } schemaViolation) {
            return [new DocumentValidationError(Message: schemaViolation, Path: "schema")];
        }

        var errors = new List<DocumentValidationError>();

        if (string.IsNullOrWhiteSpace(value: document.Channel)) {
            errors.Add(item: new(Message: "a channel is required.", Path: "channel"));
        }

        ValidateBuild(build: document.Build, errors: errors);

        if (document.WorldSchemaBundle is null) {
            errors.Add(item: new(Message: "a worldSchemaBundle is required.", Path: "worldSchemaBundle"));
        } else {
            ValidateObject(contentType: document.WorldSchemaBundle.ContentType, errors: errors, hash: document.WorldSchemaBundle.Hash, path: "worldSchemaBundle", pathValue: document.WorldSchemaBundle.Path, size: document.WorldSchemaBundle.Size);
        }

        ValidateEngine(engine: document.Engine, errors: errors);
        ValidateDocuments(documents: document.Documents, errors: errors);
        ValidateComposed(composed: document.Composed, errors: errors);
        ValidateAssets(assets: document.Assets, errors: errors);

        DocumentCanonicalizer.ValidateExtensions(
            addError: (path, message) => errors.Add(item: new(Message: message, Path: path)),
            extensions: document.Extensions,
            knownMemberNames: KnownMemberNames
        );

        return errors;
    }
    /// <summary>Runs <see cref="Validate"/> and throws when it finds anything.</summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="source">An optional source label (a file path or channel) for the exception message.</param>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a structural invariant.</exception>
    public static void ValidateOrThrow(OfficialManifest document, string? source = null) =>
        DocumentCanonicalizer.ThrowIfInvalid(errors: Validate(document: document), source: source);
    /// <summary>Normalizes an already-schema-valid document: sorts <see cref="OfficialManifest.Engine"/>'s files by
    /// name, <see cref="OfficialManifest.Documents"/> by name, <see cref="OfficialManifest.Composed"/> by name, and
    /// <see cref="OfficialManifest.Assets"/> by (family, name) — all ordinal — so a build's own authoring/walk order
    /// never affects the canonical bytes. A document entry's own <c>imports</c> list keeps authored order (it is
    /// composition order, not a set); its <c>exports</c> list is deduplicated and sorted, since it is a flattened
    /// summary with no authored order of its own. Idempotent. Does NOT itself validate; <see cref="Canonicalize"/>
    /// always validates first.</summary>
    /// <param name="document">The document to normalize.</param>
    /// <returns>The normalized document.</returns>
    public static OfficialManifest Normalize(OfficialManifest document) {
        ArgumentNullException.ThrowIfNull(document);

        var engine = (document.Engine with {
            Files = document.Engine.Files.OrderBy(keySelector: static file => file.Name, comparer: StringComparer.Ordinal).ToList(),
        });
        var documents = document.Documents
            .Select(selector: static entry => entry with {
                Exports = entry.Exports.Distinct().OrderBy(keySelector: static name => name, comparer: StringComparer.Ordinal).ToList(),
            })
            .OrderBy(keySelector: static entry => entry.Name, comparer: StringComparer.Ordinal)
            .ToList();
        var composed = document.Composed.OrderBy(keySelector: static entry => entry.Name, comparer: StringComparer.Ordinal).ToList();
        var assets = document.Assets
            .OrderBy(keySelector: static entry => entry.Family, comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: static entry => entry.Name, comparer: StringComparer.Ordinal)
            .ToList();

        return (document with {
            Assets = assets,
            Composed = composed,
            Documents = documents,
            Engine = engine,
            Schema = OfficialManifest.CurrentSchema,
        });
    }
    /// <summary>THE full pipeline: validates schema + structural invariants (throwing on either), normalizes the
    /// self-heal, then serializes to canonical UTF-8 bytes and hashes them through
    /// <see cref="DocumentCanonicalizer.Canonicalize{TDocument}(TDocument)"/>.</summary>
    /// <param name="document">The document to canonicalize.</param>
    /// <param name="source">An optional source label for a validation-failure message.</param>
    /// <returns>The validated, normalized document plus its canonical bytes and hash.</returns>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a structural invariant.</exception>
    public static CanonicalDocument<OfficialManifest> Canonicalize(OfficialManifest document, string? source = null) {
        ValidateOrThrow(document: document, source: source);

        return DocumentCanonicalizer.Canonicalize(document: Normalize(document: document));
    }

    private static void ValidateBuild(OfficialBuildInfo build, List<DocumentValidationError> errors) {
        if (build is null) {
            errors.Add(item: new(Message: "a build is required.", Path: "build"));

            return;
        }

        if (string.IsNullOrWhiteSpace(value: build.Commit)) {
            errors.Add(item: new(Message: "a commit is required.", Path: "build.commit"));
        }

        if (string.IsNullOrWhiteSpace(value: build.Generator)) {
            errors.Add(item: new(Message: "a generator is required.", Path: "build.generator"));
        }

        if (string.IsNullOrWhiteSpace(value: build.WorldSchema)) {
            errors.Add(item: new(Message: "a worldSchema is required.", Path: "build.worldSchema"));
        }
    }
    private static void ValidateEngine(OfficialEngine engine, List<DocumentValidationError> errors) {
        if (engine is null) {
            errors.Add(item: new(Message: "an engine is required.", Path: "engine"));

            return;
        }

        if (string.IsNullOrWhiteSpace(value: engine.Entry)) {
            errors.Add(item: new(Message: "an entry is required.", Path: "engine.entry"));
        }

        if ((engine.Files is null) || (engine.Files.Count == 0)) {
            errors.Add(item: new(Message: "at least one file is required.", Path: "engine.files"));

            return;
        }

        if ((engine.Entry is { Length: > 0 }) && !engine.Files.Any(predicate: file => string.Equals(a: file.Name, b: engine.Entry, comparisonType: StringComparison.Ordinal))) {
            errors.Add(item: new(Message: $"'{engine.Entry}' does not name a file in engine.files.", Path: "engine.entry"));
        }

        var seenNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var i = 0; (i < engine.Files.Count); i++) {
            var file = engine.Files[i];
            var path = $"engine.files[{i}]";

            if (string.IsNullOrWhiteSpace(value: file.Name)) {
                errors.Add(item: new(Message: "a name is required.", Path: $"{path}.name"));
            } else if (!seenNames.Add(item: file.Name)) {
                errors.Add(item: new(Message: $"name '{file.Name}' is declared more than once.", Path: $"{path}.name"));
            }

            ValidateObject(contentType: file.ContentType, errors: errors, hash: file.Hash, path: path, pathValue: file.Path, size: file.Size);
        }
    }
    private static void ValidateDocuments(IReadOnlyList<OfficialDocumentEntry> documents, List<DocumentValidationError> errors) {
        if ((documents is null) || (documents.Count == 0)) {
            errors.Add(item: new(Message: "at least one document is required.", Path: "documents"));

            return;
        }

        var seenNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var i = 0; (i < documents.Count); i++) {
            var entry = documents[i];
            var path = $"documents[{i}]";

            if (string.IsNullOrWhiteSpace(value: entry.Name)) {
                errors.Add(item: new(Message: "a name is required.", Path: $"{path}.name"));
            } else if (!seenNames.Add(item: entry.Name)) {
                errors.Add(item: new(Message: $"name '{entry.Name}' is declared more than once.", Path: $"{path}.name"));
            }

            if (!OfficialDocumentRoles.All.Contains(item: entry.Role)) {
                errors.Add(item: new(Message: $"'{entry.Role}' is not a recognized role ({string.Join(separator: " | ", values: OfficialDocumentRoles.All)}).", Path: $"{path}.role"));
            }

            foreach (var import in (entry.Imports ?? [])) {
                if (string.IsNullOrWhiteSpace(value: import.Document)) {
                    errors.Add(item: new(Message: "a document path is required.", Path: $"{path}.imports[].document"));
                }
            }

            if ((entry.Exports ?? []).Any(predicate: static name => string.IsNullOrWhiteSpace(value: name))) {
                errors.Add(item: new(Message: "an export entry must not be empty.", Path: $"{path}.exports"));
            }

            ValidateObject(contentType: entry.ContentType, errors: errors, hash: entry.Hash, path: path, pathValue: entry.Path, size: entry.Size);

            if ((entry.Pin is { Length: > 0 }) && !IsWellFormedShortPin(pin: entry.Pin)) {
                errors.Add(item: new(Message: $"'{entry.Pin}' is not a well-formed sha256-64/<hex16> pin.", Path: $"{path}.pin"));
            }
        }
    }
    private static void ValidateComposed(IReadOnlyList<OfficialComposedEntry> composed, List<DocumentValidationError> errors) {
        if ((composed is null) || (composed.Count == 0)) {
            errors.Add(item: new(Message: "at least one composed world is required.", Path: "composed"));

            return;
        }

        var seenIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var i = 0; (i < composed.Count); i++) {
            var entry = composed[i];
            var path = $"composed[{i}]";

            if (string.IsNullOrWhiteSpace(value: entry.DocumentId)) {
                errors.Add(item: new(Message: "a documentId is required.", Path: $"{path}.documentId"));
            } else if (!seenIds.Add(item: entry.DocumentId)) {
                errors.Add(item: new(Message: $"documentId '{entry.DocumentId}' is declared more than once.", Path: $"{path}.documentId"));
            }

            if (string.IsNullOrWhiteSpace(value: entry.Name)) {
                errors.Add(item: new(Message: "a name is required.", Path: $"{path}.name"));
            }

            ValidateObject(contentType: entry.ContentType, errors: errors, hash: entry.Hash, path: path, pathValue: entry.Path, size: entry.Size);

            if ((entry.Pin is { Length: > 0 }) && !IsWellFormedShortPin(pin: entry.Pin)) {
                errors.Add(item: new(Message: $"'{entry.Pin}' is not a well-formed sha256-64/<hex16> pin.", Path: $"{path}.pin"));
            }
        }
    }
    private static void ValidateAssets(IReadOnlyList<OfficialAssetEntry> assets, List<DocumentValidationError> errors) {
        if (assets is null) {
            errors.Add(item: new(Message: "an assets list is required (empty when none apply).", Path: "assets"));

            return;
        }

        var seen = new HashSet<(string Family, string Name)>();

        for (var i = 0; (i < assets.Count); i++) {
            var entry = assets[i];
            var path = $"assets[{i}]";

            if (!OfficialAssetFamilies.All.Contains(item: entry.Family)) {
                errors.Add(item: new(Message: $"'{entry.Family}' is not a recognized family ({string.Join(separator: " | ", values: OfficialAssetFamilies.All)}).", Path: $"{path}.family"));
            }

            if (string.IsNullOrWhiteSpace(value: entry.Name)) {
                errors.Add(item: new(Message: "a name is required.", Path: $"{path}.name"));
            } else if (!seen.Add(item: (entry.Family, entry.Name))) {
                errors.Add(item: new(Message: $"family '{entry.Family}' name '{entry.Name}' is declared more than once.", Path: $"{path}.name"));
            }

            if (string.IsNullOrWhiteSpace(value: entry.Source)) {
                errors.Add(item: new(Message: "a source is required.", Path: $"{path}.source"));
            }

            ValidateObject(contentType: entry.ContentType, errors: errors, hash: entry.Hash, path: path, pathValue: entry.Path, size: entry.Size);

            if ((entry.Pin is { Length: > 0 }) && !IsWellFormedContentHash(hash: entry.Pin)) {
                errors.Add(item: new(Message: $"'{entry.Pin}' is not a well-formed sha256/<hex64> pin.", Path: $"{path}.pin"));
            }
        }
    }
    private static void ValidateObject(string contentType, List<DocumentValidationError> errors, string hash, string path, string pathValue, long size) {
        if (string.IsNullOrWhiteSpace(value: pathValue) ||
            pathValue.StartsWith(value: '/') ||
            pathValue.Contains(comparisonType: StringComparison.Ordinal, value: "..")
        ) {
            errors.Add(item: new(Message: "a path must be a non-empty relative path with no '..' segment.", Path: $"{path}.path"));
        }

        if (!IsWellFormedContentHash(hash: hash)) {
            errors.Add(item: new(Message: $"'{hash}' is not a well-formed sha256/<hex64> content hash.", Path: $"{path}.hash"));
        }

        if (size < 0) {
            errors.Add(item: new(Message: "must not be negative.", Path: $"{path}.size"));
        }

        if (string.IsNullOrWhiteSpace(value: contentType)) {
            errors.Add(item: new(Message: "a contentType is required.", Path: $"{path}.contentType"));
        }
    }
    private static bool IsWellFormedContentHash(string hash) {
        const string Prefix = "sha256/";

        return (
            (hash is not null) &&
            hash.StartsWith(comparisonType: StringComparison.Ordinal, value: Prefix) &&
            (hash.Length == (Prefix.Length + 64)) &&
            hash.AsSpan(start: Prefix.Length).ToArray().All(predicate: Uri.IsHexDigit)
        );
    }
    private static bool IsWellFormedShortPin(string pin) {
        const string Prefix = "sha256-64/";

        return (
            (pin is not null) &&
            pin.StartsWith(comparisonType: StringComparison.Ordinal, value: Prefix) &&
            (pin.Length == (Prefix.Length + 16)) &&
            pin.AsSpan(start: Prefix.Length).ToArray().All(predicate: Uri.IsHexDigit)
        );
    }
}
