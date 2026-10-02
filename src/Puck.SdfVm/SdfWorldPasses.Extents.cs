using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPasses {
    /// <inheritdoc/>
    public IShaderPipelineRenderExtent? RenderExtentOf(string instance) => Refresh(instance: instance);
    /// <inheritdoc/>
    public RenderGraphPackageFragment? FragmentOf(string instance) {
        var entry = Refresh(instance: instance);

        return (entry.TemporalEnabled ? SdfWorldPackage.TemporalFragment
            : (entry.RequiresResolve ? SdfWorldPackage.Fragment : SdfWorldPackage.NativeFragment));
    }

    private sealed partial class Entry {
        private SdfViewSnapshot Snapshot {
            get {
                var view = View;
                var views = view?.Residency.Frame?.Views;

                return ((views is { Count: > 0 }) ? views[Math.Min(val1: view!.Value.View, val2: (views.Count - 1))] : default);
            }
        }
        private double Ceiling => RenderGraphExtent.Quantize(fraction: ((Snapshot.RenderScale > 0f) ? Snapshot.RenderScale : 1f));

        public double CurrentScale => ((Snapshot.ResolvedRenderScale > 0f) ? Math.Min(val1: Snapshot.ResolvedRenderScale, val2: Ceiling) : Ceiling);
        public double RenderedScale { get; set; }
        public float CurrentSharpness => (RequiresResolve ? Snapshot.UpscaleSharpness : 0f);
        public float RenderedSharpness { get; set; }
        public bool TemporalEnabled => Snapshot.Temporal;
        public bool RenderedTemporal { get; set; }
        public long CurrentCut => Snapshot.CutRevision;
        public long RenderedCut { get; set; }
        public bool RequiresResolve => (TemporalEnabled || (Ceiling < 1d) || (Snapshot.ResolvedRenderScale > 0f));

        long IShaderPipelineRenderExtent.Revision => BitConverter.DoubleToInt64Bits(value: Ceiling);

        public (uint Width, uint Height) CeilingAt(uint width, uint height) => Pixels(width: width, height: height, scale: Ceiling);
        public (uint Width, uint Height) FrameAt(uint width, uint height) => Pixels(width: width, height: height, scale: CurrentScale);

        private static (uint Width, uint Height) Pixels(uint width, uint height, double scale) => (
            ((uint)RenderGraphExtent.Pixels(display: checked((int)width), fraction: scale)),
            ((uint)RenderGraphExtent.Pixels(display: checked((int)height), fraction: scale))
        );
    }
}
