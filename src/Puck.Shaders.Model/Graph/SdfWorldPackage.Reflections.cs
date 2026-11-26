namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The screen-space reflections as views reads them: <see cref="ViewsReflectionWords"/> words a render-extent
    /// pixel, laid out as the reactivity is (<c>passes/sdf-views-reflections.hlsli</c> owns the packing): the colour the
    /// pixel's mirror ray reaches, at the stored light scale (<see cref="LightScale"/>), and the shares of the reflection it
    /// answers by that colour and by the open sky, which views blends over the irradiance volume's reflection.</summary>
    public const string ViewsReflections = "sdfViewsReflections";
    /// <summary>The screen-space reflections as the reflection pass writes them.</summary>
    public const string ViewsReflectionsWritten = "sdfViewsReflectionsRW";
    /// <summary>The preceding frame's resolved colour the reflection pass reads a mirror ray's hit from: the history colour
    /// (<see cref="HistoryColor"/>) of a temporal fragment, which holds the lit image premultiplied by coverage.</summary>
    public const string ReflectionHistory = "sdfReflectionHistory";
    /// <summary>The pass-group value set to one when the view's fragment holds the reflection pass
    /// (<see cref="Parts.ViewsReflections"/>), so views reads its record; zero, views reflects the irradiance volume alone
    /// (<c>uint</c>).</summary>
    public const string ScreenReflections = "screenReflections";
    /// <summary>The words of one pixel's reflection record. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_VIEWS_REFLECTION_WORDS</c>.</summary>
    public const uint ViewsReflectionWords = 2;

    /// <summary>Returns a fragment whose views pass reads screen-space reflections: <see cref="Parts.ViewsReflections"/>
    /// runs before views, marching each glossy pixel's mirror ray through the mesh target's depth and reading what it
    /// reaches from the preceding frame's history colour, and views blends the answer over the irradiance volume's
    /// reflection. Only a fragment that keeps the history colour (<see cref="IsTemporal"/>) has a picture to read.</summary>
    /// <param name="fragment">A temporal fragment.</param>
    /// <returns>The fragment with the reflection pass and its record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fragment"/> keeps no history colour, has no views pass, or
    /// already reflects.</exception>
    public static RenderGraphPackageFragment WithReflections(RenderGraphPackageFragment fragment) {
        ArgumentNullException.ThrowIfNull(argument: fragment);

        if (!IsTemporal(fragment: fragment)) {
            throw new ArgumentException(message: "The fragment keeps no history colour to reflect.", paramName: nameof(fragment));
        }
        if (!fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.Views))) {
            throw new ArgumentException(message: "The fragment has no views pass to reflect for.", paramName: nameof(fragment));
        }
        if (fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.ViewsReflections))) {
            throw new ArgumentException(message: "The fragment already reflects.", paramName: nameof(fragment));
        }

        // The record follows the visibility record's extent basis, the render extent's in a temporal view.
        var visibility = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.Visibility));
        var passes = new List<RenderGraphFragmentPass>(capacity: (fragment.Passes.Count + 1));

        foreach (var pass in fragment.Passes) {
            if (pass.Name != Parts.Views) {
                passes.Add(item: pass);
                continue;
            }
            passes.Add(item: new RenderGraphFragmentPass(
                CountsKernelWork: true,
                InputAccesses: [RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead],
                Inputs: [
                    new ResourceReference(Name: Parts.MeshTarget),
                    new ResourceReference(Name: Parts.Visibility),
                    new ResourceReference(Name: Parts.HistoryColor, PreviousFrame: true),
                ],
                Name: Parts.ViewsReflections,
                OutputAccesses: [RenderGraphPortAccess.ComputeWrite],
                Outputs: [new ResourceReference(Name: Parts.Reflections)]
            ));
            passes.Add(item: pass with {
                InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead],
                Inputs = [.. pass.Inputs, new ResourceReference(Name: Parts.Reflections)],
            });
        }

        return fragment with {
            Passes = passes,
            Resources = [
                .. fragment.Resources,
                visibility with {
                    Name = Parts.Reflections,
                    StrideBytes = sizeof(uint),
                    Count = [.. visibility.Count!.Select(selector: static term => term with { Elements = ViewsReflectionWords })],
                },
            ],
        };
    }

    public static partial class Parts {
        /// <summary>The reflection pass: each glossy pixel's mirror ray marched through the mesh target's depth, its hit read
        /// from the preceding frame's history colour, into the record views blends over the irradiance volume's
        /// reflection.</summary>
        public const string ViewsReflections = "views-reflections";
        /// <summary>The reflection records the reflection pass writes (<see cref="SdfWorldPackage.ViewsReflections"/>).</summary>
        public const string Reflections = "viewsReflections";
    }
}
