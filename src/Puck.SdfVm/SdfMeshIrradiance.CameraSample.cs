using Puck.Abstractions.Collections;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradiance {
    private Task? m_gpuCameraSampleTask;
    // The camera sample's lighting rows, which the next sample rewrites once this one's job has answered.
    private Vector4[]? m_gpuCameraSampleLightingRows;
    // The latest completed GPU reading. Exposure adapts over seconds, so a view reads its own latest row at whatever
    // position its camera now stands; only a cut (fresh) asks for a row scheduled after the view's cut.
    private GpuCameraSampleReading? m_gpuCameraSample;
    // What the last scheduled camera sample asked: its distinct positions, the volume key and scene, and each view's cut.
    private Vector3[] m_gpuCameraSampleAsked = [];
    private ReferenceRevision[] m_gpuCameraSampleAskedCuts = [];
    private (int Geometry, int Window, int Lighting, long Relit) m_gpuCameraSampleKey;
    private SdfMeshIrradianceScene? m_gpuCameraSampleScene;
    private SdfMeshProbeGpuWork? m_gpuCameraSampleAskedOf;
    private long m_gpuCameraSampleAt;
    // The frame's views as the latest Stage saw them, so TrySampleCamera, which names a position, finds its view's row.
    private Vector3[] m_gpuCameraSampleViewPositions = [];
    private ReferenceRevision[] m_gpuCameraSampleViewCuts = [];

    // One completed reading: SdfMeshProbeRelight.CameraSampleOutputWords words a distinct position, and for each view its row
    // (or -1) and its cut revision.
    private sealed record GpuCameraSampleReading(SdfMeshProbeGpuWork Client, uint[] Words, Vector3[] Positions, int[] Rows, ReferenceRevision[] Cuts);

    // Enqueue after dropping the volume gate: service admission takes its own gate before reading this volume.
    // A converged frame also awaits its final camera sample, so its lighting never depends on asynchronous progress.
    private void ScheduleGpuCameraSamples(SdfFrame frame) {
        SdfMeshProbeGpuWork gpu;
        Snapshot snapshot;
        Vector3[] positions;
        int[] rows;
        ReferenceRevision[] cuts;
        Vector4[] origins;
        Vector4[] lighting;
        int words;
        lock (m_gate) {
            var count = frame.Views.Count;
            if (m_gpuCameraSampleViewPositions.Length != count) {
                m_gpuCameraSampleViewPositions = new Vector3[count];
                m_gpuCameraSampleViewCuts = new ReferenceRevision[count];
            }
            for (var view = 0; view < count; view++) {
                m_gpuCameraSampleViewPositions[view] = frame.Views[view].Camera.Position;
                m_gpuCameraSampleViewCuts[view] = frame.Views[view].Cut;
            }
            if (GpuTrace is not { } client || m_disposed || !m_windowValid || m_gpuVolume is null) { return; }
            // A fault is Stage's to report, once, as the worker's is.
            if (m_gpuCameraSampleTask is { IsCompleted: false } or { IsFaulted: true }) { return; }
            if (frame.ConvergesLighting && (m_owed || m_worker is { IsCompleted: false })) { return; }
            rows = new int[count];
            cuts = new ReferenceRevision[count];
            var distinct = new List<Vector3>(count);
            for (var view = 0; view < count; view++) {
                cuts[view] = m_gpuCameraSampleViewCuts[view];
                if (frame.Views[view].Elsewhere) { rows[view] = -1; continue; }
                var position = m_gpuCameraSampleViewPositions[view];
                var row = distinct.IndexOf(position);
                if (row < 0) { row = distinct.Count; distinct.Add(position); }
                rows[view] = row;
            }
            if (distinct.Count == 0) { return; }
            positions = [.. distinct];
            var key = (m_geometry, m_windowVersion, m_lightingVersion, m_relit);
            var sameClient = ReferenceEquals(m_gpuCameraSampleAskedOf, client);
            var sameScene = SdfMeshIrradianceScene.SameGpuContent(m_gpuCameraSampleScene, m_scene);
            var cut = !cuts.AsSpan().SequenceEqual(m_gpuCameraSampleAskedCuts);
            var moved = !positions.AsSpan().SequenceEqual(m_gpuCameraSampleAsked);
            if (sameClient && sameScene && m_gpuCameraSampleKey == key && !moved && !cut) { return; }
            // A camera sample shares the service's one admitted job with the relight, so movement and lighting are read at
            // most every LightingInterval; a cut is read at once, since its view's exposure waits for it.
            if (!frame.ConvergesLighting && !cut && Time.GetElapsedTime(m_gpuCameraSampleAt) < LightingInterval) { return; }
            if (TakeSnapshot() is not { } captured) { return; }
            snapshot = captured;
            // Rewritten in place only here, with the one camera sample a volume runs at a time completed (above), so no job in
            // flight still reads the rows a new camera sample rewrites.
            lighting = (m_gpuCameraSampleLightingRows = LightingRows(snapshot.Lighting, in snapshot, snapshot.Lighting.Feedback && snapshot.Layout.Bounce, rows: m_gpuCameraSampleLightingRows));
            gpu = client;
            words = m_volume.Length;
            origins = new Vector4[positions.Length];
            for (var i = 0; i < origins.Length; i++) {
                var reuse = sameClient && sameScene && key.Item1 == m_gpuCameraSampleKey.Geometry && i < m_gpuCameraSampleAsked.Length && positions[i] == m_gpuCameraSampleAsked[i];
                origins[i] = new(positions[i], reuse ? 0 : 1);
            }
            m_gpuCameraSampleAsked = positions;
            m_gpuCameraSampleAskedCuts = cuts;
            m_gpuCameraSampleKey = key;
            m_gpuCameraSampleScene = m_scene;
            m_gpuCameraSampleAskedOf = client;
            m_gpuCameraSampleAt = Time.GetTimestamp();
        }
        m_gpuCameraSampleTask = ReadCameraSample(gpu, snapshot, origins, lighting, words, positions, rows, cuts);
    }
    // A method of its own rather than a local function of ScheduleGpuCameraSamples: a local function that captures the schedule's
    // locals makes the compiler allocate their closure on entry, on every frame, though most frames return before they
    // are set.
    private async Task ReadCameraSample(SdfMeshProbeGpuWork gpu, Snapshot snapshot, Vector4[] origins, Vector4[] lighting, int words, Vector3[] positions, int[] rows, ReferenceRevision[] cuts) {
        try {
            var answer = await gpu.CameraSample(snapshot.Scene, origins, lighting, words, snapshot.Geometry, snapshot.WindowVersion).ConfigureAwait(false);
            // Admission checked the scene and window, and the device read the volume they stood for, so the reading
            // stands for its positions even when the window has moved on since.
            lock (m_gate) {
                if (m_disposed || !ReferenceEquals(GpuTrace, gpu)) { return; }
                m_gpuCameraSample = new GpuCameraSampleReading(gpu, answer, positions, rows, cuts);
            }
        } catch (OperationCanceledException) {
            // Admission saw an obsolete window or a stopping device; the next Stage asks again.
            lock (m_gate) { m_gpuCameraSampleAskedOf = null; }
        } finally { snapshot.Scene.Dispose(); }
    }

    /// <summary>Names what a view's camera sample waits for while it has none, for <c>world.exposure</c>: <c>window</c>
    /// before the volume has a window, <c>scene-build</c> before the volume's scene is built (which the CPU trace answers
    /// from), <c>cpu</c> when the volume samples on the CPU alone; and for the device's camera sample, <c>scene</c> or
    /// <c>scene-upload</c> before the device holds the volume's scene, <c>unpublished</c> before it holds a publication of
    /// the volume, <c>camera-sample</c> while a camera sample is in flight, <c>unasked</c> before any answered, and <c>cut</c> while
    /// the answered camera sample was asked before the view's latest cut.</summary>
    /// <returns>The wait's name.</returns>
    internal string CameraSampleWait() {
        // The client's gate is never taken under this one: its admission takes this gate under its own.
        var client = GpuTrace;
        var scene = client?.SceneWait;
        lock (m_gate) {
            if (!m_windowValid) { return "window"; }
            if (m_scene is null) { return "scene-build"; }
            if (client is null) { return "cpu"; }
            if (scene is not null) { return scene; }
            if (m_gpuVolume is null) { return "unpublished"; }
            if (m_gpuCameraSampleTask is { IsCompleted: false }) { return "camera-sample"; }
            return ((m_gpuCameraSample is null) ? "unasked" : "cut");
        }
    }
    // Called under the volume gate. No hierarchy traversal, ray shading or waiting occurs on the frame thread once the
    // device has answered (before that, TrySampleCamera traces the camera on the CPU): a view reads its own latest row, at
    // most one camera sample round trip old. At a cut (fresh) only a row asked after the view's cut
    // answers, so the reading after a teleport is where the camera stands, never where it stood.
    private bool TryGpuCameraSample(Vector3 position, bool fresh, Span<Vector3> light, out float skyVisibility, out SdfCameraSampleSource source) {
        skyVisibility = 1;
        source = SdfCameraSampleSource.None;
        if (m_gpuCameraSample is not { } reading || !ReferenceEquals(reading.Client, GpuTrace)) { return false; }
        var row = -1;
        var view = Array.IndexOf(m_gpuCameraSampleViewPositions, position);
        if (view >= 0 && view < reading.Rows.Length && (!fresh || reading.Cuts[view] == m_gpuCameraSampleViewCuts[view])) { row = reading.Rows[view]; }
        // A position read by no view of this frame answers only from its own row.
        if (row < 0 && view < 0) { row = Array.IndexOf(reading.Positions, position); }
        if (row < 0) { return false; }
        var words = reading.Words.AsSpan(row * SdfMeshProbeRelight.CameraSampleOutputWords, SdfMeshProbeRelight.CameraSampleOutputWords);
        source = (words[^1] & 3) switch { 1 => SdfCameraSampleSource.Probes, 2 => SdfCameraSampleSource.CameraTrace, _ => SdfCameraSampleSource.None };
        if (source == SdfCameraSampleSource.None) { return false; }
        skyVisibility = BitConverter.UInt32BitsToSingle(words[^2]);
        if (!light.IsEmpty) { MemoryMarshal.Cast<uint, Vector3>(words[..18]).CopyTo(light); }
        return true;
    }
}
