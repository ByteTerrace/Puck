using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    /// <inheritdoc/>
    public IShaderPipelineRenderExtent? RenderExtentOf(string instance) => Refresh(instance: instance);
    /// <inheritdoc/>
    /// <remarks>A view that asks for temporal reconstruction runs <see cref="SdfWorldPackage.TemporalFragment"/> at
    /// any render scale; otherwise a reduced view runs <see cref="SdfWorldPackage.Fragment"/> and a native one
    /// <see cref="SdfWorldPackage.NativeFragment"/>. Each uses the fade-capacity variant supplied by
    /// <see cref="SdfWorldPackage.FragmentFor"/>.</remarks>
    public RenderGraphPackageFragment? FragmentOf(string instance) => Refresh(instance: instance).Fragment;

    private sealed partial class Entry {
        private SdfViewSnapshot Snapshot {
            get {
                var view = View;
                var views = view?.Residency.Frame?.Views;

                return ((views is { Count: > 0 }) ? views[Math.Min(val1: view!.Value.View, val2: (views.Count - 1))] : default);
            }
        }
        // Ceiling, temporal reconstruction and fade capacity choose the fragment and its storage. The revision covers
        // all three; the resolved scale moves only the grid inside it (SdfViewSnapshot.RenderGrid).
        private double Ceiling => Snapshot.RenderCeiling;

        public double CurrentScale => Snapshot.RenderGrid;
        public double RenderedScale { get; set; }
        public float CurrentSharpness => ((RequestsTemporal || Snapshot.Reconstructs) ? Snapshot.UpscaleSharpness : 0f);
        public float RenderedSharpness { get; set; }
        // Whether the resolved view asks for temporal reconstruction, which chooses its fragment as the ceiling does.
        public bool RequestsTemporal => Snapshot.Quality.Temporal;
        public int CurrentShadowFadeCapacity => (View?.Residency.Frame?.Lights.ShadowSlots.FadeCapacity ?? 0);
        public int RenderedShadowFadeCapacity { get; set; } = -1;
        public RenderGraphPackageFragment Fragment => SdfWorldPackage.FragmentFor(reconstructs: Snapshot.Reconstructs, temporal: RequestsTemporal, fadeCapacity: CurrentShadowFadeCapacity);

        // Three low bits distinguish temporal reconstruction and the three fade capacities. Render ceilings are
        // fractions from zero through one, whose binary exponents share the high bits shifted out here.
        long IShaderPipelineRenderExtent.Revision => (BitConverter.DoubleToInt64Bits(value: Ceiling) << 3) | (RequestsTemporal ? 1L : 0L) | (((long)CurrentShadowFadeCapacity) << 1);
        double IShaderPipelineRenderExtent.Grid => CurrentScale;

        public (uint Width, uint Height) CeilingAt(uint width, uint height) => Pixels(width: width, height: height, scale: Ceiling);
        public (uint Width, uint Height) FrameAt(uint width, uint height) => Pixels(width: width, height: height, scale: CurrentScale);

        private static (uint Width, uint Height) Pixels(uint width, uint height, double scale) => (
            ((uint)RenderGraphExtent.Pixels(display: checked((int)width), fraction: scale)),
            ((uint)RenderGraphExtent.Pixels(display: checked((int)height), fraction: scale))
        );
    }
}
