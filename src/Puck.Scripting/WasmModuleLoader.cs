using System.Text;

using Puck.Assets;

using Module = Wasmtime.Module;

namespace Puck.Scripting;

/// <summary>
/// Loads and compiles addon modules the way <c>ShaderModuleLoader</c> loads shaders: read bytes through an
/// <see cref="IAssetSource"/>, identify the format by its leading magic (a <c>\0asm</c> binary vs. WAT text),
/// compile to a <see cref="Wasmtime.Module"/>, and cache the compiled module in a content-addressed LRU keyed
/// by the full <see cref="ContentPin"/> so two documents naming the same bytes compile once.
/// </summary>
public sealed class WasmModuleLoader {
    private const int DefaultMaxCachedModules = 64;

    private static readonly SemaphoreSlim CompilationSlots = new(initialCount: AddonModuleLimits.ConcurrentCompilations);

    private readonly Lock m_cacheGate = new();
    private readonly IAssetSource m_assetSource;
    private readonly ScriptingEngine m_engine;
    private readonly ContentAddressedLruCache<ContentPin, CompiledModule> m_moduleCache;

    /// <summary>Initializes a loader over the given engine and asset source with a default cache capacity.</summary>
    /// <param name="engine">The engine compiled modules bind to.</param>
    /// <param name="assetSource">The source module bytes are read from.</param>
    public WasmModuleLoader(ScriptingEngine engine, IAssetSource assetSource)
        : this(
        assetSource: assetSource,
        engine: engine,
        maxCachedModules: DefaultMaxCachedModules
    ) {
    }
    /// <summary>Initializes a loader over the given engine and asset source.</summary>
    /// <param name="engine">The engine compiled modules bind to.</param>
    /// <param name="assetSource">The source module bytes are read from.</param>
    /// <param name="maxCachedModules">The compiled-module cache capacity. Must be positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> or <paramref name="assetSource"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCachedModules"/> is not positive.</exception>
    public WasmModuleLoader(ScriptingEngine engine, IAssetSource assetSource, int maxCachedModules) {
        ArgumentNullException.ThrowIfNull(argument: assetSource);
        ArgumentNullException.ThrowIfNull(argument: engine);

        if (maxCachedModules <= 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: maxCachedModules,
                message: "Max cached modules must be positive.",
                paramName: nameof(maxCachedModules)
            );
        }

        m_assetSource = assetSource;
        m_engine = engine;
        // No eviction disposal: active instances retain their module independently of this cache. Wasmtime's
        // module handle is reclaimed only after neither a live instance nor the cache references it.
        m_moduleCache = new ContentAddressedLruCache<ContentPin, CompiledModule>(capacity: maxCachedModules);
    }

    // A compiled module beside the memories its binary declares, cached together under the bytes' content hash.
    private sealed record CompiledModule(Module Module, IReadOnlyList<WasmMemoryDeclaration> Memories);

    // Reads the binary's declarations before Wasmtime sees it, so a malformed memory section is refused here by name
    // and a memory past the store's ceiling is known without instantiating anything. WAT text is converted to its
    // binary first, which parses but neither compiles nor instantiates.
    private CompiledModule Compile(ReadOnlyMemory<byte> content, string name, ContentPin hash) {
        var binary = (content.Span.StartsWith(value: WasmBinaryFormat.WasmMagic)
            ? content.ToArray()
            : Module.ConvertText(wat: Encoding.UTF8.GetString(bytes: content.Span)));

        if (binary.Length > AddonModuleLimits.BinaryBytes) {
            throw new InvalidDataException(message: $"The addon module {name} exceeds the {AddonModuleLimits.BinaryBytes}-byte binary limit.");
        }

        if (!WasmModuleDeclarations.TryRead(
            declarations: out var declarations,
            error: out var error,
            module: binary
        )) {
            throw new InvalidDataException(message: $"The addon module {name} is malformed: {error}.");
        }

        if (declarations.Imports.Count != 0) {
            var import = declarations.Imports[0];

            throw new InvalidDataException(message: $"The addon module {name} declares import '{import.Module}::{import.Name}'; addons may import nothing.");
        }

        if (declarations.Memories.Count > AddonModuleLimits.Memories) {
            throw new AddonModuleLoadException(AddonFaultKind.MemoryLimit, hash,
                $"MemoryLimit — the module declares {declarations.Memories.Count} memories; at most {AddonModuleLimits.Memories} is admitted, exported or not");
        }
        foreach (var memory in declarations.Memories) {
            if (memory.MinimumBytes > AddonModuleLimits.MemoryBytes) {
                throw new AddonModuleLoadException(AddonFaultKind.MemoryLimit, hash,
                    $"MemoryLimit — memory declares {memory.MinimumPages} pages of 2^{memory.PageSizeLog2} bytes, past the {AddonModuleLimits.MemoryBytes}-byte ceiling");
            }
        }
        if ((declarations.Tables.Count > AddonModuleLimits.Tables) ||
            declarations.Tables.Any(predicate: table => (table.MinimumElements > AddonModuleLimits.TableElements))) {
            throw new InvalidDataException(message: $"The addon module {name} exceeds the table limit: at most {AddonModuleLimits.Tables} table of {AddonModuleLimits.TableElements} elements, exported or not.");
        }

        return new CompiledModule(
            Memories: declarations.Memories,
            Module: Module.FromBytes(
                bytes: binary,
                engine: m_engine.Engine,
                name: name
            )
        );
    }

    /// <summary>Reads, compiles (or returns a cached), and identifies the addon module at <paramref name="path"/>.</summary>
    /// <param name="path">The module path (a <c>.wasm</c> binary or a <c>.wat</c> text module).</param>
    /// <param name="expectedHash">The optional document hash checked against source bytes before conversion,
    /// compilation, or cache lookup. Only a full canonical SHA-256 pin is admitted.</param>
    /// <returns>The compiled module and its content identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="FileNotFoundException">No module exists at <paramref name="path"/>.</exception>
    /// <exception cref="InvalidDataException">The module is empty, or its binary is malformed in a section the host reads
    /// before compiling (its imports, memories, tables or exports), exceeds a source/binary/table limit, or the
    /// process-wide concurrent module-load capacity is occupied. A capacity refusal may be retried.</exception>
    /// <exception cref="AddonModuleLoadException">The source hash or declared memory resources are refused before compilation.</exception>
    /// <exception cref="Wasmtime.WasmtimeException">The bytes are not valid wasm/WAT.</exception>
    public ScriptingModuleInfo Load(string path, string? expectedHash = null) {
        if (!CompilationSlots.Wait(millisecondsTimeout: 0)) {
            throw new InvalidDataException(message: $"Addon compilation capacity is full ({AddonModuleLimits.ConcurrentCompilations} concurrent loads); retry after a load completes.");
        }
        try {
            return LoadAdmitted(expectedHash: expectedHash, path: path);
        } finally {
            CompilationSlots.Release();
        }
    }

    private ScriptingModuleInfo LoadAdmitted(string path, string? expectedHash) {
        if (string.IsNullOrWhiteSpace(value: path)) {
            throw new ArgumentException(
                message: "Addon module path must be provided.",
                paramName: nameof(path)
            );
        }

        var fullPath = Path.GetFullPath(path: path);

        if (!m_assetSource.Exists(path: fullPath)) {
            throw new FileNotFoundException(
                fileName: fullPath,
                message: $"The addon module file was not found: {fullPath}"
            );
        }

        var content = m_assetSource.Read(maximumBytes: AddonModuleLimits.SourceBytes, path: fullPath);
        var byteLength = content.Length;

        // Also check the return value, so an incorrect custom source cannot pass oversized bytes to native code.
        if (byteLength > AddonModuleLimits.SourceBytes) {
            throw new InvalidDataException(message: $"The addon module {fullPath} exceeds the {AddonModuleLimits.SourceBytes}-byte source limit.");
        }

        if (byteLength == 0) {
            throw new InvalidDataException(message: $"The addon module file is empty: {fullPath}");
        }

        var contentHash = ContentPin.Compute(content: content.Span);

        if ((expectedHash is not null) && !string.Equals(a: contentHash.ToString(), b: expectedHash, comparisonType: StringComparison.Ordinal)) {
            throw new AddonModuleLoadException(hash: contentHash, kind: AddonFaultKind.HashMismatch,
                message: $"HashMismatch — content {contentHash} does not match the declared moduleHash pin {expectedHash}");
        }
        var moduleName = Path.GetFileNameWithoutExtension(path: fullPath);
        CompiledModule compiled;

        lock (m_cacheGate) {
            compiled = m_moduleCache.GetOrAdd(
                hash: contentHash,
                valueFactory: () => Compile(
                    content: content,
                    hash: contentHash,
                    name: moduleName
                )
            );
        }

        return new ScriptingModuleInfo(
            ByteLength: byteLength,
            ContentHash: contentHash,
            Memories: compiled.Memories,
            Module: compiled.Module,
            Path: fullPath
        );
    }
}
