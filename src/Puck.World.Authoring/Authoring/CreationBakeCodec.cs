using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Puck.Assets;
using Puck.Assets.Textures;
using Puck.SignedDistance.Baking;

namespace Puck.World.Authoring;

/// <summary>
/// The canonical bytes of one bake's outcome: a bake, or the refusal of a creation that has none. A refusal is stored
/// as well as a bake, because it is as much a function of the key as a bake is, and a cache that forgot it would bake
/// the creation again. The layout opens with a byte, <c>1</c> for a bake and <c>0</c> for a refusal; a refusal follows
/// with its reason as text. A bake follows with its work counts, then its mesh (cell size, tile columns, the vertices as
/// eight little-endian 32-bit floats each, the 32-bit indices), then its surface textures, then its impostor (center,
/// radius, views, view texels, and its albedo, normal, depth and emission textures, each refused under any other usage).
/// A texture is its usage, format and color space bytes, its first level's width and height, its tile side, its level
/// count, and each level's bytes in its format, the first level first. A texture's format and color space are its usage's (<see cref="SdfBakedTexture.PlanFor"/>) and its
/// level count is its tile's (<see cref="TextureMipChain.LevelCount"/>), so a decoder refuses any other. Counts and
/// dimensions are the canonical variable-length integers of
/// <see cref="CanonicalBinaryWriterExtensions"/>. A change to this layout is a change to what a bake produces, so it
/// moves <see cref="SdfBaker.Version"/>.
/// </summary>
public static class CreationBakeCodec {
    private const int MaximumDimension = (1 << 16);
    private const int MaximumReasonBytes = 4096;

    /// <summary>Encodes a bake.</summary>
    /// <param name="bake">The bake.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bake"/> is <see langword="null"/>.</exception>
    public static byte[] Encode(SdfBake bake) {
        ArgumentNullException.ThrowIfNull(argument: bake);

        var writer = new ArrayBufferWriter<byte>();
        var mesh = bake.Mesh;

        writer.WriteByte(value: 1);
        writer.WriteUInt64(value: ((ulong)bake.Work.MeshEvaluations));
        writer.WriteUInt64(value: ((ulong)bake.Work.TextureEvaluations));
        writer.WriteUInt64(value: ((ulong)bake.Work.ImpostorEvaluations));
        writer.WriteUInt64(value: ((ulong)bake.Work.Rays));
        WriteFloat(writer: writer, value: mesh.CellSize);
        writer.WriteVarUInt(value: ((uint)mesh.TileColumns));
        writer.WriteVarUInt(value: ((uint)mesh.Vertices.Length));

        var vertices = new byte[(mesh.Vertices.Length * SdfBakedVertex.PackedBytes)];

        for (var index = 0; (index < mesh.Vertices.Length); index++) {
            var vertex = mesh.Vertices[index];
            var at = vertices.AsSpan(start: (index * SdfBakedVertex.PackedBytes));

            BinaryPrimitives.WriteSingleLittleEndian(destination: at, value: vertex.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(destination: at[4..], value: vertex.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(destination: at[8..], value: vertex.Position.Z);
            BinaryPrimitives.WriteSingleLittleEndian(destination: at[12..], value: vertex.Normal.X);
            BinaryPrimitives.WriteSingleLittleEndian(destination: at[16..], value: vertex.Normal.Y);
            BinaryPrimitives.WriteSingleLittleEndian(destination: at[20..], value: vertex.Normal.Z);
            BinaryPrimitives.WriteSingleLittleEndian(destination: at[24..], value: vertex.Uv.X);
            BinaryPrimitives.WriteSingleLittleEndian(destination: at[28..], value: vertex.Uv.Y);
        }

        writer.WriteBytes(value: vertices);
        writer.WriteVarUInt(value: ((uint)mesh.Indices.Length));

        var indices = new byte[(mesh.Indices.Length * sizeof(uint))];

        for (var index = 0; (index < mesh.Indices.Length); index++) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination: indices.AsSpan(start: (index * sizeof(uint))), value: mesh.Indices[index]);
        }

        writer.WriteBytes(value: indices);
        writer.WriteVarUInt(value: ((uint)bake.Textures.Count));

        foreach (var texture in bake.Textures) {
            WriteTexture(texture: texture, writer: writer);
        }

        var impostor = bake.Impostor;

        WriteFloat(writer: writer, value: impostor.Center.X);
        WriteFloat(writer: writer, value: impostor.Center.Y);
        WriteFloat(writer: writer, value: impostor.Center.Z);
        WriteFloat(writer: writer, value: impostor.Radius);
        writer.WriteVarUInt(value: ((uint)impostor.Views));
        writer.WriteVarUInt(value: ((uint)impostor.ViewTexels));
        WriteTexture(texture: impostor.Albedo, writer: writer);
        WriteTexture(texture: impostor.Normal, writer: writer);
        WriteTexture(texture: impostor.Depth, writer: writer);
        WriteTexture(texture: impostor.Emission, writer: writer);

        return writer.WrittenSpan.ToArray();
    }
    /// <summary>Encodes the refusal of a creation that has no bake.</summary>
    /// <param name="reason">Why the creation has no bake.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is <see langword="null"/>.</exception>
    public static byte[] EncodeRefusal(string reason) {
        ArgumentNullException.ThrowIfNull(argument: reason);

        var writer = new ArrayBufferWriter<byte>();
        var text = ((reason.Length > (MaximumReasonBytes / 4)) ? reason[..(MaximumReasonBytes / 4)] : reason);

        writer.WriteByte(value: 0);
        writer.WriteText(value: text);

        return writer.WrittenSpan.ToArray();
    }
    /// <summary>Decodes a bake's outcome.</summary>
    /// <param name="content">The bytes <see cref="Encode"/> or <see cref="EncodeRefusal"/> wrote.</param>
    /// <param name="bake">The bake, or <see langword="null"/> when the bytes hold a refusal.</param>
    /// <param name="refusal">The refusal's reason, or <see langword="null"/> when the bytes hold a bake.</param>
    /// <exception cref="InvalidDataException">The bytes are not a canonical bake outcome.</exception>
    public static void Decode(ReadOnlySpan<byte> content, out SdfBake? bake, out string? refusal) {
        var reader = new CanonicalBinaryReader(content: content);

        bake = null;
        refusal = null;

        switch (reader.ReadByte()) {
            case 0:
                refusal = reader.ReadText(maximumByteCount: MaximumReasonBytes);
                reader.ExpectEnd();
                return;
            case 1:
                break;
            default:
                throw new InvalidDataException(message: "a bake's kind byte is neither a bake nor a refusal");
        }

        var meshEvaluations = ((long)reader.ReadUInt64());
        var textureEvaluations = ((long)reader.ReadUInt64());
        var impostorEvaluations = ((long)reader.ReadUInt64());
        var rays = ((long)reader.ReadUInt64());
        var cellSize = ReadFloat(reader: ref reader);
        var tileColumns = reader.ReadBoundedInt(maximum: MaximumDimension);
        var vertexCount = reader.ReadBoundedInt(maximum: (content.Length / SdfBakedVertex.PackedBytes));
        var vertexBytes = reader.ReadBytes(count: (vertexCount * SdfBakedVertex.PackedBytes));
        var vertices = new SdfBakedVertex[vertexCount];

        for (var index = 0; (index < vertexCount); index++) {
            var at = vertexBytes[(index * SdfBakedVertex.PackedBytes)..];

            vertices[index] = new SdfBakedVertex(
                Normal: new Vector3(
                    x: BinaryPrimitives.ReadSingleLittleEndian(source: at[12..]),
                    y: BinaryPrimitives.ReadSingleLittleEndian(source: at[16..]),
                    z: BinaryPrimitives.ReadSingleLittleEndian(source: at[20..])
                ),
                Position: new Vector3(
                    x: BinaryPrimitives.ReadSingleLittleEndian(source: at),
                    y: BinaryPrimitives.ReadSingleLittleEndian(source: at[4..]),
                    z: BinaryPrimitives.ReadSingleLittleEndian(source: at[8..])
                ),
                Uv: new Vector2(
                    x: BinaryPrimitives.ReadSingleLittleEndian(source: at[24..]),
                    y: BinaryPrimitives.ReadSingleLittleEndian(source: at[28..])
                )
            );
        }

        var indexCount = reader.ReadBoundedInt(maximum: (content.Length / sizeof(uint)));
        var indexBytes = reader.ReadBytes(count: (indexCount * sizeof(uint)));
        var indices = new uint[indexCount];

        for (var index = 0; (index < indexCount); index++) {
            indices[index] = BinaryPrimitives.ReadUInt32LittleEndian(source: indexBytes[(index * sizeof(uint))..]);

            if (indices[index] >= ((uint)vertexCount)) {
                throw new InvalidDataException(message: "a bake's index names a vertex it does not have");
            }
        }

        var textures = new SdfBakedTexture[reader.ReadBoundedInt(maximum: 16)];

        for (var index = 0; (index < textures.Length); index++) {
            textures[index] = ReadTexture(reader: ref reader);
        }

        var center = new Vector3(
            x: ReadFloat(reader: ref reader),
            y: ReadFloat(reader: ref reader),
            z: ReadFloat(reader: ref reader)
        );
        var radius = ReadFloat(reader: ref reader);
        var views = reader.ReadBoundedInt(maximum: MaximumDimension);
        var viewTexels = reader.ReadBoundedInt(maximum: MaximumDimension);
        var albedo = ReadImpostorTexture(reader: ref reader, usage: SdfBakeTextureUsage.Albedo);
        var normal = ReadImpostorTexture(reader: ref reader, usage: SdfBakeTextureUsage.Normal);
        var depth = ReadImpostorTexture(reader: ref reader, usage: SdfBakeTextureUsage.Depth);
        var emission = ReadImpostorTexture(reader: ref reader, usage: SdfBakeTextureUsage.Emission);

        reader.ExpectEnd();

        var mesh = new SdfBakedMesh(
            CellSize: cellSize,
            Indices: indices,
            TileColumns: Math.Max(val1: 1, val2: tileColumns),
            Vertices: vertices
        );

        bake = new SdfBake(
            Impostor: new SdfBakedImpostor(
                Albedo: albedo,
                Center: center,
                Depth: depth,
                Emission: emission,
                Normal: normal,
                Radius: radius,
                ViewTexels: viewTexels,
                Views: views
            ),
            Mesh: mesh,
            Textures: textures,
            Work: new SdfBakeWork(
                ImpostorEvaluations: impostorEvaluations,
                MeshEvaluations: meshEvaluations,
                Rays: rays,
                TextureEvaluations: textureEvaluations,
                Triangles: mesh.Triangles,
                Vertices: vertexCount
            )
        );
    }
    /// <summary>Decodes a bake's outcome, answering whether it holds a bake.</summary>
    /// <param name="content">The bytes.</param>
    /// <param name="bake">The bake, or <see langword="null"/> when the bytes hold a refusal.</param>
    /// <param name="refusal">The refusal's reason, or <see langword="null"/> when the bytes hold a bake.</param>
    /// <returns><see langword="true"/> when the bytes hold a bake.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a canonical bake outcome.</exception>
    public static bool TryDecode(ReadOnlySpan<byte> content, [NotNullWhen(returnValue: true)] out SdfBake? bake, out string? refusal) {
        Decode(
            bake: out bake,
            content: content,
            refusal: out refusal
        );

        return (bake is not null);
    }

    private static void WriteFloat(ArrayBufferWriter<byte> writer, float value) {
        Span<byte> bytes = stackalloc byte[sizeof(float)];

        BinaryPrimitives.WriteSingleLittleEndian(destination: bytes, value: value);
        writer.WriteBytes(value: bytes);
    }
    private static float ReadFloat(ref CanonicalBinaryReader reader) =>
        BinaryPrimitives.ReadSingleLittleEndian(source: reader.ReadBytes(count: sizeof(float)));
    private static void WriteTexture(ArrayBufferWriter<byte> writer, SdfBakedTexture texture) {
        writer.WriteByte(value: ((byte)texture.Usage));
        writer.WriteByte(value: ((byte)texture.Format));
        writer.WriteByte(value: ((byte)texture.ColorSpace));
        writer.WriteVarUInt(value: ((uint)texture.Width));
        writer.WriteVarUInt(value: ((uint)texture.Height));
        writer.WriteVarUInt(value: ((uint)texture.TileTexels));
        writer.WriteVarUInt(value: ((uint)texture.Levels.Count));

        foreach (var level in texture.Levels) {
            writer.WriteBytes(value: level);
        }
    }
    // An impostor's texture sits in a slot of one usage; a texture of any other usage there is not an impostor's.
    private static SdfBakedTexture ReadImpostorTexture(ref CanonicalBinaryReader reader, SdfBakeTextureUsage usage) {
        var texture = ReadTexture(reader: ref reader);

        if (texture.Usage != usage) {
            throw new InvalidDataException(message: $"a bake's impostor holds a {texture.Usage} texture where its {usage} belongs");
        }

        return texture;
    }
    private static SdfBakedTexture ReadTexture(ref CanonicalBinaryReader reader) {
        var usage = ((SdfBakeTextureUsage)reader.ReadByte());
        var format = ((TextureFormat)reader.ReadByte());
        var colorSpace = ((TextureColorSpace)reader.ReadByte());

        if (!Enum.IsDefined(value: usage)) {
            throw new InvalidDataException(message: "a baked texture names a usage that does not exist");
        }

        var plan = SdfBakedTexture.PlanFor(usage: usage);

        if ((format != plan.Stored) || (colorSpace != plan.ColorSpace)) {
            throw new InvalidDataException(message: $"a baked {usage} texture is stored as {format} in {colorSpace}, not its usage's {plan.Stored} in {plan.ColorSpace}");
        }

        var width = reader.ReadBoundedInt(maximum: MaximumDimension);
        var height = reader.ReadBoundedInt(maximum: MaximumDimension);
        var tile = reader.ReadBoundedInt(maximum: MaximumDimension);

        if ((tile <= 0) || !int.IsPow2(value: tile) || (width <= 0) || (height <= 0) || ((width % tile) != 0) || ((height % tile) != 0)) {
            throw new InvalidDataException(message: $"a baked texture of {width}x{height} texels is not whole tiles of {tile}");
        }

        var count = reader.ReadBoundedInt(maximum: 32);

        if (count != TextureMipChain.LevelCount(tileTexels: tile)) {
            throw new InvalidDataException(message: $"a baked texture over {tile}-texel tiles holds {count} levels, not {TextureMipChain.LevelCount(tileTexels: tile)}");
        }

        var levels = new byte[count][];

        for (var level = 0; (level < count); level++) {
            var (levelWidth, levelHeight) = TextureFormats.LevelExtent(height: height, level: level, width: width);
            var length = TextureFormats.LevelBytes(format: format, height: levelHeight, width: levelWidth);

            if (length > reader.Remaining) {
                throw new InvalidDataException(message: "a baked texture is truncated");
            }

            levels[level] = reader.ReadBytes(count: ((int)length)).ToArray();
        }

        return new SdfBakedTexture(
            ColorSpace: colorSpace,
            Format: format,
            Height: height,
            Levels: levels,
            TileTexels: tile,
            Usage: usage,
            Width: width
        );
    }
}
