using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Numerics;
using Puck.SignedDistance.Illumination;
using Puck.Abstractions.Memory;
using static Puck.SdfVm.SdfMeshStoreCodec;

namespace Puck.SdfVm;

// Restores the versioned entry into the storage chosen by its reader.
internal static unsafe class SdfMeshStoreDecoder {
    internal static bool TryPayload(ReadOnlySpan<byte> bytes, UInt128 key, out ReadOnlySpan<byte> compressed,
        out int length, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? refusal) {
        compressed = default;
        length = 0;
        refusal = "is truncated";
        if (bytes.Length < (HeaderBytes + ChecksumBytes)) { return false; }
        var body = bytes[..(bytes.Length - ChecksumBytes)];

        refusal = "fails its checksum";
        if (XxHash128.HashToUInt128(body) != BinaryPrimitives.ReadUInt128LittleEndian(source: bytes[body.Length..])) { return false; }
        refusal = "is not a mesh entry of this format";
        if (BinaryPrimitives.ReadUInt64LittleEndian(source: body) != Magic) { return false; }
        refusal = "holds another geometry's mesh";
        if (BinaryPrimitives.ReadUInt128LittleEndian(source: body[8..]) != key) { return false; }
        refusal = "holds invalid compressed mesh data";
        length = BinaryPrimitives.ReadInt32LittleEndian(source: body[24..]);
        compressed = body[HeaderBytes..];
        return (length > 0) && (length <= 1_073_741_824) && !compressed.IsEmpty;
    }

    internal static bool TryInflate(ReadOnlySpan<byte> compressed, Span<byte> destination) {
        try {
            fixed (byte* start = compressed) {
                using var input = new UnmanagedMemoryStream(pointer: start, length: compressed.Length);
                using var decoder = new ZLibStream(mode: CompressionMode.Decompress, stream: input);
                decoder.ReadExactly(destination);
                return decoder.ReadByte() == -1;
            }
        } catch (Exception exception) when (exception is IOException or InvalidDataException) {
            return false;
        }
    }

    // Decodes an entry into a storage, which keeps each plane where the mesh will live. `taking` is the taking group's
    // material ids: the triangle ids are written renumbered to them when the mesh can be held in them and renumbered back
    // by place (SdfMeshMaterials.Remap), and as the entry stores them otherwise; `materials` answers the ids the written
    // mesh carries. `declined` marks an entry that is sound but whose shape the storage did not take.
    internal static bool TryDecode(ReadOnlySpan<byte> bytes, UInt128 key, SdfMeshStorage storage, int[]? taking, out SdfMesh? mesh,
        out int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? refusal, out bool declined, in SdfMeshStoreWorkspace workspace, Puck.Hosting.Jobs.MemoryAccount.Owner owner, IAllocator? allocator = null) {
        mesh = null;
        materials = [];
        declined = false;
        if (!TryPayload(bytes, key, out var compressed, out var length, out refusal)) { return false; }
        refusal = "holds invalid compressed mesh data";

        var finished = false;

        try {
            // Expanded entries belong to this decode alone. A shared pool retains a large buffer per size bucket
            // on every worker after a parallel store load, even though no mesh borrows the encoded planes.
            using var decoded = new OwnedBuffer<byte>(allocator: allocator, observer: owner);

            // The expanded planes are reserved before they are allocated, beside the entry's bytes and under the
            // read's one owner, so a read admitted as the forced job is admitted whole. A decode that finds no room in
            // its patience declines the entry, which stays in the store: its caller misses, and the mesh is derived
            // or read again.
            if (!workspace.TryReserve(bytes: (((long)bytes.Length) + length), owner: owner)) {
                refusal = SdfMeshStore.WorkspaceRefusal;
                declined = true;

                return false;
            }
            decoded.Resize(clear: false, length: length, preserve: false);
            if (!TryInflate(compressed, decoded.GetSpan())) { return false; }

            var cursor = new Cursor(data: decoded.GetSpan());
            var palette = new int[cursor.Count(itemBytes: 4)];

            for (var index = 0; (index < palette.Length); index++) { palette[index] = cursor.ReadInt32(); }
            var chunked = cursor.ReadBoolean();
            var pieces = new SdfMeshPieceShape[cursor.Count(itemBytes: 32)];

            if ((chunked && (pieces.Length == 0)) || (!chunked && (pieces.Length > 1)) || ((pieces.Length == 0) && (palette.Length != 0))) { return false; }
            // The first walk reads each plane's header and skips its bytes, which gives the shape before any plane is decoded; the second decodes.
            var planes = cursor.Position;

            for (var index = 0; (index < pieces.Length); index++) { pieces[index] = Walk(cursor: ref cursor, piece: index, remap: null, storage: null); }
            if (cursor.Position != cursor.Length) { return false; }
            var shape = Shaped(chunked: chunked, pieces: pieces);
            var remap = SdfMeshMaterials.RemapOf(from: palette, to: taking);

            try {
                while (true) {
                    if (!storage.Begin(shape: shape)) {
                        declined = true;
                        refusal = "holds a mesh whose parts no longer fit the storage that restores them";
                        return false;
                    }
                    cursor.Position = planes;
                    for (var index = 0; (index < pieces.Length); index++) { _ = Walk(cursor: ref cursor, piece: index, remap: remap, storage: storage); }
                    // An id outside the palette that the taking group names: the mesh is held in the entry's ids, and the group renumbers it by place.
                    if (remap is not { Collided: true }) { break; }
                    storage.Abandon();
                    remap = null;
                }
                mesh = storage.Finish();
                finished = true;
            } finally {
                if (!finished) { storage.Abandon(); }
            }
            materials = ((remap is null) ? palette : taking!);
            refusal = null;
            return true;
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or ArgumentException or OverflowException)) {
            refusal = $"is no mesh: {exception.Message}";
            return false;
        }
    }

    // Validates the planes' counts against each other before a storage sizes its parts by them.
    private static SdfMeshShape Shaped(bool chunked, SdfMeshPieceShape[] pieces) {
        foreach (var piece in pieces) {
            if (((piece.Indices % 3) != 0) || ((piece.Materials != 0) && (piece.Materials != piece.Triangles)) || ((piece.Normals != 0) && (piece.Normals != piece.Vertices))) {
                throw new InvalidDataException(message: "Invalid attribute counts.");
            }
            // A chunk is a view of the joined attributes: every chunk carries the attributes the first does, and no skin.
            if (chunked && (((piece.Normals != 0) != (pieces[0].Normals != 0)) || ((piece.Materials != 0) != (pieces[0].Materials != 0)) || (piece.Members != 0) || (piece.VertexMembers != 0) || (piece.Influences != 0))) {
                throw new InvalidDataException(message: "Invalid chunk attributes.");
            }
        }

        return new SdfMeshShape(Chunked: chunked, Encloses: true, Pieces: pieces);
    }
    // Reads one piece's planes in the entry's order. With no storage it reads their headers alone and returns the piece's
    // shape; with one it decodes each plane the storage takes into the span it hands out, and skips the others.
    private static SdfMeshPieceShape Walk(ref Cursor cursor, int piece, SdfMeshMaterials.Remap? remap, SdfMeshStorage? storage) {
        var cell = cursor.ReadVector();

        if (!float.IsFinite(f: cell.LengthSquared()) || (Vector3.Min(value1: cell, value2: Vector3.Zero) != Vector3.Zero)) { throw new InvalidDataException(message: "Invalid contour cell."); }
        var width = cursor.ReadByte();

        if (width is not (2 or 3 or 4)) { throw new InvalidDataException(message: "Invalid position width."); }
        var low = cursor.ReadVector();
        var high = cursor.ReadVector();
        var x = ReadPlaneBytes(cursor: ref cursor);
        var y = ReadPlaneBytes(cursor: ref cursor);
        var z = ReadPlaneBytes(cursor: ref cursor);

        RequireVectorPlanes(width: width, x: x, y: y, z: z);
        var vertices = x.Count;

        if ((storage is not null) && storage.Positions(destination: out var positions, piece: piece)) { DecodeVectors(destination: positions, high: high, low: low, width: width, x: x, y: y, z: z); }
        var normals = 0;

        if (cursor.ReadBoolean()) {
            var words = ReadPlaneBytes(cursor: ref cursor);

            if (words.Width != 4) { throw new InvalidDataException(message: "Invalid octahedral width."); }
            normals = words.Count;
            if ((storage is not null) && storage.Normals(destination: out var octahedral, piece: piece)) { DecodeOctahedral(destination: octahedral, plane: words); }
        } else {
            x = ReadPlaneBytes(cursor: ref cursor);
            y = ReadPlaneBytes(cursor: ref cursor);
            z = ReadPlaneBytes(cursor: ref cursor);
            RequireVectorPlanes(width: 4, x: x, y: y, z: z);
            normals = x.Count;
            if ((storage is not null) && storage.Normals(destination: out var components, piece: piece)) { DecodeVectors(destination: components, high: default, low: default, width: 4, x: x, y: y, z: z); }
        }
        var indices = ReadPlaneBytes(cursor: ref cursor);

        if ((storage is not null) && storage.Indices(destination: out var indexWords, piece: piece)) { DecodePlane(destination: indexWords, plane: indices); }
        var materials = ReadPlaneBytes(cursor: ref cursor);

        if ((storage is not null) && storage.Materials(destination: out var materialWords, piece: piece)) {
            DecodePlane(destination: materialWords, plane: materials);
            remap?.Apply(ids: materialWords);
        }
        var members = cursor.Count(itemBytes: 32);
        var memberBytes = cursor.Take(length: (members * 32));

        if ((storage is not null) && storage.Members(destination: out var memberWords, piece: piece)) { DecodeMembers(bytes: memberBytes, destination: memberWords); }
        var vertexMembers = cursor.Count(itemBytes: 1);
        var vertexMemberBytes = cursor.Take(length: vertexMembers);

        if ((storage is not null) && storage.VertexMembers(destination: out var vertexMemberBytesOut, piece: piece)) { vertexMemberBytes.CopyTo(destination: vertexMemberBytesOut); }
        var blends = ReadPlaneBytes(cursor: ref cursor);
        var xy = ReadPlaneBytes(cursor: ref cursor);
        var gradientZ = ReadPlaneBytes(cursor: ref cursor);

        if ((blends.Count != xy.Count) || (blends.Count != gradientZ.Count)) { throw new InvalidDataException(message: "Invalid influence planes."); }
        if ((storage is not null) && storage.Influences(destination: out var influences, piece: piece)) { DecodeInfluences(blends: blends, destination: influences, gradientXY: xy, gradientZ: gradientZ); }
        var levels = new SdfMeshLevelShape[cursor.Count(itemBytes: 14)];

        for (var level = 0; (level < levels.Length); level++) {
            var error = cursor.ReadSingle();
            var levelIndices = ReadPlaneBytes(cursor: ref cursor);

            if ((storage is not null) && storage.LevelIndices(destination: out var levelIndexWords, level: level, piece: piece)) { DecodePlane(destination: levelIndexWords, plane: levelIndices); }
            var levelMaterials = ReadPlaneBytes(cursor: ref cursor);

            if ((storage is not null) && storage.LevelMaterials(destination: out var levelMaterialWords, level: level, piece: piece)) {
                DecodePlane(destination: levelMaterialWords, plane: levelMaterials);
                remap?.Apply(ids: levelMaterialWords);
            }
            levels[level] = new SdfMeshLevelShape(Error: error, Indices: levelIndices.Count, Materials: levelMaterials.Count);
        }

        return new SdfMeshPieceShape(BakeCell: cell, Indices: indices.Count, Influences: blends.Count, Levels: levels, Materials: materials.Count, Members: members, Normals: normals, VertexMembers: vertexMembers, Vertices: vertices);
    }
    private static void RequireVectorPlanes(int width, Plane x, Plane y, Plane z) {
        if ((x.Count != y.Count) || (x.Count != z.Count) || (x.Width != width) || (y.Width != width) || (z.Width != width)) { throw new InvalidDataException(message: "Invalid vector planes."); }
    }
    private static void RequireLength(int expected, int actual) {
        if (expected != actual) { throw new InvalidDataException(message: "A plane disagrees with the shape its storage was sized by."); }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void DecodeVectors(Span<Vector3> destination, int width, Vector3 low, Vector3 high, Plane x, Plane y, Plane z) {
        RequireLength(expected: x.Count, actual: destination.Length);
        uint px = 0, py = 0, pz = 0;

        if (width == 4) {
            for (var index = 0; (index < destination.Length); index++) {
                px = Add4(bytes: x.Bytes, count: x.Count, index: index, previous: px);
                py = Add4(bytes: y.Bytes, count: y.Count, index: index, previous: py);
                pz = Add4(bytes: z.Bytes, count: z.Count, index: index, previous: pz);
                destination[index] = new Vector3(x: BitConverter.UInt32BitsToSingle(value: px), y: BitConverter.UInt32BitsToSingle(value: py), z: BitConverter.UInt32BitsToSingle(value: pz));
            }
        } else {
            var divisor = Steps(width: width);
            var extentX = (((double)high.X) - low.X);
            var extentY = (((double)high.Y) - low.Y);
            var extentZ = (((double)high.Z) - low.Z);

            for (var index = 0; (index < destination.Length); index++) {
                px = ((width == 2) ? Add2(bytes: x.Bytes, count: x.Count, index: index, previous: px) : Add3(bytes: x.Bytes, count: x.Count, index: index, previous: px));
                py = ((width == 2) ? Add2(bytes: y.Bytes, count: y.Count, index: index, previous: py) : Add3(bytes: y.Bytes, count: y.Count, index: index, previous: py));
                pz = ((width == 2) ? Add2(bytes: z.Bytes, count: z.Count, index: index, previous: pz) : Add3(bytes: z.Bytes, count: z.Count, index: index, previous: pz));
                destination[index] = new Vector3(x: Expand(divisor: divisor, extent: extentX, low: low.X, value: px),
                    y: Expand(divisor: divisor, extent: extentY, low: low.Y, value: py), z: Expand(divisor: divisor, extent: extentZ, low: low.Z, value: pz));
            }
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void DecodeOctahedral(Span<Vector3> destination, Plane plane) {
        RequireLength(expected: plane.Count, actual: destination.Length);
        var previous = 0U;

        for (var index = 0; (index < plane.Count); index++) {
            var word = previous = Add4(bytes: plane.Bytes, count: plane.Count, index: index, previous: previous);
            var normal = SdfOctahedral.UnpackSigned(bits: OctahedralBits, word: word);

            destination[index] = new Vector3(x: ((float)normal.X), y: ((float)normal.Y), z: ((float)normal.Z));
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void DecodePlane(Span<uint> destination, Plane plane) {
        RequireLength(expected: plane.Count, actual: destination.Length);
        var count = plane.Count;
        var bytes = plane.Bytes;
        var previous = 0U;

        switch (plane.Width) {
            case 1:
                for (var index = 0; (index < count); index++) { destination[index] = previous = unchecked((previous + bytes[index])) & 255; }
                break;
            case 2:
                var second = bytes[count..];
                for (var index = 0; (index < count); index++) { destination[index] = previous = unchecked(((previous + bytes[index]) + (((uint)second[index]) << 8))) & 65535; }
                break;
            case 3:
                second = bytes[count..];
                var third = bytes[(2 * count)..];
                for (var index = 0; (index < count); index++) { destination[index] = previous = unchecked((((previous + bytes[index]) + (((uint)second[index]) << 8)) + (((uint)third[index]) << 16))) & 16777215; }
                break;
            default:
                second = bytes[count..];
                third = bytes[(2 * count)..];
                var fourth = bytes[(3 * count)..];
                for (var index = 0; (index < count); index++) { destination[index] = previous = unchecked(((((previous + bytes[index]) + (((uint)second[index]) << 8)) + (((uint)third[index]) << 16)) + (((uint)fourth[index]) << 24))); }
                break;
        }
    }
    private static void DecodeMembers(ReadOnlySpan<byte> bytes, Span<SdfMeshMember> destination) {
        RequireLength(expected: (bytes.Length / 32), actual: destination.Length);
        for (var index = 0; (index < destination.Length); index++) {
            var member = bytes.Slice(length: 32, start: (index * 32));

            destination[index] = new SdfMeshMember(
                BinaryPrimitives.ReadInt32LittleEndian(source: member),
                new Vector3(x: ReadSingle(at: 4, bytes: member), y: ReadSingle(at: 8, bytes: member), z: ReadSingle(at: 12, bytes: member)),
                new Quaternion(x: ReadSingle(at: 16, bytes: member), y: ReadSingle(at: 20, bytes: member), z: ReadSingle(at: 24, bytes: member), w: ReadSingle(at: 28, bytes: member)));
        }
    }
    private static float ReadSingle(ReadOnlySpan<byte> bytes, int at) => BitConverter.Int32BitsToSingle(value: BinaryPrimitives.ReadInt32LittleEndian(source: bytes[at..]));
    // The three planes of an influence are walked beside one another, a word of each at a time; skinned bodies are the only
    // meshes that carry them.
    private static void DecodeInfluences(Plane blends, Plane gradientXY, Plane gradientZ, Span<SdfMeshInfluence> destination) {
        RequireLength(expected: blends.Count, actual: destination.Length);
        uint blend = 0, xy = 0, z = 0;

        for (var index = 0; (index < destination.Length); index++) {
            blend = Add(index: index, plane: blends, previous: blend);
            xy = Add(index: index, plane: gradientXY, previous: xy);
            z = Add(index: index, plane: gradientZ, previous: z);
            destination[index] = new SdfMeshInfluence(blend: blend, gradientXY: xy, gradientZ: z);
        }
    }
    private static uint Add(Plane plane, int index, uint previous) => plane.Width switch {
        1 => (unchecked((previous + plane.Bytes[index])) & 255),
        2 => Add2(bytes: plane.Bytes, count: plane.Count, index: index, previous: previous),
        3 => Add3(bytes: plane.Bytes, count: plane.Count, index: index, previous: previous),
        _ => Add4(bytes: plane.Bytes, count: plane.Count, index: index, previous: previous),
    };

    // A read position over an entry's inflated bytes. A read past the end is an invalid entry.
    private ref struct Cursor(ReadOnlySpan<byte> data) {
        private readonly ReadOnlySpan<byte> m_data = data;

        public int Position;

        public readonly int Length => m_data.Length;

        public byte ReadByte() {
            Require(bytes: 1);

            return m_data[Position++];
        }
        public bool ReadBoolean() => (ReadByte() != 0);
        public int ReadInt32() {
            Require(bytes: 4);

            var value = BinaryPrimitives.ReadInt32LittleEndian(source: m_data[Position..]);

            Position += 4;

            return value;
        }
        public float ReadSingle() => BitConverter.Int32BitsToSingle(value: ReadInt32());
        public Vector3 ReadVector() {
            var x = ReadSingle();
            var y = ReadSingle();

            return new Vector3(x: x, y: y, z: ReadSingle());
        }
        public ReadOnlySpan<byte> Take(int length) {
            Require(bytes: length);

            var bytes = m_data.Slice(length: length, start: Position);

            Position += length;

            return bytes;
        }
        public int Count(int itemBytes) {
            var count = ReadInt32();

            if ((count < 0) || (count > ((m_data.Length - Position) / itemBytes))) { throw new InvalidDataException(message: "Invalid attribute count."); }

            return count;
        }

        private readonly void Require(int bytes) {
            if ((bytes < 0) || (bytes > (m_data.Length - Position))) { throw new InvalidDataException(message: "The entry ends inside a plane."); }
        }
    }
    private readonly ref struct Plane(ReadOnlySpan<byte> bytes, int count, int width) {
        public ReadOnlySpan<byte> Bytes { get; } = bytes;
        public int Count { get; } = count;
        public int Width { get; } = width;
    }

    private static Plane ReadPlaneBytes(ref Cursor cursor) {
        var width = cursor.ReadByte();

        if (width is < 1 or > 4) { throw new InvalidDataException(message: "Invalid attribute width."); }
        var count = cursor.Count(itemBytes: width);

        return new Plane(bytes: cursor.Take(length: checked((count * width))), count: count, width: width);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint Add2(ReadOnlySpan<byte> bytes, int count, int index, uint previous) => unchecked(((previous + bytes[index]) + (((uint)bytes[(count + index)]) << 8))) & 65535;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint Add3(ReadOnlySpan<byte> bytes, int count, int index, uint previous) => unchecked((((previous + bytes[index]) + (((uint)bytes[(count + index)]) << 8)) + (((uint)bytes[((2 * count) + index)]) << 16))) & 16777215;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint Add4(ReadOnlySpan<byte> bytes, int count, int index, uint previous) => unchecked(((((previous + bytes[index]) + (((uint)bytes[(count + index)]) << 8)) + (((uint)bytes[((2 * count) + index)]) << 16)) + (((uint)bytes[((3 * count) + index)]) << 24)));
}
