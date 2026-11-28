using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Maths;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    // One receiver's three cages, replaced as a unit. A read never grows with the volume or with the camera's route.
    private readonly Dictionary<(int Level, (int X, int Y, int Z) Cell), ProbeData> m_queryProbes = [];
    private Task? m_queryTask;
    private (Vector3 Point, Vector3 Normal, int Geometry, int Window, long Relit) m_queryKey;
    private (Vector3 Point, Vector3 Normal, int Geometry, int Window, long Relit) m_queryReadingKey;
    private (Vector3 Point, Vector3 Normal) m_queryWanted;
    private bool m_queryReady;
    private SdfMeshProbeGpuWork? m_queryReadingClient;
    private ProbeBlock? m_queryHits;
    private LightBlock? m_queryLights;
    private bool m_queryReferencesReady;
    private readonly Dictionary<(int Level, (int X, int Y, int Z) Cell), string> m_queryReferences = [];

    // Call outside the gate. A missing reading schedules bounded copies and the caller reports pending, without ever
    // waiting on a GPU job from the frame thread. Only this door starts a reading; at most one is in flight.
    private bool CpuQueryReady(Vector3 point, Vector3 normal, bool references = false) {
        lock (m_gate) {
            if (GpuTrace is not { } gpu) { return true; }
            m_queryWanted = (point, normal);
            if (references && GeometrySettling) { return true; }
            references &= !m_owed && !Dirty;
            var key = (point, normal, m_geometry, m_windowVersion, m_relit);
            var ready = m_queryReady && ReferenceEquals(m_queryReadingClient, gpu) && m_queryReadingKey.Point == point && m_queryReadingKey.Normal == normal &&
                m_queryReadingKey.Geometry == m_geometry && m_queryReadingKey.Window == m_windowVersion;
            if (ready && m_queryReadingKey.Relit == m_relit && (!references || m_queryReferencesReady)) { return true; }
            if (m_disposed || !m_windowValid || m_scene is null || m_lighting is null || m_gpuVolume is null || m_queryTask is { IsCompleted: false }) { return ready; }
            var layout = m_layout;
            var lows = Lows();
            var scene = m_scene.Acquire();
            var lighting = m_lighting;
            m_queryKey = key;
            m_queryTask = Task.Run(() => ReadCpuQuery(gpu, scene, layout, lows, lighting, point, normal, key, references));
            return ready;
        }
    }

    private async Task ReadCpuQuery(SdfMeshProbeGpuWork gpu, SdfMeshIrradianceScene scene, Layout layout,
        (int X, int Y, int Z)[] lows, Lighting lighting, Vector3 point, Vector3 normal,
        (Vector3 Point, Vector3 Normal, int Geometry, int Window, long Relit) key, bool references) {
        ProbeBlock? hits = null;
        LightBlock? lights = null;
        try {
            var count = SdfMeshProbeGpuWork.ReadRecordLimit;
            var slots = new int[count];
            var cacheSlots = new uint[count];
            var cells = new (int Level, (int X, int Y, int Z) Cell)[count];
            var metadata = new ProbeData[count];
            Array.Fill(slots, -1);
            Array.Fill(cacheSlots, uint.MaxValue);
            LocateCages(layout, lows, point, normal, cells, slots);
            using (var hold = m_blocks.Enter()) {
                lock (m_gate) {
                    if (m_disposed || m_queryKey != key) { return; }
                    for (var i = 0; i < count; i++) {
                        var (level, cell) = cells[i];
                        if (slots[i] < 0 || !m_levels[level].Cache.TryGetValue(cell, out metadata[i], in hold)) { continue; }
                        cacheSlots[i] = metadata[i].GpuSlot;
                    }
                }
            }
            var result = await gpu.ReadRecords(scene, slots, cacheSlots, layout.TotalCount * SdfMeshIrradianceProbe.ProbeWords, key.Geometry, key.Window).ConfigureAwait(false);
            hits = new ProbeBlock(count, m_allocator, payloadHits: false);
            lights = new LightBlock(count, lighting.Inputs, m_allocator);
            hits.Charge(this);
            lights.Charge(this);
            var probes = new Dictionary<(int Level, (int X, int Y, int Z) Cell), ProbeData>(count);
            for (var i = 0; i < count; i++) {
                if (slots[i] < 0) { continue; }
                var output = result.Span.Slice(i * SdfMeshProbeGpuWork.ReadRecordWords, SdfMeshProbeRelight.OutputWords);
                var record = result.Span.Slice(i * SdfMeshProbeGpuWork.ReadRecordWords + SdfMeshProbeRelight.OutputWords, SdfMeshIrradianceProbe.ProbeWords);
                if (!Holds(record, cells[i].Cell)) { continue; }
                DecodeRecord(record, hits, lights, i);
                MemoryMarshal.Cast<uint, Vector3>(output.Slice(18, 18)).CopyTo(lights.Local(i));
                var xy = VectorFunctions.UnpackHalf2x16(record[SdfMeshIrradianceProbe.OffsetWord]);
                var zi = VectorFunctions.UnpackHalf2x16(record[SdfMeshIrradianceProbe.OffsetWord + 1]);
                var probe = metadata[i] with { HitsBlock = hits.Index, HitIndex = i, ShadingBlock = lights.Index, LightIndex = i,
                    Offset = new Vector3(xy, zi.X), Inside = zi.Y, Epoch = (int)(record[0] & SdfMeshIrradianceProbe.EpochMask),
                    Invalid = (record[0] & SdfMeshIrradianceProbe.InvalidFlag) != 0 };
                probes.Add(cells[i], probe);
            }
            lock (m_gate) {
                if (m_disposed || !ReferenceEquals(GpuTrace, gpu) || m_geometry != key.Geometry || m_windowVersion != key.Window) { return; }
                ReleaseCpuQuery();
                foreach (var (cell, probe) in probes) { m_queryProbes.Add(cell, probe); }
                m_queryHits = hits; hits = null;
                m_queryLights = lights; lights = null;
                m_queryReadingKey = key;
                m_queryReadingClient = gpu;
                m_queryReady = true;
            }
            if (references) {
                var descriptions = await ReadReferences(gpu, scene, layout, lows, lighting, probes, key).ConfigureAwait(false);
                lock (m_gate) {
                    if (m_disposed || m_queryReadingKey != key || m_relit != key.Relit) { return; }
                    foreach (var (cell, description) in descriptions) { m_queryReferences.Add(cell, description); }
                    m_queryReferencesReady = true;
                }
            }
        } catch (OperationCanceledException) {
            // The window or device moved before admission. The next read asks again.
        } catch (Exception exception) {
            lock (m_gate) { ContainFault("query-readback", exception); }
        } finally {
            lock (m_gate) { hits?.Release(m_blocks); lights?.Release(m_blocks); }
            scene.Dispose();
        }
    }

    private static void DecodeRecord(ReadOnlySpan<uint> record, ProbeBlock hits, LightBlock lights, int index) {
        for (var pair = 0; pair < 3; pair++) {
            var sky = VectorFunctions.UnpackHalf2x16(record[SdfMeshIrradianceProbe.SkyWord + pair]);
            hits.Sky(index)[pair * 2] = sky.X;
            hits.Sky(index)[pair * 2 + 1] = sky.Y;
        }
        var radiance = MemoryMarshal.Cast<Vector3, float>(lights.Radiance(index));
        var skyLight = MemoryMarshal.Cast<Vector3, float>(lights.SkyLight(index));
        for (var pair = 0; pair < 9; pair++) {
            var light = VectorFunctions.UnpackHalf2x16(record[SdfMeshIrradianceProbe.RadianceWord + pair]);
            var sky = VectorFunctions.UnpackHalf2x16(record[SdfMeshIrradianceProbe.SkyLightWord + pair]);
            radiance[pair * 2] = light.X; radiance[pair * 2 + 1] = light.Y;
            skyLight[pair * 2] = sky.X; skyLight[pair * 2 + 1] = sky.Y;
        }
        record.Slice(SdfMeshIrradianceProbe.LightsWord, SdfMeshIrradianceProbe.LightWords).CopyTo(lights.Lights(index));
        record.Slice(SdfMeshIrradianceProbe.DepthWord, SdfMeshIrradianceProbe.DepthWords).CopyTo(hits.Depth(index));
    }

    private static void LocateCages(Layout layout, (int X, int Y, int Z)[] lows, Vector3 point, Vector3 normal,
        Span<(int Level, (int X, int Y, int Z) Cell)> cells, Span<int> slots) {
        slots.Fill(-1);
        for (var level = layout.Finest; level < SdfMeshIrradianceProbe.Levels; level++) {
            var (_, sample) = Lattice(layout, level, lows[level], normal, point);
            var floor = new Vector3(MathF.Floor(sample.X), MathF.Floor(sample.Y), MathF.Floor(sample.Z));
            var dims = layout.DimsOf(level);
            var low = lows[level];
            for (var corner = 0; corner < 8; corner++) {
                var i = level * 8 + corner;
                var cell = ((int)floor.X + (corner & 1), (int)floor.Y + ((corner >> 1) & 1), (int)floor.Z + ((corner >> 2) & 1));
                cells[i] = (level, cell);
                if (cell.Item1 >= low.X + dims.X || cell.Item2 >= low.Y + dims.Y || cell.Item3 >= low.Z + dims.Z) { continue; }
                slots[i] = layout.OffsetOf(level) + SlotOf(cell.Item1, cell.Item2, cell.Item3, dims);
            }
        }
    }

    // Under the volume gate. Readers outside it retain the blocks through the existing reader epochs.
    private void ReleaseCpuQuery() {
        m_queryProbes.Clear();
        m_queryReferences.Clear();
        m_queryReferencesReady = false;
        m_queryHits?.Release(m_blocks); m_queryHits = null;
        m_queryLights?.Release(m_blocks); m_queryLights = null;
        m_queryReady = false;
        m_queryReadingClient = null;
    }
}
