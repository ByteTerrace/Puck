using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Numerics;
using Puck.Abstractions;
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
    // Every shared seam vertex uses exactly the same grid, including its width. A per-piece grid opens cracks.
    private static PositionGrid GridOf(IReadOnlyList<SdfMesh> pieces) {
        var low = ((pieces.Count == 0) ? Vector3.Zero : pieces[0].PositionLow);
        var high = ((pieces.Count == 0) ? Vector3.Zero : pieces[0].PositionHigh);

        foreach (var piece in pieces) {
            low = Vector3.Min(value1: low, value2: piece.PositionLow);
            high = Vector3.Max(value1: high, value2: piece.PositionHigh);
        }

        return new PositionGrid(Low: low, High: high, Width: ((pieces.Count == 0) ? 4 : PositionWidth(high: high, low: low, pieces: pieces)));
    }
    // Whether a piece's normals are stored as octahedral words: every one is a unit vector.
    private static bool IsOctahedral(ReadOnlySpan<Vector3> normals) {
        var oct = true;

        foreach (var normal in normals) { oct &= (MathF.Abs(x: (normal.LengthSquared() - 1f)) <= 0.00001f); }

        return oct;
    }
    /// <summary>Returns the grid an entry of a mesh stores its positions on, which <see cref="Conform"/> reads again.</summary>
    /// <param name="mesh">The mesh, whole or chunked.</param>
    /// <returns>The grid.</returns>
    internal static PositionGrid GridOf(SdfMesh mesh) => GridOf(pieces: PiecesOf(mesh: mesh));
    /// <summary>Writes a piece's planes as a decode of the mesh's entry would: positions on the entry's grid and unit
    /// normals through their octahedral words, the two planes the format stores lossily. The planes are the copy's own,
    /// written from the mesh and not yet read by anything, so the published mesh is what the store answers without the
    /// store being read back (<see cref="SdfMeshLibrary.Store"/>).</summary>
    /// <param name="grid">The mesh's grid (<see cref="GridOf(SdfMesh)"/>).</param>
    /// <param name="normals">The piece's normals, as copied.</param>
    /// <param name="positions">The piece's positions, as copied.</param>
    internal static void Conform(in PositionGrid grid, Span<Vector3> positions, Span<Vector3> normals) {
        if (grid.Width != 4) {
            for (var index = 0; (index < positions.Length); index++) { positions[index] = OnGrid(grid: in grid, position: positions[index]); }
        }
        if (IsOctahedral(normals: normals)) {
            for (var index = 0; (index < normals.Length); index++) {
                var normal = normals[index];
                var unpacked = SdfOctahedral.UnpackSigned(bits: OctahedralBits, word: SdfOctahedral.PackSigned(bits: OctahedralBits, direction: new Double3(X: normal.X, Y: normal.Y, Z: normal.Z)));

                normals[index] = new Vector3(x: ((float)unpacked.X), y: ((float)unpacked.Y), z: ((float)unpacked.Z));
            }
        }
    }

    internal static void Encode(Stream output, UInt128 key, SdfMesh? mesh, int[] materials) {
        var pieces = PiecesOf(mesh: mesh);
        var canonical = SdfMeshMaterials.Canonical(materials: materials, pieces: pieces);
        EncodePlanes(output, key, pieces, mesh is { Chunks.Count: > 0 }, GridOf(pieces), materials, canonical, exact: false);
    }

    // A checkpoint feeds later geometry operations. Quantizing it here would make a warm derivation differ from a cold one.
    internal static void EncodeExact(Stream output, UInt128 key, SdfMesh? mesh) => EncodePlanes(
        output, key, PiecesOf(mesh), mesh is { Chunks.Count: > 0 }, new PositionGrid(default, default, 4), [], [], exact: true);

    private static void EncodePlanes(Stream output, UInt128 key, IReadOnlyList<SdfMesh> pieces, bool chunked, PositionGrid grid,
        int[] materials, int[] canonical, bool exact) {
        var (low, high, width) = grid;
        using var payload = new MemoryStream();

        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write(value: canonical.Length);
            foreach (var material in canonical) { writer.Write(value: material); }
            writer.Write(value: chunked);
            writer.Write(value: pieces.Count);
            foreach (var piece in pieces) { WriteMesh(from: materials, high: high, low: low, mesh: piece, to: canonical, width: width, writer: writer, exact: exact); }
        }
        EncodePayload(output, key, payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
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
    private static void WriteMesh(BinaryWriter writer, SdfMesh mesh, Vector3 low, Vector3 high, int width, int[] from, int[] to, bool exact) {
        WriteVector(writer, mesh.BakeCell);
        var positions = mesh.Positions.Span;

        writer.Write(value: ((byte)width));
        WriteVector(value: low, writer: writer);
        WriteVector(value: high, writer: writer);
        for (var axis = 0; (axis < 3); axis++) {
            var words = new uint[positions.Length];

            for (var index = 0; (index < words.Length); index++) {
                words[index] = ((width == 4) ? BitConverter.SingleToUInt32Bits(value: positions[index][axis]) : Quantize(positions[index][axis], low[axis], high[axis], width));
            }
            WritePlane(width: width, words: words, writer: writer);
        }
        var oct = !exact && IsOctahedral(normals: mesh.Normals.Span);

        writer.Write(value: oct);
        if (oct) {
            var words = new uint[mesh.Normals.Length];

            for (var index = 0; (index < words.Length); index++) {
                var normal = mesh.Normals.Span[index];

                words[index] = SdfOctahedral.PackSigned(bits: OctahedralBits, direction: new Double3(X: normal.X, Y: normal.Y, Z: normal.Z));
            }
            WritePlane(width: 4, words: words, writer: writer);
        } else {
            for (var axis = 0; (axis < 3); axis++) {
                var words = new uint[mesh.Normals.Length];

                for (var index = 0; (index < words.Length); index++) { words[index] = BitConverter.SingleToUInt32Bits(value: mesh.Normals.Span[index][axis]); }
                WritePlane(width: 4, words: words, writer: writer);
            }
        }
        WritePlane(writer, mesh.Indices.Span, ((positions.Length <= 65536) ? 2 : 4));
        WriteMaterials(writer, mesh.TriangleMaterials.Span, from, to);
        writer.Write(value: mesh.Members.Length);
        foreach (var member in mesh.Members.Span) {
            writer.Write(value: member.SlotOffset);
            WriteVector(writer, member.Position);
            writer.Write(value: member.Orientation.X); writer.Write(value: member.Orientation.Y); writer.Write(value: member.Orientation.Z); writer.Write(value: member.Orientation.W);
        }
        writer.Write(value: mesh.VertexMembers.Length);
        writer.Write(buffer: mesh.VertexMembers.Span);
        WritePlane(writer, mesh.Influences.Span.ToArray().Select(selector: i => i.Blend).ToArray(), 4);
        WritePlane(writer, mesh.Influences.Span.ToArray().Select(selector: i => i.GradientXY).ToArray(), 4);
        WritePlane(writer, mesh.Influences.Span.ToArray().Select(selector: i => i.GradientZ).ToArray(), 4);
        writer.Write(value: mesh.Levels.Count);
        foreach (var level in mesh.Levels) {
            writer.Write(value: level.Error);
            WritePlane(writer, level.Indices.Span, ((positions.Length <= 65536) ? 2 : 4));
            WriteMaterials(writer, level.TriangleMaterials.Span, from, to);
        }
    }
    private static int PositionWidth(IReadOnlyList<SdfMesh> pieces, Vector3 low, Vector3 high) {
        for (var width = 2; (width <= 3); width++) {
            var fits = true;

            foreach (var piece in pieces) {
                if (!PositionsFit(high: high, low: low, mesh: piece, width: width)) { fits = false; break; }
            }
            if (fits) { return width; }
        }
        return 4;
    }
    private static bool PositionsFit(SdfMesh mesh, Vector3 low, Vector3 high, int width) {
        var budget = (mesh.BakeCell.Length() / 1024f);

        if (!(budget > 0) || !float.IsFinite(f: budget)) { return false; }
        var grid = new PositionGrid(Low: low, High: high, Width: width);

        foreach (var position in mesh.Positions.Span) {
            if (Vector3.Distance(value1: position, value2: OnGrid(grid: in grid, position: position)) > budget) { return false; }
        }
        return true;
    }
    // The position a decode of the grid's entry answers for one the encoder was given: the one quantize and expansion the
    // encoder's fit check and Conform both run, so the budget the encoder holds is the error a published mesh carries.
    private static Vector3 OnGrid(in PositionGrid grid, Vector3 position) {
        var (low, high, width) = (grid.Low, grid.High, grid.Width);
        var divisor = Steps(width: width);

        return new Vector3(
            x: Expand(divisor: divisor, extent: (((double)high.X) - low.X), low: low.X, value: Quantize(high: high.X, low: low.X, value: position.X, width: width)),
            y: Expand(divisor: divisor, extent: (((double)high.Y) - low.Y), low: low.Y, value: Quantize(high: high.Y, low: low.Y, value: position.Y, width: width)),
            z: Expand(divisor: divisor, extent: (((double)high.Z) - low.Z), low: low.Z, value: Quantize(high: high.Z, low: low.Z, value: position.Z, width: width)));
    }
    // The largest quantized value of a position width, which spans its axis's low to high.
    internal static double Steps(int width) => ((1u << (width * 8)) - 1);
    private static uint Quantize(float value, float low, float high, int width) => ((high == low) ? 0 : (uint)Math.Clamp(Math.Round(a: (((((double)value) - low) / (((double)high) - low)) * Steps(width: width))), 0, Steps(width: width)));
    // The position a quantized value decodes to: the one expansion both the decode and the encoder's fit check run, so the
    // encoder's budget holds for what the decode writes.
    internal static float Expand(uint value, double low, double extent, double divisor) => ((float)(low + ((extent * value) / divisor)));
    private static void WriteMaterials(BinaryWriter writer, ReadOnlySpan<uint> materials, int[] from, int[] to) {
        var width = 1;

        foreach (var material in materials) { if (SdfMeshMaterials.Renumber(from: from, material: material, to: to) > byte.MaxValue) { width = 4; break; } }
        WritePlane(from: from, to: to, width: width, words: materials, writer: writer);
    }
    private static void WritePlane(BinaryWriter writer, ReadOnlySpan<uint> words, int width, int[]? from = null, int[]? to = null) {
        writer.Write(value: ((byte)width));
        writer.Write(value: words.Length);
        var bytes = new byte[checked((words.Length * width))];
        var previous = 0U;

        for (var index = 0; (index < words.Length); index++) {
            var word = ((from is null) ? words[index] : SdfMeshMaterials.Renumber(words[index], from, to!));
            var delta = unchecked((word - previous));

            previous = word;
            for (var plane = 0; (plane < width); plane++) { bytes[((plane * words.Length) + index)] = ((byte)(delta >> (plane * 8))); }
        }
        writer.Write(buffer: bytes);
    }
    private static void WriteVector(BinaryWriter writer, Vector3 value) { writer.Write(value: value.X); writer.Write(value: value.Y); writer.Write(value: value.Z); }
}
