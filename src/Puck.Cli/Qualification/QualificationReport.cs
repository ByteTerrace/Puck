using Puck.Abstractions.Gpu;

namespace Puck.Cli.Qualification;

/// <summary>
/// A <c>puck.qualification.report.v1</c> document: what one <c>puck qualify</c> run held a package to and what it
/// found. It records the package and the profile, the functional canaries' outcome, and for every matrix cell its
/// backend, extent, workload lengths, the device and driver it ran on, the memory it read and its threshold, and the
/// verdict with every finding behind it.
/// </summary>
/// <param name="Schema">The document schema tag, <see cref="SchemaVersion"/>.</param>
/// <param name="Commit">The checkout's <c>HEAD</c> commit the profile was read from.</param>
/// <param name="Profile">The profile's path.</param>
/// <param name="Package">The package directory qualified.</param>
/// <param name="Publish">The publish section the package was verified against.</param>
/// <param name="Functional">The functional canaries' outcome, or <see langword="null"/> when the profile names
/// none.</param>
/// <param name="Cells">Every matrix cell's result, in the order they ran.</param>
/// <param name="Deferred">The checks the profile defers, with why.</param>
/// <param name="Outcome">The run's outcome: a failure anywhere fails it, otherwise a blocked check blocks it.</param>
internal sealed record QualificationReport(
    string Schema,
    string Commit,
    string Profile,
    string Package,
    ReleasePublish Publish,
    QualificationFunctionalResult? Functional,
    IReadOnlyList<QualificationCellResult> Cells,
    IReadOnlyList<QualificationDeferral> Deferred,
    QualificationOutcome Outcome
) {
    /// <summary>The document schema tag every report carries.</summary>
    public const string SchemaVersion = "puck.qualification.report.v1";
}
/// <summary>The functional canaries' outcome.</summary>
/// <param name="Canaries">The canary ids run against the package.</param>
/// <param name="ExitCode">The canary runner's exit code.</param>
/// <param name="Outcome">The outcome: 0 passes, 1 fails, anything else is blocked.</param>
internal sealed record QualificationFunctionalResult(IReadOnlyList<string> Canaries, int ExitCode, QualificationOutcome Outcome);
/// <summary>One matrix cell's result.</summary>
/// <param name="Cell">The cell's name.</param>
/// <param name="Workload">The workload's name.</param>
/// <param name="Backend">The backend.</param>
/// <param name="Width">The offscreen width.</param>
/// <param name="Height">The offscreen height.</param>
/// <param name="WarmupTicks">The warm-up, in ticks.</param>
/// <param name="SoakTicks">Each soak window, in ticks.</param>
/// <param name="WorldReloads">The world reloads made.</param>
/// <param name="Pipeline">The pipeline churn made, or <see langword="null"/>.</param>
/// <param name="DebugLayers">Whether the backend's validation layer was on.</param>
/// <param name="Device">The device the World reported, or <see langword="null"/> when no reading arrived.</param>
/// <param name="MemoryProfile">The device's memory profile as the first inspection echoed it, or
/// <see langword="null"/>.</param>
/// <param name="PeakOwnedPipelineBytes">The most bytes the pipeline instance owned or planned, or
/// <see langword="null"/>.</param>
/// <param name="PeakOwnedPipelineBytesThreshold">The cell's threshold for it, or <see langword="null"/>.</param>
/// <param name="ValidationMessages">The validation-layer messages the leg printed.</param>
/// <param name="Outcome">The verdict.</param>
/// <param name="Findings">Why the cell failed or was blocked.</param>
internal sealed record QualificationCellResult(
    string Cell,
    string Workload,
    string Backend,
    int Width,
    int Height,
    int WarmupTicks,
    int SoakTicks,
    int WorldReloads,
    QualificationPipeline? Pipeline,
    bool DebugLayers,
    GpuDeviceIdentity? Device,
    string? MemoryProfile,
    long? PeakOwnedPipelineBytes,
    long? PeakOwnedPipelineBytesThreshold,
    int ValidationMessages,
    QualificationOutcome Outcome,
    IReadOnlyList<string> Findings
);
