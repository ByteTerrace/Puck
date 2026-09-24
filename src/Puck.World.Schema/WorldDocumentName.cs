using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>
/// The one mapping between a world document's NAME and the files that carry it. A <c>basis</c>, an
/// <c>imports[].document</c> and a <c>references[].document</c> each name a document, never a file form: <c>"klondike"</c>,
/// <c>"../avatars/moth"</c>, <c>"shards/quilt-nw"</c>. The document itself lives in the directory the name leads to:
/// in the <c>.puck</c> source there that emits the name, whatever its stem (usually <see cref="SourceFile"/>; a
/// composition emits each world it declares), which only the transpiler's document composer can read, or else in its
/// <c>.world.json</c> document (<see cref="DocumentFile"/>), which is either a hand-authored JSON world or a build's
/// compiled output. Owned worlds
/// are stored under the same mapping, locally and in the cloud key space (<see cref="For"/>).
/// <para>A reference spelled as a file (<c>"klondike.world.json"</c>, <c>"moth.puck"</c>) is refused by name
/// (<see cref="TryValidate"/>), never read, so a document has one spelling and a compiled document can never stand in
/// for its source under a second one.</para>
/// <para>The id-to-file mapping takes a <see cref="SafeName"/> rather than a raw string, so it escapes nothing: the
/// type it arrives as has already refused every character the mapping would otherwise have had to collapse. The
/// mapping is injective into file-name strings, not into storage locations: NTFS and default APFS resolve a name
/// case-insensitively, so <c>Amber</c> and <c>amber</c> are distinct <see cref="SafeName"/>s addressing one local file
/// (a cloud object namespace is case-sensitive, so the same pair addresses two blobs there). One id therefore
/// addresses one location only under a case-insensitive uniqueness rule, held by the two doors that admit an id: the
/// world document's seed list (<c>WorldDefinitionValidator.ValidateIdentitySeeds</c>) and the catalog directory
/// itself (<c>Server.WorldOwnedWorlds</c>). This type lives in the document project because the rule has to hold at
/// the earliest door: a world document is validated long before any catalog or composer exists.</para>
/// </summary>
public static class WorldDocumentName {
    /// <summary>The suffix of the file a world document is stored in: hand-authored JSON, a build's compiled output,
    /// and every owned world.</summary>
    public const string DocumentSuffix = ".world.json";
    /// <summary>The suffix of a world document's <c>.puck</c> source.</summary>
    public const string SourceSuffix = ".puck";

    private static readonly string[] FileFormSuffixes = [".json", SourceSuffix];

    /// <summary>Returns the file a document named <paramref name="name"/> is stored in.</summary>
    /// <param name="name">The document name, optionally carrying a directory (<c>"games/klondike"</c>).</param>
    /// <returns>The document file (<c>"games/klondike.world.json"</c>).</returns>
    public static string DocumentFile(string name) => (name + DocumentSuffix);
    /// <summary>Returns the file an owned world persists under, locally and in the cloud key space.</summary>
    /// <param name="id">The owned world id.</param>
    /// <returns>The file name.</returns>
    public static string For(SafeName id) => DocumentFile(name: id.Value);
    /// <summary>Returns whether <paramref name="path"/> names a document file.</summary>
    /// <param name="path">A file name or path.</param>
    /// <returns><see langword="true"/> when it ends in <see cref="DocumentSuffix"/>.</returns>
    public static bool IsDocumentFile(string path) => path.EndsWith(
        comparisonType: StringComparison.OrdinalIgnoreCase,
        value: DocumentSuffix
    );
    /// <summary>Returns the document name a document file carries — the inverse of <see cref="DocumentFile"/>.</summary>
    /// <param name="path">A file name or path ending in <see cref="DocumentSuffix"/>.</param>
    /// <returns>The same text without the suffix.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> does not end in <see cref="DocumentSuffix"/>.</exception>
    public static string OfDocumentFile(string path) {
        if (!IsDocumentFile(path: path)) {
            throw new ArgumentException(
                message: $"'{path}' is not a world document file ending in '{DocumentSuffix}'.",
                paramName: nameof(path)
            );
        }

        return path[..^DocumentSuffix.Length];
    }
    /// <summary>Returns whether <paramref name="path"/> names a <c>.puck</c> source file.</summary>
    /// <param name="path">A file name or path.</param>
    /// <returns><see langword="true"/> when it ends in <see cref="SourceSuffix"/>, in any letter case.</returns>
    public static bool IsSourceFile(string path) => path.EndsWith(
        comparisonType: StringComparison.OrdinalIgnoreCase,
        value: SourceSuffix
    );
    /// <summary>Returns the document name a source file carries — the inverse of <see cref="SourceFile"/>.</summary>
    /// <param name="path">A file name or path ending in <see cref="SourceSuffix"/>.</param>
    /// <returns>The same text without the suffix.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> does not end in <see cref="SourceSuffix"/>.</exception>
    public static string OfSourceFile(string path) {
        if (!IsSourceFile(path: path)) {
            throw new ArgumentException(
                message: $"'{path}' is not a world source file ending in '{SourceSuffix}'.",
                paramName: nameof(path)
            );
        }

        return path[..^SourceSuffix.Length];
    }
    /// <summary>Returns the document name either file that carries a document holds: its <c>.puck</c> source or its
    /// <c>.world.json</c> document.</summary>
    /// <param name="path">A file name or path ending in <see cref="SourceSuffix"/> or <see cref="DocumentSuffix"/>.</param>
    /// <returns>The same text without the suffix.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> ends in neither suffix.</exception>
    public static string OfCarrierFile(string path) {
        if (IsSourceFile(path: path)) {
            return path[..^SourceSuffix.Length];
        }

        if (IsDocumentFile(path: path)) {
            return path[..^DocumentSuffix.Length];
        }

        throw new ArgumentException(
            message: $"'{path}' carries no world document: it ends in neither '{SourceSuffix}' nor '{DocumentSuffix}'.",
            paramName: nameof(path)
        );
    }
    /// <summary>Returns the sidecar file <c>&lt;stem&gt;&lt;suffix&gt;</c> beside a document's carrying file, where the
    /// stem is the document name the file carries: a lock a compile of that source reads (<c>moth.puck</c> →
    /// <c>moth.assets.json</c>).</summary>
    /// <param name="sourcePath">The carrying file (a <c>.puck</c> source or a <c>.world.json</c> document), or the
    /// sidecar itself, which is returned as it stands.</param>
    /// <param name="suffix">The sidecar's suffix, including its leading dot (<c>".assets.json"</c>).</param>
    /// <returns>The absolute sidecar path.</returns>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/> or <paramref name="suffix"/> is empty or
    /// white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/> or <paramref name="suffix"/> is
    /// <see langword="null"/>.</exception>
    public static string SidecarFile(string sourcePath, string suffix) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: suffix);

        if (sourcePath.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: suffix
        )) {
            return Path.GetFullPath(path: sourcePath);
        }

        var file = Path.GetFileName(path: sourcePath);
        var stem = ((IsSourceFile(path: file) || IsDocumentFile(path: file))
            ? OfCarrierFile(path: file)
            : file
        );

        return Path.GetFullPath(path: Path.Combine(
            path1: (Path.GetDirectoryName(path: sourcePath) ?? "."),
            path2: (stem + suffix)
        ));
    }
    /// <summary>Returns the file a document named <paramref name="name"/> is authored in as source.</summary>
    /// <param name="name">The document name, optionally carrying a directory.</param>
    /// <returns>The source file (<c>"games/klondike.puck"</c>).</returns>
    public static string SourceFile(string name) => (name + SourceSuffix);
    /// <summary>Names the documents a set of claims carries, one per document name. Each claim is a document name
    /// and a file carrying it: a <c>.world.json</c> document carries the name its path spells, and a <c>.puck</c>
    /// source carries exactly the names it emits, which only the transpiler can read (its name index,
    /// <c>WorldSourceIndex</c>, over <c>WorldCompilation.EmittedNames</c>), so a source may make several claims or
    /// none, under any name. This is the one rule every door resolves a set of claims by.</summary>
    /// <remarks>A document name is unique ignoring letter case (<see cref="DocumentName"/>). Two claims whose names
    /// differ only in case (<c>Foo.puck</c> and <c>foo.world.json</c>, or <c>Games/go.puck</c> and
    /// <c>games/go.puck</c>) are refused by name rather than resolved: a case-insensitive file system (NTFS, default
    /// APFS) resolves both to one file while a case-sensitive one (a Linux checkout, the in-memory file system a web
    /// client mounts sources into) keeps two, so picking either would make the set of documents depend on the machine.
    /// The one pair two claims may form is a source and its own document: a source whose file stem is exactly the
    /// name, emitting that name, beside the document of that exact name, where the source wins. A source claiming a
    /// name its file does not spell, beside a document carrying it, names one document two ways.</remarks>
    /// <param name="claims">The claims, in the order a collision names them first.</param>
    /// <param name="carriers">On success, one carrier per document, in ordinal order of its path, then its name.</param>
    /// <param name="reason">The named refusal (<see cref="DocumentName.Collision"/>), or empty on success.</param>
    /// <returns><see langword="true"/> when no two claims name one document.</returns>
    public static bool TryCarriers(IEnumerable<WorldDocumentCarrier> claims, out IReadOnlyList<WorldDocumentCarrier> carriers, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: claims);

        carriers = [];

        var byName = new Dictionary<string, WorldDocumentCarrier>(comparer: DocumentName.Comparer);

        foreach (var carrier in claims) {
            if (!byName.TryGetValue(
                key: carrier.Name,
                value: out var held
            )) {
                byName.Add(
                    key: carrier.Name,
                    value: carrier
                );

                continue;
            }

            // The one pair two claims may form: a source and its own document under the exact same name, where the
            // source wins. Every other pair names one document two ways.
            if (string.Equals(
                a: held.Name,
                b: carrier.Name,
                comparisonType: StringComparison.Ordinal
            ) && (held.IsSource != carrier.IsSource) && SpellsItsName(source: (carrier.IsSource
                ? carrier
                : held
            ))) {
                if (carrier.IsSource) {
                    byName[carrier.Name] = carrier;
                }

                continue;
            }

            reason = DocumentName.Collision(
                heldFile: held.Path,
                heldName: held.Name,
                otherFile: carrier.Path,
                otherName: carrier.Name
            );

            return false;
        }

        carriers = [.. byName.Values.Order(comparer: Comparer<WorldDocumentCarrier>.Create(comparison: static (left, right) => {
            var byPath = string.CompareOrdinal(
                strA: left.Path,
                strB: right.Path
            );

            return ((byPath != 0)
                ? byPath
                : string.CompareOrdinal(
                    strA: left.Name,
                    strB: right.Name
                )
            );
        }))];
        reason = string.Empty;

        return true;
    }

    // Whether a source's claim names the source's own file: the claim's last segment is exactly the file's stem.
    private static bool SpellsItsName(WorldDocumentCarrier source) => string.Equals(
        a: source.Name[(source.Name.LastIndexOf(value: '/') + 1)..],
        b: OfSourceFile(path: Path.GetFileName(path: source.Path)),
        comparisonType: StringComparison.Ordinal
    );

    /// <summary>Reads a document reference addressed into a flat owned-world namespace (a cloud container, a hosted
    /// owner's worlds), where every document name is an owned world id.</summary>
    /// <param name="name">The authored reference.</param>
    /// <param name="id">The owned world id on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is a document name that is also a
    /// <see cref="SafeName"/>, which carries no directory, traversal, or surrounding whitespace.</returns>
    public static bool TryParseId(string name, out SafeName id, out string reason) {
        id = default;

        if (!TryValidate(
            name: name,
            reason: out reason
        )) {
            return false;
        }

        if (!SafeName.TryParse(
            candidate: name,
            name: out id,
            reason: out var nameReason
        )) {
            reason = $"'{name}' is not an owned world id: {nameReason}";

            return false;
        }

        return true;
    }
    /// <summary>Validates an authored document reference: non-empty, free of surrounding whitespace, and naming the
    /// document rather than one of its files.</summary>
    /// <param name="name">The authored reference.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is a document name.</returns>
    public static bool TryValidate(string name, out string reason) {
        if (string.IsNullOrWhiteSpace(value: name)) {
            reason = "a document reference names no document";

            return false;
        }

        if (!string.Equals(
            a: name,
            b: name.Trim(),
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"'{name}' carries leading or trailing whitespace; a document is named by its exact name";

            return false;
        }

        foreach (var suffix in FileFormSuffixes) {
            if (name.EndsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: suffix
            )) {
                var bare = (IsDocumentFile(path: name)
                    ? name[..^DocumentSuffix.Length]
                    : name[..^suffix.Length]
                );

                reason = $"'{name}' names a file; a document reference names the document ('{bare}')";

                return false;
            }
        }

        reason = string.Empty;

        return true;
    }
}
