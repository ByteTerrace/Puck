using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The pass-block value naming the render path a view takes: zero the interpreter, which marches the SDF
    /// program per pixel, one the baked-mesh path, which rasterizes the program's baked meshes and reads no field
    /// (<c>uint</c>).</summary>
    public const string RenderPath = "renderPath";
    /// <summary>The pass-block value holding the shadow cascades' orthographic light cameras, four rows each: position and
    /// half height, right and aspect, up and near distance, forward and far distance (<c>float4[12]</c>): the key light's
    /// near and far cascades, then the second shadow slot's.</summary>
    public const string MeshShadowCascades = "meshShadowCascades";
    /// <summary>The pass-block value holding the key light's cascades the mesh path rendered this frame, zero when it casts
    /// no shadow (<c>uint</c>).</summary>
    public const string MeshShadowCount = "meshShadowCount";
    /// <summary>The pass-block value holding the shadows' penumbra slopes and the receiver's depth bias, in world units: x
    /// the key light's penumbra slope, y the constant bias, z the normal offset per texel, w the second shadow slot's
    /// penumbra slope, zero when that slot casts no shadow (<c>float4</c>).</summary>
    public const string MeshShadowParameters = "meshShadowParameters";
    /// <summary>The pass-block value holding the lattices one bake dispatch samples, <see cref="MeshBakeLatticeRows"/> rows
    /// each: the first sample's world position and, as bits, the lattice's first sample in the dispatch; the spacing along
    /// each axis and, as bits, the selector (a dynamic-transform slot whose instances it samples when non-negative, the one
    /// instance <c>-(selector + 1)</c> otherwise); then, as bits, the samples along each axis and whether it measures
    /// gradients (<c>float4[48]</c>).</summary>
    public const string MeshBakeLattices = "meshBakeLattices";
    /// <summary>The pass-block value holding the lattices in <see cref="MeshBakeLattices"/> (<c>uint</c>).</summary>
    public const string MeshBakeCount = "meshBakeCount";
    /// <summary>The most lattices one bake dispatch samples: each selects its instances in its own tile's mask words.</summary>
    public const int MeshBakeBatch = 16;
    /// <summary>The rows one lattice takes in <see cref="MeshBakeLattices"/>.</summary>
    public const int MeshBakeLatticeRows = 3;
    /// <summary>The near shadow cascade's light-space depth target, sampled by the mesh path's surface pass.</summary>
    public const string MeshShadowNear = "meshShadowNear";
    /// <summary>The far shadow cascade's light-space depth target, sampled by the mesh path's surface pass.</summary>
    public const string MeshShadowFar = "meshShadowFar";
    /// <summary>The bake samples the bake pass writes: per lattice sample its field value and material, and, when the bake
    /// asks for it, its gradient (<see cref="MeshBakeSampleWords"/> words).</summary>
    public const string MeshBakeSamplesWritten = "meshBakeSamplesRW";
    /// <summary>The words one bake sample holds: the value, the material, then the gradient's three components.</summary>
    public const int MeshBakeSampleWords = 5;
    /// <summary>The most samples a bake lattice holds along one axis.</summary>
    public const int MeshBakeMaxSamplesPerAxis = 65;
    /// <summary>The most samples one bake dispatch writes: a lattice of <see cref="MeshBakeMaxSamplesPerAxis"/> samples a
    /// side.</summary>
    public const int MeshBakeMaxSamples = ((MeshBakeMaxSamplesPerAxis * MeshBakeMaxSamplesPerAxis) * MeshBakeMaxSamplesPerAxis);
    /// <summary>The mesh bake's point table, a World-group table of one float4 a point: the surface points a points lattice
    /// samples the field's material at.</summary>
    public const string MeshBakePoints = "sdfMeshBakePoints";
    /// <summary>The second shadow slot's cascade target, sampled by the mesh path's surface pass.</summary>
    public const string MeshShadowSecond = "meshShadowSecond";
    /// <summary>The shadow cascades the mesh path renders: the key light's near and far cascades, then the second shadow
    /// slot's one.</summary>
    public const int MeshShadowCascadeCount = 3;
    /// <summary>The rows one cascade's light camera takes in <see cref="MeshShadowCascades"/>.</summary>
    public const int MeshShadowCascadeRows = 4;
    /// <summary>The texels along each side of one shadow cascade's target.</summary>
    public const int MeshShadowResolution = 2048;

    /// <summary>The names of the mesh path's own passes and versions.</summary>
    public static class MeshParts {
        /// <summary>The bake pass, sampling one instance group's field on a lattice for the CPU to mesh.</summary>
        public const string Bake = "bake";
        /// <summary>The bake samples, copied back to the host after the bake pass.</summary>
        public const string BakeSamples = "bakeSamples";
        /// <summary>The near shadow cascade's raster pass.</summary>
        public const string ShadowNear = "shadow-near";
        /// <summary>The far shadow cascade's raster pass.</summary>
        public const string ShadowFar = "shadow-far";
        /// <summary>The near cascade's target: per texel the light-space depth of the nearest caster.</summary>
        public const string ShadowNearTarget = "shadowNearTarget";
        /// <summary>The near cascade's depth attachment.</summary>
        public const string ShadowNearDepth = "shadowNearDepth";
        /// <summary>The far cascade's target.</summary>
        public const string ShadowFarTarget = "shadowFarTarget";
        /// <summary>The far cascade's depth attachment.</summary>
        public const string ShadowFarDepth = "shadowFarDepth";
        /// <summary>The second shadow slot's cascade raster pass.</summary>
        public const string ShadowSecond = "shadow-second";
        /// <summary>The second slot's cascade target.</summary>
        public const string ShadowSecondTarget = "shadowSecondTarget";
        /// <summary>The second slot's cascade depth attachment.</summary>
        public const string ShadowSecondDepth = "shadowSecondDepth";
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RenderGraphPackageFragment, RenderGraphPackageFragment> MeshPathFragments = [];

    /// <summary>Returns the baked-mesh path's form of a fragment (<see cref="MeshPathFragment"/>), one instance per source
    /// fragment, so a view that asks for the same path again plans the same fragment.</summary>
    /// <param name="source">The interpreter path's fragment.</param>
    /// <returns>The mesh path's fragment.</returns>
    public static RenderGraphPackageFragment MeshPathFragmentOf(RenderGraphPackageFragment source) =>
        MeshPathFragments.GetValue(key: source, createValueCallback: MeshPathFragment);

    /// <summary>Returns the baked-mesh path's form of a fragment: the fragment's passes, with the bake pass after the mask
    /// pass, the two shadow cascades' raster passes before the mesh pass, and primary reading the cascades' targets and
    /// the bake samples. On the mesh path primary records the mesh surface kernel, which writes the whole visibility record
    /// from the mesh target and the cascades, and the passes that march the field skip; the rest of the frame (views, the
    /// resolve, the sky and the composite) is the interpreter path's.</summary>
    /// <param name="source">The interpreter path's fragment.</param>
    /// <returns>The mesh path's fragment.</returns>
    public static RenderGraphPackageFragment MeshPathFragment(RenderGraphPackageFragment source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var passes = new List<RenderGraphFragmentPass>();

        foreach (var pass in source.Passes) {
            if (pass.Name == Parts.Mesh) {
                passes.Add(item: ShadowPass(name: MeshParts.ShadowNear, target: MeshParts.ShadowNearTarget, depth: MeshParts.ShadowNearDepth));
                passes.Add(item: ShadowPass(name: MeshParts.ShadowFar, target: MeshParts.ShadowFarTarget, depth: MeshParts.ShadowFarDepth));
                passes.Add(item: ShadowPass(name: MeshParts.ShadowSecond, target: MeshParts.ShadowSecondTarget, depth: MeshParts.ShadowSecondDepth));
            }
            if (pass.Name == Parts.Primary) {
                passes.Add(item: pass with {
                    Inputs = [.. pass.Inputs, new ResourceReference(Name: MeshParts.ShadowNearTarget), new ResourceReference(Name: MeshParts.ShadowFarTarget),
                        new ResourceReference(Name: MeshParts.ShadowSecondTarget), new ResourceReference(Name: MeshParts.BakeSamples)],
                    InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead],
                });
            } else {
                passes.Add(item: pass);
            }
            if (pass.Name == Parts.Mask) {
                passes.Add(item: Pass(inputs: [Parts.InstanceMasks], name: MeshParts.Bake, outputs: [MeshParts.BakeSamples]));
            }
        }

        var extent = ShaderPipelineDimensions.Absolute(height: MeshShadowResolution, width: MeshShadowResolution);

        return source with {
            Passes = passes,
            Resources = [
                .. source.Resources,
                new ShaderPipelineResource(
                    Kind: ShaderPipelineResourceKind.Buffer,
                    Name: MeshParts.BakeSamples,
                    Retained: true,
                    SizeBytes: (((ulong)MeshBakeMaxSamples) * MeshBakeSampleWords * sizeof(uint)),
                    StrideBytes: sizeof(uint)
                ),
                .. ShadowResources(target: MeshParts.ShadowNearTarget, depth: MeshParts.ShadowNearDepth, extent: extent),
                .. ShadowResources(target: MeshParts.ShadowFarTarget, depth: MeshParts.ShadowFarDepth, extent: extent),
                .. ShadowResources(target: MeshParts.ShadowSecondTarget, depth: MeshParts.ShadowSecondDepth, extent: extent),
            ],
        };
    }

    // A cascade's raster pass: the mesh pass's pipeline from the cascade's light camera, into its own target and depth.
    private static RenderGraphFragmentPass ShadowPass(string name, string target, string depth) => new(
        CountsKernelWork: true,
        InputAccesses: [],
        Inputs: [],
        Name: name,
        OutputAccesses: [RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.ColorAttachmentWrite],
        Outputs: [new ResourceReference(Name: target), new ResourceReference(Name: depth)]
    );
    private static ShaderPipelineResource[] ShadowResources(string target, string depth, ShaderPipelineDimensions extent) => [
        new ShaderPipelineResource(
            Dimensions: extent,
            Format: MeshTargetFormat.ToString(),
            Name: target,
            Retained: true
        ),
        new ShaderPipelineResource(
            ClearDepth: MeshClearDepth,
            Dimensions: extent,
            Format: MeshDepthFormat.ToString(),
            Kind: ShaderPipelineResourceKind.Depth,
            Name: depth,
            Retained: true
        ),
    ];
}
