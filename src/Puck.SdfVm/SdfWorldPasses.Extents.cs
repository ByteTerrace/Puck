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
    // The shapes a fragment's cascades take: each resolution with and without the second slot's cascade.
    private const int ShadowShapes = 6;

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

        return WithEnvironment(fragment: entry.Fragment, residency: entry.View?.Residency);
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
        public RenderGraphPackageFragment Fragment => Shadowed(fragment: Glassed(fragment: (RequestsTemporal
            ? SdfWorldPackage.TemporalFragment
            : (Snapshot.Reconstructs ? SdfWorldPackage.Fragment : SdfWorldPackage.NativeFragment))));

        // The low bits distinguish temporal reconstruction, the shadows, their resolution, the second slot's cascade and
        // the glass passes. Render ceilings are quantized fractions from zero through one (RenderGraphExtent.Quantize):
        // their binary exponents share the two high bits shifted out here, and their mantissas' low bits, which the
        // shadow and glass bits land in, are zero.
        long IShaderPipelineRenderExtent.Revision => (BitConverter.DoubleToInt64Bits(value: Ceiling) << 2) |
            (RequestsGlass ? 32L : 0L) | (RequestsSecondShadow ? 16L : 0L) | (((long)ShadowResolution) << 2) | (RequestsShadows ? 2L : 0L) | (RequestsTemporal ? 1L : 0L);

        private RenderGraphPackageFragment Glassed(RenderGraphPackageFragment fragment) =>
            (RequestsGlass ? GlassFragments.GetValue(key: fragment, createValueCallback: SdfWorldPackage.WithGlass) : fragment);

        private RenderGraphPackageFragment Shadowed(RenderGraphPackageFragment fragment) {
            if (!RequestsShadows) {
                return ShadowlessFragments.GetValue(key: fragment, createValueCallback: SdfWorldPackage.WithoutShadows);
            }

            var resolution = ShadowResolution;
            var second = RequestsSecondShadow;
            var shaped = ShadowedFragments.GetValue(key: fragment, createValueCallback: static _ => new RenderGraphPackageFragment[ShadowShapes]);
            var index = ((((int)resolution) * 2) + (second ? 1 : 0));

            // The first fragment made for a shape is the one every later frame takes, whichever thread made it.
            return (Volatile.Read(location: ref shaped[index]) ?? Interlocked.CompareExchange(
                comparand: null,
                location1: ref shaped[index],
                value: SdfWorldPackage.WithShadows(fragment: fragment, second: second, texels: SdfShadowResolutions.Texels(resolution: resolution))
            ) ?? shaped[index]!);
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
