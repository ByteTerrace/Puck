namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The overlap spans as views reads them: <see cref="OverlapSpanWords"/> words a render pixel, which the overlap
    /// raster pass (<see cref="Parts.Overlaps"/>) writes through the mesh interface's raster accumulation binding
    /// (<see cref="GlassSumsWritten"/>). <c>frame/sdf-overlaps.hlsli</c> owns the packing.</summary>
    public const string OverlapSpans = "sdfOverlapSpans";
    /// <summary>The words one render pixel holds in the overlap spans: the nearest front face of another object behind the
    /// pixel's visible surface, then the nearest back face of any object behind it, each a ray distance's bits inverted so
    /// an atomic maximum keeps the nearest and zero reads as none. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_OVERLAP_SPAN_WORDS</c>.</summary>
    public const uint OverlapSpanWords = 2;
    /// <summary>The pass-group value the debug views read beside their mode (<c>DebugViewOptions</c>): the overlap view's
    /// tolerance, in world units, then one where bodies take part (<c>float4</c>).</summary>
    public const string DebugOptions = "debugOptions";

    /// <summary>Returns a fragment with the overlap raster pass added after its surface pass, for a view showing the overlap
    /// debug view (<c>DebugViewModes.Overlaps</c>); a view in any other mode runs the fragment as it stands and pays nothing.
    /// The pass clears the overlap spans and draws every opaque draw the view's frustum keeps, at the view's own levels and
    /// with no depth test and no face culled: each face behind the pixel's visible surface keeps the nearest front face of
    /// another object and the nearest back face of any (<c>passes/sdf-mesh-overlaps.frag.hlsl</c>). Views reads the spans:
    /// a ray inside the visible surface's solid that enters another object's before it leaves is inside both, and the
    /// distance between the two faces is the first overlap's depth along the ray.</summary>
    /// <param name="fragment">The fragment, any of the package's.</param>
    /// <returns>The fragment with the overlap pass and its storages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fragment"/> has no surface or views pass, or already holds the
    /// overlap pass.</exception>
    public static RenderGraphPackageFragment WithOverlaps(RenderGraphPackageFragment fragment) {
        ArgumentNullException.ThrowIfNull(argument: fragment);

        if (!fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.Surface)) || !fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.Views))) {
            throw new ArgumentException(message: "The fragment has no surface and views passes to find overlaps between.", paramName: nameof(fragment));
        }
        if (fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.Overlaps))) {
            throw new ArgumentException(message: "The fragment already holds the overlap pass.", paramName: nameof(fragment));
        }

        var meshTarget = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.MeshTarget));
        var meshDepth = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.MeshDepth));
        var visibility = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.Visibility));
        var passes = new List<RenderGraphFragmentPass>(capacity: (fragment.Passes.Count + 1));

        foreach (var pass in fragment.Passes) {
            if (pass.Name == Parts.Views) {
                passes.Add(item: pass with {
                    InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead],
                    Inputs = [.. pass.Inputs, new ResourceReference(Name: Parts.OverlapSpans)],
                });
                continue;
            }
            passes.Add(item: pass);
            if (pass.Name == Parts.Surface) {
                // The raster pass's target and depth hold nothing anyone reads: its fragment stage fails the depth test
                // on every face and writes only the spans.
                passes.Add(item: new RenderGraphFragmentPass(
                    CountsKernelWork: true,
                    InputAccesses: [RenderGraphPortAccess.FragmentSampled],
                    Inputs: [new ResourceReference(Name: Parts.Visibility)],
                    Name: Parts.Overlaps,
                    OutputAccesses: [RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.FragmentWrite],
                    Outputs: [new ResourceReference(Name: Parts.OverlapTarget), new ResourceReference(Name: Parts.OverlapDepth), new ResourceReference(Name: Parts.OverlapSpans)]
                ));
            }
        }

        return fragment with {
            Passes = passes,
            Resources = [
                .. fragment.Resources,
                meshTarget with { Name = Parts.OverlapTarget },
                meshDepth with { Name = Parts.OverlapDepth },
                visibility with { Name = Parts.OverlapSpans, StrideBytes = sizeof(uint), Count = [.. visibility.Count!.Select(selector: static term => term with { Elements = OverlapSpanWords })] },
            ],
        };
    }

    public static partial class Parts {
        /// <summary>The overlap raster pass: the frame's opaque draws into the overlap spans
        /// (<see cref="SdfWorldPackage.WithOverlaps"/>).</summary>
        public const string Overlaps = "overlaps";
        /// <summary>The overlap raster pass's target, in the mesh target's format, which nothing reads.</summary>
        public const string OverlapTarget = "overlapTarget";
        /// <summary>The overlap raster pass's own reversed-Z depth, which nothing reads.</summary>
        public const string OverlapDepth = "overlapDepth";
        /// <summary>The overlap spans (<see cref="SdfWorldPackage.OverlapSpans"/>).</summary>
        public const string OverlapSpans = "overlapSpans";
    }
}
