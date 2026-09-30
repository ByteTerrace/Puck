using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    /// <inheritdoc/>
    public IShaderPipelineRenderExtent? RenderExtentOf(string instance) => Refresh(instance: instance);
    /// <inheritdoc/>
    public RenderGraphPackageFragment? FragmentOf(string instance) => (Refresh(instance: instance).RequiresResolve
        ? SdfWorldPackage.Fragment
        : SdfWorldPackage.NativeFragment);

    private sealed partial class Entry {
        private SdfViewSnapshot Snapshot {
            get {
                var view = View;
                var views = view?.Residency.Frame?.Views;

                return ((views is { Count: > 0 }) ? views[Math.Min(val1: view!.Value.View, val2: (views.Count - 1))] : default);
            }
        }
        // The ceiling alone chooses the fragment and sizes the scratch, so the revision a graph is built against covers
        // both; the resolved scale moves only the grid inside it (SdfViewSnapshot.RenderGrid).
        private double Ceiling => Snapshot.RenderCeiling;

        public double CurrentScale => Snapshot.RenderGrid;
        public double RenderedScale { get; set; }
        public float CurrentSharpness => (RequiresResolve ? Snapshot.UpscaleSharpness : 0f);
        public float RenderedSharpness { get; set; }
        public bool RequiresResolve => Snapshot.Reconstructs;

        long IShaderPipelineRenderExtent.Revision => BitConverter.DoubleToInt64Bits(value: Ceiling);

        public (uint Width, uint Height) CeilingAt(uint width, uint height) => Pixels(width: width, height: height, scale: Ceiling);
        public (uint Width, uint Height) FrameAt(uint width, uint height) => Pixels(width: width, height: height, scale: CurrentScale);

        private static (uint Width, uint Height) Pixels(uint width, uint height, double scale) => (
            ((uint)RenderGraphExtent.Pixels(display: checked((int)width), fraction: scale)),
            ((uint)RenderGraphExtent.Pixels(display: checked((int)height), fraction: scale))
        );
    }
}
