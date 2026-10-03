using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The lights table and the sky's block and tables: four regions of generated records (SdfKernelInterfaces.LightAndSkyTables),
// packed from each frame's SdfLights and SdfSky with their host bakes and written whole, so each owes only the words
// that changed. The World set binds them like every table, and only the passes that read them reference them: the
// lights the shadow and views passes, the sky block and stops the sky and views passes, the softboxes views alone.
public sealed partial class SdfWorldTables {
    private const int LightRegionIndex = 9;
    private const int SkyRegionIndex = 10;
    private const int SkyStopRegionIndex = 11;
    private const int SoftboxRegionIndex = 12;

    private readonly SdfLight[] m_lightRecords = new SdfLight[SdfLights.MaxLights];
    private readonly SdfSkyBlock[] m_skyRecord = new SdfSkyBlock[1];
    private readonly SdfSkyStop[] m_skyStopRecords = new SdfSkyStop[SdfSky.MaxStops];
    private readonly SdfSoftbox[] m_softboxRecords = new SdfSoftbox[SdfSky.MaxSoftboxes];

    private readonly GpuRegion m_lightRegion;
    private readonly GpuRegion m_skyRegion;
    private readonly GpuRegion m_skyStopRegion;
    private readonly GpuRegion m_softboxRegion;

    // Packs a frame's lights and sky into the four regions.
    private void PackLightsAndSky(SdfFrame frame) {
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
    // Writes the four regions' buffers in a ring slot, and the sky's environment map every slot shares, into a World set.
    private void WriteLightAndSkySet(nint set, int slot) {
        WriteWorldBuffer(buffer: m_lightRegion.Buffer(slot: slot), member: SdfKernelInterfaces.Lights, set: set);
        WriteWorldBuffer(buffer: m_skyRegion.Buffer(slot: slot), member: SdfKernelInterfaces.Sky, set: set);
        WriteWorldBuffer(buffer: m_skyStopRegion.Buffer(slot: slot), member: SdfKernelInterfaces.SkyStops, set: set);
        WriteWorldBuffer(buffer: m_softboxRegion.Buffer(slot: slot), member: SdfKernelInterfaces.Softboxes, set: set);
        WriteWorldBuffer(buffer: m_skyEnvironment.Map, member: SdfKernelInterfaces.SkyEnvironment, set: set);
    }
    // The bytes of a whole table of records.
    private static int RecordBytes<T>(T[] records) where T : unmanaged =>
        MemoryMarshal.AsBytes(span: records.AsSpan()).Length;
}
