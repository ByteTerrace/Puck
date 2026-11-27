using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh path's irradiance table (SdfMeshIrradiance): SdfMeshIrradianceProbe.ProbeWords words a probe, the slots of the
// active tier's volume, read by the views pass through the World set (sdfMeshIrradiance). It is the probe service's
// feedback table itself (SdfMeshProbeGpuWork), which its relights read and commit into, so the volume stands once on the
// device: a publication binds that table and moves the irradiance revision, and copies nothing. Until the volume first
// publishes into these tables the World set binds the dummy buffer and the volume's parameters are disabled
// (SdfMeshIrradiance.Stage).
//
// Invariant: a feedback table is disposed only once no submission that reads it can still run: the probe jobs that
// recorded against it and every view whose World set bound it. Enforced at the one door the probe service retires a
// table through (RetireBackgroundResource): a table no World set binds is held behind the jobs submitted before it; a
// bound one is held until the World sets stop naming it, which happens only at a preparation (SettleMeshIrradiance,
// since a set is rewritten only in a frame's pack or upload), and the upload carrying that unbinding has reached the
// queue, after which the retirement's checkpoint follows every view that bound it.
//
// A view reads the feedback as the device last wrote it, which may run a few frames ahead of the published parameters
// (a commit that landed but was not yet published); every slot carries the cell it holds and the views reject a slot of
// another cell, and a still view stands until a publication moves the irradiance revision its signature folds. The
// probe service hands the table to the views' stages (TableReaders) after every write it records.
public sealed partial class SdfWorldTables {
    // The feedback table the World sets bind, or null for the dummy buffer; and the bound table its probe service retired,
    // which the next preparation unbinds.
    private IGpuBuffer? m_meshIrradiance;
    private UnboundTable? m_meshIrradianceUnbinding;
    // Moves with every publication, which changes what the passes that gather the volume read behind one binding: the
    // tables' signature and the lighting passes' signature fold it (UpdateTablesSignature).
    private long m_meshIrradianceRevision;

    /// <summary>Gets the device bytes of the irradiance table the views read: the probe service's feedback table, which
    /// the service's own bytes count (<see cref="SdfMeshProbeGpuWork.OwnedBytes"/>), so no total adds them again.</summary>
    public GpuMemoryBytes MeshIrradianceBufferBytes => new(DeviceLocal: m_meshIrradiance?.SizeBytes ?? 0, HostVisible: 0);
    /// <summary>Gets whether the World sets bind an irradiance table: the views may read the volume.</summary>
    internal bool BindsMeshIrradiance => (m_meshIrradiance is not null);
    /// <summary>Gets the residency's reservation of the lighting's device storage.</summary>
    public SdfLightingHold Lighting => m_lighting;

    // Called at a publication, in the frame's pack: binds the feedback table the published slots lie in.
    internal void PublishMeshIrradiance(IGpuBuffer feedback) {
        if (!ReferenceEquals(objA: m_meshIrradiance, objB: feedback)) {
            m_meshIrradiance = feedback;
            m_bindingRevision++;
        }
        m_meshIrradianceRevision++;
    }
    // Called at every preparation before the volume stages, in the frame's pack: stops binding a table its probe service
    // retired, so its retirement can follow the upload this preparation leads to.
    internal void SettleMeshIrradiance() {
        if (m_meshIrradianceUnbinding is not { } unbinding) { return; }
        m_meshIrradianceUnbinding = null;
        if (ReferenceEquals(objA: m_meshIrradiance, objB: unbinding.Buffer)) {
            m_meshIrradiance = null;
            m_bindingRevision++;
        }
        unbinding.Revision = m_bindingRevision;
    }

    // A bound table its probe service retired: held until the upload that carries its unbinding (Revision, set by
    // SettleMeshIrradiance) has reached the queue.
    private sealed class UnboundTable(IGpuBuffer buffer) {
        public IGpuBuffer Buffer { get; } = buffer;
        public long Revision = long.MaxValue;
    }
}
