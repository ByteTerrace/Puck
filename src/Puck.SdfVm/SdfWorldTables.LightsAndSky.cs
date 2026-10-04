using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The lights and sky's three record tables (the lights, the sky block and its layer table) owe only changed
// words. Active handoff controls reserve their own staged
// region and upload whole records. The World set binds each generated record table, and a kernel references only
// the tables its pass reads.
public sealed partial class SdfWorldTables {
    private const int LightRegionIndex = 9;
    private const int SkyRegionIndex = 10;
    private const int SkyLayerRegionIndex = 11;
    private const int ShadowHandoffRegionIndex = 12;

    private readonly SdfLight[] m_lightRecords = new SdfLight[SdfLights.MaxLights];
    private readonly SdfSkyBlock[] m_skyRecord = new SdfSkyBlock[1];
    private readonly SdfSkyLayer[] m_skyLayerRecords = new SdfSkyLayer[SdfSky.MaxLayers];
    private readonly int[] m_skyAuthoredIndices = new int[SdfSky.MaxLayers];

    private readonly GpuRegion m_lightRegion;
    private readonly GpuRegion m_skyRegion;
    private readonly GpuRegion m_skyLayerRegion;
    // The detail rows the sky's layers count in, shared across the composition's residencies.
    private readonly SdfSkyDetails m_skyDetails;

    private readonly SdfShadowHandoff[] m_shadowHandoffs = new SdfShadowHandoff[SdfShadowSlots.MaxFadeSlots];

    private readonly IGpuBuffer m_shadowHandoffBuffer;
    private readonly GpuRegion m_shadowHandoffRegion;

    private int m_shadowHandoffCount;
    private int m_shadowFadeCapacity;

    /// <summary>Gets the detail rows the sky, composite and environment passes count the sky's runs and layers in, in row
    /// order.</summary>
    public SdfSkyDetails SkyDetails => m_skyDetails;

    /// <summary>Gets each packed sky layer's authored index, followed by −1 for unused rows. Muted and
    /// tier-excluded layers consume no packed row; counter detail identities are composition-wide, not packed ordinals.</summary>
    public ReadOnlySpan<int> SkyAuthoredIndices => m_skyAuthoredIndices;

    // Packs a frame's lights and sky and retains its active controls until the counted upload.
    private void PackLightsAndSky(SdfFrame frame) {
        m_shadowHandoffCount = frame.Lights.ShadowSlots.FadeCount;
        m_shadowFadeCapacity = frame.Lights.ShadowSlots.FadeCapacity;
        frame.Lights.ShadowSlots.Handoffs.CopyTo(destination: m_shadowHandoffs);
        frame.Lights.Pack(records: m_lightRecords);
        frame.Sky.Pack(
            authoredIndices: m_skyAuthoredIndices,
            block: out m_skyRecord[0],
            details: m_skyDetails,
            farDistance: frame.FarDistance,
            layers: m_skyLayerRecords,
            lights: frame.Lights
        );
        _ = m_lightRegion.Write(bytes: MemoryMarshal.AsBytes(span: m_lightRecords.AsSpan()), offset: 0);
        _ = m_skyRegion.Write(bytes: MemoryMarshal.AsBytes(span: m_skyRecord.AsSpan()), offset: 0);
        _ = m_skyLayerRegion.Write(bytes: MemoryMarshal.AsBytes(span: m_skyLayerRecords.AsSpan()), offset: 0);
    }
    // Writes the generated record buffers and the shared sky environment map into a ring slot's World set.
    private void WriteLightAndSkySet(nint set, int slot) {
        WriteWorldBuffer(buffer: m_lightRegion.Buffer(slot: slot), member: SdfKernelInterfaces.Lights, set: set);
        WriteWorldBuffer(buffer: m_skyRegion.Buffer(slot: slot), member: SdfKernelInterfaces.Sky, set: set);
        WriteWorldBuffer(buffer: m_skyLayerRegion.Buffer(slot: slot), member: SdfKernelInterfaces.SkyLayers, set: set);
        WriteWorldBuffer(buffer: m_skyEnvironment.Coefficients, member: SdfKernelInterfaces.SkyCoefficients, set: set);
        WriteWorldBuffer(buffer: m_skyEnvironment.Map, member: SdfKernelInterfaces.SkyEnvironment, set: set);
        WriteWorldBuffer(buffer: m_shadowHandoffBuffer, member: SdfKernelInterfaces.ShadowHandoffs, set: set);
    }
    // An external destination starts owing nothing. Retargeting before each active upload makes every live record's
    // four words owed, including unchanged indices, without copying inactive records or allocating at a crossing.
    private void StageShadowHandoffs() {
        if (m_shadowHandoffCount == 0) {
            return;
        }
        m_shadowHandoffRegion.Target(destinationWord: 0);
        _ = m_shadowHandoffRegion.Write(offset: 0, bytes: MemoryMarshal.AsBytes(span: m_shadowHandoffs.AsSpan(length: m_shadowHandoffCount, start: 0)));
    }
    // The bytes of a whole table of records.
    private static int RecordBytes<T>(T[] records) where T : unmanaged =>
        MemoryMarshal.AsBytes(span: records.AsSpan()).Length;
}
