using System.CommandLine;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>An installed release host. Its operations drive the shared durable release coordinator through a
/// provider's runtime adapter; registration and command creation perform no deployment I/O.</summary>
public abstract class WorldReleaseHost {
    public abstract string RootDocumentName { get; }
    public abstract Command CreateCommand(TimeProvider clock);
    public abstract Task<WorldReleaseGroupSnapshot?> ReadWorldReleaseStatusAsync(CancellationToken cancellationToken);
    public abstract Task<WorldReleaseGroupSnapshot> FinalizeWorldReleaseAsync(TimeProvider clock, CancellationToken cancellationToken);
    public abstract Task<WorldReleaseRunResult> ResumeWorldReleaseAsync(TimeProvider clock, CancellationToken cancellationToken);
    public abstract Task<WorldReleaseRunResult> DeployWorldReleaseAsync(string packageDirectory, Guid? operationId, TimeProvider clock, CancellationToken cancellationToken);
    public abstract Task<WorldReleaseRunResult> RollbackWorldReleaseAsync(Guid? operationId, TimeProvider clock, CancellationToken cancellationToken);
    public abstract Task<WorldReleaseFixtureManifest> CaptureWorldReleasePointAsync(Guid requestId, TimeProvider clock, CancellationToken token);
    public abstract Task<WorldReleaseRunResult> RestoreWorldReleaseAsync(Guid pointId, Guid? operationId, bool discardProgress, TimeProvider clock, CancellationToken token);

    /// <summary>Selects a named host, or the single installed host when no name is supplied.</summary>
    public static WorldReleaseHost Select(string? name = null) => CliExtensions.Installed.Select<WorldReleaseHost>(key: name, purpose: "release host");
}
