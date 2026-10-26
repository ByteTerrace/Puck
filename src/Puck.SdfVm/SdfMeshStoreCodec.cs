using System.Buffers.Binary;
using System.Buffers;
using System.IO.Compression;
using System.IO.Hashing;
using System.Numerics;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

// Mesh streams use independent attribute planes, delta predicted within each plane, then zlib.
// The envelope checks compressed bytes before allocation; decoded counts are bounded by remaining bytes.
internal static class SdfMeshStoreCodec {
    internal const ulong Magic = 0x3948534D4B435550ul; // PUCKMSH9
    internal const int HeaderBytes = 28;

    private const int ChecksumBytes = 16;

    internal static byte[] Encode(UInt128 key, SdfMesh? mesh, int[] materials) {
        var pieces = ((mesh is null) ? Array.Empty<SdfMesh>() : ((mesh.Chunks.Count == 0) ? new[] { mesh } : mesh.Chunks));
        var low = ((pieces.Count == 0) ? Vector3.Zero : pieces[0].Positions.Span[0]);
        var high = low;

        foreach (var piece in pieces) {
            foreach (var position in piece.Positions.Span) { low = Vector3.Min(value1: low, value2: position); high = Vector3.Max(value1: high, value2: position); }
        }
        // Every shared seam vertex uses exactly the same grid, including its width. A per-piece grid opens cracks.
        var width = ((pieces.Count == 0) ? 4 : PositionWidth(high: high, low: low, pieces: pieces));
        using var payload = new MemoryStream();

        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write(value: materials.Length);
            foreach (var material in materials) { writer.Write(value: material); }
            writer.Write(value: (mesh is { Chunks.Count: > 0 }));
            writer.Write(value: pieces.Count);
            foreach (var piece in pieces) { WriteMesh(high: high, low: low, mesh: piece, width: width, writer: writer); }
        }
        using var output = new MemoryStream();

        output.Write(buffer: new byte[HeaderBytes]);
        using (var compressor = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true)) {
            compressor.Write(buffer: payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
        }
        var length = checked((int)output.Length);

        output.Write(buffer: new byte[ChecksumBytes]);
        var bytes = output.ToArray();

        BinaryPrimitives.WriteUInt64LittleEndian(destination: bytes, value: Magic);
        BinaryPrimitives.WriteUInt128LittleEndian(destination: bytes.AsSpan(start: 8), value: key);
        BinaryPrimitives.WriteInt32LittleEndian(destination: bytes.AsSpan(start: 24), value: checked((int)payload.Length));
        BinaryPrimitives.WriteUInt128LittleEndian(destination: bytes.AsSpan(start: length), value: XxHash128.HashToUInt128(bytes.AsSpan(length: length, start: 0)));
        return bytes;
    }
    internal static bool TryDecode(byte[] bytes, UInt128 key, out SdfMesh? mesh,
        out int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? refusal) {
        mesh = null;
        materials = [];
        refusal = "is truncated";
        if (bytes.Length < (HeaderBytes + ChecksumBytes)) { return false; }
        var body = bytes.AsSpan(0, (bytes.Length - ChecksumBytes));

        refusal = "fails its checksum";
        if (XxHash128.HashToUInt128(body) != BinaryPrimitives.ReadUInt128LittleEndian(source: bytes.AsSpan(start: body.Length))) { return false; }
        refusal = "is not a mesh entry of this format";
        if (BinaryPrimitives.ReadUInt64LittleEndian(source: body) != Magic) { return false; }
        refusal = "holds another geometry's mesh";
        if (BinaryPrimitives.ReadUInt128LittleEndian(source: body[8..]) != key) { return false; }
        refusal = "holds invalid compressed mesh data";
        var length = BinaryPrimitives.ReadInt32LittleEndian(source: body[24..]);

        if ((length <= 0) || (length > 1_073_741_824)) { return false; }
        byte[]? decoded = null;

        try {
            decoded = ArrayPool<byte>.Shared.Rent(minimumLength: length);
            try {
                using var compressed = new MemoryStream(bytes, HeaderBytes, (body.Length - HeaderBytes), writable: false);
                using var decoder = new ZLibStream(mode: CompressionMode.Decompress, stream: compressed);

                decoder.ReadExactly(buffer: decoded.AsSpan(length: length, start: 0));
                if (decoder.ReadByte() != -1) { return false; }
            } catch (Exception exception) when ((exception is IOException or InvalidDataException)) { return false; }
            using var input = new MemoryStream(decoded, 0, length, writable: false, publiclyVisible: true);
            using var reader = new BinaryReader(input: input);
            var palette = new int[Count(itemBytes: 4, reader: reader)];

            for (var index = 0; (index < palette.Length); index++) { palette[index] = reader.ReadInt32(); }
            var chunked = reader.ReadBoolean();
            var chunks = new SdfMesh[Count(itemBytes: 32, reader: reader)];

            if ((chunked && (chunks.Length == 0)) || (!chunked && (chunks.Length > 1)) || ((chunks.Length == 0) && (palette.Length != 0))) { return false; }
            for (var index = 0; (index < chunks.Length); index++) { chunks[index] = ReadMesh(reader: reader); }
            if (input.Position != input.Length) { return false; }
            mesh = ((chunks.Length == 0) ? null : (chunked ? SdfMeshChunks.Join(chunks: chunks) : chunks[0]));
            materials = palette;
            refusal = null;
            return true;
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or ArgumentException or OverflowException)) {
            refusal = $"is no mesh: {exception.Message}";
            return false;
        } finally {
            if (decoded is not null) { ArrayPool<byte>.Shared.Return(decoded); }
        }
    }

    private static void WriteMesh(BinaryWriter writer, SdfMesh mesh, Vector3 low, Vector3 high, int width) {
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
        var oct = true;

        foreach (var normal in mesh.Normals.Span) { oct &= (MathF.Abs(x: (normal.LengthSquared() - 1f)) <= 0.00001f); }
        writer.Write(value: oct);
        if (oct) {
            var words = new uint[mesh.Normals.Length];

            for (var index = 0; (index < words.Length); index++) {
                var normal = mesh.Normals.Span[index];

                var (x, y) = SdfOctahedral.Encode(direction: new Double3(X: normal.X, Y: normal.Y, Z: normal.Z));
                words[index] = ((uint)((ushort)((short)Math.Round(a: (x * 32767))))) | (((uint)((ushort)((short)Math.Round(a: (y * 32767))))) << 16);
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
        WriteMaterials(writer, mesh.TriangleMaterials.Span);
        writer.Write(value: mesh.Members.Length);
        foreach (var member in mesh.Members.Span) {
            writer.Write(value: member.SlotOffset);
            WriteVector(writer, member.Position);
            writer.Write(value: member.Orientation.X); writer.Write(value: member.Orientation.Y); writer.Write(value: member.Orientation.Z); writer.Write(value: member.Orientation.W);
        }
        writer.Write(value: mesh.VertexMembers.Length);
        writer.Write(buffer: mesh.VertexMembers.Span);
        writer.Write(value: mesh.Levels.Count);
        foreach (var level in mesh.Levels) {
            writer.Write(value: level.Error);
            WritePlane(writer, level.Indices.Span, ((positions.Length <= 65536) ? 2 : 4));
            WriteMaterials(writer, level.TriangleMaterials.Span);
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static SdfMesh ReadMesh(BinaryReader reader) {
        var cell = ReadVector(reader: reader);

        if (!float.IsFinite(f: cell.LengthSquared()) || (Vector3.Min(value1: cell, value2: Vector3.Zero) != Vector3.Zero)) { throw new InvalidDataException(message: "Invalid contour cell."); }
        var width = reader.ReadByte();

        if (width is not (2 or 3 or 4)) { throw new InvalidDataException(message: "Invalid position width."); }
        var low = ReadVector(reader: reader);
        var high = ReadVector(reader: reader);
        var positions = ReadVectors(high: high, low: low, reader: reader, width: width);
        Vector3[] normals;

        if (reader.ReadBoolean()) {
            var words = ReadPlaneBytes(reader: reader);

            if (words.Width != 4) { throw new InvalidDataException(message: "Invalid octahedral width."); }
            normals = GC.AllocateUninitializedArray<Vector3>(words.Count);
            var previous = 0U;

            for (var index = 0; (index < words.Count); index++) {
                var word = previous = Add4(bytes: words.Bytes, count: words.Count, index: index, previous: previous);
                var normal = SdfOctahedral.Decode(x: (((short)(word & 65535)) / 32767.0), y: (((short)(word >> 16)) / 32767.0));

                normals[index] = new Vector3(x: ((float)normal.X), y: ((float)normal.Y), z: ((float)normal.Z));
            }
        } else { normals = ReadVectors(high: default, low: default, reader: reader, width: 4); }
        var indices = ReadPlane(reader: reader);
        var materials = ReadPlane(reader: reader);
        var members = new SdfMeshMember[Count(itemBytes: 32, reader: reader)];

        for (var index = 0; (index < members.Length); index++) {
            var slot = reader.ReadInt32();
            var position = ReadVector(reader: reader);

            members[index] = new SdfMeshMember(slot, position, new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        }
        var vertexMembers = reader.ReadBytes(count: Count(itemBytes: 1, reader: reader));
        var levels = new SdfMeshLevel[Count(itemBytes: 14, reader: reader)];

        for (var index = 0; (index < levels.Length); index++) {
            var error = reader.ReadSingle();

            levels[index] = new SdfMeshLevel(ReadPlane(reader: reader), ReadPlane(reader: reader), error);
        }
        return new SdfMesh(indices: indices, members: members, normals: normals, positions: positions, triangleMaterials: materials, vertexMembers: vertexMembers) { BakeCell = cell, Levels = levels };
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static Vector3[] ReadVectors(BinaryReader reader, int width, Vector3 low, Vector3 high) {
        var x = ReadPlaneBytes(reader: reader);
        var y = ReadPlaneBytes(reader: reader);
        var z = ReadPlaneBytes(reader: reader);

        if ((x.Count != y.Count) || (x.Count != z.Count) || (x.Width != width) || (y.Width != width) || (z.Width != width)) { throw new InvalidDataException(message: "Invalid vector planes."); }
        var vectors = GC.AllocateUninitializedArray<Vector3>(x.Count);
        uint px = 0, py = 0, pz = 0;

        if (width == 4) {
            for (var index = 0; (index < vectors.Length); index++) {
                px = Add4(bytes: x.Bytes, count: x.Count, index: index, previous: px);
                py = Add4(bytes: y.Bytes, count: y.Count, index: index, previous: py);
                pz = Add4(bytes: z.Bytes, count: z.Count, index: index, previous: pz);
                vectors[index] = new Vector3(x: BitConverter.UInt32BitsToSingle(value: px), y: BitConverter.UInt32BitsToSingle(value: py), z: BitConverter.UInt32BitsToSingle(value: pz));
            }
        } else {
            var divisor = ((double)((1u << (width * 8)) - 1));
            var extentX = (((double)high.X) - low.X);
            var extentY = (((double)high.Y) - low.Y);
            var extentZ = (((double)high.Z) - low.Z);

            for (var index = 0; (index < vectors.Length); index++) {
                px = ((width == 2) ? Add2(bytes: x.Bytes, count: x.Count, index: index, previous: px) : Add3(bytes: x.Bytes, count: x.Count, index: index, previous: px));
                py = ((width == 2) ? Add2(bytes: y.Bytes, count: y.Count, index: index, previous: py) : Add3(bytes: y.Bytes, count: y.Count, index: index, previous: py));
                pz = ((width == 2) ? Add2(bytes: z.Bytes, count: z.Count, index: index, previous: pz) : Add3(bytes: z.Bytes, count: z.Count, index: index, previous: pz));
                vectors[index] = new Vector3(x: ((float)(low.X + ((extentX * px) / divisor))),
                    y: ((float)(low.Y + ((extentY * py) / divisor))), z: ((float)(low.Z + ((extentZ * pz) / divisor))));
            }
        }
        return vectors;
    }

    private readonly ref struct Plane(ReadOnlySpan<byte> bytes, int count, int width) {
        public ReadOnlySpan<byte> Bytes { get; } = bytes;
        public int Count { get; } = count;
        public int Width { get; } = width;
    }

    private static Plane ReadPlaneBytes(BinaryReader reader) {
        var width = reader.ReadByte();

        if (width is < 1 or > 4) { throw new InvalidDataException(message: "Invalid attribute width."); }
        var count = Count(itemBytes: width, reader: reader);
        var input = ((MemoryStream)reader.BaseStream);
        var bytes = input.GetBuffer().AsSpan(checked((int)input.Position), checked((count * width)));

        input.Position += bytes.Length;
        return new Plane(bytes: bytes, count: count, width: width);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint Add2(ReadOnlySpan<byte> bytes, int count, int index, uint previous) => unchecked(((previous + bytes[index]) + (((uint)bytes[(count + index)]) << 8))) & 65535;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint Add3(ReadOnlySpan<byte> bytes, int count, int index, uint previous) => unchecked((((previous + bytes[index]) + (((uint)bytes[(count + index)]) << 8)) + (((uint)bytes[((2 * count) + index)]) << 16))) & 16777215;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint Add4(ReadOnlySpan<byte> bytes, int count, int index, uint previous) => unchecked(((((previous + bytes[index]) + (((uint)bytes[(count + index)]) << 8)) + (((uint)bytes[((2 * count) + index)]) << 16)) + (((uint)bytes[((3 * count) + index)]) << 24)));
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
        foreach (var position in mesh.Positions.Span) {
            var decoded = new Vector3(x: Expand(Quantize(high: high.X, low: low.X, value: position.X, width: width), low.X, high.X, width),
                y: Expand(Quantize(high: high.Y, low: low.Y, value: position.Y, width: width), low.Y, high.Y, width),
                z: Expand(Quantize(high: high.Z, low: low.Z, value: position.Z, width: width), low.Z, high.Z, width));

            if (Vector3.Distance(value1: position, value2: decoded) > budget) { return false; }
        }
        return true;
    }
    private static uint Quantize(float value, float low, float high, int width) => ((high == low) ? 0 : (uint)Math.Clamp(Math.Round(a: (((((double)value) - low) / (((double)high) - low)) * ((1u << (width * 8)) - 1))), 0, ((1u << (width * 8)) - 1)));
    private static float Expand(uint value, float low, float high, int width) => ((float)(low + (((((double)high) - low) * value) / ((1u << (width * 8)) - 1))));
    private static void WriteMaterials(BinaryWriter writer, ReadOnlySpan<uint> materials) {
        var width = 1;

        foreach (var material in materials) { if (material > byte.MaxValue) { width = 4; break; } }
        WritePlane(width: width, words: materials, writer: writer);
    }
    private static void WritePlane(BinaryWriter writer, ReadOnlySpan<uint> words, int width) {
        writer.Write(value: ((byte)width));
        writer.Write(value: words.Length);
        var bytes = new byte[checked((words.Length * width))];
        var previous = 0U;

        for (var index = 0; (index < words.Length); index++) {
            var delta = unchecked((words[index] - previous));

            previous = words[index];
            for (var plane = 0; (plane < width); plane++) { bytes[((plane * words.Length) + index)] = ((byte)(delta >> (plane * 8))); }
        }
        writer.Write(buffer: bytes);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static uint[] ReadPlane(BinaryReader reader) {
        var width = reader.ReadByte();

        if (width is < 1 or > 4) { throw new InvalidDataException(message: "Invalid attribute width."); }
        var count = Count(itemBytes: width, reader: reader);
        // Attribute bytes already live in the decoded payload. Slicing it avoids another allocation and copy per plane.
        var input = ((MemoryStream)reader.BaseStream);
        var bytes = input.GetBuffer().AsSpan(checked((int)input.Position), checked((count * width)));

        input.Position += bytes.Length;
        var words = new uint[count];
        var previous = 0U;

        switch (width) {
            case 1:
                for (var index = 0; (index < count); index++) { words[index] = previous = unchecked((previous + bytes[index])) & 255; }
                break;
            case 2:
                var second = bytes[count..];
                for (var index = 0; (index < count); index++) { words[index] = previous = unchecked(((previous + bytes[index]) + (((uint)second[index]) << 8))) & 65535; }
                break;
            case 3:
                second = bytes[count..];
                var third = bytes[(2 * count)..];
                for (var index = 0; (index < count); index++) { words[index] = previous = unchecked((((previous + bytes[index]) + (((uint)second[index]) << 8)) + (((uint)third[index]) << 16))) & 16777215; }
                break;
            default:
                second = bytes[count..];
                third = bytes[(2 * count)..];
                var fourth = bytes[(3 * count)..];
                for (var index = 0; (index < count); index++) { words[index] = previous = unchecked(((((previous + bytes[index]) + (((uint)second[index]) << 8)) + (((uint)third[index]) << 16)) + (((uint)fourth[index]) << 24))); }
                break;
        }
        return words;
    }
    private static int Count(BinaryReader reader, int itemBytes) {
        var count = reader.ReadInt32();

        if ((count < 0) || (count > ((reader.BaseStream.Length - reader.BaseStream.Position) / itemBytes))) { throw new InvalidDataException(message: "Invalid attribute count."); }
        return count;
    }
    private static void WriteVector(BinaryWriter writer, Vector3 value) { writer.Write(value: value.X); writer.Write(value: value.Y); writer.Write(value: value.Z); }
    private static Vector3 ReadVector(BinaryReader reader) => new(x: reader.ReadSingle(), y: reader.ReadSingle(), z: reader.ReadSingle());
}
