using Puck.Assets;
using Puck.Assets.Documents;

namespace Puck.Launcher.Release;

/// <summary>
/// THE strict validate → normalize → canonicalize boundary every <see cref="OfficialManifest"/> crosses before it is
/// written or trusted — the official-tree family's adapter over <see cref="DocumentCanonicalizer"/>, mirroring
/// <see cref="ReleaseCanonicalizer"/>'s shape exactly.
/// </summary>
public static class OfficialCanonicalizer {
    private static readonly HashSet<string> KnownMemberNames = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "schema", "channel", "build", "worldSchemaBundle", "engine", "sources", "documents", "composed", "assets", "signature",
    };

    // A workspace name (a sources[] file, or a document name) is a forward-slash path relative to the worlds
    // directory: no backslash, no drive or scheme colon, and no empty, '.', or '..' segment (so no rooted form
    // either), so a client mounting the workspace can never place a file outside the mounted tree.
    private static bool IsWellFormedWorkspaceName(string name) => (
        !string.IsNullOrEmpty(value: name) &&
        !name.Contains(value: '\\') &&
        !name.Contains(value: ':') &&
        name.Split(separator: '/').All(predicate: static segment => (
            (segment.Length > 0) &&
            (segment != ".") &&
            (segment != "..")
        ))
    );
    private static void ValidateAssets(IReadOnlyList<OfficialAssetEntry> assets, List<DocumentValidationError> errors) {
        if (assets is null) {
            errors.Add(item: new(
                Message: "an assets list is required (empty when none apply).",
                Path: "assets"
            ));

            return;
        }

        var seen = new HashSet<(string Family, string Name)>();

        for (var i = 0; (i < assets.Count); i++) {
            var entry = assets[i];
            var path = $"assets[{i}]";

            if (!AssetRowFamilies.All.Contains(item: entry.Family)) {
                errors.Add(item: new(
                    Message: $"'{entry.Family}' is not a recognized family ({string.Join(
                        separator: " | ",
                        values: AssetRowFamilies.All
                    )}).",
                    Path: $"{path}.family"
                ));
            }

            if (string.IsNullOrWhiteSpace(value: entry.Name)) {
                errors.Add(item: new(
                    Message: "a name is required.",
                    Path: $"{path}.name"
                ));
            } else if (!seen.Add(item: (entry.Family, entry.Name))) {
                errors.Add(item: new(
                    Message: $"family '{entry.Family}' name '{entry.Name}' is declared more than once.",
                    Path: $"{path}.name"
                ));
            }

            if (string.IsNullOrWhiteSpace(value: entry.Source)) {
                errors.Add(item: new(
                    Message: "a source is required.",
                    Path: $"{path}.source"
                ));
            }

            ValidateObject(
                contentType: entry.ContentType,
                errors: errors,
                hash: entry.Hash,
                path: path,
                pathValue: entry.Path,
                size: entry.Size
            );

            if (
                (entry.Pin is { Length: > 0 }) &&
                !ContentPin.TryParse(
                pin: out _,
                text: entry.Pin
            )
            ) {
                errors.Add(item: new(
                    Message: $"'{entry.Pin}' is not a well-formed sha256/<64 lowercase hex> pin.",
                    Path: $"{path}.pin"
                ));
            }
        }
    }
    private static void ValidateBuild(OfficialBuildInfo build, List<DocumentValidationError> errors) {
        if (build is null) {
            errors.Add(item: new(
                Message: "a build is required.",
                Path: "build"
            ));

            return;
        }

        if (string.IsNullOrWhiteSpace(value: build.Commit)) {
            errors.Add(item: new(
                Message: "a commit is required.",
                Path: "build.commit"
            ));
        }

        if (string.IsNullOrWhiteSpace(value: build.Generator)) {
            errors.Add(item: new(
                Message: "a generator is required.",
                Path: "build.generator"
            ));
        }

        if (string.IsNullOrWhiteSpace(value: build.WorldSchema)) {
            errors.Add(item: new(
                Message: "a worldSchema is required.",
                Path: "build.worldSchema"
            ));
        }
    }
    private static void ValidateComposed(IReadOnlyList<OfficialComposedEntry> composed, IReadOnlySet<string> documentNames, List<DocumentValidationError> errors) {
        if (
            (composed is null) ||
            (composed.Count == 0)
        ) {
            errors.Add(item: new(
                Message: "at least one composed world is required.",
                Path: "composed"
            ));

            return;
        }

        var seenIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var i = 0; (i < composed.Count); i++) {
            var entry = composed[i];
            var path = $"composed[{i}]";

            if (string.IsNullOrWhiteSpace(value: entry.DocumentId)) {
                errors.Add(item: new(
                    Message: "a documentId is required.",
                    Path: $"{path}.documentId"
                ));
            } else if (!seenIds.Add(item: entry.DocumentId)) {
                errors.Add(item: new(
                    Message: $"documentId '{entry.DocumentId}' is declared more than once.",
                    Path: $"{path}.documentId"
                ));
            }

            if (!IsWellFormedWorkspaceName(name: entry.Name)) {
                errors.Add(item: new(
                    Message: $"'{entry.Name}' is not a forward-slash document name relative to the worlds directory with no empty, '.', or '..' segment.",
                    Path: $"{path}.name"
                ));
            } else if (!documentNames.Contains(value: entry.Name)) {
                errors.Add(item: new(
                    Message: $"'{entry.Name}' does not name a document in documents.",
                    Path: $"{path}.name"
                ));
            }

            ValidateObject(
                contentType: entry.ContentType,
                errors: errors,
                hash: entry.Hash,
                path: path,
                pathValue: entry.Path,
                size: entry.Size
            );

            if (
                (entry.Pin is { Length: > 0 }) &&
                !AssetContentHash.TryParse(
                hash: out _,
                text: entry.Pin
            )
            ) {
                errors.Add(item: new(
                    Message: $"'{entry.Pin}' is not a well-formed sha256-64/<16 lowercase hex> pin.",
                    Path: $"{path}.pin"
                ));
            }
        }
    }
    // Returns every well-formed, distinct document name, the set each composed[].name must resolve into. A document
    // name is unique ignoring case (DocumentName), so two names differing only in letter case are refused as one name
    // carried twice; a composed[].name still resolves by its exact spelling.
    private static HashSet<string> ValidateDocuments(IReadOnlyList<OfficialDocumentEntry> documents, IReadOnlySet<string> sourceNames, List<DocumentValidationError> errors) {
        var seenNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var carriedBy = new Dictionary<string, OfficialDocumentEntry>(comparer: DocumentName.Comparer);

        if (
            (documents is null) ||
            (documents.Count == 0)
        ) {
            errors.Add(item: new(
                Message: "at least one document is required.",
                Path: "documents"
            ));

            return seenNames;
        }

        for (var i = 0; (i < documents.Count); i++) {
            var entry = documents[i];
            var path = $"documents[{i}]";

            if (!IsWellFormedWorkspaceName(name: entry.Name)) {
                errors.Add(item: new(
                    Message: $"'{entry.Name}' is not a forward-slash document name relative to the worlds directory with no empty, '.', or '..' segment.",
                    Path: $"{path}.name"
                ));
            } else if (carriedBy.TryGetValue(
                key: entry.Name,
                value: out var held
            )) {
                errors.Add(item: new(
                    Message: DocumentName.Collision(
                        heldFile: held.Source,
                        heldName: held.Name,
                        otherFile: entry.Source,
                        otherName: entry.Name
                    ),
                    Path: $"{path}.name"
                ));
            } else {
                carriedBy.Add(
                    key: entry.Name,
                    value: entry
                );
                seenNames.Add(item: entry.Name);
            }

            if (string.IsNullOrWhiteSpace(value: entry.Source)) {
                errors.Add(item: new(
                    Message: "a source is required.",
                    Path: $"{path}.source"
                ));
            } else if (!sourceNames.Contains(value: entry.Source)) {
                errors.Add(item: new(
                    Message: $"'{entry.Source}' does not name a file in sources.",
                    Path: $"{path}.source"
                ));
            }

            if (!OfficialDocumentRoles.All.Contains(item: entry.Role)) {
                errors.Add(item: new(
                    Message: $"'{entry.Role}' is not a recognized role ({string.Join(
                        separator: " | ",
                        values: OfficialDocumentRoles.All
                    )}).",
                    Path: $"{path}.role"
                ));
            }

            foreach (var import in (entry.Imports ?? [])) {
                if (string.IsNullOrWhiteSpace(value: import.Document)) {
                    errors.Add(item: new(
                        Message: "a document path is required.",
                        Path: $"{path}.imports[].document"
                    ));
                }
            }

            if ((entry.Exports ?? []).Any(predicate: static name => string.IsNullOrWhiteSpace(value: name))) {
                errors.Add(item: new(
                    Message: "an export entry must not be empty.",
                    Path: $"{path}.exports"
                ));
            }

            ValidateObject(
                contentType: entry.ContentType,
                errors: errors,
                hash: entry.Hash,
                path: path,
                pathValue: entry.Path,
                size: entry.Size
            );

            if (
                (entry.Pin is { Length: > 0 }) &&
                !AssetContentHash.TryParse(
                hash: out _,
                text: entry.Pin
            )
            ) {
                errors.Add(item: new(
                    Message: $"'{entry.Pin}' is not a well-formed sha256-64/<16 lowercase hex> pin.",
                    Path: $"{path}.pin"
                ));
            }
        }

        return seenNames;
    }
    private static void ValidateEngine(OfficialEngine engine, List<DocumentValidationError> errors) {
        if (engine is null) {
            errors.Add(item: new(
                Message: "an engine is required.",
                Path: "engine"
            ));

            return;
        }

        if (string.IsNullOrWhiteSpace(value: engine.Entry)) {
            errors.Add(item: new(
                Message: "an entry is required.",
                Path: "engine.entry"
            ));
        }

        if (
            (engine.Files is null) ||
            (engine.Files.Count == 0)
        ) {
            errors.Add(item: new(
                Message: "at least one file is required.",
                Path: "engine.files"
            ));

            return;
        }

        if (
            (engine.Entry is { Length: > 0 }) &&
            !engine.Files.Any(predicate: file => string.Equals(
            a: file.Name,
            b: engine.Entry,
            comparisonType: StringComparison.Ordinal
        ))
        ) {
            errors.Add(item: new(
                Message: $"'{engine.Entry}' does not name a file in engine.files.",
                Path: "engine.entry"
            ));
        }

        var seenNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var i = 0; (i < engine.Files.Count); i++) {
            var file = engine.Files[i];
            var path = $"engine.files[{i}]";

            if (string.IsNullOrWhiteSpace(value: file.Name)) {
                errors.Add(item: new(
                    Message: "a name is required.",
                    Path: $"{path}.name"
                ));
            } else if (!seenNames.Add(item: file.Name)) {
                errors.Add(item: new(
                    Message: $"name '{file.Name}' is declared more than once.",
                    Path: $"{path}.name"
                ));
            }

            ValidateObject(
                contentType: file.ContentType,
                errors: errors,
                hash: file.Hash,
                path: path,
                pathValue: file.Path,
                size: file.Size
            );
        }
    }
    private static void ValidateObject(string contentType, List<DocumentValidationError> errors, string hash, string path, string pathValue, long size) {
        if (
            string.IsNullOrWhiteSpace(value: pathValue) ||
            pathValue.StartsWith(value: '/') ||
            pathValue.Contains(
            comparisonType: StringComparison.Ordinal,
            value: ".."
        )
        ) {
            errors.Add(item: new(
                Message: "a path must be a non-empty relative path with no '..' segment.",
                Path: $"{path}.path"
            ));
        }

        if (!ContentPin.TryParse(
            pin: out _,
            text: hash
        )) {
            errors.Add(item: new(
                Message: $"'{hash}' is not a well-formed sha256/<64 lowercase hex> content hash.",
                Path: $"{path}.hash"
            ));
        }

        if (size < 0) {
            errors.Add(item: new(
                Message: "must not be negative.",
                Path: $"{path}.size"
            ));
        }

        if (string.IsNullOrWhiteSpace(value: contentType)) {
            errors.Add(item: new(
                Message: "a contentType is required.",
                Path: $"{path}.contentType"
            ));
        }
    }
    // Returns every well-formed, distinct source name, the set each documents[].source must resolve into. A source
    // name is a path, not a document name, but it is unique under the same comparer for the same reason: a client
    // mounting the workspace on a case-insensitive file system holds two names differing only in letter case as one
    // file. A documents[].source still resolves by its exact spelling.
    private static HashSet<string> ValidateSources(IReadOnlyList<OfficialSourceEntry> sources, List<DocumentValidationError> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var spelledAs = new Dictionary<string, string>(comparer: DocumentName.Comparer);

        if (sources is null) {
            errors.Add(item: new(
                Message: "a sources list is required.",
                Path: "sources"
            ));

            return names;
        }

        for (var i = 0; (i < sources.Count); i++) {
            var entry = sources[i];
            var path = $"sources[{i}]";

            if (!IsWellFormedWorkspaceName(name: entry.Name)) {
                errors.Add(item: new(
                    Message: $"'{entry.Name}' is not a forward-slash path relative to the worlds directory with no empty, '.', or '..' segment.",
                    Path: $"{path}.name"
                ));
            } else if (spelledAs.TryGetValue(
                key: entry.Name,
                value: out var held
            )) {
                errors.Add(item: new(
                    Message: (string.Equals(
                        a: held,
                        b: entry.Name,
                        comparisonType: StringComparison.Ordinal
                    )
                        ? $"name '{entry.Name}' is declared more than once."
                        : $"'{held}' and '{entry.Name}' differ only in letter case; a workspace path is unique ignoring case, since a case-insensitive file system holds both as one file."
                    ),
                    Path: $"{path}.name"
                ));
            } else {
                spelledAs.Add(
                    key: entry.Name,
                    value: entry.Name
                );
                names.Add(item: entry.Name);
            }

            ValidateObject(
                contentType: entry.ContentType,
                errors: errors,
                hash: entry.Hash,
                path: path,
                pathValue: entry.Path,
                size: entry.Size
            );
        }

        return names;
    }

    /// <summary>THE full pipeline: validates schema + structural invariants (throwing on either), normalizes the
    /// self-heal, then serializes to canonical UTF-8 bytes and hashes them through
    /// <see cref="DocumentCanonicalizer.Canonicalize{TDocument}(TDocument, System.Text.Json.JsonSerializerOptions?)"/>.</summary>
    /// <param name="document">The document to canonicalize.</param>
    /// <param name="source">An optional source label for a validation-failure message.</param>
    /// <returns>The validated, normalized document plus its canonical bytes and hash.</returns>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a structural invariant.</exception>
    public static CanonicalDocument<OfficialManifest> Canonicalize(OfficialManifest document, string? source = null) {
        ValidateOrThrow(
            document: document,
            source: source
        );

        return DocumentCanonicalizer.Canonicalize(document: Normalize(document: document));
    }
    /// <summary>Normalizes an already-schema-valid document: sorts <see cref="OfficialManifest.Engine"/>'s files by
    /// name, <see cref="OfficialManifest.Sources"/>, <see cref="OfficialManifest.Documents"/>, and
    /// <see cref="OfficialManifest.Composed"/> by name, and
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
            Files = document.Engine.Files.OrderBy(
            keySelector: static file => file.Name,
            comparer: StringComparer.Ordinal
        ).ToList(),
        });
        var documents = document.Documents
            .Select(selector: static entry => entry with {
                Exports = entry.Exports.Distinct().OrderBy(
            keySelector: static name => name,
            comparer: StringComparer.Ordinal
        ).ToList(),
            })
            .OrderBy(
            keySelector: static entry => entry.Name,
            comparer: StringComparer.Ordinal
        )
            .ToList();
        var sources = document.Sources.OrderBy(
            keySelector: static entry => entry.Name,
            comparer: StringComparer.Ordinal
        ).ToList();
        var composed = document.Composed.OrderBy(
            keySelector: static entry => entry.Name,
            comparer: StringComparer.Ordinal
        ).ToList();
        var assets = document.Assets
            .OrderBy(
            keySelector: static entry => entry.Family,
            comparer: StringComparer.Ordinal
        )
            .ThenBy(
            keySelector: static entry => entry.Name,
            comparer: StringComparer.Ordinal
        )
            .ToList();

        return (document with {
            Assets = assets,
            Composed = composed,
            Documents = documents,
            Engine = engine,
            Schema = OfficialManifest.CurrentSchema,
            Sources = sources,
        });
    }
    /// <summary>Validates a document's schema and structural invariants in one pass — every violation is collected
    /// rather than throwing on the first. An absent or foreign <see cref="OfficialManifest.Schema"/> short-circuits
    /// to that one violation.</summary>
    /// <param name="document">The document to validate, as constructed — not yet normalized.</param>
    /// <returns>Every violation found; empty when the document is a valid <c>puck.official.manifest.v1</c> value.</returns>
    public static IReadOnlyList<DocumentValidationError> Validate(OfficialManifest document) {
        ArgumentNullException.ThrowIfNull(document);

        if (DocumentCanonicalizer.SchemaViolationMessage(
            declared: document.Schema,
            recognized: OfficialManifest.CurrentSchema
        ) is { } schemaViolation) {
            return [new DocumentValidationError(
                    Message: schemaViolation,
                    Path: "schema"
                )];
        }

        var errors = new List<DocumentValidationError>();

        if (string.IsNullOrWhiteSpace(value: document.Channel)) {
            errors.Add(item: new(
                Message: "a channel is required.",
                Path: "channel"
            ));
        }

        ValidateBuild(
            build: document.Build,
            errors: errors
        );

        if (document.WorldSchemaBundle is null) {
            errors.Add(item: new(
                Message: "a worldSchemaBundle is required.",
                Path: "worldSchemaBundle"
            ));
        } else {
            ValidateObject(
                contentType: document.WorldSchemaBundle.ContentType,
                errors: errors,
                hash: document.WorldSchemaBundle.Hash,
                path: "worldSchemaBundle",
                pathValue: document.WorldSchemaBundle.Path,
                size: document.WorldSchemaBundle.Size
            );
        }

        ValidateEngine(
            engine: document.Engine,
            errors: errors
        );

        var sourceNames = ValidateSources(
            errors: errors,
            sources: document.Sources
        );

        var documentNames = ValidateDocuments(
            documents: document.Documents,
            errors: errors,
            sourceNames: sourceNames
        );

        ValidateComposed(
            composed: document.Composed,
            documentNames: documentNames,
            errors: errors
        );
        ValidateAssets(
            assets: document.Assets,
            errors: errors
        );

        DocumentCanonicalizer.ValidateExtensions(
            addError: (path, message) => errors.Add(item: new(
                Message: message,
                Path: path
            )),
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
        DocumentCanonicalizer.ThrowIfInvalid(
            errors: Validate(document: document),
            source: source
        );
}
