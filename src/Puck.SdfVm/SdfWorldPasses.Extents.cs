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
    public RenderGraphPackageFragment? FragmentOf(string instance) {
        var entry = Refresh(instance: instance);
        if (entry.View is { LightView: true } light) { return LightFragment(residency: light.Residency); }
        var fragment = entry.Fragment;
        var tier = (entry.View?.Residency.IndirectTier ?? Puck.SignedDistance.SdfIndirectTier.Off);

        if (tier == Puck.SignedDistance.SdfIndirectTier.Off) { return fragment; }
        var maps = ((entry.View is { } current) && (LightViewName(residency: current.Residency) is not null) ? LightMapCount(residency: current.Residency) : -1);
        var key = (fragment, tier, maps);

        if (!m_indirectFragments.TryGetValue(key: key, value: out var indirect)) {
            indirect = SdfWorldPackage.WithIndirect(fragment: fragment, bytes: new Puck.SignedDistance.SdfIndirectLayout(tier: tier).ByteLength);
            if (maps >= 0) { indirect = SdfWorldPackage.WithLightViews(fragment: indirect, maps: maps); }
            m_indirectFragments.Add(key: key, value: indirect);
        }
        return indirect;
    }

    private readonly Dictionary<(RenderGraphPackageFragment, Puck.SignedDistance.SdfIndirectTier, int), RenderGraphPackageFragment> m_indirectFragments = [];

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
        private double Ceiling => ((View is { LightView: true }) ? 1.0 : Snapshot.RenderCeiling);

        public double CurrentScale => ((View is { LightView: true }) ? 1.0 : Snapshot.RenderGrid);
        public double RenderedScale { get; set; }
        public float CurrentSharpness => ((RequestsTemporal || Snapshot.Reconstructs) ? Snapshot.UpscaleSharpness : 0f);
        public float RenderedSharpness { get; set; }
        // Whether the resolved view asks for temporal reconstruction, which chooses its fragment as the ceiling does.
        public bool RequestsTemporal => ((View is not { LightView: true }) && Snapshot.Quality.Temporal);
        public int CurrentShadowFadeCapacity => ((View is { LightView: true }) ? 0 : (View?.Residency.Frame?.Lights.ShadowSlots.FadeCapacity ?? 0));
        public int RenderedShadowFadeCapacity { get; set; } = -1;
        public RenderGraphPackageFragment Fragment => SdfWorldPackage.FragmentFor(reconstructs: Snapshot.Reconstructs, temporal: RequestsTemporal, fadeCapacity: CurrentShadowFadeCapacity);

        // Three low bits distinguish temporal reconstruction and the three fade capacities. Render ceilings are
        // fractions from zero through one, whose binary exponents share the high bits shifted out here.
        long IShaderPipelineRenderExtent.Revision => (BitConverter.DoubleToInt64Bits(value: Ceiling) << 3) | (RequestsTemporal ? 1L : 0L) | (((long)CurrentShadowFadeCapacity) << 1);
        double IShaderPipelineRenderExtent.Grid => CurrentScale;

        public (uint Width, uint Height) CeilingAt(uint width, uint height) => ((View is { LightView: true }) ? (SdfIndirectLightLayout.Resolution, SdfIndirectLightLayout.Resolution) : Pixels(width: width, height: height, scale: Ceiling));
        public (uint Width, uint Height) FrameAt(uint width, uint height) => ((View is { LightView: true }) ? (SdfIndirectLightLayout.Resolution, SdfIndirectLightLayout.Resolution) : Pixels(width: width, height: height, scale: CurrentScale));

        private static (uint Width, uint Height) Pixels(uint width, uint height, double scale) => (
            ((uint)RenderGraphExtent.Pixels(display: checked((int)width), fraction: scale)),
            ((uint)RenderGraphExtent.Pixels(display: checked((int)height), fraction: scale))
        );
    }
}
