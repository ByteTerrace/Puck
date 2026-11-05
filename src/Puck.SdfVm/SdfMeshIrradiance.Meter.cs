using System.Diagnostics;
using System.Numerics;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    private Task? m_gpuMeterTask;
    private Vector3[] m_gpuMeterPositions = [];
    private uint[] m_gpuMeterWords = [];
    private (int Geometry, int Window, int Lighting, long Relit) m_gpuMeterKey;
    private SdfMeshIrradianceScene? m_gpuMeterScene;
    private SdfMeshProbeGpuWork? m_gpuMeterClient;
    private long m_gpuMeterAt;

    // Enqueue after dropping the volume gate: service admission takes its own gate before reading this volume.
    // A converged frame also awaits its final meter, so exposure never depends on asynchronous progress.
    private void ScheduleGpuMeters(SdfFrame frame) {
        SdfMeshProbeGpuWork gpu;
        Snapshot snapshot;
        Vector3[] positions;
        Vector4[] origins;
        (int Geometry, int Window, int Lighting, long Relit) key;
        int words;
        lock (m_gate) {
            if (GpuTrace is not { } client || m_disposed || !m_windowValid || m_gpuVolume is null) { return; }
            if (m_gpuMeterTask is { IsFaulted: true } failed) { throw new InvalidOperationException("The GPU exposure meter failed.", failed.Exception); }
            if (m_gpuMeterTask is { IsCompleted: false }) { return; }
            if (frame.ConvergesLighting && (m_owed || m_worker is { IsCompleted: false })) { return; }
            if (!frame.ConvergesLighting && Stopwatch.GetElapsedTime(m_gpuMeterAt) < LightingInterval) { return; }
            positions = frame.Views.Where(view => !view.Elsewhere).Select(view => view.Camera.Position).Distinct().ToArray();
            if (positions.Length == 0) { return; }
            key = (m_geometry, m_windowVersion, m_lightingVersion, m_relit);
            var sameClient = ReferenceEquals(m_gpuMeterClient, client);
            var sameScene = ReferenceEquals(m_gpuMeterScene, m_scene);
            if (sameClient && sameScene && m_gpuMeterKey == key && positions.AsSpan().SequenceEqual(m_gpuMeterPositions)) { return; }
            if (TakeSnapshot() is not { } captured) { return; }
            snapshot = captured;
            gpu = client;
            words = m_volume.Length;
            origins = new Vector4[positions.Length];
            for (var i = 0; i < origins.Length; i++) {
                var reuse = sameClient && sameScene && key.Geometry == m_gpuMeterKey.Geometry && i < m_gpuMeterPositions.Length && positions[i] == m_gpuMeterPositions[i];
                origins[i] = new(positions[i], reuse ? 0 : 1);
            }
            m_gpuMeterAt = Stopwatch.GetTimestamp();
        }
        m_gpuMeterTask = ReadMeter();
        async Task ReadMeter() {
            try {
                var rows = LightingRows(snapshot.Lighting, in snapshot, snapshot.Lighting.Feedback && snapshot.Layout.Bounce);
                var answer = await gpu.Meter(snapshot.Scene, origins, rows, words, snapshot.Geometry, snapshot.WindowVersion).ConfigureAwait(false);
                lock (m_gate) {
                    if (m_disposed || !ReferenceEquals(GpuTrace, gpu) || !Stands(in snapshot)) { m_gpuMeterClient = null; return; }
                    m_gpuMeterWords = answer;
                    m_gpuMeterPositions = positions;
                    m_gpuMeterKey = key;
                    m_gpuMeterScene = snapshot.Scene;
                    m_gpuMeterClient = gpu;
                }
            } catch (OperationCanceledException) {
                // Admission saw an obsolete window or a stopping device. Stage retries the current one.
                lock (m_gate) { m_gpuMeterClient = null; }
            } finally { snapshot.Scene.Dispose(); }
        }
    }

    // Called under the volume gate. No hierarchy traversal, ray shading or waiting occurs on the frame thread.
    private bool TryGpuMeter(Vector3 position, Vector3 forward, out float luminance, out float skyVisibility, out SdfExposureMeterSource source) {
        luminance = 0;
        skyVisibility = 1;
        source = SdfExposureMeterSource.None;
        if (!ReferenceEquals(m_gpuMeterClient, GpuTrace) || !ReferenceEquals(m_gpuMeterScene, m_scene) || m_gpuMeterKey.Geometry != m_geometry || m_gpuMeterKey.Window != m_windowVersion) { return false; }
        var index = Array.IndexOf(m_gpuMeterPositions, position);
        if (index < 0) { return false; }
        var row = m_gpuMeterWords.AsSpan(index * SdfMeshProbeRelight.MeterOutputWords, SdfMeshProbeRelight.MeterOutputWords);
        source = (row[7] & 3) switch { 1 => SdfExposureMeterSource.Probes, 2 => SdfExposureMeterSource.CameraTrace, _ => SdfExposureMeterSource.None };
        if (source == SdfExposureMeterSource.None) { return false; }
        var total = 0f;
        for (var axis = 0; axis < 6; axis++) {
            var ahead = MathF.Max(0, Vector3.Dot(Axes[axis], forward));
            var weight = MeterSurround + ahead * ahead;
            luminance += BitConverter.UInt32BitsToSingle(row[axis]) * weight;
            total += weight;
        }
        luminance /= total;
        skyVisibility = BitConverter.UInt32BitsToSingle(row[6]);
        return true;
    }
}
