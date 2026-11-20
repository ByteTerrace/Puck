using System.Collections.Concurrent;
using System.Text;
using Puck.Assets;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Modules;
using Puck.World.Transpiler.Composition;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler;

/// <summary>
/// The one compile session a host holds, and the only door into the world compiler's stages: the CLI's verbs, the
/// World's boot, reload and source watch, the language server and the browser engine each compile, compose, load and
/// diagnose a <c>.puck</c> world through a session, and <see cref="WorldCompiler"/> is reachable from nowhere else. A
/// session keeps what an unchanged input already produced, so a second compile in one process reuses it: the parse of
/// every library the import walk reads, by the digest of its bytes (<see cref="Sources"/>); each source's compiled
/// documents (<see cref="TryCompile"/>), keyed by whether tests are lowered and the source's full path, spelled with
/// <c>/</c> on every platform and case-folded where the file system ignores case; the latest compile of each editor
/// buffer (<see cref="CompileBuffer"/>), by its path and text; and each composed image
/// (<see cref="IWorldCompositionStore"/>), through the composition every basis and import read resolves by
/// (<see cref="IWorldDocumentSource"/>).
/// <para>
/// A held product is served again only while every file fact it read still holds: the source, each module its import
/// walk read, the embedding and asset locks, every asset it hashed, every path it only probed, absent or present, and
/// the listing and sources of every directory a name resolved in through the name index (<see cref="WorldSourceIndex"/>,
/// <see cref="CompileInputs"/>). Editing any of them recompiles exactly the sources that read it; nothing else is
/// consulted, so no clock takes part.
/// </para>
/// <para>
/// Held compiles live in memory for the session and, once <see cref="Persist"/> names a directory, in that directory
/// too, so a later process compiles nothing an earlier one already compiled. An entry belongs to one compiler build:
/// the identity of every non-framework assembly the compiler loads, with any hot-reload update applied to one of them
/// (<see cref="Puck.Abstractions.CodeRevision.AppliedTo"/>), is part of the entry's name and is recorded inside it, so a
/// rebuilt or hot-reloaded compiler never reads an older one's output, and builds that share a directory (an editor's
/// <c>puck lsp</c>, a candidate CLI, the game) keep their own entries rather than overwriting each other's. A read
/// that serves an entry stamps it as used, and a write keeps the directory within <see cref="MaxPersistedEntries"/>
/// entries and <see cref="MaxPersistedBytes"/> bytes, least recently used out
/// (<see cref="Puck.Abstractions.CacheRetention"/>), and also removes the temporary files an interrupted write left
/// behind. A compile that fails is never held: its diagnostics come from compiling again.
/// </para>
/// <para>
/// Callers on several threads may compile at once. Concurrent misses on one source compile it once: the rest wait for
/// that compile and are served it as a hit. A hit never replaces a held compile with an older one.
/// </para>
/// </summary>
public sealed partial class WorldCompileSession : IWorldCompositionStore, IWorldDocumentSource {
    /// <summary>The most entries the persistent directory keeps; writing one more removes the least recently used.</summary>
    public const int MaxPersistedEntries = 1024;
    /// <summary>The most bytes the persistent directory's entries hold between them.</summary>
    public const long MaxPersistedBytes = (256L << 20);
    /// <summary>The most editor buffers whose latest compile a session holds; holding one more forgets the least recently used.</summary>
    public const int MaxHeldBuffers = 64;

    private const string EntryExtension = ".compiled";

    private static readonly byte[] Magic = "PUCKWCC1"u8.ToArray();

    // What every view of one session shares; a view rooted at an already compiled source (WithRoot) shares it whole.
    private sealed class State(string? directory, PuckSourceTable sources) {
        public string? Directory = directory;

        public ConcurrentDictionary<string, Lock> Compiling { get; } = new(comparer: StringComparer.Ordinal);
        public ConcurrentDictionary<string, WorldCompiledSource> Held { get; } = new(comparer: StringComparer.Ordinal);
        public CodeDerivedCache<string, HeldBuffer> Buffers { get; } = new(capacity: MaxHeldBuffers);
        public Lock PersistLock { get; } = new();
        public PuckSourceTable Sources { get; } = sources;
    }
    // An editor buffer's latest compile: the text and vocabulary it compiled, and the compilation, whose diagnostics are
    // the session's own copy.
    private sealed record HeldBuffer(string Text, WorldDocumentVocabulary Vocabulary, WorldCompilation Compilation);

    private readonly State m_state;
    private readonly string? m_rootPath;
    private readonly WorldCompiledSource? m_rootCompilation;

    /// <summary>Initializes a new instance of the <see cref="WorldCompileSession"/> class, with parses of its own.</summary>
    /// <param name="directory">The directory held compiles persist in, created on first write; <see langword="null"/>
    /// to hold them in memory only.</param>
    public WorldCompileSession(string? directory = null) : this(
        root: null,
        state: new State(
            directory: ((directory is null) ? null : Path.GetFullPath(path: directory)),
            sources: new PuckSourceTable()
        )
    ) { }
    private WorldCompileSession(State state, WorldCompiledSource? root) {
        m_state = state;
        m_rootPath = (((root is { Inputs.Count: > 0 }) && (root.Inputs[0].Kind == CompileInputKind.Content))
            ? Puck.Abstractions.PuckPaths.Normalize(path: Path.GetFullPath(path: root.Inputs[0].Path))
            : null);
        m_rootCompilation = root;
    }

    /// <summary>Gets the process's session: the one every host's verbs, the boot loader, the document source a host
    /// installs (<see cref="WorldDefinitionFileSource.UseLocalDocuments"/>) and the language server compile through,
    /// over the process's parses (<see cref="PuckSourceTable.Shared"/>). It holds compiles in memory until a host names
    /// a directory through <see cref="Persist"/>; only an executable's entry point names one, so a test composing World
    /// services never persists to the per-user cache.</summary>
    public static WorldCompileSession Shared { get; } = new(root: null, state: new State(directory: null, sources: PuckSourceTable.Shared));
    /// <summary>Gets the directory held compiles persist in, or <see langword="null"/> when they are held in memory
    /// only.</summary>
    public string? Directory => Volatile.Read(location: ref m_state.Directory);
    /// <summary>Gets the parses this session's import walks read every library through.</summary>
    public PuckSourceTable Sources => m_state.Sources;

    // The pin over every Puck module the compiler reaches, with the update applied to any of them folded in
    // (CodeRevision.AppliedTo): a product of the parser or compiler is valid only under the identity it was written by.
    internal static string CompilerIdentity {
        get {
            var (built, modules) = Identity.Value;
            var applied = Puck.Abstractions.CodeRevision.AppliedToAssemblies(assemblies: modules);

            return ((applied.Length == 0) ? built : ContentPin.OfText(text: $"{built}\n{applied}").Hex);
        }
    }

    // The identity walks the compiler's reference graph from its own assembly. In a trimmed application a reference
    // the trimmer removed is code the compile cannot run, so its load failing and entering the identity as absent is
    // exactly the right answer.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        category: "Trimming",
        checkId: "IL2026",
        Justification = "A reference the trimmer removed cannot run, and its failed load enters the identity as absent."
    )]
    private static (string Built, IReadOnlySet<string> Modules) ComputeCompilerIdentity() {
        var modules = new SortedDictionary<string, Guid>(comparer: StringComparer.Ordinal);
        foreach (var (name, assembly) in AssemblyReach.Of(
            root: typeof(WorldCompiler).Assembly,
            include: static reference => !IsFramework(name: reference.Name),
            unavailable: reference => modules.TryAdd(key: (reference.Name ?? string.Empty), value: Guid.Empty))) {
            modules[name] = assembly.ManifestModule.ModuleVersionId;
        }

        var text = new StringBuilder();

        foreach (var (name, version) in modules) {
            _ = text.Append(value: name).Append(value: '=').Append(value: version.ToString(format: "N")).Append(value: ';');
        }

        return (ContentPin.OfText(text: text.ToString()).Hex, modules.Keys.ToHashSet(comparer: StringComparer.Ordinal));
    }
    private static bool IsFramework(string? name) => (
        (name is null) ||
        name.StartsWith(comparisonType: StringComparison.Ordinal, value: "System") ||
        name.StartsWith(comparisonType: StringComparison.Ordinal, value: "Microsoft") ||
        (name is "netstandard" or "mscorlib" or "WindowsBase")
    );
    private static bool Stands(IReadOnlyList<CompileInput> inputs, int from = 0) {
        for (var index = from; (index < inputs.Count); index++) {
            if (!inputs[index].StillHolds()) {
                return false;
            }
        }

        return true;
    }
    // A served compilation reports into a bag of its caller's own, so nothing a caller reports reaches the held one.
    private static WorldCompilation WithOwnDiagnostics(WorldCompilation compilation) {
        var diagnostics = new DiagnosticBag();

        diagnostics.AddRange(diagnostics: compilation.Diagnostics);
        return (compilation with { Diagnostics = diagnostics });
    }

    /// <summary>Returns a view of this session that composes with <paramref name="sourceCompilation"/> standing for
    /// its own source: a composition rooted at that source reads its documents from the compile rather than compiling
    /// the source again, and an unsaved buffer's compile composes as the buffer, never as the file on disk. Every other
    /// source compiles through this session, whose held products the view shares.</summary>
    /// <param name="sourceCompilation">The already compiled root. Its first input identifies its source; carrier
    /// selection, bytes and current facts still govern reuse.</param>
    /// <returns>The rooted view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceCompilation"/> is <see langword="null"/>.</exception>
    public WorldCompileSession WithRoot(WorldCompiledSource sourceCompilation) {
        ArgumentNullException.ThrowIfNull(argument: sourceCompilation);

        return new WorldCompileSession(root: sourceCompilation, state: m_state);
    }
    /// <summary>Names the directory held compiles persist in, for this session's life. A host calls this once at
    /// startup; naming the same directory again changes nothing.</summary>
    /// <param name="directory">The directory, created on first write.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white space.</exception>
    /// <exception cref="InvalidOperationException">A different directory was already named.</exception>
    public void Persist(string directory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);

        var full = Path.GetFullPath(path: directory);

        lock (m_state.PersistLock) {
            if (m_state.Directory is { } named) {
                if (!string.Equals(
                    a: Puck.Abstractions.PuckPaths.Key(path: named),
                    b: Puck.Abstractions.PuckPaths.Key(path: full),
                    comparisonType: StringComparison.Ordinal
                )) {
                    throw new InvalidOperationException(message: $"The compile session already persists in '{named}'; it cannot also persist in '{full}'.");
                }

                return;
            }

            Volatile.Write(
                location: ref m_state.Directory,
                value: full
            );
        }
    }
    /// <summary>Compiles <paramref name="source"/> to its canonical world document through this session: its import
    /// walk reads every library through <see cref="Sources"/>, and a basis read for its enums or a test's composed
    /// state composes through this session. The result is the caller's own and is not held; a source on disk that only
    /// needs its documents compiles through <see cref="TryCompile"/>, and an editor buffer through
    /// <see cref="CompileBuffer"/>.</summary>
    /// <param name="source">The <c>.puck</c> source text.</param>
    /// <param name="sourcePath">The file the text came from, when it came from one. It roots relative asset paths,
    /// the import walk, and the <c>.embeddings.json</c> lock beside it.</param>
    /// <param name="basePath">The directory relative assets resolve against; defaults to
    /// <paramref name="sourcePath"/>'s own directory.</param>
    /// <param name="imports">What the import graph is worth to this compile.</param>
    /// <param name="embeddings">An embedding lock to resolve authored vector text against; defaults to the one
    /// beside <paramref name="sourcePath"/>.</param>
    /// <param name="defaultSchema">The schema to assume when the source declares none.</param>
    /// <param name="diagnostics">The bag every stage reports into; a fresh one when omitted.</param>
    /// <param name="sourceMap">The map the lowering registers pointers in; a fresh one when omitted.</param>
    /// <param name="cancellationToken">Cancels evaluation and expansion.</param>
    /// <param name="vocabulary">The described vocabulary every stage parses and lowers against; the shipped
    /// <see cref="WorldDocumentVocabulary.Instance"/> when omitted.</param>
    /// <param name="allowMultiple">Whether this consumer accepts every emitted world through <see cref="WorldCompilation.Worlds"/>.</param>
    /// <param name="updateAssets">Prepares refreshed asset pins without writing them. The caller saves the lock only after validation succeeds.</param>
    /// <param name="includeTests">Whether to lower test blocks into generated worlds.</param>
    /// <returns>The compilation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public WorldCompilation Compile(
        string source,
        string? sourcePath = null,
        string? basePath = null,
        ImportHandling imports = ImportHandling.Validate,
        EmbeddingLock? embeddings = null,
        string? defaultSchema = null,
        DiagnosticBag? diagnostics = null,
        SourceMap? sourceMap = null,
        CancellationToken cancellationToken = default,
        WorldDocumentVocabulary? vocabulary = null,
        bool allowMultiple = false,
        bool updateAssets = false,
        bool includeTests = true
    ) => WorldCompiler.Compile(
        allowMultiple: allowMultiple,
        basePath: basePath,
        cancellationToken: cancellationToken,
        defaultSchema: defaultSchema,
        diagnostics: diagnostics,
        embeddings: embeddings,
        imports: imports,
        includeTests: includeTests,
        session: this,
        source: source,
        sourceMap: sourceMap,
        sourcePath: sourcePath,
        updateAssets: updateAssets,
        vocabulary: vocabulary
    );
    /// <summary>Reads <paramref name="path"/> and compiles it (<see cref="Compile"/>). The result is the caller's own
    /// and is not held.</summary>
    /// <param name="path">The <c>.puck</c> source file.</param>
    /// <param name="imports">What the import graph is worth to this compile.</param>
    /// <param name="embeddings">An embedding lock to resolve authored vector text against; defaults to the one
    /// beside <paramref name="path"/>.</param>
    /// <param name="diagnostics">The bag every stage reports into; a fresh one when omitted.</param>
    /// <param name="sourceMap">The map the lowering registers pointers in; a fresh one when omitted.</param>
    /// <param name="cancellationToken">Cancels evaluation and expansion.</param>
    /// <param name="allowMultiple">Whether the caller accepts every emitted world through <see cref="WorldCompilation.Worlds"/>.</param>
    /// <param name="updateAssets">Prepares refreshed asset pins for an explicit save after validation.</param>
    /// <param name="includeTests">Whether to lower test blocks into generated worlds.</param>
    /// <returns>The compilation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public WorldCompilation CompileFile(
        string path,
        ImportHandling imports = ImportHandling.Validate,
        EmbeddingLock? embeddings = null,
        DiagnosticBag? diagnostics = null,
        SourceMap? sourceMap = null,
        CancellationToken cancellationToken = default,
        bool allowMultiple = false,
        bool updateAssets = false,
        bool includeTests = true
    ) => WorldCompiler.CompileFile(
        allowMultiple: allowMultiple,
        cancellationToken: cancellationToken,
        diagnostics: diagnostics,
        embeddings: embeddings,
        imports: imports,
        includeTests: includeTests,
        path: path,
        session: this,
        sourceMap: sourceMap,
        updateAssets: updateAssets
    );
    /// <summary>Compiles an editor buffer the way every diagnosis of it does: imports walked, every emitted world
    /// accepted and test blocks lowered. The latest compile of each buffer is held, by its path, while its text, its
    /// vocabulary and every other file fact it read stand, so a diagnosis, a hover and a completion over one unchanged
    /// buffer compile it once. A buffer with no path is compiled every time.</summary>
    /// <param name="source">The buffer's text, which may differ from the file on disk.</param>
    /// <param name="sourcePath">The file the buffer stands for, or <see langword="null"/> for a buffer that has none.</param>
    /// <param name="vocabulary">The vocabulary the buffer is written in; the shipped
    /// <see cref="WorldDocumentVocabulary.Instance"/> when omitted.</param>
    /// <param name="cancellationToken">Cancels evaluation and expansion; a cancelled compile is not held.</param>
    /// <returns>The compilation. Its diagnostics are the caller's own; its tree, document and source map are shared
    /// with every caller served the same compile and are read, never changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public WorldCompilation CompileBuffer(string source, string? sourcePath, WorldDocumentVocabulary? vocabulary = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: source);

        vocabulary ??= WorldDocumentVocabulary.Instance;

        if (sourcePath is null) {
            return Compile(allowMultiple: true, cancellationToken: cancellationToken, source: source, vocabulary: vocabulary);
        }

        var key = Puck.Abstractions.PuckPaths.Key(path: Path.GetFullPath(path: sourcePath));

        // The buffer's own text is its first input (WorldCompiler.Compile notes it before anything is read), and it is
        // compared whole here, so only the facts after it are asked of the file system.
        if (m_state.Buffers.TryGetValue(key: key, value: out var held) &&
            string.Equals(a: held.Text, b: source, comparisonType: StringComparison.Ordinal) &&
            ReferenceEquals(objA: held.Vocabulary, objB: vocabulary) &&
            Stands(from: 1, inputs: held.Compilation.Inputs)) {
            WorldBootWork.Count(kind: WorldBootWork.PuckCacheHits);
            CompileInputs.Restate(inputs: held.Compilation.Inputs);

            return WithOwnDiagnostics(compilation: held.Compilation);
        }

        var compilation = Compile(
            allowMultiple: true,
            cancellationToken: cancellationToken,
            source: source,
            sourcePath: sourcePath,
            vocabulary: vocabulary
        );

        m_state.Buffers.Set(key: key, value: new HeldBuffer(Compilation: WithOwnDiagnostics(compilation: compilation), Text: source, Vocabulary: vocabulary));

        return compilation;
    }
    /// <summary>Returns the documents <paramref name="path"/> compiles to, from a held compile whose every file fact
    /// still holds, or by compiling it and holding the result. Callers that miss on one source at once compile it
    /// once; the others wait for that compile and are served it.</summary>
    /// <param name="path">The <c>.puck</c> source. A relative path resolves against the current directory, and the
    /// compile sees the full path, so every file fact it records is a full path that any process can check.</param>
    /// <param name="includeTests">Whether to lower test worlds as well as the document; part of the key.</param>
    /// <param name="compiled">The source's documents on success; <see langword="null"/> when it does not compile.</param>
    /// <param name="failure">The failed compilation, whose diagnostics say why; <see langword="null"/> on
    /// success.</param>
    /// <param name="hold">Whether a compile this call makes, or reads back from the persistent directory, is held in
    /// memory for the session; <see langword="false"/> for a caller that reads a source's generated worlds once (a test
    /// run staging each source in turn), so they do not stay alive for the session's life. A compile already held is
    /// served either way.</param>
    /// <returns><see langword="true"/> when the source compiled, now or earlier.</returns>
    /// <exception cref="IOException">The source could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The source may not be read.</exception>
    public bool TryCompile(string path, out WorldCompiledSource? compiled, out WorldCompilation? failure, bool includeTests = false, bool hold = true) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        var fullPath = Path.GetFullPath(path: path);
        // A held compile is a product of the compiler, so the compiler's identity keys it in memory as on disk: a compile
        // held before an update to the compiler was applied is never served after it.
        var key = (CompilerIdentity + "\0" + Puck.Abstractions.PuckPaths.Key(path: fullPath) + (includeTests ? "\0tests" : "\0document"));

        failure = null;

        if (TryServe(compiled: out compiled, hold: hold, key: key)) {
            return true;
        }

        // One compile per source at a time: a caller that waited here finds the compile the one before it held.
        lock (m_state.Compiling.GetOrAdd(key: key, valueFactory: static _ => new Lock())) {
            if (TryServe(compiled: out compiled, hold: hold, key: key)) {
                return true;
            }

            var compilation = CompileFile(
                includeTests: includeTests,
                allowMultiple: true,
                path: fullPath
            );

            if (!compilation.Success) {
                compiled = null;
                failure = compilation;

                return false;
            }

            compiled = WorldCompiledSource.From(compilation: compilation);
            // Only the caller holding this source's lock stores a compile, so the one it stores is the newest.
            if (hold) {
                m_state.Held[key] = compiled;
            }
            WritePersisted(
                compiled: compiled,
                key: key
            );

            return true;
        }
    }

    // A hit: the held compile if it still stands, else the persisted entry if it stands. An entry read back from disk
    // is held only in place of the entry this caller found, so a newer compile stored meanwhile is never displaced.
    private bool TryServe(string key, bool hold, out WorldCompiledSource? compiled) {
        var found = m_state.Held.TryGetValue(
            key: key,
            value: out var held
        );

        if (found && Stands(inputs: held!.Inputs)) {
            compiled = held;
        } else if (TryReadPersisted(
            compiled: out var persisted,
            key: key
        ) && Stands(inputs: persisted!.Inputs)) {
            if (hold) {
                _ = (found
                    ? m_state.Held.TryUpdate(
                        comparisonValue: held!,
                        key: key,
                        newValue: persisted!
                    )
                    : m_state.Held.TryAdd(
                        key: key,
                        value: persisted!
                    ));
            }
            compiled = persisted;
        } else {
            compiled = null;

            return false;
        }

        WorldBootWork.Count(kind: WorldBootWork.PuckCacheHits);
        // A compile reading this source's output (a child reading its basis's enums) rests on what the held compile
        // read, as it would had it compiled the source itself.
        CompileInputs.Restate(inputs: compiled!.Inputs);

        return true;
    }
    private string? EntryPath(string key) => ((Directory is { } directory)
        ? Path.Combine(
            path1: directory,
            path2: (ContentPin.OfText(text: $"{CompilerIdentity}\n{key}").Hex[..32] + EntryExtension)
        )
        : null
    );
    private bool TryReadPersisted(string key, out WorldCompiledSource? compiled) {
        compiled = ReadPersisted(key: key, read: static reader => {
            var inputs = new CompileInput[reader.ReadInt32()];

            for (var index = 0; (index < inputs.Length); index++) {
                inputs[index] = new CompileInput(
                    ContentHash: reader.ReadString(),
                    Kind: ((CompileInputKind)reader.ReadByte()),
                    Path: reader.ReadString()
                );
            }

            var document = (reader.ReadBoolean()
                ? reader.ReadBytes(count: reader.ReadInt32())
                : null);
            var emitsDocument = reader.ReadBoolean();
            var worlds = ReadWorlds(reader: reader);
            var schema = (reader.ReadBoolean()
                ? reader.ReadString()
                : null);
            var tests = new WorldCompiledTest[reader.ReadInt32()];

            for (var index = 0; (index < tests.Length); index++) {
                tests[index] = new WorldCompiledTest(
                    Name: reader.ReadString(),
                    Test: reader.ReadString(),
                    Subject: (reader.ReadBoolean()
                        ? reader.ReadString()
                        : null),
                    Json: reader.ReadBytes(count: reader.ReadInt32()),
                    Siblings: ReadWorlds(reader: reader),
                    Foreign: WorldTestForeign.FromJson(json: reader.ReadString())
                );
            }

            return new WorldCompiledSource(
                Document: document,
                EmitsDocument: emitsDocument,
                Inputs: inputs,
                Schema: schema,
                Tests: tests,
                Worlds: worlds
            );

        });
        return (compiled is not null);
    }
    private void WritePersisted(string key, WorldCompiledSource compiled) => WritePersisted(key: key, write: writer => {
        writer.Write(value: compiled.Inputs.Count);
        foreach (var input in compiled.Inputs) {
            writer.Write(value: input.ContentHash);
            writer.Write(value: ((byte)input.Kind));
            writer.Write(value: input.Path);
        }
        writer.Write(value: (compiled.Document is not null));
        if (compiled.Document is { } document) {
            writer.Write(value: document.Length);
            writer.Write(buffer: document);
        }
        writer.Write(value: compiled.EmitsDocument);
        WriteWorlds(
            worlds: compiled.Worlds,
            writer: writer
        );
        writer.Write(value: (compiled.Schema is not null));
        if (compiled.Schema is { } schema) {
            writer.Write(value: schema);
        }
        writer.Write(value: compiled.Tests.Count);
        foreach (var test in compiled.Tests) {
            writer.Write(value: test.Name);
            writer.Write(value: test.Test);
            writer.Write(value: (test.Subject is not null));
            if (test.Subject is { } subject) {
                writer.Write(value: subject);
            }
            writer.Write(value: test.Json.Length);
            writer.Write(buffer: test.Json);
            WriteWorlds(
                worlds: test.Siblings,
                writer: writer
            );
            writer.Write(value: WorldTestForeign.ToJson(expectations: test.Foreign));
        }
    });
    // The order every field is written in is the order it is read back in; a world is its entry flag, its name and
    // its length-prefixed document.
    private static WorldCompiledWorld[] ReadWorlds(BinaryReader reader) {
        var worlds = new WorldCompiledWorld[reader.ReadInt32()];

        for (var index = 0; (index < worlds.Length); index++) {
            worlds[index] = new WorldCompiledWorld(
                Entry: reader.ReadBoolean(),
                Name: reader.ReadString(),
                Json: reader.ReadBytes(count: reader.ReadInt32())
            );
        }

        return worlds;
    }
    private static void WriteWorlds(BinaryWriter writer, IReadOnlyList<WorldCompiledWorld> worlds) {
        writer.Write(value: worlds.Count);
        foreach (var world in worlds) {
            writer.Write(value: world.Entry);
            writer.Write(value: world.Name);
            writer.Write(value: world.Json.Length);
            writer.Write(buffer: world.Json);
        }
    }
    private static readonly Lazy<(string Built, IReadOnlySet<string> Modules)> Identity = new(valueFactory: ComputeCompilerIdentity);
}
