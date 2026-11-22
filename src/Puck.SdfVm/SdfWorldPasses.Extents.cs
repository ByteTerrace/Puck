using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    // Each fragment with the glass passes added (SdfWorldPackage.WithGlass), made once, so a view's fragment keeps its
    // identity from frame to frame.
    private static readonly Puck.Abstractions.CodeDerivedTable<RenderGraphPackageFragment, RenderGraphPackageFragment> GlassFragments = new();
    // Each fragment with its views pass shading in bins (SdfWorldPackage.WithBins), made once for the same reason.
    private static readonly Puck.Abstractions.CodeDerivedTable<RenderGraphPackageFragment, RenderGraphPackageFragment> BinnedFragments = new();

    // Each fragment with its display stage (SdfWorldPackage.WithDisplay), one per bloom quality in BloomQuality's order,
    // made once for the same reason.
    private static readonly Puck.Abstractions.CodeDerivedTable<RenderGraphPackageFragment, RenderGraphPackageFragment[]> DisplayFragments = new();

    /// <inheritdoc/>
    public IShaderPipelineRenderExtent? RenderExtentOf(string instance) => Refresh(instance: instance);
    /// <inheritdoc/>
    /// <remarks>A view that asks for temporal reconstruction runs <see cref="SdfWorldPackage.TemporalFragment"/> at
    /// any render scale; otherwise a reduced view runs <see cref="SdfWorldPackage.Fragment"/> and a native one
    /// <see cref="SdfWorldPackage.NativeFragment"/>. No fragment draws shadows: every view reads its world's shared layers
    /// (<see cref="SdfWorldShadows"/>), which the residency's upload draws.</remarks>
    public RenderGraphPackageFragment? FragmentOf(string instance) {
        var entry = Refresh(instance: instance);
        var fragment = WithEnvironment(fragment: entry.Fragment, residency: entry.View?.Residency);

        return (entry.Converts ? DisplayFragments.GetValue(fragment, static source => [
            SdfWorldPackage.WithDisplay(fragment: source, quality: BloomQuality.Off),
            SdfWorldPackage.WithDisplay(fragment: source, quality: BloomQuality.Low),
            SdfWorldPackage.WithDisplay(fragment: source, quality: BloomQuality.High),
        ])[(int)entry.OpticalBloom] : fragment);
    }

    private sealed partial class Entry {
        private SdfViewSnapshot Snapshot {
            get {
                var view = View;
                var views = view?.Residency.Frame?.Views;

                return ((views is { Count: > 0 }) ? views[Math.Min(val1: view!.Value.View, val2: (views.Count - 1))] : default);
            }
        }
        // Ceiling and temporal reconstruction choose the fragment and its storage. The revision covers both; the resolved
        // scale moves only the grid inside it (SdfViewSnapshot.RenderGrid).
        private double Ceiling => Snapshot.RenderCeiling;

        // Whether the view's graph converts its scene light for the display (SdfWorldPackage.WithDisplay): every view but
        // one published as light for a window (Unexposed) or for a sky layer (GeometryOnly), whose consumer exposes it.
        // The screen shading reads the same split by the screen's face: an opening shows a window's light, a display a
        // working image (SdfScreenFace).
        public bool Converts {
            get {
                var snapshot = Snapshot;

                return !(snapshot.Unexposed || snapshot.Quality.GeometryOnly);
            }
        }
        public BloomQuality OpticalBloom {
            get {
                var snapshot = Snapshot;
                var mode = (View?.Residency.Tables?.PassValues.DebugMode ?? 0);

                return ((!Converts || (snapshot.ScreenFidelity >= 1f) ||
                    (snapshot.Bloom is not { Scatter: > 0f }) || ((mode != 0) && (mode != DebugViewModes.Bloom)))
                    ? BloomQuality.Off : snapshot.BloomQuality);
            }
        }
        public double CurrentScale => Snapshot.RenderGrid;
        public double RenderedScale { get; set; }
        public float CurrentSharpness => ((RequestsTemporal || Snapshot.Reconstructs) ? Snapshot.UpscaleSharpness : 0f);
        public float RenderedSharpness { get; set; }
        // Whether the resolved view asks for temporal reconstruction, which chooses its fragment as the ceiling does.
        public bool RequestsTemporal => Snapshot.Quality.Temporal;
        // Whether the view's program holds a glass shape, so the view runs the glass passes and allocates their
        // storages (SdfWorldPackage.WithGlass); a view of any other program pays nothing for them.
        private bool RequestsGlass => (View?.Residency.Frame?.Program.HasGlass ?? false);
        // Whether the view's residency shades authored programs in bins (SdfWorldResidency.ShadesInBins), so the view
        // runs the binned views passes and allocates their storages (SdfWorldPackage.WithBins); a view of a frame with no
        // program pays nothing for them.
        private bool RequestsBins => (View?.Residency.ShadesInBins ?? false);

        private bool RequestsViewsRate => RequestsTemporal && View?.Residency.Frame?.Settings.ViewsRate is { MaxRate: > 1 };

        public RenderGraphPackageFragment Fragment => Glassed(fragment: Binned(fragment: (RequestsTemporal
            ? (RequestsViewsRate ? SdfWorldPackage.RateFragment : SdfWorldPackage.TemporalFragment)
            : (Snapshot.Reconstructs ? SdfWorldPackage.Fragment : SdfWorldPackage.NativeFragment))));

        // The low bits distinguish temporal reconstruction, the glass passes, the binned views passes and the views rate.
        // Render ceilings are quantized fractions from zero through one (RenderGraphExtent.Quantize): their binary exponents
        // share the two high bits shifted out here, and their mantissas' low bits, which these bits land in, are zero.
        long IShaderPipelineRenderExtent.Revision => (BitConverter.DoubleToInt64Bits(value: Ceiling) << 2) | (RequestsViewsRate ? 8L : 0L) | (RequestsBins ? 4L : 0L) |
            (RequestsGlass ? 2L : 0L) | (RequestsTemporal ? 1L : 0L);

        private RenderGraphPackageFragment Binned(RenderGraphPackageFragment fragment) =>
            (RequestsBins ? BinnedFragments.GetValue(derive: SdfWorldPackage.WithBins, key: fragment) : fragment);
        private RenderGraphPackageFragment Glassed(RenderGraphPackageFragment fragment) =>
            (RequestsGlass ? GlassFragments.GetValue(derive: SdfWorldPackage.WithGlass, key: fragment) : fragment);

        double IShaderPipelineRenderExtent.Grid => CurrentScale;

        public (uint Width, uint Height) CeilingAt(uint width, uint height) => Pixels(width: width, height: height, scale: Ceiling);
        public (uint Width, uint Height) FrameAt(uint width, uint height) => Pixels(width: width, height: height, scale: CurrentScale);

        private static (uint Width, uint Height) Pixels(uint width, uint height, double scale) => (
            ((uint)RenderGraphExtent.Pixels(display: checked((int)width), fraction: scale)),
            ((uint)RenderGraphExtent.Pixels(display: checked((int)height), fraction: scale))
        );
    }
}
