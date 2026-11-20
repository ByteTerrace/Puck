namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The pass-block value holding the cascade set's orthographic light cameras (<c>SdfMeshShadowCascades</c>),
    /// four rows each: position and half height, right and aspect, up and near distance, forward and far distance
    /// (<c>float4</c>, <see cref="MeshShadowCascadeRows"/> a camera of <see cref="MeshShadowCascadeCount"/>): each entry's,
    /// then the camera the far entry's exit layer was drawn from.</summary>
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
    /// rendered entries (<c>uint4</c>): the view's shadow quality selects the cascades' and the authored distant map the
    /// distant entry's (<see cref="WithShadows"/>).</summary>
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
    /// face turned from the light, where the light leaves the first solid it crosses (<see cref="Parts.ShadowNearExit"/>).</summary>
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
    /// <summary>The pass-block array holding each irradiance level's probe spacing along each axis in metres in xyz,
    /// finest first, zero for a level not yet published (<c>float4</c> a level).</summary>
    public const string MeshIrradianceSpacings = "meshIrradianceSpacings";
    /// <summary>The pass-block array holding each irradiance level's window, finest first: its first cell in xyz and its
    /// probes along each axis in w, packed ten bits each, x lowest (<c>int4</c> a level).</summary>
    public const string MeshIrradianceLevels = "meshIrradianceLevels";
    /// <summary>The irradiance volume's levels: nested windows about the camera, each coarser than the one inside it, so a
    /// surface past the finest window takes a coarser level's measured light rather than none.</summary>
    public const int MeshIrradianceLevelCount = 3;
    /// <summary>The secondary shadow role's cascade depth, sampled by the surface pass.</summary>
    public const string MeshShadowSecond = "meshShadowSecond";
    /// <summary>The entries of a view's cascade set (<c>SdfMeshShadowCascades</c>), each with its own depth targets and
    /// pass: the primary role's near and far, then the secondary role's, then the primary role's distant map.</summary>
    public const int MeshShadowEntries = 4;
    /// <summary>The light cameras the pass block carries: each cascade entry's, then the camera the far entry's exit layer
    /// was drawn from, which trails the far camera by a frame while a refresh draws the far entry's two layers on
    /// consecutive frames.</summary>
    public const int MeshShadowCascadeCount = (MeshShadowEntries + 1);
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

    // The cascades that carry glass extinction, in the order of their optical storages.
    private static readonly string[] ShadowParts = [Parts.ShadowNear, Parts.ShadowFar, Parts.ShadowSecond];
    private static readonly string[] DistantStorages = [Parts.ShadowDistantDepth, Parts.ShadowDistantExit];
    private static readonly string[] DistantBackStorages = [Parts.ShadowDistantBackDepth, Parts.ShadowDistantBackExit];
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

        return Without(fragment: fragment, parts: [.. ShadowParts, Parts.ShadowDistant], storages: [.. ShadowStorages, .. DistantStorages]);
    }
    /// <summary>Returns a fragment whose cascades are <paramref name="texels"/> a side, with the second shadow slot's
    /// cascade only when <paramref name="second"/> and the distant map only when <paramref name="distantTexels"/> is
    /// positive, at that size, with its back map only when <paramref name="distantSliced"/>: a view allocates the cascades
    /// its shadow quality, its world's shadow slots and its world's distant map draw, and no more. The back map's two
    /// depths are further outputs of the distant pass, which draws a redraw spread over several frames into them and
    /// copies them into the map the surface pass reads once its last share is drawn.</summary>
    /// <param name="fragment">The fragment, any of the package's that holds the cascades.</param>
    /// <param name="texels">The texels along each side of every cascade, from 1 through <see cref="MeshShadowResolution"/>.</param>
    /// <param name="second">Whether the fragment keeps the second shadow slot's cascade.</param>
    /// <param name="distantTexels">The texels along each side of the distant map, from 1 through
    /// <see cref="MeshDistantShadowMaxResolution"/>, or zero for a fragment without it.</param>
    /// <param name="distantSliced">Whether the distant map keeps a back map for redraws spread over several frames;
    /// ignored without the distant map.</param>
    /// <returns>The fragment with the cascades sized and chosen.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="texels"/> lies outside 1 through
    /// <see cref="MeshShadowResolution"/>, or <paramref name="distantTexels"/> is negative or past
    /// <see cref="MeshDistantShadowMaxResolution"/>.</exception>
    public static RenderGraphPackageFragment WithShadows(RenderGraphPackageFragment fragment, int texels, bool second, int distantTexels, bool distantSliced = false) {
        ArgumentNullException.ThrowIfNull(argument: fragment);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: texels, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: texels, other: MeshShadowResolution);
        ArgumentOutOfRangeException.ThrowIfNegative(value: distantTexels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: distantTexels, other: MeshDistantShadowMaxResolution);

        var shaped = (second ? fragment : Without(fragment: fragment, parts: [Parts.ShadowSecond], storages: [Parts.ShadowSecondDepth, Parts.ShadowSecondExit, Parts.ShadowSecondOptical]));

        shaped = ((distantTexels > 0) ? shaped : Without(fragment: shaped, parts: [Parts.ShadowDistant], storages: DistantStorages));
        if ((distantTexels > 0) && distantSliced) {
            shaped = shaped with {
                Passes = [.. shaped.Passes.Select(selector: static pass => ((pass.Name != Parts.ShadowDistant) ? pass : (pass with {
                    OutputAccesses = [.. pass.OutputAccesses, RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.ColorAttachmentWrite],
                    Outputs = [.. pass.Outputs, .. DistantBackStorages.Select(selector: static name => new ResourceReference(Name: name))],
                })))],
                Resources = [.. shaped.Resources, .. DistantBackStorages.Select(selector: name => ShadowDepth(name: name, texels: distantTexels))],
            };
        }

        return (shaped with {
            Resources = [.. shaped.Resources.Select(selector: resource => ((DistantStorages.Contains(value: resource.Name) || DistantBackStorages.Contains(value: resource.Name))
                ? ShadowDepth(name: resource.Name, texels: distantTexels)
                : ((ShadowStorages.Contains(value: resource.Name) && (texels != MeshShadowResolution))
                    ? (ShadowOpticalStorages.Contains(resource.Name) ? ShadowOptical(resource.Name, texels) : ShadowDepth(name: resource.Name, texels: texels))
                    : resource)))],
        });
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
