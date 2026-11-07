namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The pass-block value holding the shadow cascades' orthographic light cameras, four rows each: position and
    /// half height, right and aspect, up and near distance, forward and far distance (<c>float4[12]</c>): the key light's
    /// near and far cascades, then the second shadow slot's.</summary>
    public const string MeshShadowCascades = "meshShadowCascades";
    /// <summary>The pass-block value holding the key light's cascades the frame rendered this frame, zero when it casts
    /// no shadow (<c>uint</c>).</summary>
    public const string MeshShadowCount = "meshShadowCount";
    /// <summary>The pass-block value holding the shadows' penumbra slopes and the receiver's depth bias, in world units: x
    /// the key light's penumbra slope, y the constant bias, z the normal offset per texel, w the second shadow slot's
    /// penumbra slope, zero when that slot casts no shadow (<c>float4</c>).</summary>
    public const string MeshShadowParameters = "meshShadowParameters";
    /// <summary>The pass-block value holding the texels along each side of the view's cascades (<c>uint</c>), which the
    /// view's shadow quality selects (<see cref="WithShadows"/>).</summary>
    public const string MeshShadowTexels = "meshShadowTexels";
    /// <summary>The pass-block value holding the lattices one bake dispatch samples, <see cref="MeshBakeLatticeRows"/> rows
    /// each: the first sample's world position and, as bits, the lattice's first sample in the dispatch; the spacing along
    /// each axis and, as bits, the selector (a dynamic-transform slot whose instances it samples when non-negative, the one
    /// instance <c>-(selector + 1)</c> otherwise); then, as bits, the samples along each axis and whether it measures
    /// gradients; then, as bits, the point table entry the poses its group bakes through start at, their first slot and how
    /// many slots they cover (<c>float4</c>, <see cref="MeshBakeLatticeRows"/> per lattice of <see cref="MeshBakeBatch"/>).</summary>
    public const string MeshBakeLattices = "meshBakeLattices";
    /// <summary>The pass-block value holding the lattices in <see cref="MeshBakeLattices"/> (<c>uint</c>).</summary>
    public const string MeshBakeCount = "meshBakeCount";
    /// <summary>The most lattices one bake dispatch samples: each selects its instances in its own mask words. Every group
    /// owing a lattice can wait on a dispatch, so this bounds how many groups bake at once; their rows keep the pass block
    /// within the portable 16 KiB uniform range.</summary>
    public const int MeshBakeBatch = 240;
    /// <summary>The rows one lattice takes in <see cref="MeshBakeLattices"/>.</summary>
    public const int MeshBakeLatticeRows = 4;
    /// <summary>The near shadow cascade's depth, sampled by the surface pass.</summary>
    public const string MeshShadowNear = "meshShadowNear";
    /// <summary>The far shadow cascade's depth, sampled by the surface pass.</summary>
    public const string MeshShadowFar = "meshShadowFar";
    /// <summary>The near shadow cascade's exit layer, sampled by the surface pass: per texel the depth of the nearest
    /// face turned from the light, where the light leaves the first solid it crosses (<see cref="Parts.ShadowNearExit"/>).</summary>
    public const string MeshShadowNearExit = "meshShadowNearExit";
    /// <summary>The far shadow cascade's exit layer, sampled by the surface pass.</summary>
    public const string MeshShadowFarExit = "meshShadowFarExit";
    /// <summary>The second shadow slot's cascade exit layer, sampled by the surface pass.</summary>
    public const string MeshShadowSecondExit = "meshShadowSecondExit";
    /// <summary>The bake samples the bake pass writes: per lattice sample its field value and material, and, when the bake
    /// asks for it, its gradient (<see cref="MeshBakeSampleWords"/> words).</summary>
    public const string MeshBakeSamplesWritten = "meshBakeSamplesRW";
    /// <summary>The words one bake sample holds: the value, the material, then the gradient's three components.</summary>
    public const int MeshBakeSampleWords = 5;
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
    /// <summary>The pass-block value saying whether views read the irradiance volume, nonzero while it is on, published
    /// or not (<c>uint</c>).</summary>
    public const string MeshIrradianceVolume = "meshIrradianceVolume";
    /// <summary>The pass-block array holding each irradiance level's probe spacing along each axis in metres in xyz,
    /// finest first, zero for a level not yet published (<c>float4</c> a level).</summary>
    public const string MeshIrradianceSpacings = "meshIrradianceSpacings";
    /// <summary>The pass-block array holding each irradiance level's window, finest first: its first cell in xyz and its
    /// probes along each axis in w, packed ten bits each, x lowest (<c>int4</c> a level).</summary>
    public const string MeshIrradianceLevels = "meshIrradianceLevels";
    /// <summary>The irradiance volume's levels: nested windows about the camera, each coarser than the one inside it, so a
    /// surface past the finest window takes a coarser level's measured light rather than none.</summary>
    public const int MeshIrradianceLevelCount = 3;
    /// <summary>The second shadow slot's cascade depth, sampled by the surface pass.</summary>
    public const string MeshShadowSecond = "meshShadowSecond";
    /// <summary>The shadow cascades the frame renders: the key light's near and far cascades, then the second shadow
    /// slot's one.</summary>
    public const int MeshShadowCascadeCount = 3;
    /// <summary>The rows one cascade's light camera takes in <see cref="MeshShadowCascades"/>.</summary>
    public const int MeshShadowCascadeRows = 4;
    /// <summary>The most texels along each side of one shadow cascade's depth, which full shadow quality renders; a lower
    /// quality renders a power-of-two fraction of it (<see cref="WithShadows"/>).</summary>
    public const int MeshShadowResolution = 2048;

    /// <summary>Each cascade's signed optical depth to its first opaque receiver and nearest glass entry.</summary>
    public const string MeshShadowNearOptical = "meshShadowNearOptical";
    /// <summary>The far cascade's optical-depth term.</summary>
    public const string MeshShadowFarOptical = "meshShadowFarOptical";
    /// <summary>The second light's cascade optical-depth term.</summary>
    public const string MeshShadowSecondOptical = "meshShadowSecondOptical";
    private static readonly string[] ShadowOpticalStorages = [Parts.ShadowNearOptical, Parts.ShadowFarOptical, Parts.ShadowSecondOptical];

    /// <summary>Adds optical storage and dependencies to the cascades of a program with positive glass shadow density.</summary>
    /// <param name="fragment">The fragment before cascade sizing.</param>
    /// <returns>The fragment with its optical cascades.</returns>
    public static RenderGraphPackageFragment WithGlassShadows(RenderGraphPackageFragment fragment) {
        ArgumentNullException.ThrowIfNull(fragment);
        return fragment with {
            Resources = [.. fragment.Resources, .. ShadowOpticalStorages.Select(static name => ShadowOptical(name, MeshShadowResolution))],
            Passes = [.. fragment.Passes.Select(static pass => {
                var cascade = Array.IndexOf(ShadowParts, pass.Name);
                if (cascade >= 0) {
                    return pass with {
                        Outputs = [.. pass.Outputs, new ResourceReference(Name: ShadowOpticalStorages[cascade])],
                        OutputAccesses = [.. pass.OutputAccesses, RenderGraphPortAccess.FragmentWrite],
                    };
                }
                return pass.Name != Parts.Surface ? pass : pass with {
                    Inputs = [.. pass.Inputs, .. ShadowOpticalStorages.Select(static name => new ResourceReference(Name: name))],
                    InputAccesses = [.. pass.InputAccesses, .. ShadowOpticalStorages.Select(static _ => RenderGraphPortAccess.ComputeRead)],
                };
            })],
        };
    }

    private static readonly string[] ShadowParts = [Parts.ShadowNear, Parts.ShadowFar, Parts.ShadowSecond];
    private static readonly string[] ShadowStorages = [
        Parts.ShadowNearDepth, Parts.ShadowFarDepth, Parts.ShadowSecondDepth,
        Parts.ShadowNearExit, Parts.ShadowFarExit, Parts.ShadowSecondExit,
        .. ShadowOpticalStorages,
    ];

    /// <summary>Returns a fragment without the shadow cascades, for a view whose quality skips shadows: no cascade pass, no
    /// cascade depth, and a surface pass that reads none, so the view allocates none of the cascades and its surface pass
    /// binds the tables' filler in their place.</summary>
    /// <param name="fragment">The fragment, any of the package's.</param>
    /// <returns>The fragment without the cascades.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    public static RenderGraphPackageFragment WithoutShadows(RenderGraphPackageFragment fragment) {
        ArgumentNullException.ThrowIfNull(argument: fragment);

        return Without(fragment: fragment, parts: ShadowParts, storages: ShadowStorages);
    }
    /// <summary>Returns a fragment whose cascades are <paramref name="texels"/> a side, with the second shadow slot's
    /// cascade only when <paramref name="second"/>: a view allocates the cascades its shadow quality and its world's shadow
    /// slots draw, and no more. A fragment already so is returned as it is.</summary>
    /// <param name="fragment">The fragment, any of the package's that holds the cascades.</param>
    /// <param name="texels">The texels along each side of every cascade, from 1 through <see cref="MeshShadowResolution"/>.</param>
    /// <param name="second">Whether the fragment keeps the second shadow slot's cascade.</param>
    /// <returns>The fragment with the cascades sized and chosen.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="texels"/> lies outside 1 through
    /// <see cref="MeshShadowResolution"/>.</exception>
    public static RenderGraphPackageFragment WithShadows(RenderGraphPackageFragment fragment, int texels, bool second) {
        ArgumentNullException.ThrowIfNull(argument: fragment);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: texels, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: texels, other: MeshShadowResolution);

        var shaped = (second ? fragment : Without(fragment: fragment, parts: [Parts.ShadowSecond], storages: [Parts.ShadowSecondDepth, Parts.ShadowSecondExit, Parts.ShadowSecondOptical]));

        return ((texels == MeshShadowResolution)
            ? shaped
            : (shaped with {
                Resources = [.. shaped.Resources.Select(selector: resource => (ShadowStorages.Contains(value: resource.Name)
                    ? (ShadowOpticalStorages.Contains(resource.Name) ? ShadowOptical(resource.Name, texels) : ShadowDepth(name: resource.Name, texels: texels))
                    : resource))],
            }));
    }
    // The fragment without some of its passes and storages, its other passes reading none of the storages.
    private static RenderGraphPackageFragment Without(RenderGraphPackageFragment fragment, string[] parts, string[] storages) => fragment with {
        Passes = [.. fragment.Passes.Where(predicate: pass => !parts.Contains(value: pass.Name)).Select(selector: pass => {
            var kept = Enumerable.Range(start: 0, count: pass.Inputs.Count).Where(predicate: index => !storages.Contains(value: pass.Inputs[index].Name)).ToArray();

            return ((kept.Length == pass.Inputs.Count)
                ? pass
                : (pass with {
                    InputAccesses = [.. kept.Select(selector: index => pass.InputAccesses[index])],
                    Inputs = [.. kept.Select(selector: index => pass.Inputs[index])],
                }));
        })],
        Resources = [.. fragment.Resources.Where(predicate: resource => !storages.Contains(value: resource.Name))],
    };
}
