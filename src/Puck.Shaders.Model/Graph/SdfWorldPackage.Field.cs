namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The field view's colours as the composite reads them: one word a render pixel, which the field pass
    /// (<see cref="Parts.Field"/>) writes through <see cref="FieldColorsWritten"/>: red, green and blue display-encoded in
    /// the low three bytes and the share of the pixel they cover in the high byte. <c>passes/sdf-world-field.comp.hlsl</c>
    /// owns the packing.</summary>
    public const string FieldColors = "sdfFieldColors";
    /// <summary>The field view's colours as the field pass writes them (<see cref="FieldColors"/>).</summary>
    public const string FieldColorsWritten = "sdfFieldColorsRW";
    /// <summary>The pass-group value the debug views read beside their mode (<c>DebugViewOptions</c>): the proximity
    /// view's tolerance in metres, one where bodies take part, and its band spacing in metres (<c>float4</c>).</summary>
    public const string DebugOptions = "debugOptions";
    /// <summary>The rows one field entry holds in <see cref="FieldEntries"/> (<c>Puck.SdfVm.SdfFieldEntries</c>):
    /// its bounding sphere, its frame's position and scale, its frame's orientation, and its instance run, object key and
    /// flags. <c>SdfIsaHlsl</c> generates it as <c>SDF_FIELD_ENTRY_ROWS</c>.</summary>
    public const uint FieldEntryRows = 4;
    /// <summary>The entries one field pass group keeps for its tile: the entries whose bounding spheres may answer a
    /// pixel of the tile; a tile that finds more walks every entry. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_FIELD_TILE_ENTRIES</c>.</summary>
    public const uint FieldTileEntries = 512;
    /// <summary>The edge, in pixels, of the square tile one field pass group shades. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_FIELD_TILE_EDGE</c>.</summary>
    public const uint FieldTileEdge = 16;

    /// <summary>Returns a fragment with the field pass added after its surface pass, for a view showing a field view (the
    /// proximity debug view, <c>DebugViewModes.Proximity</c>, or the slicing plane, <c>Puck.SdfVm.SdfFieldView</c>); a view
    /// showing neither runs the fragment as it stands and pays nothing. The pass evaluates the residency's field at every
    /// render pixel through the field interpreter (<c>passes/sdf-world-field.comp.hlsl</c>), over the field entries whose
    /// bounding spheres may answer the pixel's tile, and writes the field colours, which the composite lays over the
    /// view.</summary>
    /// <param name="fragment">The fragment, any of the package's.</param>
    /// <returns>The fragment with the field pass, its colours and the composite reading them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fragment"/> has no surface or composite pass, or already holds
    /// the field pass.</exception>
    public static RenderGraphPackageFragment WithField(RenderGraphPackageFragment fragment) {
        ArgumentNullException.ThrowIfNull(argument: fragment);

        if (!fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.Surface)) || !fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.Composite))) {
            throw new ArgumentException(message: "The fragment has no surface and composite passes to show the field between.", paramName: nameof(fragment));
        }
        if (fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.Field))) {
            throw new ArgumentException(message: "The fragment already holds the field pass.", paramName: nameof(fragment));
        }

        var visibility = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.Visibility));
        var passes = new List<RenderGraphFragmentPass>(capacity: (fragment.Passes.Count + 1));

        foreach (var pass in fragment.Passes) {
            if (pass.Name == Parts.Composite) {
                passes.Add(item: pass with {
                    InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead],
                    Inputs = [.. pass.Inputs, new ResourceReference(Name: Parts.FieldColors)],
                });
                continue;
            }
            passes.Add(item: pass);
            if (pass.Name == Parts.Surface) {
                passes.Add(item: Pass(inputs: [Parts.Visibility], name: Parts.Field, outputs: [Parts.FieldColors]));
            }
        }

        return fragment with {
            Passes = passes,
            Resources = [
                .. fragment.Resources,
                visibility with { Name = Parts.FieldColors, StrideBytes = sizeof(uint) },
            ],
        };
    }

    public static partial class Parts {
        /// <summary>The field pass: the residency's field at every render pixel, into the field colours
        /// (<see cref="SdfWorldPackage.WithField"/>).</summary>
        public const string Field = "field";
        /// <summary>The field colours (<see cref="SdfWorldPackage.FieldColors"/>).</summary>
        public const string FieldColors = "fieldColors";
    }
}
