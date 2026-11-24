namespace Puck.SdfVm;

internal sealed partial class SdfMeshProbeGpuWork {
    // The scene words this client's queries read lie in the probe scenes its tables' mesh region holds for every
    // residency attached to it (SdfMeshProbeScenes): the upload under way, the export published there, whose root word
    // base each traced dispatch pushes, and the store, taken at the first upload.
    private SdfMeshProbeScenes.Upload? m_sceneUpload;
    private SdfMeshIrradianceScene.GpuExport? m_sceneExport;
    private SdfMeshProbeScenes? m_sceneStore;
    private long m_sceneUploads;
    private long m_sceneWrittenBytes;
    private long m_sceneLastBytes;

    // No job of this client carries scene words: the residencies' uploads do (SdfMeshProbeScenes.RecordOwed).
    public int UploadBytes(object job) => 0;

    // Begins the upload of a scene. Only with no query in flight, and none is taken while it runs, so no query reads a
    // root word base other than the published one. Under the gate.
    private void BeginSceneUpload(SdfMeshIrradianceScene scene, IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, long version) {
        if ((m_sceneStore ??= m_tables.ProbeScenes) is not { } store) { return; }
        m_sceneUpload = store.Begin(claimant: this, placementVersion: version, placements: placements, rooted: m_tables.GpuWork.Wake, scene: scene);
    }
    // Serves the upload under way: publishes it once its root words have landed, the one door a scene is published
    // through, or withdraws one the volume no longer keeps. True while it is still under way, when no query is taken.
    // Under the gate.
    private bool ServeSceneUpload() {
        if (m_sceneUpload is not { } upload) { return false; }
        if (upload.Work.IsFaulted && !upload.Withdrawn) { upload.Work.GetAwaiter().GetResult(); }
        if (!upload.Withdrawn && !upload.Cancelled && m_volume.KeepsGpuScene(upload.Scene)) {
            if (!upload.Rooted) { return true; }
            // No query is in flight (a scene upload begins only then, and none is taken while it runs), so the prefix
            // the store frees as it publishes is read by nothing.
            m_sceneStore!.Publish(upload: upload, previous: m_sceneExport);
            m_sceneExport = upload.Export;
            m_scene?.Dispose();
            m_scene = upload.Scene.Acquire();
            m_scenePlacements = upload.Placements;
            m_placementVersion = upload.PlacementVersion;
            m_sceneUploads++;
            m_sceneLastBytes = upload.Bytes;
            m_sceneWrittenBytes += upload.Bytes;
            upload.Dispose();
            m_sceneUpload = null;
            return false;
        }
        // Stop producing now; the store frees the withdrawn prefix and keeps the completed geometry for the next scene, so
        // roster churn cannot restart it forever. No frame thread joins the worker: it is disposed once it has stopped.
        m_sceneStore!.Withdraw(upload: upload);
        if (!upload.Work.IsCompleted) { return true; }
        upload.Dispose();
        m_sceneUpload = null;
        return false;
    }
    // Lets go of the store's words once the client's submissions have completed (Dispose).
    private void ReleaseScene() {
        if (m_sceneUpload is { } upload) {
            m_sceneStore?.Withdraw(upload: upload);
            upload.Dispose();
            m_sceneUpload = null;
        }
        m_sceneStore?.Release(claimant: this, published: m_sceneExport);
        m_sceneExport = null;
    }
}
