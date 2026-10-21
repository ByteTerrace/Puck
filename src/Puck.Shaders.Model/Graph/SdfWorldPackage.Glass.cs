using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The glass raster pass's writable binding of the glass sums, a member of the mesh interface: two words a
    /// render-extent pixel (<see cref="GlassSumWords"/>), the signed sum of every glass face's ray distance in front of
    /// the opaque hit, in units of <see cref="GlassSumScale"/>, a front face subtracting and a back face adding, and the
    /// count of front faces less back faces, both accumulated atomically. Their sum plus the unmatched front faces'
    /// opaque distance is the distance the ray travels through glass.</summary>
    public const string GlassSumsWritten = "sdfGlassSumsRW";
    /// <summary>The glass sums as the glass shading pass reads them.</summary>
    public const string GlassSums = "sdfGlassSums";
    /// <summary>The glass visibility target as the glass shading pass samples it: per pixel the nearest glass front
    /// face's ray parameter, its draw plus one (zero where no glass face lies before the opaque hit) and its triangle,
    /// in the mesh target's format.</summary>
    public const string GlassVisibility = "glassVisibility";
    /// <summary>The words one render-extent pixel holds in the glass sums. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_GLASS_SUM_WORDS</c>.</summary>
    public const uint GlassSumWords = 2;
    /// <summary>The steps a world unit a glass face's ray distance is accumulated at, as a whole number: a quarter of a
    /// millimetre, so sixty faces at the far distance's limit sum within a 32-bit word. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_GLASS_SUM_SCALE</c>.</summary>
    public const float GlassSumScale = 4096f;

    /// <summary>The glass shading pass's interface: the common frame values, the lit image views wrote (sampled), the
    /// glass visibility target (sampled), the visibility records and the glass sums it reads, the reactivity it raises
    /// at a glass pixel in a temporal view (a filler elsewhere), the image it writes, the work counters and the World
    /// group's tables.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> GlassMembers => GlassDeclaration.Members;

    private static class GlassDeclaration {
        internal static readonly IReadOnlyList<ShaderInterfaceMember> Members = [
            .. Values,
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: LitImage, type: ShaderValueType.Float4),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: GlassVisibility, type: ShaderValueType.Float4),
            Read(element: ShaderValueType.Uint, name: VisibilityRecords),
            Read(element: ShaderValueType.Uint, name: GlassSums),
            Written(element: ShaderValueType.Float, name: ReactivityWritten),
            ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: Output, type: ShaderValueType.Float4),
            ShaderWorkCounters.BufferMember,
            .. Tables,
        ];
    }

    /// <summary>Returns a fragment with the glass contract added after its views pass, for a view of a program that
    /// holds a glass shape (<c>SdfProgram.HasGlass</c>); a view of any other program runs the fragment as it stands and
    /// pays nothing. Three passes follow views: <see cref="Parts.GlassClear"/> zeroes the glass sums; <see cref="Parts.Glass"/>
    /// rasterizes the glass draws, every face accumulating its signed ray distance into the sums where it lies before
    /// the opaque hit and the nearest front face winning the glass target through a depth of its own; and
    /// <see cref="Parts.GlassShade"/> writes the lit image over again with each glass pixel shaded: the opaque image
    /// sampled along the refracted ray and absorbed over the distance through the glass, the sky reflected by Fresnel,
    /// the lights' highlights, frosted by the material's roughness. Every pass after views reads that image in the lit
    /// image's place, and a temporal view's resolve reads the reactivity the glass pass raised. The sums' version the
    /// raster writes declares its cleared predecessor preserved: the clear and the raster record on one signature
    /// (the geometry's), so a standing raster never accumulates onto a clear it did not see.</summary>
    /// <param name="fragment">The fragment, any of the package's, with or without its cascades.</param>
    /// <returns>The fragment with the glass passes and their storages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fragment"/> has no views pass, or already holds the glass
    /// passes.</exception>
    public static RenderGraphPackageFragment WithGlass(RenderGraphPackageFragment fragment) {
        ArgumentNullException.ThrowIfNull(argument: fragment);

        var views = fragment.Passes.FirstOrDefault(predicate: static pass => (pass.Name == Parts.Views));

        if (views is null) {
            throw new ArgumentException(message: "The fragment has no views pass to shade glass after.", paramName: nameof(fragment));
        }
        if (fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.GlassShade))) {
            throw new ArgumentException(message: "The fragment already holds the glass passes.", paramName: nameof(fragment));
        }

        var lit = views.Outputs[0].Name;
        var temporal = fragment.Resources.Any(predicate: static resource => (resource.Name == Parts.Reactivity));
        var litResource = fragment.Resources.Single(predicate: resource => (resource.Name == lit));
        var meshTarget = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.MeshTarget));
        var meshDepth = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.MeshDepth));
        var visibility = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.Visibility));
        var sumCount = visibility.Count!.Select(selector: static term => term with { Elements = GlassSumWords }).ToArray();
        var passes = new List<RenderGraphFragmentPass>(capacity: (fragment.Passes.Count + 3));
        var after = false;

        foreach (var pass in fragment.Passes) {
            if (pass.Name == Parts.Views) {
                after = true;
                passes.Add(item: pass);
                passes.Add(item: new RenderGraphFragmentPass(
                    CountsKernelWork: false,
                    InputAccesses: [],
                    Inputs: [],
                    Name: Parts.GlassClear,
                    OutputAccesses: [RenderGraphPortAccess.TransferWrite],
                    Outputs: [new ResourceReference(Name: Parts.GlassSumsCleared)]
                ));
                passes.Add(item: new RenderGraphFragmentPass(
                    CountsKernelWork: true,
                    InputAccesses: [RenderGraphPortAccess.FragmentSampled],
                    Inputs: [new ResourceReference(Name: Parts.Visibility)],
                    Name: Parts.Glass,
                    OutputAccesses: [RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.FragmentWrite],
                    Outputs: [new ResourceReference(Name: Parts.GlassTarget), new ResourceReference(Name: Parts.GlassDepth), new ResourceReference(Name: Parts.GlassSums)]
                ));
                passes.Add(item: Pass(
                    inputs: [lit, Parts.Visibility, Parts.GlassTarget, Parts.GlassSums],
                    name: Parts.GlassShade,
                    outputs: (temporal ? [Parts.GlassLit, Parts.GlassReactivity] : [Parts.GlassLit])
                ) with { Members = GlassMembers });
                continue;
            }
            passes.Add(item: (!after
                ? pass
                : pass with {
                    Inputs = [.. pass.Inputs.Select(selector: input => ((input.Name == lit)
                        ? input with { Name = Parts.GlassLit }
                        : ((input.Name == Parts.Reactivity) ? input with { Name = Parts.GlassReactivity } : input)))],
                }));
        }

        return fragment with {
            Passes = passes,
            Resources = [
                .. fragment.Resources,
                litResource with { Name = Parts.GlassLit },
                meshTarget with { Name = Parts.GlassTarget },
                meshDepth with { Name = Parts.GlassDepth },
                visibility with { Name = Parts.GlassSumsCleared, StrideBytes = sizeof(uint), Count = sumCount },
                new ShaderPipelineResource(
                    Count: sumCount,
                    From: Parts.GlassSumsCleared,
                    Kind: ShaderPipelineResourceKind.Buffer,
                    Name: Parts.GlassSums,
                    PreservesPredecessor: true,
                    StrideBytes: sizeof(uint)
                ),
                .. (temporal
                    ? new[] {
                        new ShaderPipelineResource(
                            Count: fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.Reactivity)).Count,
                            From: Parts.Reactivity,
                            Kind: ShaderPipelineResourceKind.Buffer,
                            Name: Parts.GlassReactivity,
                            PreservesPredecessor: true,
                            StrideBytes: sizeof(float)
                        ),
                    }
                    : []),
            ],
        };
    }

    public static partial class Parts {
        /// <summary>The pass zeroing the glass sums ahead of the glass raster pass, a buffer clear.</summary>
        public const string GlassClear = "glass-clear";
        /// <summary>The glass raster pass: the frame's glass draws into the glass target and the glass sums.</summary>
        public const string Glass = "glass";
        /// <summary>The glass shading pass: the lit image written again with each glass pixel shaded over the opaque
        /// image behind it.</summary>
        public const string GlassShade = "glass-shade";
        /// <summary>The glass visibility target the glass raster pass draws: the nearest glass front face before the
        /// opaque hit, as the mesh target holds a hit.</summary>
        public const string GlassTarget = "glassTarget";
        /// <summary>The glass raster pass's own reversed-Z depth, cleared each frame, which keeps the nearest front
        /// face.</summary>
        public const string GlassDepth = "glassDepth";
        /// <summary>The glass sums as the clear leaves them: zero in every word.</summary>
        public const string GlassSumsCleared = "glassSumsCleared";
        /// <summary>The glass sums as the raster pass leaves them: each pixel's signed distance sum and face count
        /// (<see cref="SdfWorldPackage.GlassSumsWritten"/>).</summary>
        public const string GlassSums = "glassSums";
        /// <summary>The lit image with its glass pixels shaded, which the sky, the resolve and the composite read in
        /// the lit image's place.</summary>
        public const string GlassLit = "glassLit";
        /// <summary>The reactivity with each glass pixel's raised where its refracted sample comes from elsewhere on
        /// screen, which a temporal view's resolve reads in the reactivity's place.</summary>
        public const string GlassReactivity = "glassReactivity";
    }
}
