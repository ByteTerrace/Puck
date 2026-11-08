using System.CommandLine;
using Puck.Abstractions;
using Puck.Cli.Automation;
using Puck.World.Server;

[assembly: PuckExtension(typeof(Puck.Cli.Azure.AzureCliExtension))]

namespace Puck.Cli.Azure;

/// <summary>Installs the Azure release host and its deployment commands without accessing Azure.</summary>
public sealed class AzureCliExtension : IPuckExtension {
    public string Name => "Puck.Cli.Azure";
    public void Register(IPuckExtensionRegistry registry) => registry.Add<WorldReleaseHost>(key: "azure", contribution: new AzureReleaseHost());

    private sealed class AzureReleaseHost : WorldReleaseHost {
        public override string RootDocumentName => AzureCommand.HostedWorldName;
        public override Command CreateCommand(TimeProvider clock) => AzureCommand.Create(clock: clock);
        public override Task<WorldReleaseGroupSnapshot?> ReadWorldReleaseStatusAsync(CancellationToken cancellationToken) => AzureCommand.ReadWorldReleaseStatusAsync(cancellationToken);
        public override Task<WorldReleaseGroupSnapshot> FinalizeWorldReleaseAsync(TimeProvider clock, CancellationToken cancellationToken) => AzureCommand.FinalizeWorldReleaseAsync(clock, cancellationToken);
        public override Task<WorldReleaseRunResult> ResumeWorldReleaseAsync(TimeProvider clock, CancellationToken cancellationToken) => AzureCommand.ResumeWorldReleaseAsync(clock, cancellationToken);
        public override Task<WorldReleaseRunResult> DeployWorldReleaseAsync(string packageDirectory, Guid? operationId, TimeProvider clock, CancellationToken cancellationToken) => AzureCommand.DeployWorldReleaseAsync(packageDirectory, operationId, clock, cancellationToken);
        public override Task<WorldReleaseRunResult> RollbackWorldReleaseAsync(Guid? operationId, TimeProvider clock, CancellationToken cancellationToken) => AzureCommand.RollbackWorldReleaseAsync(operationId, clock, cancellationToken);
        public override Task<WorldReleaseFixtureManifest> CaptureWorldReleasePointAsync(Guid requestId, TimeProvider clock, CancellationToken token) => AzureCommand.CaptureWorldReleasePointAsync(requestId, clock, token);
        public override Task<WorldReleaseRunResult> RestoreWorldReleaseAsync(Guid pointId, Guid? operationId, bool discardProgress, TimeProvider clock, CancellationToken token) => AzureCommand.RestoreWorldReleaseAsync(pointId, operationId, discardProgress, clock, token);
    }
}
