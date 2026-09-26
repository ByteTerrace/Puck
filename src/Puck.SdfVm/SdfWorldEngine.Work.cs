using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    // Pass indices into PassLabelTable, in submission order.
    private const int UploadPass = 0;
    private const int SkyPass = 1;
    private const int MaskPass = 2;
    private const int BeamPass = 3;
    private const int CullArgsPass = 4;
    private const int MeshPass = 5;
    private const int PrimaryPass = 6;
    private const int SurfacePass = 7;
    private const int AmbientPass = 8;
    private const int ViewsPass = 9;

    private static readonly WorkClass[] PassClassTable = BuildPassClasses();

    // The ledger every wrapped GPU service counts into: the owner's (a node or view that outlives device-loss rebuilds)
    // or the engine's own.
    private readonly GpuWorkLedger m_work;

    // The pass configuration's revision: one more for every UploadProgram and every InstallReload that installs a
    // pipeline, so a sample says which program and kernel set its counts ran under.
    private long m_workRevision;

    /// <summary>Gets the labels of the passes a cadence-skipped frame does not run, in pass order: every pass but
    /// <c>upload</c>, which copies whatever changed in the frame's tables whatever the passes do with them, while each
    /// view's retained output stands.</summary>
    public static ReadOnlySpan<string> CadenceSkippedPassLabels =>
        PassLabelTable.AsSpan(
            length: ((ViewsPass - SkyPass) + 1),
            start: SkyPass
        );
    /// <summary>Gets the render passes' labels, in submission order — the GPU work ledger's per-pass column names
    /// (<see cref="Work"/>) and the width a caller sizes a per-pass read to.</summary>
    public static ReadOnlySpan<string> PassLabels => PassLabelTable;
    /// <summary>Gets what two runs of each pass may be held to agree on, in <see cref="PassLabels"/> order:
    /// <c>upload</c> is <see cref="WorkClass.PerBackendDeterministic"/>, because what it writes and copies follows the
    /// residency policy each device's memory profile selects, and every other pass is
    /// <see cref="WorkClass.Deterministic"/>.</summary>
    public static ReadOnlySpan<WorkClass> PassClasses => PassClassTable;
    /// <summary>Gets the GPU work this engine recorded: per pass, for the newest submission known to have completed.
    /// A frame's host-visible uploads, brick uploads and bakes, and the barriers before its first pass are counted
    /// outside every pass. A pass the cadence gate skipped reads skipped, not zero.</summary>
    public IGpuWorkSource Work =>
        m_work;
    /// <summary>Gets the GPU objects this engine's ledger has seen created (pipelines, shader modules, images,
    /// buffers, descriptor pools and sets), over the ledger's whole life.</summary>
    public IWorkCounterSource WorkLifetime =>
        m_work;

    private static WorkClass[] BuildPassClasses() {
        var classes = new WorkClass[(ViewsPass + 1)];

        classes[UploadPass] = WorkClass.PerBackendDeterministic;

        return classes;
    }
    private void ReconfigureWork() {
        m_workRevision++;
        m_work.Configure(
            passClasses: PassClassTable,
            passLabels: PassLabelTable,
            revision: m_workRevision
        );
    }
}
