using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    // The passes an upload counts, in recording order: the fillers' first transitions and clears, the brick pool's writes
    // (a queued host-baked brick's staging and copy, and the carve bake's slices), the regions' copies and the sky's
    // environment (SdfWorldTables.SkyEnvironment.cs).
    private const int FillersPass = 0;
    private const int BricksPass = 1;
    private const int UploadPass = 2;
    private const int EnvironmentPass = 3;

    // The environment's named rows, in kernel index order after its plain row (SdfWorldWorkDetails.Sky's gradient).
    private static readonly GpuWorkDetail[] EnvironmentDetails = [new(Pass: EnvironmentPass, Detail: "plain"), new(Pass: EnvironmentPass, Detail: "gradient")];

    private static readonly string[] PassLabelTable = ["fillers", "bricks", "upload", "environment"];
    private static readonly WorkClass[] PassClassTable = [WorkClass.Deterministic, WorkClass.PerBackendDeterministic, WorkClass.PerBackendDeterministic, WorkClass.Deterministic];

    // The ledger every wrapped GPU service counts into: the owner's (a residency that outlives device-loss rebuilds) or
    // the tables' own.
    private readonly GpuWorkLedger m_work;

    // The pass configuration's revision: one more for every UploadProgram and every InstallReload that installs a
    // pipeline, so a sample says which program and kernel set its counts ran under.
    private long m_workRevision;

    /// <summary>Gets the labels of an upload's passes, in submission order: <c>fillers</c>, the fillers' first transitions
    /// and clears, on the first upload alone; <c>bricks</c>, the brick pool's writes (a queued host-baked brick's staging
    /// and copy, the carve bake's slices and the barriers around them), on an upload that writes the pool; and
    /// <c>upload</c>, the region copies; and <c>environment</c>, the sky's environment map and its coefficients, on an
    /// upload whose sky's field runs moved while the fog reads them. An upload skips a pass it has no work for, which then
    /// reads skipped.</summary>
    public static ReadOnlySpan<string> PassLabels => PassLabelTable;
    /// <summary>Gets what two runs of each pass may be held to agree on, in <see cref="PassLabels"/> order:
    /// <c>fillers</c> and <c>environment</c> are <see cref="WorkClass.Deterministic"/>; <c>bricks</c> and <c>upload</c>
    /// are <see cref="WorkClass.PerBackendDeterministic"/>, because what they write and copy follows the residency policy
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
        m_work.ConfigureDetails(details: EnvironmentDetails);
    }
}
