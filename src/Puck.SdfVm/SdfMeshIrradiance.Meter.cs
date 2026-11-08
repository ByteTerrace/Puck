using System.Diagnostics;
using System.Numerics;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    private Task? m_gpuMeterTask;
    // The meter's lighting rows, which the next meter rewrites once this one's job has answered.
    private Vector4[]? m_gpuMeterLightingRows;
    // The latest completed GPU reading. Exposure adapts over seconds, so a view reads its own latest row at whatever
    // position its camera now stands; only a cut (fresh) asks for a row scheduled after the view's cut.
    private GpuMeterReading? m_gpuMeter;
    // What the last scheduled meter asked: its distinct positions, the volume key and scene, and each view's cut.
    private Vector3[] m_gpuMeterAsked = [];
    private long[] m_gpuMeterAskedCuts = [];
    private (int Geometry, int Window, int Lighting, long Relit) m_gpuMeterKey;
    private SdfMeshIrradianceScene? m_gpuMeterScene;
    private SdfMeshProbeGpuWork? m_gpuMeterAskedOf;
    private long m_gpuMeterAt;
    // The frame's views as the latest Stage saw them, so TryMeter, which names a position, finds its view's row.
    private Vector3[] m_gpuMeterViewPositions = [];
    private long[] m_gpuMeterViewCuts = [];

    // One completed reading: eight words a distinct position, and for each view its row (or -1) and its cut revision.
    private sealed record GpuMeterReading(SdfMeshProbeGpuWork Client, uint[] Words, Vector3[] Positions, int[] Rows, long[] Cuts);

    // Enqueue after dropping the volume gate: service admission takes its own gate before reading this volume.
    // A converged frame also awaits its final meter, so exposure never depends on asynchronous progress.
    private void ScheduleGpuMeters(SdfFrame frame) {
        SdfMeshProbeGpuWork gpu;
        Snapshot snapshot;
        Vector3[] positions;
        int[] rows;
        long[] cuts;
        Vector4[] origins;
        Vector4[] lighting;
        int words;
        lock (m_gate) {
            var count = frame.Views.Count;
            if (m_gpuMeterViewPositions.Length != count) {
                m_gpuMeterViewPositions = new Vector3[count];
                m_gpuMeterViewCuts = new long[count];
            }
            for (var view = 0; view < count; view++) {
                m_gpuMeterViewPositions[view] = frame.Views[view].Camera.Position;
                m_gpuMeterViewCuts[view] = frame.Views[view].CutRevision;
            }
            if (GpuTrace is not { } client || m_disposed || !m_windowValid || m_gpuVolume is null) { return; }
            // A fault is Stage's to report, once, as the worker's is.
            if (m_gpuMeterTask is { IsCompleted: false } or { IsFaulted: true }) { return; }
            if (frame.ConvergesLighting && (m_owed || m_worker is { IsCompleted: false })) { return; }
            rows = new int[count];
            cuts = new long[count];
            var distinct = new List<Vector3>(count);
            for (var view = 0; view < count; view++) {
                cuts[view] = m_gpuMeterViewCuts[view];
                if (frame.Views[view].Elsewhere) { rows[view] = -1; continue; }
                var position = m_gpuMeterViewPositions[view];
                var row = distinct.IndexOf(position);
                if (row < 0) { row = distinct.Count; distinct.Add(position); }
                rows[view] = row;
            }
            if (distinct.Count == 0) { return; }
            positions = [.. distinct];
            var key = (m_geometry, m_windowVersion, m_lightingVersion, m_relit);
            var sameClient = ReferenceEquals(m_gpuMeterAskedOf, client);
            var sameScene = SdfMeshIrradianceScene.SameGpuContent(m_gpuMeterScene, m_scene);
            var cut = !cuts.AsSpan().SequenceEqual(m_gpuMeterAskedCuts);
            var moved = !positions.AsSpan().SequenceEqual(m_gpuMeterAsked);
            if (sameClient && sameScene && m_gpuMeterKey == key && !moved && !cut) { return; }
            // A meter shares the service's one admitted job with the relight, so movement and lighting are read at
            // most every LightingInterval; a cut is read at once, since its view's exposure waits for it.
            if (!frame.ConvergesLighting && !cut && Stopwatch.GetElapsedTime(m_gpuMeterAt) < LightingInterval) { return; }
            if (TakeSnapshot() is not { } captured) { return; }
            snapshot = captured;
            // Rewritten in place only here, with the one meter a volume runs at a time completed (above), so no job in
            // flight still reads the rows a new meter rewrites.
            lighting = (m_gpuMeterLightingRows = LightingRows(snapshot.Lighting, in snapshot, snapshot.Lighting.Feedback && snapshot.Layout.Bounce, rows: m_gpuMeterLightingRows));
            gpu = client;
            words = m_volume.Length;
            origins = new Vector4[positions.Length];
            for (var i = 0; i < origins.Length; i++) {
                var reuse = sameClient && sameScene && key.Item1 == m_gpuMeterKey.Geometry && i < m_gpuMeterAsked.Length && positions[i] == m_gpuMeterAsked[i];
                origins[i] = new(positions[i], reuse ? 0 : 1);
            }
            m_gpuMeterAsked = positions;
            m_gpuMeterAskedCuts = cuts;
            m_gpuMeterKey = key;
            m_gpuMeterScene = m_scene;
            m_gpuMeterAskedOf = client;
            m_gpuMeterAt = Stopwatch.GetTimestamp();
        }
        m_gpuMeterTask = ReadMeter();
        async Task ReadMeter() {
            try {
                var answer = await gpu.Meter(snapshot.Scene, origins, lighting, words, snapshot.Geometry, snapshot.WindowVersion).ConfigureAwait(false);
                // Admission checked the scene and window, and the device read the volume they stood for, so the reading
                // stands for its positions even when the window has moved on since.
                lock (m_gate) {
                    if (m_disposed || !ReferenceEquals(GpuTrace, gpu)) { return; }
                    m_gpuMeter = new GpuMeterReading(gpu, answer, positions, rows, cuts);
                }
            } catch (OperationCanceledException) {
                // Admission saw an obsolete window or a stopping device; the next Stage asks again.
                lock (m_gate) { m_gpuMeterAskedOf = null; }
            } finally { snapshot.Scene.Dispose(); }
        }
    }

    // Called under the volume gate. No hierarchy traversal, ray shading or waiting occurs on the frame thread: a view
    // reads its own latest row, at most one meter round trip old. At a cut (fresh) only a row asked after the view's cut
    // answers, so the reading after a teleport is where the camera stands, never where it stood.
    private bool TryGpuMeter(Vector3 position, Vector3 forward, bool fresh, out float luminance, out float skyVisibility, out SdfExposureMeterSource source) {
        luminance = 0;
        skyVisibility = 1;
        source = SdfExposureMeterSource.None;
        if (m_gpuMeter is not { } reading || !ReferenceEquals(reading.Client, GpuTrace)) { return false; }
        var row = -1;
        var view = Array.IndexOf(m_gpuMeterViewPositions, position);
        if (view >= 0 && view < reading.Rows.Length && (!fresh || reading.Cuts[view] == m_gpuMeterViewCuts[view])) { row = reading.Rows[view]; }
        // A position read by no view of this frame answers only from its own row.
        if (row < 0 && view < 0) { row = Array.IndexOf(reading.Positions, position); }
        if (row < 0) { return false; }
        var words = reading.Words.AsSpan(row * SdfMeshProbeRelight.MeterOutputWords, SdfMeshProbeRelight.MeterOutputWords);
        source = (words[7] & 3) switch { 1 => SdfExposureMeterSource.Probes, 2 => SdfExposureMeterSource.CameraTrace, _ => SdfExposureMeterSource.None };
        if (source == SdfExposureMeterSource.None) { return false; }
        var total = 0f;
        for (var axis = 0; axis < 6; axis++) {
            var ahead = MathF.Max(0, Vector3.Dot(Axes[axis], forward));
            var weight = MeterSurround + ahead * ahead;
            luminance += BitConverter.UInt32BitsToSingle(words[axis]) * weight;
            total += weight;
        }
        luminance /= total;
        skyVisibility = BitConverter.UInt32BitsToSingle(words[6]);
        return true;
    }
}
