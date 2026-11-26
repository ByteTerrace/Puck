using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Numerics;
using Puck.Abstractions;
using Puck.Abstractions.Memory;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

// Mesh streams use independent attribute planes, delta predicted within each plane, then zlib.
// The envelope checks compressed bytes before allocation; decoded counts are bounded by remaining bytes.
internal static class SdfMeshStoreCodec {
    // The envelope version, checked before decoding. The stage keys also include the reachable codec code.
    internal const ulong Magic = 0x4148534D4B435550ul; // PUCKMSHA
    internal const int HeaderBytes = 28;

    internal const int ChecksumBytes = 16;
    // The width of each coordinate of a stored octahedral normal (SdfOctahedral.PackSigned): the format's own, not the
    // renderer's visibility record's, so a change to either leaves the other's bytes alone.
    internal const int OctahedralBits = 16;

    // The grid an entry stores a mesh's positions on: the box of every piece and the width the encoder's budget allows.
    internal readonly record struct PositionGrid(Vector3 Low, Vector3 High, int Width);

    private static IReadOnlyList<SdfMesh> PiecesOf(SdfMesh? mesh) => ((mesh is null) ? Array.Empty<SdfMesh>() : ((mesh.Chunks.Count == 0) ? new[] { mesh } : mesh.Chunks));
    // The same words prove the error bound, feed the encoder and place the native copy on its storage grid.
    internal sealed record Positions(PositionGrid Grid, uint[][] Planes) {
        public static Positions Prepare(IReadOnlyList<SdfMesh> pieces) {
            var low = pieces.Count == 0 ? Vector3.Zero : pieces[0].PositionLow;
            var high = pieces.Count == 0 ? Vector3.Zero : pieces[0].PositionHigh;
            foreach (var piece in pieces) {
                low = Vector3.Min(low, piece.PositionLow);
                high = Vector3.Max(high, piece.PositionHigh);
            }
            if (pieces.Count != 0 && pieces.All(static piece => piece.BakeCell.Length() / 1024f is > 0f and < float.PositiveInfinity)) {
                var planes = new uint[pieces.Count][];
                for (var width = 2; width <= 3; width++) {
                    var grid = new PositionGrid(low, high, width);
                    var index = 0;
                    for (; index < pieces.Count; index++) {
                        var mesh = pieces[index];
                        var words = planes[index] ??= new uint[3 * mesh.VertexCount];
                        if (!Fits(mesh, grid, words)) { break; }
                    }
                    if (index == pieces.Count) { return new(grid, planes); }
                }
            }
            return new(new PositionGrid(low, high, 4), []);
        }

        private static bool Fits(SdfMesh mesh, PositionGrid grid, uint[] words) {
            var budget = mesh.BakeCell.Length() / 1024f;
            using var held = mesh.Hold();
            var positions = held.Positions.Span;
            var steps = Steps(grid.Width);
            for (var vertex = 0; vertex < positions.Length; vertex++) {
                var point = positions[vertex];
                var expanded = Vector3.Zero;
                for (var axis = 0; axis < 3; axis++) {
                    var word = Quantize(point[axis], grid.Low[axis], grid.High[axis], grid.Width);
                    words[axis * positions.Length + vertex] = word;
                    expanded[axis] = Expand(word, grid.Low[axis], (double)grid.High[axis] - grid.Low[axis], steps);
                }
                if (Vector3.Distance(point, expanded) > budget) { return false; }
            }
            return true;
        }
    }
    // Whether a piece's normals are stored as octahedral words: every one is a unit vector.
    private static bool IsOctahedral(ReadOnlySpan<Vector3> normals) {
        var oct = true;

        foreach (var normal in normals) { oct &= (MathF.Abs(x: (normal.LengthSquared() - 1f)) <= 0.00001f); }

        return oct;
    }
    internal static void Conform(Positions stored, int piece, Span<Vector3> positions, Span<Vector3> normals) {
        var grid = stored.Grid;
        if (grid.Width != 4) {
            var words = stored.Planes[piece];
            for (var axis = 0; axis < 3; axis++) {
                var low = grid.Low[axis];
                var extent = (double)grid.High[axis] - low;
                var steps = Steps(grid.Width);
                for (var index = 0; index < positions.Length; index++) {
                    positions[index][axis] = Expand(words[axis * positions.Length + index], low, extent, steps);
                }
            }
        }
        if (IsOctahedral(normals: normals)) {
            for (var index = 0; (index < normals.Length); index++) {
                var normal = normals[index];
                var unpacked = SdfOctahedral.UnpackSigned(bits: OctahedralBits, word: SdfOctahedral.PackSigned(bits: OctahedralBits, direction: new Double3(X: normal.X, Y: normal.Y, Z: normal.Z)));

                normals[index] = new Vector3(x: ((float)unpacked.X), y: ((float)unpacked.Y), z: ((float)unpacked.Z));
            }
        }
    }

    // Streams a mesh's entry to the output and returns the position planes it stored, which the caller's native copy
    // conforms to (Conform): the one place both are computed, so a published mesh holds exactly what its entry answers.
    internal static Positions Encode(Stream output, UInt128 key, SdfMesh? mesh, int[] materials, IAllocator? allocator = null) {
        var pieces = PiecesOf(mesh: mesh);
        var canonical = SdfMeshMaterials.Canonical(materials: materials, pieces: pieces);
        var stored = Positions.Prepare(pieces);

        EncodePlanes(output, key, pieces, mesh is { Chunks.Count: > 0 }, stored, materials, canonical, exact: false, allocator);
        return stored;
    }

    // A checkpoint feeds later geometry operations. Quantizing it here would make a warm derivation differ from a cold one.
    internal static void EncodeExact(Stream output, UInt128 key, SdfMesh? mesh, IAllocator? allocator = null) => EncodePlanes(
        output, key, PiecesOf(mesh), mesh is { Chunks.Count: > 0 }, new Positions(new PositionGrid(default, default, 4), []), [], [], exact: true, allocator);

    private static void EncodePlanes(Stream output, UInt128 key, IReadOnlyList<SdfMesh> pieces, bool chunked, Positions stored,
        int[] materials, int[] canonical, bool exact, IAllocator? allocator) {
        var (low, high, width) = stored.Grid;
        // The payload's length leads the envelope, so it is collected whole; its storage is the store's native allocator's
        // when it has one, so an encode on the store's writer never reaches the collector, and rented otherwise.
        using var payload = new RentedMemoryStream(allocator: allocator, capacity: PayloadEstimate(pieces: pieces));

        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write(value: canonical.Length);
            foreach (var material in canonical) { writer.Write(value: material); }
            writer.Write(value: chunked);
            writer.Write(value: pieces.Count);
            var map = materials.Length == 0 ? null : new SdfMeshMaterials.Remap(materials, canonical);
            for (var piece = 0; piece < pieces.Count; piece++) {
                WriteMesh(writer, pieces[piece], low, high, width, map, exact, width == 4 ? default : stored.Planes[piece]);
            }
        }
        EncodePayload(output, key, payload.Written);
    }

    // The payload's size to within a small share: sixteen bytes a vertex (positions at up to four bytes an axis and a packed
    // normal), each index plane's width, and a byte a triangle's material, so the stream rents once and does not grow.
    private static int PayloadEstimate(IReadOnlyList<SdfMesh> pieces) {
        var bytes = (1L << 12);

        foreach (var piece in pieces) {
            var width = ((piece.VertexCount <= 65536) ? 2 : 4);

            bytes += ((16L * piece.VertexCount) + (((long)width) * piece.IndexCount) + (4L * piece.TriangleCount));
            foreach (var level in piece.Levels) { bytes += ((((long)width) * level.IndexCount) + (4L * level.MaterialCount)); }
        }

        return ((int)Math.Min(val1: bytes, val2: Array.MaxLength));
    }

    // Writes an entry's envelope, header then zlib-compressed payload then the checksum of both, straight to the stream
    // with no entry-sized staging: the checksum is accumulated over the bytes as they pass.
    internal static void EncodePayload(Stream output, UInt128 key, ReadOnlySpan<byte> payload) {
        Span<byte> header = stackalloc byte[HeaderBytes];

        BinaryPrimitives.WriteUInt64LittleEndian(destination: header, value: Magic);
        BinaryPrimitives.WriteUInt128LittleEndian(destination: header[8..], value: key);
        BinaryPrimitives.WriteInt32LittleEndian(destination: header[24..], value: payload.Length);

        var hashed = new HashingStream(output);

        hashed.Write(buffer: header);
        using (var compressor = new ZLibStream(hashed, CompressionLevel.Fastest, leaveOpen: true)) {
            compressor.Write(buffer: payload);
        }
        Span<byte> checksum = stackalloc byte[ChecksumBytes];

        BinaryPrimitives.WriteUInt128LittleEndian(destination: checksum, value: hashed.Hash);
        output.Write(buffer: checksum);
    }

    // Passes bytes to the entry's stream and folds each into a running XxHash128, the checksum the envelope ends in.
    private sealed class HashingStream(Stream inner) : DigestingStream(inner: inner) {
        private readonly XxHash128 m_hash = new();

        public UInt128 Hash => m_hash.GetCurrentHashAsUInt128();

        protected override void Append(ReadOnlySpan<byte> bytes) => m_hash.Append(source: bytes);
    }
    private static void WriteMesh(BinaryWriter writer, SdfMesh mesh, Vector3 low, Vector3 high, int width, SdfMeshMaterials.Remap? map, bool exact, ReadOnlySpan<uint> quantized) {
        WriteVector(writer, mesh.BakeCell);
        using var held = mesh.Hold();

        if (!held.Readable) { throw new InvalidOperationException(message: "A mesh whose store could not restore its planes cannot be encoded."); }

        var positions = held.Positions.Span;

        writer.Write(value: ((byte)width));
        WriteVector(value: low, writer: writer);
        WriteVector(value: high, writer: writer);
        for (var axis = 0; (axis < 3); axis++) {
            if (width != 4) {
                WritePlane(writer, quantized.Slice(axis * positions.Length, positions.Length), width);
                continue;
            }
            using var rented = OwnedBuffer<uint>.OfLength(allocator: null, clear: false, length: positions.Length);
            var words = rented.AsSpan(start: 0, length: positions.Length);

            for (var index = 0; index < words.Length; index++) {
                words[index] = BitConverter.SingleToUInt32Bits(positions[index][axis]);
            }
            WritePlane(width: width, words: words, writer: writer);
        }

        var normals = held.Normals.Span;
        var oct = !exact && IsOctahedral(normals: normals);

        writer.Write(value: oct);
        {
            using var rented = OwnedBuffer<uint>.OfLength(allocator: null, clear: false, length: normals.Length);
            var words = rented.AsSpan(start: 0, length: normals.Length);

            if (oct) {
                for (var index = 0; (index < words.Length); index++) {
                    var normal = normals[index];

                    words[index] = SdfOctahedral.PackSigned(bits: OctahedralBits, direction: new Double3(X: normal.X, Y: normal.Y, Z: normal.Z));
                }
                WritePlane(width: 4, words: words, writer: writer);
            } else {
                for (var axis = 0; (axis < 3); axis++) {
                    for (var index = 0; (index < words.Length); index++) { words[index] = BitConverter.SingleToUInt32Bits(value: normals[index][axis]); }
                    WritePlane(width: 4, words: words, writer: writer);
                }
            }
        }
        WritePlane(writer, held.Indices.Span, ((positions.Length <= 65536) ? 2 : 4));
        WriteMaterials(writer, held.TriangleMaterials.Span, map);
        writer.Write(value: mesh.Members.Length);
        foreach (var member in mesh.Members.Span) {
            writer.Write(value: member.SlotOffset);
            WriteVector(writer, member.Position);
            writer.Write(value: member.Orientation.X); writer.Write(value: member.Orientation.Y); writer.Write(value: member.Orientation.Z); writer.Write(value: member.Orientation.W);
        }
        writer.Write(value: mesh.VertexMembers.Length);
        writer.Write(buffer: mesh.VertexMembers.Span);
        {
            var influences = mesh.Influences.Span;

            using var rented = OwnedBuffer<uint>.OfLength(allocator: null, clear: false, length: influences.Length);
            var words = rented.AsSpan(start: 0, length: influences.Length);

            for (var field = 0; (field < 3); field++) {
                for (var index = 0; (index < words.Length); index++) {
                    words[index] = ((field == 0) ? influences[index].Blend : ((field == 1) ? influences[index].GradientXY : influences[index].GradientZ));
                }
                WritePlane(width: 4, words: words, writer: writer);
            }
        }
        writer.Write(value: mesh.Levels.Count);
        for (var level = 1; (level <= mesh.Levels.Count); level++) {
            writer.Write(value: mesh.Levels[(level - 1)].Error);
            WritePlane(writer, held.LevelIndices(level: level).Span, ((positions.Length <= 65536) ? 2 : 4));
            WriteMaterials(writer, held.LevelMaterials(level: level).Span, map);
        }
    }
    // The largest quantized value of a position width, which spans its axis's low to high.
    internal static double Steps(int width) => ((1u << (width * 8)) - 1);
    private static uint Quantize(float value, float low, float high, int width) => ((high == low) ? 0 : (uint)Math.Clamp(Math.Round(a: (((((double)value) - low) / (((double)high) - low)) * Steps(width: width))), 0, Steps(width: width)));
    // The position a quantized value decodes to: the one expansion both the decode and the encoder's fit check run, so the
    // encoder's budget holds for what the decode writes.
    internal static float Expand(uint value, double low, double extent, double divisor) => ((float)(low + ((extent * value) / divisor)));
    private static void WriteMaterials(BinaryWriter writer, ReadOnlySpan<uint> materials, SdfMeshMaterials.Remap? map) {
        var width = 1;

        foreach (var material in materials) { if ((map?.Renumber(material) ?? material) > byte.MaxValue) { width = 4; break; } }
        WritePlane(map: map, width: width, words: materials, writer: writer);
    }
    private static void WritePlane(BinaryWriter writer, ReadOnlySpan<uint> words, int width, SdfMeshMaterials.Remap? map = null) {
        writer.Write(value: ((byte)width));
        writer.Write(value: words.Length);
        // Byte planes lie one after another, each the low-first byte of every word's delta; a plane is written in chunks so
        // no plane-sized array stands between the words and the stream.
        Span<byte> chunk = stackalloc byte[4096];

        for (var plane = 0; (plane < width); plane++) {
            var previous = 0U;

            for (var start = 0; (start < words.Length); start += chunk.Length) {
                var count = Math.Min(val1: chunk.Length, val2: (words.Length - start));

                for (var at = 0; (at < count); at++) {
                    var word = (map?.Renumber(words[(start + at)]) ?? words[(start + at)]);

                    chunk[at] = ((byte)(unchecked((word - previous)) >> (plane * 8)));
                    previous = word;
                }
                writer.Write(buffer: chunk[..count]);
            }
        }
    }
    private static void WriteVector(BinaryWriter writer, Vector3 value) { writer.Write(value: value.X); writer.Write(value: value.Y); writer.Write(value: value.Z); }
}
