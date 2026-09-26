using System.Buffers;
using System.Collections;
using System.Text;
using Puck.Assets;

namespace Puck.World;

/// <summary>One derivation a compiled world stores the product of: a chunk code, the version of the code that derives
/// it, and how to derive, load, and re-check it. A deliberate change to what a derivation produces moves its
/// <see cref="Version"/>, so a compiled world written before the change re-derives the chunk instead of serving
/// it.</summary>
public interface ICompiledWorldChunk {
    /// <summary>Gets the chunk's code.</summary>
    ChunkCode Code { get; }
    /// <summary>Gets the codes of the earlier chunks whose products this derivation reads; when a boot derives one of
    /// them afresh, it derives this chunk afresh too rather than keeping it.</summary>
    IReadOnlyList<ChunkCode> DependsOn { get; }
    /// <summary>Gets whether a boot derives this chunk when no compiled world holds it. A derivation too heavy for a
    /// boot's critical path answers <see langword="false"/>: only <see cref="CompiledWorld.TryCompile"/> derives it, and
    /// a boot that finds none leaves the chunk out, for background work to cover.</summary>
    bool DerivesOnBoot { get; }
    /// <summary>Gets the version of the derivation.</summary>
    uint Version { get; }

    /// <summary>Returns the current content hash of one input the derivation recorded.</summary>
    /// <param name="context">The world the input is read for; its authored definition's
    /// <see cref="WorldDefinition.DocumentDirectory"/> roots a document-relative input.</param>
    /// <param name="name">The input's name, as the derivation recorded it.</param>
    /// <returns>The input's content hash now, or <see langword="null"/> when it is absent.</returns>
    AssetContentHash? ReadInput(CompiledWorldContext context, string name);
    /// <summary>Derives the chunk's product and leaves <paramref name="context"/> as loading that product would.</summary>
    /// <param name="context">The world being compiled; earlier chunks' products are already in it.</param>
    /// <param name="product">The payload and the inputs it read, or <see langword="null"/> on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the product derived.</returns>
    bool TryDerive(CompiledWorldContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out CompiledWorldProduct? product, out string reason);
    /// <summary>Loads a stored payload into <paramref name="context"/>.</summary>
    /// <param name="context">The world being booted; earlier chunks' products are already in it.</param>
    /// <param name="payload">The stored payload.</param>
    /// <param name="reason">Why the payload cannot be loaded, or empty on success.</param>
    /// <returns><see langword="true"/> when the payload loaded; otherwise the chunk is derived instead.</returns>
    bool TryLoad(CompiledWorldContext context, ReadOnlyMemory<byte> payload, out string reason);
}
/// <summary>A derivation's product: the payload a chunk stores and the inputs the derivation read beyond the
/// definition, in ordinal order of their names.</summary>
/// <param name="Payload">The payload.</param>
/// <param name="Inputs">The inputs read.</param>
public sealed record CompiledWorldProduct(ReadOnlyMemory<byte> Payload, IReadOnlyList<ChunkInput> Inputs);
/// <summary>The world a compiled world's chunks are derived for or loaded into: the authored definition the header
/// keys, the instance its draws are seeded for, the document it stands for, and the products chunks have produced so
/// far.</summary>
public sealed class CompiledWorldContext {
    /// <summary>Initializes a new instance of the <see cref="CompiledWorldContext"/> class.</summary>
    /// <param name="authored">The parsed, composed, undrawn definition.</param>
    /// <param name="instanceIdentity">The instance identity the draws are seeded for.</param>
    /// <param name="sourceName">The origin echoed in refusals.</param>
    /// <param name="documentPath">The full path of the document a boot loads, which a chunk's references to files
    /// beside it resolve against, or <see langword="null"/> when there is none.</param>
    public CompiledWorldContext(WorldDefinition authored, string instanceIdentity, string sourceName, string? documentPath = null) {
        ArgumentNullException.ThrowIfNull(argument: authored);

        Authored = authored;
        DocumentPath = documentPath;
        InstanceIdentity = instanceIdentity;
        SourceName = sourceName;
    }

    /// <summary>Gets the parsed, composed, undrawn definition.</summary>
    public WorldDefinition Authored { get; }
    /// <summary>Gets the full path of the document a boot loads, which a chunk's references to files beside it resolve
    /// against, or <see langword="null"/> when there is none.</summary>
    public string? DocumentPath { get; }
    /// <summary>Gets the drawn, resolved definition, or <see langword="null"/> until the <c>DEFN</c> chunk has been
    /// derived or loaded.</summary>
    public WorldDefinition? Drawn { get; set; }
    /// <summary>Gets the instance identity the draws are seeded for.</summary>
    public string InstanceIdentity { get; }
    /// <summary>Gets the origin echoed in refusals.</summary>
    public string SourceName { get; }

    /// <summary>Returns the drawn definition a chunk after <c>DEFN</c> derives from.</summary>
    /// <returns>The drawn definition.</returns>
    /// <exception cref="InvalidOperationException">No <c>DEFN</c> chunk has been derived or loaded.</exception>
    public WorldDefinition RequireDrawn() =>
        (Drawn ?? throw new InvalidOperationException(message: "a compiled-world chunk read the drawn definition before DEFN produced it"));
}
/// <summary>
/// The derivations a compiled world stores, in the order they derive and load. <c>DEFN</c> is always first, so the drawn
/// definition is in the context before any later chunk runs; a chunk that reads an earlier chunk's product names it in
/// <see cref="ICompiledWorldChunk.DependsOn"/>. A package adds its chunk with <see cref="With"/>; the container and the
/// header do not change.
/// </summary>
public sealed class CompiledWorldChunks : IReadOnlyList<ICompiledWorldChunk> {
    private readonly ICompiledWorldChunk[] m_chunks;

    private CompiledWorldChunks(ICompiledWorldChunk[] chunks) {
        m_chunks = chunks;
    }

    /// <summary>Gets the standard derivations: <see cref="DefinitionChunk"/> and <see cref="AssetChunk"/>.</summary>
    public static CompiledWorldChunks Standard { get; } = new(chunks: [DefinitionChunk.Instance, AssetChunk.Instance]);
    /// <inheritdoc/>
    public int Count => m_chunks.Length;

    /// <inheritdoc/>
    public ICompiledWorldChunk this[int index] => m_chunks[index];

    /// <inheritdoc/>
    public IEnumerator<ICompiledWorldChunk> GetEnumerator() =>
        ((IEnumerable<ICompiledWorldChunk>)m_chunks).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() =>
        GetEnumerator();

    /// <summary>Checks a stored chunk against its derivation: its version, and every input it recorded.</summary>
    /// <param name="chunk">The derivation.</param>
    /// <param name="stored">The stored chunk carrying the derivation's code.</param>
    /// <param name="context">The world the stored chunk would load into.</param>
    /// <returns><see langword="true"/> when the stored chunk still holds.</returns>
    public static bool Holds(ICompiledWorldChunk chunk, ContainerChunk stored, CompiledWorldContext context) {
        ArgumentNullException.ThrowIfNull(argument: chunk);
        ArgumentNullException.ThrowIfNull(argument: stored);

        if (stored.Version != chunk.Version) {
            return false;
        }

        foreach (var input in stored.Inputs) {
            if (chunk.ReadInput(context: context, name: input.Name) != input.Hash) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Derives one chunk fresh into the container form it is stored in, counting the derivation.</summary>
    /// <param name="chunk">The derivation.</param>
    /// <param name="context">The world being compiled.</param>
    /// <param name="derived">The stored form, or <see langword="null"/> on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the chunk derived.</returns>
    public bool TryDerive(ICompiledWorldChunk chunk, CompiledWorldContext context,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out ContainerChunk? derived, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: chunk);
        derived = null;

        if (!chunk.TryDerive(
            context: context,
            product: out var product,
            reason: out reason
        )) {
            return false;
        }

        WorldBootWork.Count(kind: WorldBootWork.ChunkDerivations);
        derived = new ContainerChunk(
            code: chunk.Code,
            inputs: product.Inputs,
            payload: product.Payload,
            version: chunk.Version
        );
        return true;
    }
    /// <summary>Returns these derivations with <paramref name="chunk"/> appended.</summary>
    /// <param name="chunk">The derivation to add; it derives after every derivation already here.</param>
    /// <returns>The extended set.</returns>
    /// <exception cref="ArgumentException">A derivation here already carries the code, or the chunk depends on a code
    /// no derivation here carries.</exception>
    public CompiledWorldChunks With(ICompiledWorldChunk chunk) {
        ArgumentNullException.ThrowIfNull(argument: chunk);

        if (m_chunks.Any(predicate: existing => (existing.Code == chunk.Code))) {
            throw new ArgumentException(
                message: $"a compiled world already derives chunk '{chunk.Code}'.",
                paramName: nameof(chunk)
            );
        }

        foreach (var dependency in chunk.DependsOn) {
            if (!m_chunks.Any(predicate: existing => (existing.Code == dependency))) {
                throw new ArgumentException(
                    message: $"chunk '{chunk.Code}' depends on '{dependency}', which no earlier derivation carries.",
                    paramName: nameof(chunk)
                );
            }
        }

        return new CompiledWorldChunks(chunks: [.. m_chunks, chunk]);
    }
}
/// <summary>The <c>DEFN</c> chunk: the drawn, resolved definition (<see cref="WorldDefinitionLoader.TryDraw"/>) as
/// compact canonical JSON (<see cref="WorldDefinitionSerialization.SerializeCompact"/>). It reads nothing beyond the
/// definition the header keys, so it records no inputs; loading it parses the JSON and resolves its state references,
/// in place of drawing the authored definition again.</summary>
public sealed class DefinitionChunk : ICompiledWorldChunk {
    private DefinitionChunk() { }

    /// <summary>Gets the one instance.</summary>
    public static DefinitionChunk Instance { get; } = new();
    /// <inheritdoc/>
    public ChunkCode Code { get; } = ChunkCode.Parse(text: "DEFN");
    /// <inheritdoc/>
    public IReadOnlyList<ChunkCode> DependsOn => [];
    /// <inheritdoc/>
    public bool DerivesOnBoot => true;
    /// <inheritdoc/>
    public uint Version => 2;

    /// <inheritdoc/>
    public AssetContentHash? ReadInput(CompiledWorldContext context, string name) => null;
    /// <inheritdoc/>
    public bool TryDerive(CompiledWorldContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out CompiledWorldProduct? product, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: context);
        product = null;

        if (!WorldDefinitionLoader.TryDraw(
            definition: context.Authored,
            drawn: out var drawn,
            instanceIdentity: context.InstanceIdentity,
            reason: out reason,
            sourceName: context.SourceName
        )) {
            return false;
        }

        context.Drawn = (drawn with { DocumentDirectory = context.Authored.DocumentDirectory });
        product = new CompiledWorldProduct(
            Inputs: [],
            Payload: WorldDefinitionSerialization.SerializeCompact(definition: drawn)
        );
        return true;
    }
    /// <inheritdoc/>
    public bool TryLoad(CompiledWorldContext context, ReadOnlyMemory<byte> payload, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: context);

        string json;

        try {
            json = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true
            ).GetString(bytes: payload.Span);
        } catch (DecoderFallbackException exception) {
            reason = $"its definition is not UTF-8: {exception.Message}";
            return false;
        }

        if (!WorldDefinitionFileSource.TryParseCompiledDocument(
            definition: out var drawn,
            json: json,
            reason: out reason,
            sourceName: context.SourceName
        )) {
            return false;
        }

        if (!WorldStateDocumentValues.TryResolve(
            definition: drawn!,
            reason: out var referenceReason
        )) {
            reason = $"{context.SourceName} could not resolve a state reference in its compiled definition: {referenceReason}";
            return false;
        }

        context.Drawn = (drawn! with { DocumentDirectory = context.Authored.DocumentDirectory });
        return true;
    }
}
/// <summary>The <c>ASST</c> chunk: the content hash of every asset file the definition's asset rows read
/// (<see cref="WorldAssetRowLoader.Rows"/>). Draws never touch asset rows, so it reads the authored definition the
/// header keys and depends on no earlier chunk. Each distinct source is an input keyed by its authored spelling, read
/// beside the document (<see cref="WorldDefinition.DocumentDirectory"/>), so an edited, added, or removed asset file
/// re-derives the chunk while the document and its assets stay movable together. The payload lists, in ordinal order of family then name,
/// each row's family, name, and source, a presence byte, and the file's 64-bit content hash when present.</summary>
public sealed class AssetChunk : ICompiledWorldChunk {
    private const int MaximumTextBytes = 4096;

    private AssetChunk() { }

    /// <summary>Gets the one instance.</summary>
    public static AssetChunk Instance { get; } = new();
    /// <inheritdoc/>
    public ChunkCode Code { get; } = ChunkCode.Parse(text: "ASST");
    /// <inheritdoc/>
    public IReadOnlyList<ChunkCode> DependsOn => [];
    /// <inheritdoc/>
    public bool DerivesOnBoot => true;
    /// <inheritdoc/>
    public uint Version => 1;

    /// <inheritdoc/>
    public AssetContentHash? ReadInput(CompiledWorldContext context, string name) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (!WorldDocumentPaths.TryResolve(
            documentDirectory: context.Authored.DocumentDirectory,
            path: name,
            reason: out _,
            resolved: out var path
        )) {
            return null;
        }

        try {
            return (File.Exists(path: path)
                ? AssetContentHash.Compute(content: File.ReadAllBytes(path: path))
                : null);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }
    }
    /// <inheritdoc/>
    public bool TryDerive(CompiledWorldContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out CompiledWorldProduct? product, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var rows = WorldAssetRowLoader.Rows(definition: context.Authored)
            .Order(comparer: Comparer<(string Family, string Name, string Source)>.Create(comparison: static (left, right) => {
                var family = string.CompareOrdinal(strA: left.Family, strB: right.Family);

                return ((family != 0)
                    ? family
                    : string.CompareOrdinal(strA: left.Name, strB: right.Name));
            }))
            .ToArray();
        var hashes = new SortedDictionary<string, AssetContentHash?>(comparer: StringComparer.Ordinal);
        var writer = new ArrayBufferWriter<byte>();

        writer.WriteVarUInt(value: checked((uint)rows.Length));

        foreach (var (family, name, source) in rows) {
            if (!hashes.TryGetValue(
                key: source,
                value: out var hash
            )) {
                hash = ReadInput(context: context, name: source);
                hashes.Add(
                    key: source,
                    value: hash
                );
            }

            writer.WriteText(value: family);
            writer.WriteText(value: name);
            writer.WriteText(value: source);

            if (hash is { } present) {
                writer.WriteByte(value: 1);
                writer.WriteUInt64(value: present.Value);
            } else {
                writer.WriteByte(value: 0);
            }
        }

        product = new CompiledWorldProduct(
            Inputs: [.. hashes.Select(selector: static pair => new ChunkInput(
                Hash: pair.Value,
                Name: pair.Key
            ))],
            Payload: writer.WrittenSpan.ToArray()
        );
        reason = string.Empty;
        return true;
    }
    /// <inheritdoc/>
    public bool TryLoad(CompiledWorldContext context, ReadOnlyMemory<byte> payload, out string reason) {
        try {
            var reader = new CanonicalBinaryReader(content: payload.Span);
            var count = reader.ReadBoundedInt(maximum: payload.Length);

            for (var index = 0; (index < count); ++index) {
                _ = reader.ReadText(maximumByteCount: MaximumTextBytes);
                _ = reader.ReadText(maximumByteCount: MaximumTextBytes);
                _ = reader.ReadText(maximumByteCount: MaximumTextBytes);

                switch (reader.ReadByte()) {
                    case 0:
                        break;
                    case 1:
                        _ = reader.ReadUInt64();
                        break;
                    default:
                        throw new InvalidDataException(message: "an asset's presence byte is invalid");
                }
            }

            reader.ExpectEnd();
        } catch (InvalidDataException exception) {
            reason = exception.Message;
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
