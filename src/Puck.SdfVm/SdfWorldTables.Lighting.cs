using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private readonly SdfLightingUpload m_lighting = new();
    private readonly GpuRegion m_lightFrameRegion;
    private readonly GpuRegion m_lightsRegion;
    private readonly GpuRegion m_skyFrameRegion;
    private GpuRegion m_skyStopsRegion;
    private readonly GpuRegion m_skySoftboxesRegion;

    /// <summary>Gets the native lighting tables, common unused-record filler, and CPU shadow and writer payloads. The GPU bytes are a
    /// subset of <see cref="TableBytes"/>; CPU totals follow <see cref="RenderGraphRegionMemory"/>'s payload accounting.</summary>
    public RenderGraphRegionMemory LightingMemory {
        get {
            var gpu = new GpuMemoryBytes(DeviceLocal: m_dummyBuffer.SizeBytes, HostVisible: 0);
            var shadow = 0UL;
            var scratch = checked(m_lighting.CpuScratchBytes + (ulong)m_skyAdmissionWords.Length * sizeof(uint));

            for (var index = LightFrameRegionIndex; index < RegionCount; index++) {
                if (RegionAt(index: index) is not { } region) { continue; }

                gpu += region.OwnedBytes;
                shadow = checked((shadow + region.CpuShadowBytes));
                scratch = checked((scratch + region.CpuScratchBytes));
            }
            return new RenderGraphRegionMemory(CpuScratchBytes: scratch, CpuShadowBytes: shadow, Gpu: gpu);
        }
    }

    // All upload words share the existing region policy and copy pool. Integer fields remain integer bytes.
    private void PackLighting(SdfFrame frame) {
        PackSkyTables(frame);
        m_lighting.Pack(environment: frame.Environment);
        _ = m_lightFrameRegion.Write(bytes: m_lighting.LightFrameBytes, offset: 0);
        _ = m_lightsRegion.Write(bytes: m_lighting.LightBytes, offset: 0);
        _ = m_skyFrameRegion.Write(bytes: m_lighting.SkyFrameBytes, offset: 0);
        if (frame.Environment.Sky?.Stops is null) { _ = m_skyStopsRegion.Write(bytes: m_lighting.StopBytes, offset: 0); }
        _ = m_skySoftboxesRegion.Write(bytes: m_lighting.SoftboxBytes, offset: 0);
    }

    // The four distinct World groups. Temporal Views uses the ordinary Views World group unchanged.
    private static class WorldGroups {
        internal static readonly SdfKernel[] Kernels = [SdfKernel.Beam, SdfKernel.Sky, SdfKernel.Shadow, SdfKernel.Views];
        internal static readonly ShaderInterfaceLayout[] Layouts = [
            SdfWorldInterfaces.WorldLayout,
            SdfWorldInterfaces.SkyParameters.Layout,
            SdfWorldInterfaces.ShadowParameters.Layout,
            SdfWorldInterfaces.ViewsParameters.Layout,
        ];

        internal static int Index(string part) => part switch {
            SdfWorldPackage.Parts.Sky => 1,
            SdfWorldPackage.Parts.Shadow => 2,
            SdfWorldPackage.Parts.Views => 3,
            _ => 0,
        };
    }

    private void WriteLighting(nint set, int slot, ShaderInterfaceLayout layout, int group) {
        if (group is 1 or 2 or 3) {
            WriteBuffer(set, layout, SdfWorldPackage.LightFrame, m_lightFrameRegion.Buffer(slot: slot));
            WriteBuffer(set, layout, SdfWorldPackage.Lights, m_lightsRegion.Buffer(slot: slot));
        }
        if (group is 1 or 3) {
            WriteBuffer(set, layout, SdfWorldPackage.SkyFrame, m_skyFrameRegion.Buffer(slot: slot));
            WriteBuffer(set, layout, SdfWorldPackage.SkyStops, m_skyStopsRegion.Buffer(slot: slot));
            WriteBuffer(set, layout, SdfWorldPackage.SkySoftboxes, m_skySoftboxesRegion.Buffer(slot: slot));
            WriteBuffer(set, layout, SdfWorldPackage.SkyLayers, m_skyLayersRegion?.Buffer(slot) ?? m_dummyBuffer);
            WriteBuffer(set, layout, SdfWorldPackage.SkyAdmission, m_skyAdmissionRegion?.Buffer(slot) ?? m_dummyBuffer);
            for (var kind = 0; kind < SdfSkyKindBindings.All.Count; kind++) {
                var buffer = m_skyLayout is not null && m_skyKindIndices[kind] >= 0
                    ? m_skyKindRegions[kind]!.Buffer(slot) : m_dummyBuffer;
                WriteBuffer(set, layout, SdfSkyKindBindings.All[kind].Member.Name, buffer);
            }
        }
    }
}
