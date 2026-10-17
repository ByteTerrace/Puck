using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    // Each fragment without its shadow cascades, made once, so a view's fragment keeps its identity from frame to frame.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RenderGraphPackageFragment, RenderGraphPackageFragment> ShadowlessFragments = new();

    /// <inheritdoc/>
    public IShaderPipelineRenderExtent? RenderExtentOf(string instance) => Refresh(instance: instance);
    /// <inheritdoc/>
    /// <remarks>A view that asks for temporal reconstruction runs <see cref="SdfWorldPackage.TemporalFragment"/> at
    /// any render scale; otherwise a reduced view runs <see cref="SdfWorldPackage.Fragment"/> and a native one
    /// <see cref="SdfWorldPackage.NativeFragment"/>, each without its shadow cascades for a view that skips shadows
    /// (<see cref="SdfWorldPackage.WithoutShadows"/>).</remarks>
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
        // Whether the resolved view renders shadows; a view without them runs no cascade pass and holds no cascade target.
        public bool RequestsShadows => !Snapshot.Quality.DisableSoftShadows;
        public RenderGraphPackageFragment Fragment => Shadowed(fragment: (RequestsTemporal
            ? SdfWorldPackage.TemporalFragment
            : (Snapshot.Reconstructs ? SdfWorldPackage.Fragment : SdfWorldPackage.NativeFragment)));

        // The low bit distinguishes temporal reconstruction and the next shadows. Render ceilings are fractions from zero
        // through one, whose binary exponents share the two high bits shifted out here.
        long IShaderPipelineRenderExtent.Revision => (BitConverter.DoubleToInt64Bits(value: Ceiling) << 2) | (RequestsShadows ? 2L : 0L) | (RequestsTemporal ? 1L : 0L);

        private RenderGraphPackageFragment Shadowed(RenderGraphPackageFragment fragment) => (RequestsShadows
            ? fragment
            : ShadowlessFragments.GetValue(key: fragment, createValueCallback: SdfWorldPackage.WithoutShadows));
        double IShaderPipelineRenderExtent.Grid => CurrentScale;

        public (uint Width, uint Height) CeilingAt(uint width, uint height) => Pixels(width: width, height: height, scale: Ceiling);
        public (uint Width, uint Height) FrameAt(uint width, uint height) => Pixels(width: width, height: height, scale: CurrentScale);

        private static (uint Width, uint Height) Pixels(uint width, uint height, double scale) => (
            ((uint)RenderGraphExtent.Pixels(display: checked((int)width), fraction: scale)),
            ((uint)RenderGraphExtent.Pixels(display: checked((int)height), fraction: scale))
        );
    }
}
