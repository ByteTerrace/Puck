using System.Buffers;
using Puck.Assets;
using Puck.SignedDistance.Baking;

namespace Puck.World;

/// <summary>
/// The one file a build output's creation bakes ship in, so a bake shared by several compiled worlds is stored once. It
/// is a <see cref="ChunkContainer"/> opening with <c>PWBK</c>, content-addressed by bake key
/// (<see cref="Puck.World.Authoring.CreationBakeKey.Pin"/>): its header lists the keys, each as its pin's hex, in
/// strictly ascending ordinal order after a count, and chunk <c>i</c>, coded <c>BAKE</c> and versioned with the baker
/// that wrote it (<see cref="SdfBaker.Version"/>), holds key <c>i</c>'s encoded outcome
/// (<see cref="Puck.World.Authoring.CreationBakeCodec"/>). A compiled world's <c>BAKE</c> chunk
/// (<see cref="WorldBakeChunk"/>) names the keys it needs and where this file lies relative to its document.
/// <para>A build writes one beside the root of its output (<see cref="FileName"/>), holding exactly the keys its
/// compiled worlds name. Because an outcome is a function of its key, a pack is never repaired or adapted: a key it
/// lacks is baked on the device, and a pack that cannot be read counts as lacking every key.</para>
/// </summary>
public sealed class WorldBakePack {
    /// <summary>The extension of a bake pack's file.</summary>
    public const string Extension = ".puckbake";
    /// <summary>The name a build gives its bake pack.</summary>
    public const string FileName = ("bakes" + Extension);
    /// <summary>The version of the pack format: its header layout and the rule that the keys ascend.</summary>
    public const uint FormatVersion = 1;

    private const int MaximumKeyBytes = 128;

    private static readonly ChunkCode EntryCode = ChunkCode.Parse(text: "BAKE");

    private readonly Dictionary<ContentPin, ReadOnlyMemory<byte>> m_outcomes;

    private WorldBakePack(Dictionary<ContentPin, ReadOnlyMemory<byte>> outcomes) {
        m_outcomes = outcomes;
    }

    /// <summary>Gets the magic a bake pack's container opens with.</summary>
    public static ReadOnlySpan<byte> Magic => "PWBK"u8;
    /// <summary>Gets the number of outcomes the pack holds.</summary>
    public int Count => m_outcomes.Count;
    /// <summary>Gets the keys the pack holds, in no particular order.</summary>
    public IEnumerable<ContentPin> Keys => m_outcomes.Keys;

    /// <summary>Encodes a pack holding <paramref name="outcomes"/>.</summary>
    /// <param name="outcomes">Each key's encoded outcome, each key once, in any order.</param>
    /// <returns>The pack's bytes, which depend only on the set of keys and their outcomes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcomes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A key appears more than once.</exception>
    public static byte[] Encode(IEnumerable<KeyValuePair<ContentPin, ReadOnlyMemory<byte>>> outcomes) {
        ArgumentNullException.ThrowIfNull(argument: outcomes);

        var ordered = new SortedDictionary<string, ReadOnlyMemory<byte>>(comparer: StringComparer.Ordinal);

        foreach (var (key, outcome) in outcomes) {
            if (!ordered.TryAdd(key: key.Hex, value: outcome)) {
                throw new ArgumentException(message: $"bake key {key.Hex} appears more than once in a pack.", paramName: nameof(outcomes));
            }
        }

        var header = new ArrayBufferWriter<byte>();
        var chunks = new List<ContainerChunk>(capacity: ordered.Count);

        header.WriteVarUInt(value: checked((uint)ordered.Count));

        foreach (var (hex, outcome) in ordered) {
            header.WriteText(value: hex);
            chunks.Add(item: new ContainerChunk(
                code: EntryCode,
                payload: outcome,
                version: SdfBaker.Version
            ));
        }

        return new ChunkContainer(
            chunks: chunks,
            formatVersion: FormatVersion,
            header: header.WrittenSpan.ToArray()
        ).Encode(magic: Magic);
    }
    /// <summary>Returns the pack reference a document at <paramref name="documentPath"/> records for the pack at
    /// <paramref name="packPath"/>: the pack's path relative to the document's directory, with forward slashes.</summary>
    /// <param name="documentPath">The document's path.</param>
    /// <param name="packPath">The pack's path.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="documentPath"/> or <paramref name="packPath"/> is
    /// <see langword="null"/>, empty, or white space.</exception>
    public static string Reference(string documentPath, string packPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: documentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: packPath);

        return Path.GetRelativePath(
            path: Path.GetFullPath(path: packPath),
            relativeTo: (Path.GetDirectoryName(path: Path.GetFullPath(path: documentPath)) ?? ".")
        ).Replace(newChar: '/', oldChar: '\\');
    }
    /// <summary>Returns the full path of the pack a document at <paramref name="documentPath"/> references with
    /// <paramref name="reference"/>.</summary>
    /// <param name="documentPath">The document's path.</param>
    /// <param name="reference">The reference (<see cref="Reference"/>).</param>
    /// <returns>The pack's full path.</returns>
    /// <exception cref="ArgumentException"><paramref name="documentPath"/> or <paramref name="reference"/> is
    /// <see langword="null"/>, empty, or white space.</exception>
    public static string Resolve(string documentPath, string reference) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: documentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: reference);

        return Path.GetFullPath(path: Path.Combine(
            path1: (Path.GetDirectoryName(path: Path.GetFullPath(path: documentPath)) ?? "."),
            path2: reference
        ));
    }
    /// <summary>Reads a pack.</summary>
    /// <param name="content">The file's bytes; the outcomes are slices of them, never copies.</param>
    /// <param name="pack">The pack, when this returns <see langword="true"/>.</param>
    /// <param name="reason">Why the bytes are not a pack, or empty on success.</param>
    /// <returns><see langword="true"/> when the bytes are a canonical pack of this format version.</returns>
    public static bool TryDecode(ReadOnlyMemory<byte> content, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldBakePack? pack, out string reason) {
        pack = null;

        try {
            var container = ChunkContainer.Decode(content: content, magic: Magic);

            if (container.FormatVersion != FormatVersion) {
                reason = $"bake-pack format version {container.FormatVersion} is not {FormatVersion}";
                return false;
            }

            var reader = new CanonicalBinaryReader(content: container.Header.Span);
            var count = reader.ReadBoundedInt(maximum: container.Header.Length);

            if (count != container.Chunks.Count) {
                throw new InvalidDataException(message: $"the pack lists {count} keys for {container.Chunks.Count} outcomes");
            }

            var outcomes = new Dictionary<ContentPin, ReadOnlyMemory<byte>>(capacity: count);
            string? previous = null;

            for (var index = 0; (index < count); index++) {
                var hex = reader.ReadText(maximumByteCount: MaximumKeyBytes);

                if (!ContentPin.TryParseHex(hex: hex, pin: out var key)) {
                    throw new InvalidDataException(message: "a bake's key is not a content pin");
                }

                if (
                    (previous is not null) &&
                    (string.CompareOrdinal(strA: previous, strB: hex) >= 0)
                ) {
                    throw new InvalidDataException(message: "bake keys are not in ascending order");
                }

                if (container.Chunks[index].Code != EntryCode) {
                    throw new InvalidDataException(message: $"outcome {index} is not a '{EntryCode}' chunk");
                }

                outcomes.Add(key: key, value: container.Chunks[index].Payload);
                previous = hex;
            }

            reader.ExpectEnd();
            pack = new WorldBakePack(outcomes: outcomes);
            reason = string.Empty;
            return true;
        } catch (InvalidDataException exception) {
            reason = exception.Message;
            return false;
        }
    }
    /// <summary>Finds a key's outcome.</summary>
    /// <param name="key">The bake's key pin.</param>
    /// <param name="outcome">The encoded outcome, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the pack holds the key.</returns>
    public bool TryGet(ContentPin key, out ReadOnlyMemory<byte> outcome) =>
        m_outcomes.TryGetValue(key: key, value: out outcome);
}
