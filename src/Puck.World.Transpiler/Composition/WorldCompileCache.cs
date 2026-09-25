using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Puck.Assets;
using Puck.Transpiler.Modules;

namespace Puck.World.Transpiler.Composition;

/// <summary>One world a composition source declares, as a compile left it.</summary>
/// <param name="Name">The world's declared name.</param>
/// <param name="Entry">Whether the source declares it the world a boot starts in.</param>
/// <param name="Json">The world's canonical document, UTF-8.</param>
public sealed record WorldCompiledWorld(string Name, bool Entry, byte[] Json);
/// <summary>One world a <c>test</c> block generated, as a compile left it.</summary>
/// <param name="Name">The generated world's name.</param>
/// <param name="Test">The test's own name, as written.</param>
/// <param name="Subject">The module instantiation the test stands up, or <see langword="null"/> for the enclosing
/// document.</param>
/// <param name="Json">The generated world's canonical document, UTF-8.</param>
/// <param name="Siblings">The other worlds of the composition the test boots beside it, in order; their
/// <see cref="WorldCompiledWorld.Entry"/> is <see langword="false"/>.</param>
public sealed record WorldCompiledTest(string Name, string Test, string? Subject, byte[] Json, IReadOnlyList<WorldCompiledWorld> Siblings);
/// <summary>What one successful compile of a <c>.puck</c> world source produced, and every file fact it rests on.</summary>
/// <param name="Document">The one document the source lowers to, UTF-8, exactly the bytes
/// <c>WorldCompilation.RequireJson().ToJsonString()</c> encodes; <see langword="null"/> when the source declares
/// several worlds.</param>
/// <param name="EmitsDocument">Whether the source emits a document and so carries a document name
/// (<see cref="WorldCompilation.EmitsDocument"/>); <see langword="false"/> for a module library, whose
/// <paramref name="Document"/> is the empty document its top level lowers to.</param>
/// <param name="Worlds">The worlds the source declares, in declaration order; empty for a single-document source.</param>
/// <param name="Schema">The schema the source declares, or <see langword="null"/> for a module that declares none.</param>
/// <param name="Tests">The worlds the source's <c>test</c> blocks generate, in written order.</param>
/// <param name="Inputs">Every file fact the compile read, the source first.</param>
public sealed record WorldCompiledSource(byte[]? Document, bool EmitsDocument, IReadOnlyList<WorldCompiledWorld> Worlds, string? Schema, IReadOnlyList<WorldCompiledTest> Tests, IReadOnlyList<CompileInput> Inputs) {
    /// <summary>Returns the document names the source emits, relative to its directory
    /// (<see cref="WorldCompilation.EmittedNames"/>).</summary>
    /// <param name="sourcePath">The source's path, spelled as the file system spells its file name.</param>
    /// <returns>The emitted document names, in declaration order.</returns>
    public IReadOnlyList<string> DocumentNames(string sourcePath) => WorldCompilation.EmittedNames(
        declaredWorlds: Worlds.Select(selector: static world => world.Name),
        emitsDocument: EmitsDocument,
        sourcePath: sourcePath
    );
    /// <summary>Returns the document the source emits under <paramref name="name"/>, one of its
    /// <see cref="DocumentNames"/>: the world a composition declares under exactly that name, or the one document an
    /// ordinary source lowers to.</summary>
    /// <param name="name">An emitted document name.</param>
    /// <returns>The document's UTF-8 JSON, or <see langword="null"/> when a composition declares no world of that
    /// name.</returns>
    public byte[]? DocumentNamed(string name) => ((Worlds.Count > 0)
        ? Worlds.FirstOrDefault(predicate: world => string.Equals(
            a: world.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ))?.Json
        : Document
    );
}
/// <summary>
/// The compile cache every door that needs only a <c>.puck</c> source's documents goes through — the game's boot
/// loader, the document composer every basis and import reads through (and so <c>world.reload</c>), and
/// <c>puck test</c>'s own compile of the source its tests generate worlds from — so an unchanged source is compiled
/// once. A held compile is keyed by the source's full path, spelled with <c>/</c> on every platform and case-folded
/// where the file system ignores case, so every spelling of one file shares one entry. It is served again only while every file fact it read still
/// holds: the source, each module its import walk read, the embedding and asset locks, every asset it hashed, every
/// path it only probed, absent or present, and the listing and sources of every directory its basis name resolved in
/// through the name index (<see cref="WorldSourceIndex"/>, <see cref="CompileInputs"/>). Editing any of them recompiles exactly
/// the sources that read it; nothing else is consulted, so no clock takes part.
/// <para>
/// Held compiles live in memory for the process and, once <see cref="Persist"/> names a directory, in that directory
/// too, so a later process compiles nothing an earlier one already compiled. An entry belongs to one compiler build:
/// the identity of every non-framework assembly the compiler loads is part of the entry's name and is recorded inside
/// it, so a rebuilt compiler never reads an older one's output, and builds that share a directory (an editor's
/// <c>puck lsp</c>, a candidate CLI, the game) keep their own entries rather than overwriting each other's. The
/// directory keeps at most <see cref="MaxPersistedEntries"/> entries, and a write also removes the temporary files an
/// interrupted write left behind. A compile that fails is never held: its diagnostics come from compiling again.
/// </para>
/// <para>
/// Callers on several threads may compile at once. Concurrent misses on one source compile it once: the rest wait for
/// that compile and are served it as a hit. A hit never replaces a held compile with an older one.
/// </para>
/// </summary>
public sealed class WorldCompileCache {
    /// <summary>The most entries the persistent directory keeps; writing one more removes the least recently written.</summary>
    public const int MaxPersistedEntries = 1024;

    private const string EntryExtension = ".compiled";
    private const string TemporaryExtension = ".tmp";

    // A temporary file untouched for this long belongs to a write that will never finish; a younger one may be
    // another process's write in flight.
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(minutes: 1);
    private static readonly byte[] Magic = "PUCKWCC3"u8.ToArray();
    private readonly ConcurrentDictionary<string, Lock> m_compiling = new(comparer: StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorldCompiledSource> m_held = new(comparer: StringComparer.Ordinal);
    private readonly Lock m_persistLock = new();

    private string? m_directory;

    /// <summary>Initializes a new instance of the <see cref="WorldCompileCache"/> class.</summary>
    /// <param name="directory">The directory held compiles persist in, created on first write; <see langword="null"/>
    /// to hold them in memory only.</param>
    public WorldCompileCache(string? directory = null) {
        m_directory = ((directory is null)
            ? null
            : Path.GetFullPath(path: directory)
        );
    }

    /// <summary>Gets the process's cache, the one the boot loader and the document composer compile through. It
    /// holds compiles in memory until a host names a directory through <see cref="Persist"/>; only an executable's
    /// entry point names one, so a test composing World services never persists to the per-user cache.</summary>
    public static WorldCompileCache Shared { get; } = new();

    /// <summary>Gets the directory held compiles persist in, or <see langword="null"/> when they are held in memory
    /// only.</summary>
    public string? Directory => Volatile.Read(location: ref m_directory);

    private static string CompilerIdentity => Identity.Value;

    // The identity walks the compiler's reference graph from its own assembly. In a trimmed application a reference
    // the trimmer removed is code the compile cannot run, so its load failing and entering the identity as absent is
    // exactly the right answer.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        category: "Trimming",
        checkId: "IL2026",
        Justification = "A reference the trimmer removed cannot run, and its failed load enters the identity as absent."
    )]
    private static string ComputeCompilerIdentity() {
        var modules = new SortedDictionary<string, Guid>(comparer: StringComparer.Ordinal);
        var pending = new Stack<Assembly>();

        pending.Push(item: typeof(WorldCompiler).Assembly);
        while (pending.TryPop(result: out var assembly)) {
            var name = (assembly.GetName().Name ?? string.Empty);

            if (!modules.TryAdd(
                key: name,
                value: assembly.ManifestModule.ModuleVersionId
            )) {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies()) {
                if (IsFramework(name: reference.Name) || modules.ContainsKey(key: (reference.Name ?? string.Empty))) {
                    continue;
                }

                try {
                    pending.Push(item: Assembly.Load(assemblyRef: reference));
                } catch (Exception exception) when ((exception is FileNotFoundException or FileLoadException or BadImageFormatException)) {
                    _ = modules.TryAdd(
                        key: (reference.Name ?? string.Empty),
                        value: Guid.Empty
                    );
                }
            }
        }

        var text = new StringBuilder();

        foreach (var (name, version) in modules) {
            _ = text.Append(value: name).Append(value: '=').Append(value: version.ToString(format: "N")).Append(value: ';');
        }

        return ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: text.ToString())).Hex;
    }
    private static bool IsFramework(string? name) => (
        (name is null) ||
        name.StartsWith(comparisonType: StringComparison.Ordinal, value: "System") ||
        name.StartsWith(comparisonType: StringComparison.Ordinal, value: "Microsoft") ||
        (name is "netstandard" or "mscorlib" or "WindowsBase")
    );
    // A key spells a full path with '/' as its only separator, on every platform, and folds case only where the file
    // system ignores it, so every spelling of one file shares one entry.
    private static string KeyOf(string fullPath) {
        var key = Puck.Abstractions.PuckPaths.Normalize(path: fullPath);

        return (Puck.Abstractions.PuckPaths.FoldsCase
            ? key.ToUpperInvariant()
            : key
        );
    }
    private static bool Stands(WorldCompiledSource compiled) {
        foreach (var input in compiled.Inputs) {
            if (!input.StillHolds()) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Names the directory held compiles persist in, for this cache's life. A host calls this once at
    /// startup; naming the same directory again changes nothing.</summary>
    /// <param name="directory">The directory, created on first write.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white space.</exception>
    /// <exception cref="InvalidOperationException">A different directory was already named.</exception>
    public void Persist(string directory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);

        var full = Path.GetFullPath(path: directory);

        lock (m_persistLock) {
            if (m_directory is { } named) {
                if (!string.Equals(
                    a: KeyOf(fullPath: named),
                    b: KeyOf(fullPath: full),
                    comparisonType: StringComparison.Ordinal
                )) {
                    throw new InvalidOperationException(message: $"The compile cache already persists in '{named}'; it cannot also persist in '{full}'.");
                }

                return;
            }

            Volatile.Write(
                location: ref m_directory,
                value: full
            );
        }
    }
    /// <summary>Returns the documents <paramref name="path"/> compiles to, from a held compile whose every file fact
    /// still holds, or by compiling it and holding the result. Callers that miss on one source at once compile it
    /// once; the others wait for that compile and are served it.</summary>
    /// <param name="path">The <c>.puck</c> source. A relative path resolves against the current directory, and the
    /// compile sees the full path, so every file fact it records is a full path that any process can check.</param>
    /// <param name="compiled">The source's documents on success; <see langword="null"/> when it does not compile.</param>
    /// <param name="failure">The failed compilation, whose diagnostics say why; <see langword="null"/> on
    /// success.</param>
    /// <returns><see langword="true"/> when the source compiled, now or earlier.</returns>
    /// <exception cref="IOException">The source could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The source may not be read.</exception>
    public bool TryCompile(string path, out WorldCompiledSource? compiled, out WorldCompilation? failure) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        var fullPath = Path.GetFullPath(path: path);
        var key = KeyOf(fullPath: fullPath);

        failure = null;

        if (TryServe(compiled: out compiled, key: key)) {
            return true;
        }

        // One compile per source at a time: a caller that waited here finds the compile the one before it held.
        lock (m_compiling.GetOrAdd(key: key, valueFactory: static _ => new Lock())) {
            if (TryServe(compiled: out compiled, key: key)) {
                return true;
            }

            var reads = new CompileInputLog();
            WorldCompilation compilation;

            using (CompileInputs.Record(log: reads)) {
                compilation = WorldCompiler.CompileFile(
                    allowMultiple: true,
                    path: fullPath
                );
            }

            if (!compilation.Success) {
                compiled = null;
                failure = compilation;

                return false;
            }

            compiled = new WorldCompiledSource(
                Document: ((compilation.Json is { } document)
                    ? Encoding.UTF8.GetBytes(s: document.ToJsonString())
                    : null),
                EmitsDocument: compilation.EmitsDocument,
                Inputs: reads.Inputs,
                Schema: compilation.Document?.Schema,
                Tests: [
                    .. compilation.TestWorlds.Select(selector: static test => new WorldCompiledTest(
                        Json: Encoding.UTF8.GetBytes(s: test.Json.ToJsonString()),
                        Name: test.Name,
                        Siblings: [
                            .. test.Siblings.Select(selector: static sibling => new WorldCompiledWorld(
                                Entry: false,
                                Json: Encoding.UTF8.GetBytes(s: sibling.Json.ToJsonString()),
                                Name: sibling.Name
                            )),
                        ],
                        Subject: test.Subject,
                        Test: test.Test
                    )),
                ],
                Worlds: [
                    .. compilation.Worlds.Select(selector: static world => new WorldCompiledWorld(
                        Entry: world.Entry,
                        Json: Encoding.UTF8.GetBytes(s: world.Json.ToJsonString()),
                        Name: world.Name
                    )),
                ]
            );
            // Only the caller holding this source's lock stores a compile, so the one it stores is the newest.
            m_held[key] = compiled;
            WritePersisted(
                compiled: compiled,
                key: key
            );

            return true;
        }
    }

    // A hit: the held compile if it still stands, else the persisted entry if it stands. An entry read back from disk
    // is held only in place of the entry this caller found, so a newer compile stored meanwhile is never displaced.
    private bool TryServe(string key, out WorldCompiledSource? compiled) {
        var found = m_held.TryGetValue(
            key: key,
            value: out var held
        );

        if (found && Stands(compiled: held!)) {
            compiled = held;
        } else if (TryReadPersisted(
            compiled: out var persisted,
            key: key
        ) && Stands(compiled: persisted!)) {
            _ = (found
                ? m_held.TryUpdate(
                    comparisonValue: held!,
                    key: key,
                    newValue: persisted!
                )
                : m_held.TryAdd(
                    key: key,
                    value: persisted!
                ));
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
            path2: (ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: $"{CompilerIdentity}\n{key}")).Hex[..32] + EntryExtension)
        )
        : null
    );
    // A persisted entry is read whole and trusted only if its trailing digest covers everything before it and it was
    // written by this compiler build for this path; anything else, torn or foreign, is a miss.
    private bool TryReadPersisted(string key, out WorldCompiledSource? compiled) {
        compiled = null;

        if (EntryPath(key: key) is not { } path) {
            return false;
        }

        byte[] bytes;

        try {
            if (!File.Exists(path: path)) {
                return false;
            }

            bytes = File.ReadAllBytes(path: path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return false;
        }

        if (bytes.Length < (Magic.Length + 32)) {
            return false;
        }

        var body = bytes.AsSpan(start: 0, length: (bytes.Length - 32));
        Span<byte> digest = stackalloc byte[32];

        SHA256.HashData(
            destination: digest,
            source: body
        );

        if (!digest.SequenceEqual(other: bytes.AsSpan(start: body.Length)) || !body.StartsWith(value: Magic)) {
            return false;
        }

        try {
            using var reader = new BinaryReader(
                encoding: Encoding.UTF8,
                input: new MemoryStream(
                    buffer: bytes,
                    count: (body.Length - Magic.Length),
                    index: Magic.Length,
                    writable: false
                )
            );

            if (
                !string.Equals(a: reader.ReadString(), b: CompilerIdentity, comparisonType: StringComparison.Ordinal) ||
                !string.Equals(a: reader.ReadString(), b: key, comparisonType: StringComparison.Ordinal)
            ) {
                return false;
            }

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
                    Siblings: ReadWorlds(reader: reader)
                );
            }

            compiled = new WorldCompiledSource(
                Document: document,
                EmitsDocument: emitsDocument,
                Inputs: inputs,
                Schema: schema,
                Tests: tests,
                Worlds: worlds
            );

            return true;
        } catch (Exception exception) when ((exception is EndOfStreamException or IOException or FormatException)) {
            return false;
        }
    }
    // Best effort: an entry that cannot be written is only a later compile. Each write lands whole through a
    // sibling temporary file, so a reader never sees a torn entry under the entry's own name.
    private void WritePersisted(string key, WorldCompiledSource compiled) {
        if (EntryPath(key: key) is not { } path) {
            return;
        }

        var stream = new MemoryStream();

        stream.Write(buffer: Magic);
        using (var writer = new BinaryWriter(
            encoding: Encoding.UTF8,
            leaveOpen: true,
            output: stream
        )) {
            writer.Write(value: CompilerIdentity);
            writer.Write(value: key);
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
            }
        }

        stream.Write(buffer: SHA256.HashData(source: stream.GetBuffer().AsSpan(start: 0, length: ((int)stream.Length))));

        try {
            AtomicFile.WriteAllBytes(
                bytes: stream.ToArray(),
                path: path
            );
            Trim(directory: Path.GetDirectoryName(path: path)!);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // Best effort: an entry that cannot be written is only a later compile.
        }
    }
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
    // Keeps the newest MaxPersistedEntries entries and removes every temporary file an interrupted write abandoned.
    private static void Trim(string directory) {
        var files = new DirectoryInfo(path: directory).GetFiles();
        var abandoned = (DateTime.UtcNow - AbandonedAfter);
        var entries = new List<FileInfo>(capacity: files.Length);

        foreach (var file in files) {
            if (file.Name.EndsWith(comparisonType: StringComparison.Ordinal, value: EntryExtension)) {
                entries.Add(item: file);
            } else if (file.Name.EndsWith(comparisonType: StringComparison.Ordinal, value: TemporaryExtension) && (file.LastWriteTimeUtc < abandoned)) {
                Remove(file: file);
            }
        }

        if (entries.Count <= MaxPersistedEntries) {
            return;
        }

        foreach (var stale in entries.OrderBy(keySelector: static entry => entry.LastWriteTimeUtc).Take(count: (entries.Count - MaxPersistedEntries))) {
            Remove(file: stale);
        }
    }
    private static void Remove(FileInfo file) {
        try {
            file.Delete();
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return;
        }
    }

    private static readonly Lazy<string> Identity = new(valueFactory: ComputeCompilerIdentity);
}
