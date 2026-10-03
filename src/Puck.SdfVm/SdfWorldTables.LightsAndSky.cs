using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The lights and sky's four record tables owe only changed words. Active handoff controls reserve their own staged
// region and upload whole records. The World set binds each generated record table, and a kernel references only
// the tables its pass reads.
public sealed partial class SdfWorldTables {
    private const int LightRegionIndex = 9;
    private const int SkyRegionIndex = 10;
    private const int SkyStopRegionIndex = 11;
    private const int SoftboxRegionIndex = 12;
    private const int ShadowHandoffRegionIndex = 13;

    private readonly SdfLight[] m_lightRecords = new SdfLight[SdfLights.MaxLights];
    private readonly SdfSkyBlock[] m_skyRecord = new SdfSkyBlock[1];
    private readonly SdfSkyStop[] m_skyStopRecords = new SdfSkyStop[SdfSky.MaxStops];
    private readonly SdfSoftbox[] m_softboxRecords = new SdfSoftbox[SdfSky.MaxSoftboxes];

    private readonly GpuRegion m_lightRegion;
    private readonly GpuRegion m_skyRegion;
    private readonly GpuRegion m_skyStopRegion;
    private readonly GpuRegion m_softboxRegion;

    private readonly SdfShadowHandoff[] m_shadowHandoffs = new SdfShadowHandoff[SdfShadowSlots.MaxFadeSlots];

    private readonly IGpuBuffer m_shadowHandoffBuffer;
    private readonly GpuRegion m_shadowHandoffRegion;

    private int m_shadowHandoffCount;
    private int m_shadowFadeCapacity;

    // Packs a frame's lights and sky and retains its active controls until the counted upload.
    private void PackLightsAndSky(SdfFrame frame) {
        m_shadowHandoffCount = frame.Lights.ShadowSlots.FadeCount;
        m_shadowFadeCapacity = frame.Lights.ShadowSlots.FadeCapacity;
        frame.Lights.ShadowSlots.Handoffs.CopyTo(destination: m_shadowHandoffs);
        frame.Lights.Pack(records: m_lightRecords);
        frame.Sky.Pack(
            block: out m_skyRecord[0],
            lights: frame.Lights,
            softboxes: m_softboxRecords,
            stops: m_skyStopRecords
        );
        _ = m_lightRegion.Write(bytes: MemoryMarshal.AsBytes(span: m_lightRecords.AsSpan()), offset: 0);
        _ = m_skyRegion.Write(bytes: MemoryMarshal.AsBytes(span: m_skyRecord.AsSpan()), offset: 0);
        _ = m_skyStopRegion.Write(bytes: MemoryMarshal.AsBytes(span: m_skyStopRecords.AsSpan()), offset: 0);
        _ = m_softboxRegion.Write(bytes: MemoryMarshal.AsBytes(span: m_softboxRecords.AsSpan()), offset: 0);
    }
    // Writes the generated record buffers and the shared sky environment map into a ring slot's World set.
    private void WriteLightAndSkySet(nint set, int slot) {
        WriteWorldBuffer(buffer: m_lightRegion.Buffer(slot: slot), member: SdfKernelInterfaces.Lights, set: set);
        WriteWorldBuffer(buffer: m_skyRegion.Buffer(slot: slot), member: SdfKernelInterfaces.Sky, set: set);
        WriteWorldBuffer(buffer: m_skyStopRegion.Buffer(slot: slot), member: SdfKernelInterfaces.SkyStops, set: set);
        WriteWorldBuffer(buffer: m_softboxRegion.Buffer(slot: slot), member: SdfKernelInterfaces.Softboxes, set: set);
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
