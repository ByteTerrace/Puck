using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    // The passes an upload counts, in recording order: the fillers' first transitions and clears, the regions' copies and
    // the sky's CPU environment candidate projection. The graph counts the device map and reduction in their own passes.
    private const int FillersPass = 0;
    private const int UploadPass = 1;
    private const int EnvironmentPass = 2;

    private static readonly string[] PassLabelTable = ["fillers", "upload", "environment"];
    private static readonly WorkClass[] PassClassTable = [WorkClass.Deterministic, WorkClass.PerBackendDeterministic, WorkClass.Deterministic];

    // The ledger every wrapped GPU service counts into: the owner's (a residency that outlives device-loss rebuilds) or
    // the tables' own.
    private readonly GpuWorkLedger m_work;

    // The pass configuration's revision: one more for every UploadProgram and every InstallReload that installs a
    // pipeline, so a sample says which program and kernel set its counts ran under.
    private long m_workRevision;

    /// <summary>Gets the labels of an upload's passes, in submission order: <c>fillers</c>, the fillers' first transitions
    /// and clears, on the first upload alone; <c>upload</c>, the region copies; and <c>environment</c>, the CPU projection deciding whether a procedural sky
    /// owes a device refresh. An upload skips a pass it has no work for, which then
    /// reads skipped.</summary>
    public static ReadOnlySpan<string> PassLabels => PassLabelTable;
    /// <summary>Gets what two runs of each pass may be held to agree on, in <see cref="PassLabels"/> order:
    /// <c>fillers</c> and <c>environment</c> are <see cref="WorkClass.Deterministic"/>; <c>upload</c> is
    /// <see cref="WorkClass.PerBackendDeterministic"/>, because what it writes and copies follows the residency policy
    /// each device's memory profile selects.</summary>
    public static ReadOnlySpan<WorkClass> PassClasses => PassClassTable;
    /// <summary>Gets the GPU work the tables' uploads recorded, for the newest upload known to have completed.</summary>
    public IGpuWorkSource Work =>
        m_work;
    /// <summary>Gets the GPU objects the tables' ledger has seen created (images, buffers, descriptor pools and sets), over
    /// the ledger's whole life.</summary>
    public IWorkCounterSource WorkLifetime =>
        m_work;

    private void ReconfigureWork() {
        m_workRevision++;
        m_work.Configure(
            passClasses: PassClassTable,
            passLabels: PassLabelTable,
            revision: m_workRevision
        );
    }
}
