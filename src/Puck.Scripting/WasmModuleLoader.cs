using System.Text;

using Puck.Assets;

using Module = Wasmtime.Module;

namespace Puck.Scripting;

/// <summary>
/// Loads and compiles addon modules the way <c>ShaderModuleLoader</c> loads shaders: read bytes through an
/// <see cref="IAssetSource"/>, identify the format by its leading magic (a <c>\0asm</c> binary vs. WAT text),
/// compile to a <see cref="Wasmtime.Module"/>, and cache the compiled module in a content-addressed LRU keyed
/// by <see cref="AssetContentHash"/> so two documents naming the same bytes compile once.
/// </summary>
public sealed class WasmModuleLoader {
    private const int DefaultMaxCachedModules = 64;

    // The WebAssembly binary preamble: the four bytes '\0', 'a', 's', 'm'. Anything else is treated as WAT text.
    private static readonly byte[] WasmMagic = [0x00, 0x61, 0x73, 0x6D];
    private readonly IAssetSource m_assetSource;
    private readonly ScriptingEngine m_engine;
    private readonly ContentAddressedLruCache<Module> m_moduleCache;

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
        m_moduleCache = new ContentAddressedLruCache<Module>(capacity: maxCachedModules);
    }

    /// <summary>Reads, compiles (or returns a cached), and identifies the addon module at <paramref name="path"/>.</summary>
    /// <param name="path">The module path (a <c>.wasm</c> binary or a <c>.wat</c> text module).</param>
    /// <returns>The compiled module and its content identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="FileNotFoundException">No module exists at <paramref name="path"/>.</exception>
    /// <exception cref="InvalidDataException">The module is empty.</exception>
    /// <exception cref="Wasmtime.WasmtimeException">The bytes are not valid wasm/WAT.</exception>
    public ScriptingModuleInfo Load(string path) {
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

        var content = m_assetSource.Read(path: fullPath);
        var byteLength = content.Length;

        if (byteLength == 0) {
            throw new InvalidDataException(message: $"The addon module file is empty: {fullPath}");
        }

        var contentHash = AssetContentHash.Compute(content: content.Span);
        var moduleName = Path.GetFileNameWithoutExtension(path: fullPath);
        var module = m_moduleCache.GetOrAdd(
            hash: contentHash,
            valueFactory: () => Compile(
                content: content,
                name: moduleName
            )
        );

        return new ScriptingModuleInfo(
            ByteLength: byteLength,
            ContentHash: contentHash,
            Module: module,
            Path: fullPath
        );
    }

    private Module Compile(ReadOnlyMemory<byte> content, string name) {
        var span = content.Span;

        if (span.StartsWith(value: WasmMagic)) {
            return Module.FromBytes(
                bytes: span,
                engine: m_engine.Engine,
                name: name
            );
        }

        return Module.FromText(
            engine: m_engine.Engine,
            name: name,
            text: Encoding.UTF8.GetString(bytes: span)
        );
    }
}
