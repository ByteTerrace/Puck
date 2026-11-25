using Puck.Maths;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The pass-block value holding the cascade set's orthographic light cameras (<c>SdfMeshShadowCascades</c>),
    /// four rows each: position and half height, right and aspect, up and near distance, forward and far distance
    /// (<c>float4</c>, <see cref="MeshShadowCascadeRows"/> a camera of <see cref="MeshShadowEntries"/>): each entry's, the
    /// camera its world's one shadow solution drew it from (<c>SdfWorldShadows</c>).</summary>
    public const string MeshShadowCascades = "meshShadowCascades";
    /// <summary>The pass-block value holding the cascade set's entries the frame rendered, a prefix of the set; zero when
    /// no slotted light emits or the view skips shadows (<c>uint</c>).</summary>
    public const string MeshShadowCount = "meshShadowCount";
    /// <summary>The pass-block value holding the receiver's depth bias, in world units: x the constant bias, y the normal
    /// offset per texel, zw zero (<c>float4</c>).</summary>
    public const string MeshShadowParameters = "meshShadowParameters";
    /// <summary>The pass-block value holding each cascade entry's penumbra half-slope, its light's; zero past the rendered
    /// entries (<c>float4</c>).</summary>
    public const string MeshShadowSlopes = "meshShadowSlopes";
    /// <summary>The pass-block value holding the shadow role each cascade entry serves (<c>SdfShadowSlots.Primary</c> or
    /// <c>Secondary</c>), and <c>0xFFFFFFFF</c> past the rendered entries (<c>uint4</c>).</summary>
    public const string MeshShadowRoles = "meshShadowRoles";
    /// <summary>The pass-block value holding the texels along each side of each cascade entry's depth, zero past the
    /// rendered entries (<c>uint4</c>): the world's shadow quality selects the cascades' and the authored distant map the
    /// distant entry's.</summary>
    public const string MeshShadowTexels = "meshShadowTexels";
    /// <summary>The pass-block value holding the lattices one bake dispatch samples, <see cref="MeshBakeLatticeRows"/> rows
    /// each: the first sample's world position and, as bits, the lattice's first sample in the dispatch; the spacing along
    /// each axis and, as bits, the selector (a dynamic-transform slot whose instances it samples when non-negative, the one
    /// instance <c>-(selector + 1)</c> otherwise); then, as bits, the samples along each axis and whether it measures
    /// gradients; then, as bits, the point table entry the poses its group bakes through start at, their first slot and how
    /// many slots they cover; then the cell its group's glyphs are thickened to and the thinnest stroke they are taken to
    /// have, in units of a glyph's distance scale (the lettering stroke rule, zero cell for none) (<c>float4</c>,
    /// <see cref="MeshBakeLatticeRows"/> per lattice of <see cref="MeshBakeBatch"/>).</summary>
    public const string MeshBakeLattices = "meshBakeLattices";
    /// <summary>The pass-block value holding the lattices in <see cref="MeshBakeLattices"/> (<c>uint</c>).</summary>
    public const string MeshBakeCount = "meshBakeCount";
    /// <summary>The most lattices one bake dispatch samples: each selects its instances in its own mask words. Every group
    /// owing a lattice can wait on a dispatch, so this bounds how many groups bake at once; their rows keep the pass block
    /// within the portable 16 KiB uniform range.</summary>
    public const int MeshBakeBatch = 192;
    /// <summary>The rows one lattice takes in <see cref="MeshBakeLattices"/>.</summary>
    public const int MeshBakeLatticeRows = 5;
    /// <summary>The near shadow cascade's depth, sampled by the surface pass.</summary>
    public const string MeshShadowNear = "meshShadowNear";
    /// <summary>The far shadow cascade's depth, sampled by the surface pass.</summary>
    public const string MeshShadowFar = "meshShadowFar";
    /// <summary>The near shadow cascade's exit layer, sampled by the surface pass: per texel the depth of the nearest
    /// face turned from the light, where the light leaves the first solid it crosses, zero (the far plane) where none was
    /// drawn. A receiver turned from the light compares against it, so the body the light crosses to reach that receiver
    /// lets it through while any solid in front of that body still shadows it.</summary>
    public const string MeshShadowNearExit = "meshShadowNearExit";
    /// <summary>The far shadow cascade's exit layer, sampled by the surface pass.</summary>
    public const string MeshShadowFarExit = "meshShadowFarExit";
    /// <summary>The second shadow slot's cascade exit layer, sampled by the surface pass.</summary>
    public const string MeshShadowSecondExit = "meshShadowSecondExit";
    /// <summary>The distant shadow map's depth, sampled by the surface pass.</summary>
    public const string MeshShadowDistant = "meshShadowDistant";
    /// <summary>The distant shadow map's exit layer, sampled by the surface pass.</summary>
    public const string MeshShadowDistantExit = "meshShadowDistantExit";
    /// <summary>The most texels along each side of the distant shadow map's depth an author may ask for; the document
    /// validator's bound (<c>WorldDistantShadows.MaxTexels</c>) is the same.</summary>
    public const int MeshDistantShadowMaxResolution = 4096;
    /// <summary>The fewest texels along each side of the distant shadow map's depth: a view's shadow quality halves the
    /// authored size down to this and no further (<c>WorldDistantShadows.MinTexels</c> is the same).</summary>
    public const int MeshDistantShadowMinResolution = 256;
    /// <summary>The output planes the bake pass writes for each lattice.</summary>
    public const string MeshBakeSamplesWritten = "meshBakeSamplesRW";
    /// <summary>The most samples a bake lattice holds along one axis.</summary>
    public const int MeshBakeMaxSamplesPerAxis = 65;
    /// <summary>The most samples one bake dispatch writes, at least a lattice of <see cref="MeshBakeMaxSamplesPerAxis"/>
    /// samples a side: the bake samples buffer and its readback hold this many records. Its 64-sample thread groups stay
    /// under the 65,535 one dispatch axis allows; a cold bake's frames fill smaller dispatches and wait on them.</summary>
    public const int MeshBakeMaxSamples = (1 << 21);
    /// <summary>The mesh bake's point table, a World-group table of one float4 a point: the surface points a points lattice
    /// samples the field's material at.</summary>
    public const string MeshBakePoints = "sdfMeshBakePoints";
    /// <summary>The irradiance volume, a World-group table of uints: per probe slot its cell key and its sky
    /// visibility and radiance ambient cubes as halves (<c>SdfMeshIrradiance</c> in <c>Puck.SdfVm</c>).</summary>
    public const string MeshIrradiance = "sdfMeshIrradiance";
    /// <summary>The pass-block array holding each irradiance level's probe spacing along each axis in metres in xyz,
    /// finest first, zero for a level not yet published (<c>float4</c> a level).</summary>
    public const string MeshIrradianceSpacings = "meshIrradianceSpacings";
    /// <summary>The pass-block array holding each irradiance level's window, finest first: its first cell in xyz and its
    /// probes along each axis in w, packed ten bits each, x lowest (<c>int4</c> a level).</summary>
    public const string MeshIrradianceLevels = "meshIrradianceLevels";
    /// <summary>The pass-block serials of the irradiance volume's publications (<c>uint2</c>): in x its geometry's, which
    /// moves with each publication that lays a window's slots out again or changes the surface maps, offsets or validity
    /// of probes already held, and in y its windows' places, which moves with each window's move; neither moves with a
    /// relight. A probe gather kept from an earlier frame stands only under the geometry serial it was taken at, and past
    /// a window's move only where the windows still hold its receiver as they did.</summary>
    public const string MeshIrradianceGeometry = "meshIrradianceGeometry";
    /// <summary>The irradiance volume's levels: nested windows about the camera, each coarser than the one inside it, so a
    /// surface past the finest window takes a coarser level's measured light rather than none.</summary>
    public const int MeshIrradianceLevelCount = 3;
    /// <summary>The secondary shadow role's cascade depth, sampled by the surface pass.</summary>
    public const string MeshShadowSecond = "meshShadowSecond";
    /// <summary>The entries of a world's cascade set (<c>SdfMeshShadowCascades</c>), each with its own depth layers: the
    /// primary role's near and far, then the secondary role's, then the primary role's distant map.</summary>
    public const int MeshShadowEntries = 4;
    /// <summary>The rows one cascade's light camera takes in <see cref="MeshShadowCascades"/>.</summary>
    public const int MeshShadowCascadeRows = 4;
    /// <summary>The most texels along each side of one shadow cascade's depth, which full shadow quality renders; a lower
    /// quality renders a power-of-two fraction of it.</summary>
    public const int MeshShadowResolution = 2048;
    /// <summary>The side in texels of an optical-shadow clear tile.</summary>
    public const uint ShadowOpticalTileEdge = 16;
    /// <summary>The words per optical texel, before the dirty-tile bitmap.</summary>
    public const uint ShadowOpticalTexelWords = 3;

    /// <summary>Returns the number of bitmap words following the optical texels for a square cascade.</summary>
    /// <param name="side">The allocated cascade width in texels.</param>
    /// <returns>The number of 32-bit words in the bitmap.</returns>
    public static uint ShadowOpticalBitmapWords(uint side) {
        var tiles = side.CeilingDivide(divisor: ShadowOpticalTileEdge);
        return (tiles * tiles).CeilingDivide(divisor: 32U);
    }

    /// <summary>Each cascade's signed optical depth to its first opaque receiver and nearest glass entry.</summary>
    public const string MeshShadowNearOptical = "meshShadowNearOptical";
    /// <summary>The far cascade's optical-depth term.</summary>
    public const string MeshShadowFarOptical = "meshShadowFarOptical";
    /// <summary>The second light's cascade optical-depth term.</summary>
    public const string MeshShadowSecondOptical = "meshShadowSecondOptical";
}
