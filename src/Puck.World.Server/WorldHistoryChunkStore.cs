using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The history's keyframe memory: each encoded keyframe is cut into content-defined chunks and every distinct chunk
/// is held once, reference-counted across the keyframes that contain it. Consecutive keyframes of one world differ in
/// a few places — state values, poses, machine memory — so most of a new keyframe's chunks are already held, and the
/// bytes a keyframe adds are its changed regions rather than its whole image.
/// </summary>
/// <remarks>
/// <para>Boundaries come from a gear rolling hash over the bytes themselves, so an edit that shifts the bytes after it
/// moves only the chunks around the edit: a boundary falls where the high bits of the hash are zero, after a minimum
/// chunk length and before a maximum one. Chunks are keyed by an FNV-1a digest and compared byte for byte before
/// sharing, so two different chunks never alias.</para>
/// <para>Single-threaded, like the history that owns it.</para>
/// </remarks>
internal sealed class WorldHistoryChunkStore {
    // Chunk length bounds and the boundary mask: boundaries average about 4 KiB past the minimum.
    private const int MinimumChunk = 1024;
    private const int BoundaryShift = 52;
    private const int MaximumChunk = (16 * 1024);
    // What one held chunk costs beyond its payload: the reference a keyframe keeps to it.
    private const int ReferenceBytes = sizeof(ulong);

    // The gear table: 256 fixed pseudo-random words drawn once from a SplitMix64 stream with a fixed seed, so the
    // same bytes always cut at the same boundaries.
    private static readonly ulong[] Gear = BuildGear();

    private readonly Dictionary<ulong, List<Chunk>> m_chunks = [];

    /// <summary>One distinct chunk and how many held keyframes contain it.</summary>
    internal sealed class Chunk(byte[] bytes, ulong hash) {
        public byte[] Bytes { get; } = bytes;
        public ulong Hash { get; } = hash;

        public int References { get; set; }
    }

    /// <summary>Gets the payload bytes of every distinct chunk held.</summary>
    public long PayloadBytes { get; private set; }

    private static ulong[] BuildGear() {
        var gear = new ulong[256];
        var state = 0x9E37_79B9_7F4A_7C15UL;

        for (var index = 0; (index < gear.Length); index++) {
            state = unchecked((state + 0x9E37_79B9_7F4A_7C15UL));

            var z = state;

            z = unchecked(((z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL));
            z = unchecked(((z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL));
            gear[index] = z ^ (z >> 31);
        }

        return gear;
    }
    // The length of the next chunk at the start of `bytes`.
    private static int NextChunkLength(ReadOnlySpan<byte> bytes) {
        if (bytes.Length <= MinimumChunk) {
            return bytes.Length;
        }

        var limit = Math.Min(
            val1: bytes.Length,
            val2: MaximumChunk
        );
        var hash = 0UL;

        for (var index = MinimumChunk; (index < limit); index++) {
            hash = unchecked(((hash << 1) + Gear[bytes[index]]));

            if ((hash >> BoundaryShift) == 0UL) {
                return (index + 1);
            }
        }

        return limit;
    }

    /// <summary>Returns the bytes a set of chunk references costs beyond the payloads it shares.</summary>
    /// <param name="chunks">The references.</param>
    /// <returns>The reference bytes.</returns>
    public static long ReferenceCost(Chunk[] chunks) => (((long)chunks.Length) * ReferenceBytes);
    /// <summary>Rebuilds a stored blob.</summary>
    /// <param name="chunks">The blob's chunks, in order.</param>
    /// <param name="length">The blob's length.</param>
    /// <returns>The blob's bytes.</returns>
    public static byte[] Load(Chunk[] chunks, int length) {
        var bytes = new byte[length];
        var offset = 0;

        foreach (var chunk in chunks) {
            chunk.Bytes.CopyTo(array: bytes, index: offset);
            offset += chunk.Bytes.Length;
        }

        return bytes;
    }
    /// <summary>Releases one blob's references; a chunk no held blob contains any more is dropped.</summary>
    /// <param name="chunks">The blob's chunks.</param>
    public void Release(Chunk[] chunks) {
        foreach (var chunk in chunks) {
            chunk.References--;

            if (chunk.References > 0) {
                continue;
            }

            var bucket = m_chunks[chunk.Hash];

            _ = bucket.Remove(item: chunk);

            if (bucket.Count == 0) {
                _ = m_chunks.Remove(key: chunk.Hash);
            }

            PayloadBytes -= chunk.Bytes.Length;
        }
    }
    /// <summary>Stores a blob, sharing every chunk already held.</summary>
    /// <param name="blob">The blob.</param>
    /// <param name="addedBytes">The payload bytes the blob added: the chunks no held blob already contained.</param>
    /// <returns>The blob's chunks, in order.</returns>
    public Chunk[] Store(ReadOnlySpan<byte> blob, out long addedBytes) {
        var chunks = new List<Chunk>(capacity: ((blob.Length / (4 * 1024)) + 1));

        addedBytes = 0L;

        while (blob.Length > 0) {
            var length = NextChunkLength(bytes: blob);
            var piece = blob[..length];
            var digest = Fnv1aHash.Create();

            digest.Add(values: piece);

            var hash = digest.Value;
            Chunk? held = null;

            if (m_chunks.TryGetValue(key: hash, value: out var bucket)) {
                foreach (var candidate in bucket) {
                    if (candidate.Bytes.AsSpan().SequenceEqual(other: piece)) {
                        held = candidate;

                        break;
                    }
                }
            } else {
                bucket = [];
                m_chunks[hash] = bucket;
            }

            if (held is null) {
                held = new Chunk(
                    bytes: piece.ToArray(),
                    hash: hash
                );
                bucket.Add(item: held);
                PayloadBytes += length;
                addedBytes += length;
            }

            held.References++;
            chunks.Add(item: held);
            blob = blob[length..];
        }

        return [.. chunks];
    }
}
