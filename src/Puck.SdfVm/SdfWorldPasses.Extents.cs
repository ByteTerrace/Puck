using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    // Each fragment without its shadow cascades, made once, so a view's fragment keeps its identity from frame to frame.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RenderGraphPackageFragment, RenderGraphPackageFragment> ShadowlessFragments = new();
    // Each fragment with its cascades shaped, one per shadow resolution and second-slot choice, made once each for the
    // same reason.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RenderGraphPackageFragment, RenderGraphPackageFragment?[]> ShadowedFragments = new();
    // Each fragment with the glass passes added (SdfWorldPackage.WithGlass), made once for the same reason.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RenderGraphPackageFragment, RenderGraphPackageFragment> GlassFragments = new();

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RenderGraphPackageFragment, RenderGraphPackageFragment> GlassShadowFragments = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RenderGraphPackageFragment, RenderGraphPackageFragment> BoxCasterFragments = new();
    // Each fragment with its views pass shading in bins (SdfWorldPackage.WithBins), made once for the same reason.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RenderGraphPackageFragment, RenderGraphPackageFragment> BinnedFragments = new();

    // The shapes a fragment's cascades take: each resolution with and without the second slot's cascade.
    private const int ShadowShapes = 6;

    // Each fragment with its display stage (SdfWorldPackage.WithDisplay), one per bloom quality in BloomQuality's order,
    // made once for the same reason.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RenderGraphPackageFragment, RenderGraphPackageFragment[]> DisplayFragments = new();

    /// <inheritdoc/>
    public IShaderPipelineRenderExtent? RenderExtentOf(string instance) => Refresh(instance: instance);
    /// <inheritdoc/>
    /// <remarks>A view that asks for temporal reconstruction runs <see cref="SdfWorldPackage.TemporalFragment"/> at
    /// any render scale; otherwise a reduced view runs <see cref="SdfWorldPackage.Fragment"/> and a native one
    /// <see cref="SdfWorldPackage.NativeFragment"/>, each without its shadow cascades for a view that skips shadows
    /// (<see cref="SdfWorldPackage.WithoutShadows"/>), and otherwise with cascades at the view's
    /// <see cref="SdfViewQuality.ShadowResolution"/>, the second slot's only while its world holds a second shadow slot
    /// (<see cref="SdfWorldPackage.WithShadows"/>).</remarks>
    public RenderGraphPackageFragment? FragmentOf(string instance) {
        var entry = Refresh(instance: instance);

        var fragment = entry.Fragment;
        if (entry.View?.Residency.Frame?.MeshCull != SdfMeshCullLevel.Occlusion) {
            fragment = BoxCasterFragments.GetValue(fragment, SdfWorldPackage.WithoutCasterReceivers);
        }
        fragment = WithEnvironment(fragment: fragment, residency: entry.View?.Residency);

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
        // Whether the resolved view renders shadows; a view without them runs no cascade pass and holds no cascade.
        public bool RequestsShadows => !Snapshot.Quality.DisableSoftShadows;

        // The resolution the view's cascades are allocated at while it renders shadows.
        private SdfShadowResolution ShadowResolution => Snapshot.Quality.ShadowResolution;
        // Whether the view's world holds a second stable shadow slot, whose one cascade the view then allocates. The slot
        // count is the world's configured policy, so a handoff between lights never allocates.
        private bool RequestsSecondShadow => ((View?.Residency.Frame?.Lights.ShadowSlots.SlotCount ?? 0) > 1);
        // Whether the view's program holds a glass shape, so the view runs the glass passes and allocates their
        // storages (SdfWorldPackage.WithGlass); a view of any other program pays nothing for them.
        private bool RequestsGlass => (View?.Residency.Frame?.Program.HasGlass ?? false);

        private bool RequestsGlassShadows => RequestsShadows && (View?.Residency.Frame?.Program.HasGlassShadows ?? false);
        // Whether the view's residency shades authored programs in bins (SdfWorldResidency.ShadesInBins), so the view
        // runs the binned views passes and allocates their storages (SdfWorldPackage.WithBins); a view of a frame with no
        // program pays nothing for them.
        private bool RequestsBins => (View?.Residency.ShadesInBins ?? false);

        public RenderGraphPackageFragment Fragment => Shadowed(fragment: Glassed(fragment: Binned(fragment: (RequestsTemporal
            ? SdfWorldPackage.TemporalFragment
            : (Snapshot.Reconstructs ? SdfWorldPackage.Fragment : SdfWorldPackage.NativeFragment)))));

        // The low bits distinguish temporal reconstruction, the shadows, their resolution, the second slot's cascade, the
        // glass passes and the binned views passes. Render ceilings are quantized fractions from zero through one
        // (RenderGraphExtent.Quantize): their binary exponents share the two high bits shifted out here, and their
        // mantissas' low bits, which these bits land in, are zero.
        long IShaderPipelineRenderExtent.Revision => (BitConverter.DoubleToInt64Bits(value: Ceiling) << 2) | (RequestsBins ? 128L : 0L) |
            (RequestsGlassShadows ? 64L : 0L) | (RequestsGlass ? 32L : 0L) | (RequestsSecondShadow ? 16L : 0L) | (((long)ShadowResolution) << 2) | (RequestsShadows ? 2L : 0L) | (RequestsTemporal ? 1L : 0L);

        private RenderGraphPackageFragment Binned(RenderGraphPackageFragment fragment) =>
            (RequestsBins ? BinnedFragments.GetValue(createValueCallback: SdfWorldPackage.WithBins, key: fragment) : fragment);
        private RenderGraphPackageFragment Glassed(RenderGraphPackageFragment fragment) =>
            (RequestsGlass ? GlassFragments.GetValue(createValueCallback: SdfWorldPackage.WithGlass, key: fragment) : fragment);
        private RenderGraphPackageFragment Shadowed(RenderGraphPackageFragment fragment) {
            if (!RequestsShadows) {
                return ShadowlessFragments.GetValue(createValueCallback: SdfWorldPackage.WithoutShadows, key: fragment);
            }

            if (RequestsGlassShadows) { fragment = GlassShadowFragments.GetValue(fragment, SdfWorldPackage.WithGlassShadows); }
            var resolution = ShadowResolution;
            var second = RequestsSecondShadow;
            var shaped = ShadowedFragments.GetValue(createValueCallback: static _ => new RenderGraphPackageFragment[ShadowShapes], key: fragment);
            var index = ((((int)resolution) * 2) + (second ? 1 : 0));

            // The first fragment made for a shape is the one every later frame takes, whichever thread made it.
            return (Volatile.Read(location: ref shaped[index]) ?? (Interlocked.CompareExchange(
                comparand: null,
                location1: ref shaped[index],
                value: SdfWorldPackage.WithShadows(fragment: fragment, second: second, texels: SdfShadowResolutions.Texels(resolution: resolution))
            ) ?? shaped[index]!));
        }

        double IShaderPipelineRenderExtent.Grid => CurrentScale;

        public (uint Width, uint Height) CeilingAt(uint width, uint height) => Pixels(width: width, height: height, scale: Ceiling);
        public (uint Width, uint Height) FrameAt(uint width, uint height) => Pixels(width: width, height: height, scale: CurrentScale);

        private static (uint Width, uint Height) Pixels(uint width, uint height, double scale) => (
            ((uint)RenderGraphExtent.Pixels(display: checked((int)width), fraction: scale)),
            ((uint)RenderGraphExtent.Pixels(display: checked((int)height), fraction: scale))
        );
    }
}
