using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Numerics;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

// Mesh streams use independent attribute planes, delta predicted within each plane, then Brotli.
// The envelope checks compressed bytes before allocation; decoded counts are bounded by remaining bytes.
internal static class SdfMeshStoreCodec {
    internal const ulong Magic = 0x3648534D4B435550ul; // PUCKMSH6
    internal const int HeaderBytes = 28;
    private const int ChecksumBytes = 16;

    internal static byte[] Encode(UInt128 key, SdfMesh mesh, int[] materials) {
        var pieces = mesh.Chunks.Count == 0 ? new[] { mesh } : mesh.Chunks;
        var low = pieces[0].Positions.Span[0];
        var high = low;
        foreach (var piece in pieces) {
            foreach (var position in piece.Positions.Span) { low = Vector3.Min(low, position); high = Vector3.Max(high, position); }
        }
        // Every shared seam vertex uses exactly the same grid, including its width. A per-piece grid opens cracks.
        var width = PositionWidth(pieces, low, high);
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write(materials.Length);
            foreach (var material in materials) { writer.Write(material); }
            writer.Write(mesh.Chunks.Count != 0);
            writer.Write(mesh.Chunks.Count == 0 ? 1 : mesh.Chunks.Count);
            foreach (var piece in pieces) { WriteMesh(writer, piece, low, high, width); }
        }
        using var output = new MemoryStream();
        output.Write(new byte[HeaderBytes]);
        using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)) {
            compressor.Write(payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
        }
        var length = checked((int)output.Length);
        output.Write(new byte[ChecksumBytes]);
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt128LittleEndian(bytes.AsSpan(8), key);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), checked((int)payload.Length));
        BinaryPrimitives.WriteUInt128LittleEndian(bytes.AsSpan(length), XxHash128.HashToUInt128(bytes.AsSpan(0, length)));
        return bytes;
    }

    internal static bool TryDecode(byte[] bytes, UInt128 key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SdfMesh? mesh,
        out int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? refusal) {
        mesh = null;
        materials = [];
        refusal = "is truncated";
        if (bytes.Length < HeaderBytes + ChecksumBytes) { return false; }
        var body = bytes.AsSpan(0, bytes.Length - ChecksumBytes);
        refusal = "fails its checksum";
        if (XxHash128.HashToUInt128(body) != BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(body.Length))) { return false; }
        refusal = "is not a mesh entry of this format";
        if (BinaryPrimitives.ReadUInt64LittleEndian(body) != Magic) { return false; }
        refusal = "holds another geometry's mesh";
        if (BinaryPrimitives.ReadUInt128LittleEndian(body[8..]) != key) { return false; }
        refusal = "holds invalid compressed mesh data";
        var length = BinaryPrimitives.ReadInt32LittleEndian(body[24..]);
        if (length <= 0 || length > 1_073_741_824) { return false; }
        try {
            var decoded = new byte[length];
            using var decoder = new BrotliDecoder();
            if (decoder.Decompress(body[HeaderBytes..], decoded, out var consumed, out var written) != System.Buffers.OperationStatus.Done ||
                consumed != body.Length - HeaderBytes || written != length) { return false; }
            using var input = new MemoryStream(decoded, writable: false);
            using var reader = new BinaryReader(input);
            var palette = new int[Count(reader, 4)];
            for (var index = 0; index < palette.Length; index++) { palette[index] = reader.ReadInt32(); }
            var chunked = reader.ReadBoolean();
            var chunks = new SdfMesh[Count(reader, 32)];
            if (chunks.Length == 0 || (!chunked && chunks.Length != 1)) { return false; }
            for (var index = 0; index < chunks.Length; index++) { chunks[index] = ReadMesh(reader); }
            if (input.Position != input.Length) { return false; }
            mesh = chunked ? SdfMeshChunks.Join(chunks) : chunks[0];
            materials = palette;
            refusal = null;
            return true;
        } catch (Exception exception) when (exception is IOException or ArgumentException or OverflowException) {
            refusal = $"is no mesh: {exception.Message}";
            return false;
        }
    }

    private static void WriteMesh(BinaryWriter writer, SdfMesh mesh, Vector3 low, Vector3 high, int width) {
        WriteVector(writer, mesh.BakeCell);
        var positions = mesh.Positions.Span;
        writer.Write((byte)width);
        WriteVector(writer, low);
        WriteVector(writer, high);
        for (var axis = 0; axis < 3; axis++) {
            var words = new uint[positions.Length];
            for (var index = 0; index < words.Length; index++) {
                words[index] = width == 4 ? BitConverter.SingleToUInt32Bits(positions[index][axis]) : Quantize(positions[index][axis], low[axis], high[axis], width);
            }
            WritePlane(writer, words, width);
        }
        var oct = true;
        foreach (var normal in mesh.Normals.Span) { oct &= MathF.Abs(normal.LengthSquared() - 1f) <= 0.00001f; }
        writer.Write(oct);
        if (oct) {
            var words = new uint[mesh.Normals.Length];
            for (var index = 0; index < words.Length; index++) {
                var normal = mesh.Normals.Span[index];
                var (x, y) = SdfOctahedral.Encode(new Double3(normal.X, normal.Y, normal.Z));
                words[index] = (uint)(ushort)(short)Math.Round(x * 32767) | ((uint)(ushort)(short)Math.Round(y * 32767) << 16);
            }
            WritePlane(writer, words, 4);
        } else {
            for (var axis = 0; axis < 3; axis++) {
                var words = new uint[mesh.Normals.Length];
                for (var index = 0; index < words.Length; index++) { words[index] = BitConverter.SingleToUInt32Bits(mesh.Normals.Span[index][axis]); }
                WritePlane(writer, words, 4);
            }
        }
        WritePlane(writer, mesh.Indices.Span, positions.Length <= 65536 ? 2 : 4);
        WriteMaterials(writer, mesh.TriangleMaterials.Span);
        writer.Write(mesh.Members.Length);
        foreach (var member in mesh.Members.Span) {
            writer.Write(member.SlotOffset);
            WriteVector(writer, member.Position);
            writer.Write(member.Orientation.X); writer.Write(member.Orientation.Y); writer.Write(member.Orientation.Z); writer.Write(member.Orientation.W);
        }
        writer.Write(mesh.VertexMembers.Length);
        writer.Write(mesh.VertexMembers.Span);
        writer.Write(mesh.Levels.Count);
        foreach (var level in mesh.Levels) {
            writer.Write(level.Error);
            WritePlane(writer, level.Indices.Span, positions.Length <= 65536 ? 2 : 4);
            WriteMaterials(writer, level.TriangleMaterials.Span);
        }
    }

    private static SdfMesh ReadMesh(BinaryReader reader) {
        var cell = ReadVector(reader);
        if (!float.IsFinite(cell.LengthSquared()) || Vector3.Min(cell, Vector3.Zero) != Vector3.Zero) { throw new InvalidDataException("Invalid contour cell."); }
        var width = reader.ReadByte();
        if (width is not (2 or 3 or 4)) { throw new InvalidDataException("Invalid position width."); }
        var low = ReadVector(reader);
        var high = ReadVector(reader);
        var positions = ReadVectors(reader, width, low, high);
        Vector3[] normals;
        if (reader.ReadBoolean()) {
            var words = ReadPlane(reader);
            normals = new Vector3[words.Length];
            for (var index = 0; index < words.Length; index++) {
                var normal = SdfOctahedral.Decode((short)(words[index] & 65535) / 32767.0, (short)(words[index] >> 16) / 32767.0);
                normals[index] = new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z);
            }
        } else { normals = ReadVectors(reader, 4, default, default); }
        var indices = ReadPlane(reader);
        var materials = ReadPlane(reader);
        var members = new SdfMeshMember[Count(reader, 32)];
        for (var index = 0; index < members.Length; index++) {
            var slot = reader.ReadInt32();
            var position = ReadVector(reader);
            members[index] = new SdfMeshMember(slot, position, new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        }
        var vertexMembers = reader.ReadBytes(Count(reader, 1));
        var levels = new SdfMeshLevel[Count(reader, 14)];
        for (var index = 0; index < levels.Length; index++) {
            var error = reader.ReadSingle();
            levels[index] = new SdfMeshLevel(ReadPlane(reader), ReadPlane(reader), error);
        }
        return new SdfMesh(positions, indices, normals, materials, members, vertexMembers) { BakeCell = cell, Levels = levels };
    }

    private static Vector3[] ReadVectors(BinaryReader reader, int width, Vector3 low, Vector3 high) {
        var x = ReadPlane(reader);
        var y = ReadPlane(reader);
        var z = ReadPlane(reader);
        if (x.Length != y.Length || x.Length != z.Length) { throw new InvalidDataException("Invalid vector counts."); }
        var vectors = new Vector3[x.Length];
        for (var index = 0; index < vectors.Length; index++) {
            vectors[index] = width == 4 ? new Vector3(BitConverter.UInt32BitsToSingle(x[index]), BitConverter.UInt32BitsToSingle(y[index]), BitConverter.UInt32BitsToSingle(z[index])) :
                new Vector3(Expand(x[index], low.X, high.X, width), Expand(y[index], low.Y, high.Y, width), Expand(z[index], low.Z, high.Z, width));
        }
        return vectors;
    }

    private static int PositionWidth(IReadOnlyList<SdfMesh> pieces, Vector3 low, Vector3 high) {
        for (var width = 2; width <= 3; width++) {
            var fits = true;
            foreach (var piece in pieces) {
                if (!PositionsFit(piece, low, high, width)) { fits = false; break; }
            }
            if (fits) { return width; }
        }
        return 4;
    }
    private static bool PositionsFit(SdfMesh mesh, Vector3 low, Vector3 high, int width) {
        var budget = mesh.BakeCell.Length() / 1024f;
        if (!(budget > 0) || !float.IsFinite(budget)) { return false; }
        foreach (var position in mesh.Positions.Span) {
            var decoded = new Vector3(Expand(Quantize(position.X, low.X, high.X, width), low.X, high.X, width),
                Expand(Quantize(position.Y, low.Y, high.Y, width), low.Y, high.Y, width),
                Expand(Quantize(position.Z, low.Z, high.Z, width), low.Z, high.Z, width));
            if (Vector3.Distance(position, decoded) > budget) { return false; }
        }
        return true;
    }
    private static uint Quantize(float value, float low, float high, int width) => high == low ? 0 : (uint)Math.Clamp(Math.Round(((double)value - low) / ((double)high - low) * ((1u << (width * 8)) - 1)), 0, (1u << (width * 8)) - 1);
    private static float Expand(uint value, float low, float high, int width) => (float)(low + ((double)high - low) * value / ((1u << (width * 8)) - 1));
    private static void WriteMaterials(BinaryWriter writer, ReadOnlySpan<uint> materials) {
        var width = 1;
        foreach (var material in materials) { if (material > byte.MaxValue) { width = 4; break; } }
        WritePlane(writer, materials, width);
    }
    private static void WritePlane(BinaryWriter writer, ReadOnlySpan<uint> words, int width) {
        writer.Write((byte)width);
        writer.Write(words.Length);
        var bytes = new byte[checked(words.Length * width)];
        uint previous = 0;
        for (var index = 0; index < words.Length; index++) {
            var delta = unchecked(words[index] - previous);
            previous = words[index];
            for (var plane = 0; plane < width; plane++) { bytes[plane * words.Length + index] = (byte)(delta >> (plane * 8)); }
        }
        writer.Write(bytes);
    }
    private static uint[] ReadPlane(BinaryReader reader) {
        var width = reader.ReadByte();
        if (width is < 1 or > 4) { throw new InvalidDataException("Invalid attribute width."); }
        var count = Count(reader, width);
        var bytes = reader.ReadBytes(checked(count * width));
        var words = new uint[count];
        var mask = width == 4 ? uint.MaxValue : (1u << (width * 8)) - 1;
        uint previous = 0;
        for (var index = 0; index < count; index++) {
            uint delta = 0;
            for (var plane = 0; plane < width; plane++) { delta |= (uint)bytes[plane * count + index] << (plane * 8); }
            words[index] = previous = unchecked(previous + delta) & mask;
        }
        return words;
    }
    private static int Count(BinaryReader reader, int itemBytes) {
        var count = reader.ReadInt32();
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / itemBytes) { throw new InvalidDataException("Invalid attribute count."); }
        return count;
    }
    private static void WriteVector(BinaryWriter writer, Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
    private static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
