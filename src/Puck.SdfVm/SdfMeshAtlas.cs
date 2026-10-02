using System.Numerics;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>
/// A frame's atlases of one kind of texture set: every distinct set (<see cref="SdfTextureSet"/>) packed into one image per
/// usage. The mesh atlases pack baked meshes' surface textures (<see cref="SdfMeshTextures"/>), which the hit passes sample
/// at a mesh hit's texture coordinate; the impostor atlases pack baked impostors' views (<see cref="SdfMeshImpostor"/>).
/// Every set takes the same rectangle in all of its usages' images, so a mesh's texture coordinates move into the atlases
/// by one scale and offset (<see cref="Placement"/>), which the mesh region applies to its vertices.
/// <para>A rectangle starts on a multiple of <see cref="AlignmentOf"/> texels and spans a multiple of it, so at every level
/// of the chain it covers whole blocks of a block-compressed format and packing moves whole blocks: the atlases hold each
/// set's stored bytes unchanged, and a sampler that clamps inside a tile (<c>frame/sdf-mesh-textures.hlsli</c>) reads
/// exactly what the bake wrote. Rectangles are packed in rows, tallest first, the row width the smallest power of two
/// that holds the widest rectangle and at least the square root of their summed area; texels no rectangle covers are
/// zero.</para>
/// </summary>
public sealed class SdfMeshAtlas {
    /// <summary>The most texels an atlas spans on either axis: the largest two-dimensional image both backends
    /// create.</summary>
    public const int MaxExtent = 16384;

    private readonly Dictionary<SdfTextureSet, Vector4> m_placements;

    private SdfMeshAtlas(int width, int height, int levels, IReadOnlyList<byte[]> chains, Dictionary<SdfTextureSet, Vector4> placements) {
        Chains = chains;
        Height = height;
        Levels = levels;
        Width = width;
        m_placements = placements;
    }

    /// <summary>Gets each usage's atlas, in the order of the sets' <see cref="SdfTextureSet.UsageOrder"/>, as every level from 0 tightly
    /// packed in its stored format, the layout an image upload reads (<see cref="GpuPixelFormats.ChainByteLength"/>).</summary>
    public IReadOnlyList<byte[]> Chains { get; }
    /// <summary>Gets the atlases' height in texels at level 0.</summary>
    public int Height { get; }
    /// <summary>Gets the atlases' level count.</summary>
    public int Levels { get; }
    /// <summary>Gets the meshes the atlases hold.</summary>
    public int MeshCount => m_placements.Count;
    /// <summary>Gets the atlases' width in texels at level 0.</summary>
    public int Width { get; }

    /// <summary>Returns the alignment of every rectangle, in level-0 texels: a whole block of four texels at the last level
    /// of a chain of <paramref name="levels"/>, so every level's rectangle is whole blocks.</summary>
    /// <param name="levels">The chain's level count, at least one.</param>
    /// <returns>The alignment.</returns>
    public static int AlignmentOf(int levels) =>
        (((int)GpuPixelFormats.BlockTexels) << (levels - 1));
    /// <summary>Packs every distinct texture set, compared by reference, into one atlas per usage.</summary>
    /// <param name="textures">The texture sets, all of one kind; a set listed twice is packed once.</param>
    /// <returns>The atlases.</returns>
    /// <exception cref="ArgumentException">The list is empty, the sets are not all of one kind, their tiles or level counts
    /// differ, or the packed rows span more than <see cref="MaxExtent"/> texels.</exception>
    public static SdfMeshAtlas Pack(IEnumerable<SdfTextureSet> textures) {
        ArgumentNullException.ThrowIfNull(argument: textures);

        var distinct = textures.Distinct(comparer: ReferenceEqualityComparer.Instance).Cast<SdfTextureSet>().ToArray();

        if (distinct.Length == 0) {
            throw new ArgumentException(
                message: "An atlas holds at least one mesh's textures.",
                paramName: nameof(textures)
            );
        }
        foreach (var set in distinct) {
            ArgumentNullException.ThrowIfNull(argument: set, paramName: nameof(textures));

            if ((set.Width > MaxExtent) || (set.Height > MaxExtent)) {
                throw new ArgumentException(
                    message: $"An atlas spans at most {MaxExtent} texels on either axis; a mesh's textures span {set.Width}x{set.Height}.",
                    paramName: nameof(textures)
                );
            }
        }

        var levels = distinct[0].Levels;
        var tile = distinct[0].TileTexels;
        var usages = distinct[0].UsageOrder;

        foreach (var set in distinct) {
            if (!usages.SequenceEqual(second: set.UsageOrder)) {
                throw new ArgumentException(
                    message: "The sets an atlas holds are all of one kind, with the same usages.",
                    paramName: nameof(textures)
                );
            }
        }

        foreach (var set in distinct) {
            if ((set.Levels != levels) || (set.TileTexels != tile)) {
                throw new ArgumentException(
                    message: $"The meshes an atlas holds share one tile and level count; one is {set.TileTexels}-texel tiles over {set.Levels} levels, another {tile}-texel tiles over {levels} levels.",
                    paramName: nameof(textures)
                );
            }
        }

        var order = distinct
            .Select(selector: (set, index) => (Set: set, Index: index, Width: Align(levels: levels, texels: set.Width), Height: Align(levels: levels, texels: set.Height)))
            .OrderByDescending(keySelector: static entry => entry.Height)
            .ThenBy(keySelector: static entry => entry.Index)
            .ToArray();
        var area = order.Sum(selector: static entry => (((long)entry.Width) * entry.Height));

        if (area > (((long)MaxExtent) * MaxExtent)) {
            throw new ArgumentException(
                message: $"An atlas spans at most {MaxExtent} texels on either axis; these meshes' rectangles cover {area} texels.",
                paramName: nameof(textures)
            );
        }
        var width = ((int)BitOperations.RoundUpToPowerOf2(value: ((uint)Math.Max(
            val1: order.Max(selector: static entry => entry.Width),
            val2: ((int)Math.Ceiling(a: Math.Sqrt(d: area)))
        ))));
        var origins = new (int X, int Y)[distinct.Length];
        var x = 0;
        var y = 0;
        var rowHeight = 0;

        foreach (var entry in order) {
            if ((x + entry.Width) > width) {
                x = 0;
                y += rowHeight;
                rowHeight = 0;
            }

            origins[entry.Index] = (x, y);
            x += entry.Width;
            rowHeight = Math.Max(val1: rowHeight, val2: entry.Height);
        }

        var height = (y + rowHeight);

        if ((width > MaxExtent) || (height > MaxExtent)) {
            throw new ArgumentException(
                message: $"An atlas spans at most {MaxExtent} texels on either axis; these meshes' textures pack into {width}x{height}.",
                paramName: nameof(textures)
            );
        }

        var chains = new byte[usages.Count][];

        for (var usage = 0; (usage < chains.Length); usage++) {
            var format = SdfTextureSet.FormatOf(usage: usages[usage]);

            chains[usage] = new byte[checked((int)GpuPixelFormats.ChainByteLength(format: format, height: ((uint)height), levels: ((uint)levels), width: ((uint)width)))];

            for (var index = 0; (index < distinct.Length); index++) {
                Copy(
                    atlas: chains[usage],
                    atlasHeight: height,
                    atlasWidth: width,
                    format: format,
                    origin: origins[index],
                    texture: distinct[index].Textures[usage]
                );
            }
        }

        var placements = new Dictionary<SdfTextureSet, Vector4>(comparer: ReferenceEqualityComparer.Instance);

        for (var index = 0; (index < distinct.Length); index++) {
            var set = distinct[index];

            placements[set] = new Vector4(
                x: (((float)set.Width) / width),
                y: (((float)set.Height) / height),
                z: (((float)origins[index].X) / width),
                w: (((float)origins[index].Y) / height)
            );
        }

        return new SdfMeshAtlas(
            chains: chains,
            height: height,
            levels: levels,
            placements: placements,
            width: width
        );
    }
    /// <summary>Returns where a packed texture set sits: the scale and then the offset that move its texture coordinates
    /// into the atlases, <c>atlas = (uv * (X, Y)) + (Z, W)</c>, each a ratio of whole texels.</summary>
    /// <param name="textures">A set the atlases hold.</param>
    /// <returns>The scale in X and Y, the offset in Z and W.</returns>
    /// <exception cref="KeyNotFoundException">The atlases do not hold the set.</exception>
    public Vector4 Placement(SdfTextureSet textures) =>
        m_placements[textures];
    /// <summary>Returns whether the atlases hold a texture set.</summary>
    /// <param name="textures">The set.</param>
    /// <returns><see langword="true"/> when it was packed.</returns>
    public bool Holds(SdfTextureSet textures) =>
        m_placements.ContainsKey(key: textures);

    private static int Align(int levels, int texels) {
        var alignment = AlignmentOf(levels: levels);

        return (((texels + (alignment - 1)) / alignment) * alignment);
    }
    // Copies every level of one texture into its rectangle of an atlas chain, a row of units at a time: a unit is a
    // block of a block-compressed format and a texel of any other.
    private static void Copy(byte[] atlas, int atlasWidth, int atlasHeight, GpuPixelFormat format, (int X, int Y) origin, Puck.SignedDistance.Baking.SdfBakedTexture texture) {
        var compressed = GpuPixelFormats.IsBlockCompressed(format: format);
        var unit = ((int)GpuPixelFormats.UnitBytes(format: format));
        var unitTexels = (compressed
            ? (int)GpuPixelFormats.BlockTexels
            : 1);
        var atlasLevel = 0;

        for (var level = 0; (level < texture.Levels.Count); level++) {
            var (levelWidth, levelHeight) = GpuPixelFormats.LevelExtent(height: ((uint)atlasHeight), level: ((uint)level), width: ((uint)atlasWidth));
            var (sourceWidth, sourceHeight) = GpuPixelFormats.LevelExtent(height: ((uint)texture.Height), level: ((uint)level), width: ((uint)texture.Width));
            var atlasColumns = UnitsAcross(texels: ((int)levelWidth), unitTexels: unitTexels);
            var sourceColumns = UnitsAcross(texels: ((int)sourceWidth), unitTexels: unitTexels);
            var sourceRows = UnitsAcross(texels: ((int)sourceHeight), unitTexels: unitTexels);
            var column = ((origin.X >> level) / unitTexels);
            var row = ((origin.Y >> level) / unitTexels);
            var source = texture.Levels[level];

            for (var sourceRow = 0; (sourceRow < sourceRows); sourceRow++) {
                source.AsSpan(
                    length: (sourceColumns * unit),
                    start: ((sourceRow * sourceColumns) * unit)
                ).CopyTo(destination: atlas.AsSpan(
                    length: (sourceColumns * unit),
                    start: (atlasLevel + ((((row + sourceRow) * atlasColumns) + column) * unit))
                ));
            }

            atlasLevel += checked((int)GpuPixelFormats.LevelByteLength(format: format, height: levelHeight, width: levelWidth));
        }
    }
    private static int UnitsAcross(int texels, int unitTexels) =>
        ((texels + (unitTexels - 1)) / unitTexels);
}
