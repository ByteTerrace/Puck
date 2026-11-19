using Puck.Assets;
using Puck.Confinement;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;

namespace Puck.Transpiler.Modules;

/// <summary>
/// The parses of the <c>.puck</c> files a compile session has read, each held by the digest of the file's bytes and
/// the vocabulary it was parsed under, so a library every source imports is parsed once however many compiles read it.
/// The import walk (<see cref="ModuleResolver.ImportModules"/>) reads every library through here and nowhere else.
/// </summary>
/// <remarks>Every read goes through <see cref="CompileInputs"/>: the file's digest is recorded as what the compile read
/// there, and the file is read only when its stamp does not vouch for the digest last read, so a held parse of an
/// unchanged library costs one stat. A parse is a pure function of the bytes, the vocabulary and the parser, and the
/// tree it returns is immutable, so one tree serves every compile that reads those bytes. The table forgets every parse
/// when the parser's code is hot-reloaded (<see cref="CodeDerivedCache{TKey, TValue}"/>). Safe for concurrent
/// use.</remarks>
public sealed class PuckSourceTable {
    /// <summary>The most parses the table holds; holding one more forgets the least recently used.</summary>
    public const int MaxHeldParses = 4096;

    private readonly CodeDerivedCache<(string Digest, IDocumentVocabulary? Vocabulary), Parsed> m_parses = new(capacity: MaxHeldParses);
    private long m_parseCount;

    /// <summary>A file's parse and the length of the text it was parsed from.</summary>
    /// <param name="Document">The parsed tree.</param>
    /// <param name="SourceLength">The decoded text's length, in UTF-16 code units.</param>
    public sealed record Parsed(DocumentNode Document, int SourceLength);

    /// <summary>Gets the process's table, the one every compile session over the file system shares.</summary>
    public static PuckSourceTable Shared { get; } = new();
    /// <summary>Gets the number of files this table has parsed: one per distinct content and vocabulary it read, again
    /// after a code update.</summary>
    public long ParseCount => Interlocked.Read(location: ref m_parseCount);

    /// <summary>Returns the parse of the file at <paramref name="path"/>, recording its digest as what the calling
    /// compile read there, and parsing it only when no parse of the same bytes under <paramref name="vocabulary"/> is
    /// held.</summary>
    /// <param name="path">The <c>.puck</c> file.</param>
    /// <param name="vocabulary">The vocabulary the file is parsed under, or <see langword="null"/> for none.</param>
    /// <param name="parsed">Whether this call parsed the file rather than finding its parse held.</param>
    /// <returns>The file's parse.</returns>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    /// <exception cref="PuckParseException">The file does not parse.</exception>
    public Parsed Read(string path, IDocumentVocabulary? vocabulary, out bool parsed) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        parsed = false;

        var digest = (CompileInputs.Digest(content: out var content, path: path)
            ?? throw new FileNotFoundException(message: $"No file exists at '{path}'.", fileName: path));

        if (m_parses.TryGetValue(key: (digest, vocabulary), value: out var held)) {
            return held;
        }

        // The digest answered from the file's stamp, so its bytes were not read; read them now. Held under the digest
        // of the bytes actually read, which differ from `digest` only when the file moved since.
        if (content is null) {
            content = CompileInputs.ReadAllBytes(path: path);
            digest = ContentPin.Compute(content: content).Hex;
        }

        var text = FileContent.Text(content: content);
        var entry = new Parsed(Document: PuckParser.ParseDocument(source: text, vocabulary: vocabulary), SourceLength: text.Length);

        _ = Interlocked.Increment(location: ref m_parseCount);
        parsed = true;
        m_parses.Set(key: (digest, vocabulary), value: entry);

        return entry;
    }
    /// <summary>Forgets every held parse.</summary>
    public void Clear() => m_parses.Clear();
}
