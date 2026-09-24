using System.Buffers;
using Puck.Assets;
using Puck.SignedDistance.Baking;

namespace Puck.World;

/// <summary>
/// The <c>BAKE</c> chunk: which creation bakes a compiled world needs and where they ship, so a released world bakes
/// nothing on a player's device and a bake several worlds share is stored once. It reads the drawn definition's
/// prototypes, so it depends on <c>DEFN</c>, and records no inputs beyond the definition. Its payload is the reference to
/// the build's bake pack (<see cref="WorldBakePack.Reference"/>, relative to the document's directory) as text, then a
/// count and each distinct bake key at <see cref="Quality"/>, as its pin's hex, in ascending ordinal order. The keys
/// carry the baker's version, so the chunk's <see cref="Version"/> is the baker's and a compiled world written by
/// another baker is derived again. Baking is too heavy for a boot's critical path, so the chunk does not derive on boot:
/// a boot that finds no compiled world holding it leaves it out, and the presentation bakes what it needs in the
/// background (<c>WorldBakeSchedule</c>).
/// <para>Deriving lists the keys and, given a <see cref="Store"/>, makes sure the store holds every key's outcome
/// (<see cref="WorldBakeStore.GetOrBake"/>), so the caller writes the pack from it and one process compiling several
/// worlds bakes a creation they share once. Loading, given a store, holds each key's outcome from the pack the reference
/// names (<see cref="WorldBakeStore.HoldFromPack"/>); a key the pack lacks, or a pack that is missing, is left to the
/// background bake.</para>
/// </summary>
/// <param name="store">The cache the chunk fills and reads, or <see langword="null"/> for none.</param>
/// <param name="packReference">The reference a derivation records, or <see langword="null"/> for a pack named
/// <see cref="WorldBakePack.FileName"/> beside the document.</param>
public sealed class WorldBakeChunk(WorldBakeStore? store, string? packReference = null) : ICompiledWorldChunk {
    /// <summary>The quality tier a compiled world names its bakes at.</summary>
    public const SdfBakeQuality Quality = SdfBakeQuality.Standard;

    private const int MaximumKeyBytes = 128;
    private const int MaximumReferenceBytes = 4096;

    /// <summary>Gets the chunk's code, <c>BAKE</c>.</summary>
    public static ChunkCode BakeCode { get; } = ChunkCode.Parse(text: "BAKE");

    /// <summary>Gets the cache the chunk fills and reads, or <see langword="null"/> for none.</summary>
    public WorldBakeStore? Store { get; } = store;
    /// <summary>Gets the pack reference a derivation records.</summary>
    public string PackReference { get; } = (packReference ?? WorldBakePack.FileName);

    /// <inheritdoc/>
    public ChunkCode Code => BakeCode;
    /// <inheritdoc/>
    public IReadOnlyList<ChunkCode> DependsOn => [DefinitionChunk.Instance.Code];
    /// <inheritdoc/>
    public bool DerivesOnBoot => false;
    /// <inheritdoc/>
    public uint Version => SdfBaker.Version;

    /// <summary>Returns <paramref name="chunks"/> with a <c>BAKE</c> chunk over <paramref name="store"/> appended.</summary>
    /// <param name="chunks">The derivations to extend.</param>
    /// <param name="store">The cache the chunk fills and reads, or <see langword="null"/> for none.</param>
    /// <param name="packReference">The reference a derivation records, or <see langword="null"/> for a pack named
    /// <see cref="WorldBakePack.FileName"/> beside the document.</param>
    /// <returns>The extended derivations.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chunks"/> is <see langword="null"/>.</exception>
    public static CompiledWorldChunks Register(CompiledWorldChunks chunks, WorldBakeStore? store, string? packReference = null) {
        ArgumentNullException.ThrowIfNull(argument: chunks);

        return chunks.With(chunk: new WorldBakeChunk(packReference: packReference, store: store));
    }
    /// <summary>Reads a <c>BAKE</c> payload: the pack reference and the keys.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="packReference">The pack reference, when this returns <see langword="true"/>.</param>
    /// <param name="keys">The key pins in ascending order of their hex, when this returns <see langword="true"/>.</param>
    /// <param name="reason">Why the payload is not a <c>BAKE</c> payload, or empty on success.</param>
    /// <returns><see langword="true"/> when the payload is canonical.</returns>
    public static bool TryRead(ReadOnlySpan<byte> payload, out string packReference, out IReadOnlyList<ContentPin> keys, out string reason) {
        packReference = string.Empty;
        keys = [];

        try {
            var reader = new CanonicalBinaryReader(content: payload);
            var reference = reader.ReadText(maximumByteCount: MaximumReferenceBytes);
            var count = reader.ReadBoundedInt(maximum: payload.Length);
            var read = new ContentPin[count];
            string? previous = null;

            if (reference.Length == 0) {
                throw new InvalidDataException(message: "the bake pack's reference is empty");
            }

            for (var index = 0; (index < count); index++) {
                var hex = reader.ReadText(maximumByteCount: MaximumKeyBytes);

                if (!ContentPin.TryParseHex(hex: hex, pin: out read[index])) {
                    throw new InvalidDataException(message: "a bake's key is not a content pin");
                }

                if (
                    (previous is not null) &&
                    (string.CompareOrdinal(strA: previous, strB: hex) >= 0)
                ) {
                    throw new InvalidDataException(message: "bake keys are not in ascending order");
                }

                previous = hex;
            }

            reader.ExpectEnd();
            packReference = reference;
            keys = read;
        } catch (InvalidDataException exception) {
            reason = exception.Message;
            return false;
        }

        reason = string.Empty;
        return true;
    }
    /// <inheritdoc/>
    public AssetContentHash? ReadInput(CompiledWorldContext context, string name) => null;
    /// <inheritdoc/>
    public bool TryDerive(CompiledWorldContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out CompiledWorldProduct? product, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var keys = new SortedSet<string>(comparer: StringComparer.Ordinal);

        foreach (var request in WorldBakeStore.RequestsOf(definition: context.RequireDrawn(), quality: Quality)) {
            if (keys.Add(item: request.Key.Pin.Hex)) {
                _ = Store?.GetOrBake(request: request);
            }
        }

        var writer = new ArrayBufferWriter<byte>();

        writer.WriteText(value: PackReference);
        writer.WriteVarUInt(value: checked((uint)keys.Count));

        foreach (var key in keys) {
            writer.WriteText(value: key);
        }

        product = new CompiledWorldProduct(
            Inputs: [],
            Payload: writer.WrittenSpan.ToArray()
        );
        reason = string.Empty;
        return true;
    }
    /// <inheritdoc/>
    public bool TryLoad(CompiledWorldContext context, ReadOnlyMemory<byte> payload, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (!TryRead(
            keys: out var keys,
            packReference: out var reference,
            payload: payload.Span,
            reason: out reason
        )) {
            return false;
        }

        if (
            (Store is { } store) &&
            (context.DocumentPath is { } documentPath) &&
            (keys.Count > 0)
        ) {
            _ = store.HoldFromPack(
                keys: keys,
                packPath: WorldBakePack.Resolve(documentPath: documentPath, reference: reference)
            );
        }

        return true;
    }
}
