using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>The installed graph's host-written region storage. Counts payload bytes, not managed object headers or
/// backend allocation alignment, and excludes retired graphs and application state outside those regions.</summary>
/// <param name="Gpu">Actual owned region buffers, by memory kind.</param>
/// <param name="CpuShadowBytes">Retained region shadow payloads.</param>
/// <param name="CpuScratchBytes">Package writer, typed-row and upload-header scratch payloads.</param>
public readonly record struct RenderGraphRegionMemory(GpuMemoryBytes Gpu, ulong CpuShadowBytes, ulong CpuScratchBytes);
public sealed partial class ShaderPipelineRenderNode {
    /// <summary>Gets installed regions' actual GPU memory and retained CPU shadow and scratch payloads, including
    /// the declared scratch of package writers even while they emit no records.</summary>
    public RenderGraphRegionMemory RegionMemory {
        get {
            var gpu = default(GpuMemoryBytes);
            var shadow = 0UL;
            var scratch = 0UL;

            foreach (var pass in m_passes) {
                if (pass is null) { continue; }
                Add(region: pass.FrameRegion);
                Add(region: pass.PassRegion);
                foreach (var region in (pass.Regions ?? [])) { Add(region: region); }
                foreach (var row in (pass.RowRegions ?? [])) {
                    Add(region: row.Region);
                    scratch = checked((scratch + ((ulong)row.Region.ByteCount)));
                }
                scratch = checked((scratch + pass.RegionCpuScratchBytes));
            }
            foreach (var region in m_externalRegions.Values) { Add(region: region); }
            return new RenderGraphRegionMemory(CpuScratchBytes: scratch, CpuShadowBytes: shadow, Gpu: gpu);

            void Add(GpuRegion? region) {
                if (region is null) { return; }
                gpu += region.OwnedBytes;
                shadow = checked((shadow + region.CpuShadowBytes));
                scratch = checked((scratch + region.CpuScratchBytes));
            }
        }
    }
}
