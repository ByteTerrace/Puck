using Puck.Confinement;
using System.Text;
using Puck.Assets;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Composition;

/// <summary>The document names one carrying file in a directory claims.</summary>
/// <param name="Path">The file's path, with forward slashes, rooted as the listed directory was.</param>
/// <param name="Names">The names it claims, relative to its directory: a <c>.world.json</c> document the name its file
/// spells; a <c>.puck</c> source exactly the names it emits (<see cref="WorldSourceDeclaration.Names"/>), none for a
/// module library, or its stem when it does not parse.</param>
public sealed record WorldDocumentClaims(string Path, IReadOnlyList<string> Names) {
    /// <summary>Gets whether the file is a <c>.puck</c> source rather than a document.</summary>
    public bool IsSource => WorldDocumentName.IsSourceFile(path: Path);
}
/// <summary>
/// The one index of the document names a directory carries: each <c>.world.json</c> document its own name, and each
/// <c>.puck</c> source exactly the names it emits, read by parsing it and nothing more
/// (<see cref="WorldSourceDeclaration"/>). A name resolves to the source that emits it whatever that file's stem, so
/// resolving one name means knowing what every source beside it emits; reading that from a parse rather than a compile
/// is what lets a compile resolve its own basis through the index without compiling its siblings, whose compiles would
/// read their bases in turn. The compile session's composition (<see cref="WorldCompileSession"/>), and through it a tree compile
/// and the official build, read names through here and nowhere else.
/// <para>
/// Every read goes through <see cref="CompileInputs"/>: the directory's listing
/// (<see cref="CompileInputKind.Listing"/>) and what each source declares (<see cref="CompileInputKind.Declaration"/>,
/// which the index answers as the declarer it registers when its assembly loads). A compile that resolves a name here
/// therefore rests on both, and the compile cache serves it again only while both hold: a file added beside it,
/// removed, or renamed in any letter case moves the listing, and an edit that changes whether a source there is a
/// world source, whether it parses, or which names it emits moves that source's declaration. An edit to anything else
/// in a sibling (a value, a row) leaves every compile that only resolved a name beside it standing. What a source
/// declares, and whether it is a world source at all, is held by the digest of its bytes, so an unchanged source is
/// parsed once per process however often the index is read; and a directory's claims are held while its listing and
/// every source's digest are the ones they were built from. The door answers a listing and a digest from each path's
/// stamp while the stamp holds, so reading an unchanged directory's claims again reads no file's contents.
/// </para>
/// </summary>
public static class WorldSourceIndex {
    /// <summary>The most parsed declarations the index holds; reading one more past it forgets the least recently used.</summary>
    public const int MaxHeldDeclarations = 4096;
    /// <summary>The most directories whose claims the index holds; reading one more past it forgets the least recently used.</summary>
    public const int MaxHeldDirectories = 1024;

    private const string DeclarationExtension = ".declaration";
    private const string DeclarationsDirectory = "declarations";
    private static readonly Puck.Abstractions.CacheBound DeclarationRetention = new(MaxBytes: (4L << 20), MaxEntries: 4096);

    // A source's declaration by the digest of its bytes; null for bytes that do not parse. Every table is a product of
    // the parser, so a hot-reload edit to it forgets them all (CodeDerivedCache).
    private static readonly CodeDerivedCache<string, WorldSourceDeclaration?> Held = new(capacity: MaxHeldDeclarations);
    // Whether a source is a world source (IsWorldSource), by the digest of its bytes.
    private static readonly CodeDerivedCache<string, bool> HeldVocabulary = new(capacity: MaxHeldDeclarations);
    // A directory's claims, by the directory exactly as spelled, with the listing and digests they were built from.
    private static readonly CodeDerivedCache<string, HeldClaims> HeldDirectories = new(capacity: MaxHeldDirectories);

    // The claims a directory's files made: the files listed, each source's digest in listing order (null for one that
    // could not be read), and the claims built from them.
    private sealed record HeldClaims(IReadOnlyList<string> Files, string?[] Digests, IReadOnlyList<WorldDocumentClaims> Claims);

    /// <summary>Returns the names every carrying file directly in <paramref name="directory"/> claims, one entry per
    /// file in ordinal order of its path. A source that cannot be read or does not parse cannot say what it emits, so
    /// it claims its stem, and the door that compiles it reports why.</summary>
    /// <param name="directory">The directory. One that does not exist carries nothing.</param>
    /// <returns>The claims, one per world source (<see cref="IsWorldSourceFile"/>) and <c>.world.json</c> document
    /// there; a source of another vocabulary claims nothing.</returns>
    /// <exception cref="IOException">The directory could not be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory may not be listed.</exception>
    public static IReadOnlyList<WorldDocumentClaims> Claims(string directory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);

        var files = CompileInputs.List(directory: directory);
        var digests = new string?[files.Count];
        var known = new Known?[files.Count];

        for (var index = 0; (index < files.Count); index++) {
            if (WorldDocumentName.IsSourceFile(path: files[index])) {
                known[index] = TryKnow(digest: out digests[index], path: files[index]);
            }
        }

        if (HeldDirectories.TryGetValue(key: directory, value: out var held) &&
            held.Files.SequenceEqual(second: files, comparer: StringComparer.Ordinal) &&
            held.Digests.AsSpan().SequenceEqual(other: digests)) {
            return held.Claims;
        }

        var claims = new List<WorldDocumentClaims>();

        for (var index = 0; (index < files.Count); index++) {
            var path = Puck.Abstractions.PuckPaths.Slashed(path: files[index]);

            if (WorldDocumentName.IsDocumentFile(path: path)) {
                claims.Add(item: new WorldDocumentClaims(
                    Names: [WorldDocumentName.OfDocumentFile(path: System.IO.Path.GetFileName(path: path))],
                    Path: path
                ));
            } else if (WorldDocumentName.IsSourceFile(path: path) && (known[index]?.IsWorld ?? true)) {
                claims.Add(item: new WorldDocumentClaims(
                    Names: (known[index]?.Declaration?.Names(sourcePath: path) ?? WorldCompilation.EmittedNames(
                        declaredWorlds: [],
                        emitsDocument: true,
                        sourcePath: path
                    )),
                    Path: path
                ));
            }
        }

        HeldDirectories.Set(key: directory, value: new HeldClaims(Claims: claims, Digests: digests, Files: files));

        return claims;
    }
    // What one source's bytes declare: whether it is a world source, and the names it emits when it is (null for one
    // that does not parse).
    private readonly record struct Known(bool IsWorld, WorldSourceDeclaration? Declaration);

    // The name the index's declaration facts carry (CompileInputs.RegisterDeclarer).
    private const string DeclarerName = "world-source";

    // Registers the index as the declarer of its facts the moment the assembly loads, so any process holding a session
    // can prove a persisted compile's declaration facts before it compiles anything.
    [System.Runtime.CompilerServices.ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        category: "Usage",
        checkId: "CA2255",
        Justification = "A persisted compile's declaration facts are checked through CompileInputs, which this assembly does not own, before any type here need run; registering when the module loads is the one point every such check follows."
    )]
    internal static void RegisterDeclarer() => CompileInputs.RegisterDeclarer(
        declare: static (digest, read) => Canonical(known: Know(digest: digest, read: read)),
        name: DeclarerName
    );

    // A source's declaration through the door, recorded as a declaration fact: the compile rests on what the source
    // declares, not on its bytes, so a value edit there leaves it standing. Null for a source that is absent or cannot
    // be read, which claims its stem; `digest` is the digest of its bytes, null alike.
    private static Known? TryKnow(string path, out string? digest) {
        try {
            var known = CompileInputs.Declare<Known?>(
                declare: static (digest, read) => {
                    var known = Know(digest: digest, read: read);

                    return (known, Canonical(known: known));
                },
                declarer: DeclarerName,
                digest: out digest,
                path: path
            );

            return ((digest is null) ? null : known);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            digest = null;

            return null;
        }
    }
    // What the source whose bytes digest to `digest` declares, from the tables held by digest, reading the bytes only
    // for a digest they do not hold. What the bytes read declare is held under their own digest, which differs from
    // `digest` only when the file moved since.
    private static Known Know(string digest, Func<byte[]> read) {
        byte[]? bytes = null;

        if (!HeldVocabulary.TryGetValue(key: digest, value: out var isWorld)) {
            bytes = read();
            isWorld = IsWorldSource(source: FileContent.Text(content: bytes));
            HeldVocabulary.Set(key: ContentPin.Compute(content: bytes).Hex, value: isWorld);
        }
        if (!isWorld) {
            return new Known(Declaration: null, IsWorld: false);
        }
        if (Held.TryGetValue(key: digest, value: out var held)) {
            return new Known(Declaration: held, IsWorld: true);
        }

        return new Known(Declaration: Declaration(content: (bytes ?? read())), IsWorld: true);
    }
    // The canonical text of a declaration fact: whether the source is a world source, whether it parses, whether it
    // emits a document, and each world it declares, length-prefixed. Two sources declare the same exactly when their
    // texts are equal.
    private static string Canonical(Known known) => (!known.IsWorld
        ? "other"
        : ((known.Declaration is not { } declaration)
            ? "world unparsed"
            : string.Concat(
                str0: (declaration.EmitsDocument ? "world emits\n" : "world library\n"),
                str1: string.Concat(values: declaration.Worlds.Select(selector: static world => $"{world.Length}:{world}\n"))
            )));
    /// <summary>Returns whether source text is a world source: its <c>schema:</c> names the world vocabulary
    /// (<see cref="WorldDefinition.SchemaVersion"/>), or it names none, as a module library and an imported fragment
    /// do. A source naming another vocabulary's schema (a cartridge's) is not, whatever its folder or file
    /// name.</summary>
    /// <param name="source">The <c>.puck</c> source text.</param>
    /// <returns><see langword="true"/> when the source belongs to the world vocabulary.</returns>
    public static bool IsWorldSource(string source) => (
        !PuckParser.TryReadDocumentSchema(schema: out var schema, source: source) ||
        string.IsNullOrEmpty(value: schema) ||
        string.Equals(a: schema, b: WorldDefinition.SchemaVersion, comparisonType: StringComparison.Ordinal)
    );
    /// <summary>Returns whether the <c>.puck</c> file at <paramref name="path"/> is a world source
    /// (<see cref="IsWorldSource"/>), read through <see cref="CompileInputs"/>. A file that cannot be read is held to be
    /// one, so the door that compiles it reports why.</summary>
    /// <param name="path">The <c>.puck</c> source.</param>
    /// <returns><see langword="true"/> when the file is a world source or cannot be read.</returns>
    public static bool IsWorldSourceFile(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        return (TryKnow(digest: out _, path: path)?.IsWorld ?? true);
    }
    /// <summary>Reads what the source at <paramref name="path"/> declares, through <see cref="CompileInputs"/>.</summary>
    /// <param name="path">The <c>.puck</c> source.</param>
    /// <returns>The declaration, or <see langword="null"/> when the source cannot be read or does not parse.</returns>
    public static WorldSourceDeclaration? Declaration(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        return TryKnow(digest: out _, path: path)?.Declaration;
    }
    /// <summary>Reads what the source whose bytes are <paramref name="content"/> declares, held by the digest of
    /// those bytes and persisted beside the process's compiles (<see cref="WorldCompileSession.Shared"/>) when it
    /// persists them.</summary>
    /// <param name="content">The <c>.puck</c> source's bytes.</param>
    /// <returns>The declaration, or <see langword="null"/> when the bytes do not parse.</returns>
    public static WorldSourceDeclaration? Declaration(byte[] content) =>
        Declaration(content: content, compilations: WorldCompileSession.Shared.Directory);
    /// <summary>Reads what the source whose bytes are <paramref name="content"/> declares, held by the digest of
    /// those bytes. A declaration is a pure function of the bytes and the parser, so it is also kept in a
    /// <c>declarations</c> directory under <paramref name="compilations"/>, named by the compiler's identity and the
    /// digest: a later process resolving a name reads the declarations of the sources beside it rather than parsing
    /// every one. A file that cannot be read or written costs the parse and nothing else.</summary>
    /// <param name="content">The <c>.puck</c> source's bytes.</param>
    /// <param name="compilations">The directory compiles persist in, or <see langword="null"/> to hold the declaration
    /// in memory only.</param>
    /// <returns>The declaration, or <see langword="null"/> when the bytes do not parse.</returns>
    public static WorldSourceDeclaration? Declaration(byte[] content, string? compilations) {
        ArgumentNullException.ThrowIfNull(argument: content);

        // The persisted declaration is named by the compiler identity, which folds in an update applied to the parser.
        return Held.GetOrAdd(
            derive: key => ((compilations is null) ? Parse(content: content) : ReadPersisted(content: content, key: key, root: compilations)),
            key: ContentPin.Compute(content: content).Hex
        );
    }
    /// <summary>Forgets every declaration held in memory, so the next read of each source reads its persisted
    /// declaration or parses it.</summary>
    public static void ForgetHeld() {
        Held.Clear();
        HeldVocabulary.Clear();
        HeldDirectories.Clear();
    }

    private static WorldSourceDeclaration? ReadPersisted(byte[] content, string key, string root) {
        var directory = System.IO.Path.Combine(path1: root, path2: DeclarationsDirectory);
        var path = System.IO.Path.Combine(path1: directory, path2: $"{WorldCompileSession.CompilerIdentity[..16]}-{key}{DeclarationExtension}");

        try {
            if (File.Exists(path: path) && TryDecode(declaration: out var stored, text: File.ReadAllText(path: path))) {
                Puck.Abstractions.CacheRetention.Stamp(path: path);
                return stored;
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
        }

        var parsed = Parse(content: content);

        if (parsed?.Worlds.Any(predicate: static world => world.Contains(value: '\n')) == true) {
            return parsed;
        }

        try {
            _ = System.IO.Directory.CreateDirectory(path: directory);
            AtomicFile.WriteAllBytes(bytes: Encoding.UTF8.GetBytes(s: Encode(declaration: parsed)), path: path);
            _ = Puck.Abstractions.CacheRetention.EnforceOnWrite(
                bound: DeclarationRetention,
                directory: directory,
                inUse: path,
                isEntry: static name => name.EndsWith(comparisonType: StringComparison.Ordinal, value: DeclarationExtension)
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
        }

        return parsed;
    }
    // One line naming whether the source emits a document ('1', '0', or '-' for bytes that do not parse), then one line
    // per declared world, in declaration order. A declaration naming a world across lines is not persisted.
    private static string Encode(WorldSourceDeclaration? declaration) => ((declaration is null)
        ? "-\n"
        : string.Concat(str0: (declaration.EmitsDocument ? "1\n" : "0\n"), str1: string.Concat(values: declaration.Worlds.Select(selector: static world => (world + "\n")))));
    private static bool TryDecode(string text, out WorldSourceDeclaration? declaration) {
        declaration = null;

        var lines = text.Split(separator: '\n');

        if ((lines.Length < 2) || (lines[^1].Length != 0)) {
            return false;
        }

        switch (lines[0]) {
            case "-":
                return (lines.Length == 2);
            case "0" or "1":
                declaration = new WorldSourceDeclaration(
                    EmitsDocument: (lines[0] == "1"),
                    Worlds: lines[1..^1]
                );
                return true;
            default:
                return false;
        }
    }

    // The parse a compile of the same bytes starts from (WorldCompiler.Compile), and nothing after it. Text is decoded
    // as a compile reads it: a byte-order mark selects the encoding, UTF-8 otherwise.
    private static WorldSourceDeclaration? Parse(byte[] content) {
        var source = FileContent.Text(content: content);
        var diagnostics = new DiagnosticBag();
        var parsed = PuckParser.ParseDocumentWithDiagnostics(
            diagnostics: diagnostics,
            source: source,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        return (((parsed.Value is { } document) && !diagnostics.HasErrors)
            ? WorldSourceDeclaration.Of(document: document)
            : null
        );
    }
}
