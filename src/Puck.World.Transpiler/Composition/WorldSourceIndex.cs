using System.Collections.Concurrent;
using System.Text;
using Puck.Assets;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Modules;
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
/// read their bases in turn. The document composer (<see cref="PuckDocumentComposer"/>), and through it a tree compile
/// and the official build, read names through here and nowhere else.
/// <para>
/// Every read goes through <see cref="CompileInputs"/>: the directory's listing
/// (<see cref="CompileInputKind.Listing"/>) and each source's bytes. A compile that resolves a name here therefore
/// rests on both, and the compile cache serves it again only while both hold: a file added beside it, removed, or
/// renamed in any letter case moves the listing, and an edit to any source there, the edits that change what it
/// declares among them, moves that source's bytes. A source's declaration is held by the digest of its bytes, so an
/// unchanged source is parsed once per process however often the index is read.
/// </para>
/// </summary>
public static class WorldSourceIndex {
    /// <summary>The most parsed declarations the index holds; reading one more past it starts the hold afresh.</summary>
    public const int MaxHeldDeclarations = 4096;

    // A source's declaration by the digest of its bytes; null for bytes that do not parse.
    private static readonly ConcurrentDictionary<string, WorldSourceDeclaration?> Held = new(comparer: StringComparer.Ordinal);

    /// <summary>Returns the names every carrying file directly in <paramref name="directory"/> claims, one entry per
    /// file in ordinal order of its path. A source that cannot be read or does not parse cannot say what it emits, so
    /// it claims its stem, and the door that compiles it reports why.</summary>
    /// <param name="directory">The directory. One that does not exist carries nothing.</param>
    /// <returns>The claims, one per <c>.puck</c> source and <c>.world.json</c> document there.</returns>
    /// <exception cref="IOException">The directory could not be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory may not be listed.</exception>
    public static IReadOnlyList<WorldDocumentClaims> Claims(string directory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);

        var claims = new List<WorldDocumentClaims>();

        foreach (var file in CompileInputs.List(directory: directory)) {
            var path = file.Replace(
                newChar: '/',
                oldChar: '\\'
            );

            if (WorldDocumentName.IsDocumentFile(path: path)) {
                claims.Add(item: new WorldDocumentClaims(
                    Names: [WorldDocumentName.OfDocumentFile(path: System.IO.Path.GetFileName(path: path))],
                    Path: path
                ));
            } else if (WorldDocumentName.IsSourceFile(path: path)) {
                claims.Add(item: new WorldDocumentClaims(
                    Names: (Declaration(path: path)?.Names(sourcePath: path) ?? WorldCompilation.EmittedNames(
                        declaredWorlds: [],
                        emitsDocument: true,
                        sourcePath: path
                    )),
                    Path: path
                ));
            }
        }

        return claims;
    }
    /// <summary>Reads what the source at <paramref name="path"/> declares, through <see cref="CompileInputs"/>.</summary>
    /// <param name="path">The <c>.puck</c> source.</param>
    /// <returns>The declaration, or <see langword="null"/> when the source cannot be read or does not parse.</returns>
    public static WorldSourceDeclaration? Declaration(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        byte[] content;

        try {
            content = CompileInputs.ReadAllBytes(path: path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }

        var key = ContentPin.Compute(content: content).Hex;

        if (Held.TryGetValue(
            key: key,
            value: out var held
        )) {
            return held;
        }

        if (Held.Count >= MaxHeldDeclarations) {
            Held.Clear();
        }

        return Held.GetOrAdd(
            key: key,
            value: Parse(content: content)
        );
    }

    // The parse a compile of the same bytes starts from (WorldCompiler.Compile), and nothing after it. Text is decoded
    // as a compile reads it: a byte-order mark selects the encoding, UTF-8 otherwise.
    private static WorldSourceDeclaration? Parse(byte[] content) {
        string source;

        using (var reader = new StreamReader(
            detectEncodingFromByteOrderMarks: true,
            encoding: Encoding.UTF8,
            stream: new MemoryStream(buffer: content)
        )) {
            source = reader.ReadToEnd();
        }

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
