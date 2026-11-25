using System.CommandLine;
using Puck.Abstractions;
using Puck.Cli.Automation;
using Puck.World.Server;

[assembly: PuckExtension(typeof(Puck.Cli.Azure.AzureCliExtension))]

namespace Puck.Cli.Azure;

/// <summary>Installs the Azure release host, its release channel host and its deployment commands without accessing
/// Azure.</summary>
public sealed class AzureCliExtension : IPuckExtension {
    public string Name => "Puck.Cli.Azure";
    public void Register(IPuckExtensionRegistry registry) {
        registry.Add<WorldReleaseHost>(key: "azure", contribution: new AzureReleaseHost());
        registry.Add<Puck.Cli.Channel.ReleaseChannelHost>(key: "azure", contribution: new AzureChannelHost());
    }

    // The deployment's official-content container and key vault, named by the deployment's outputs: the file a deploy job
    // leaves in artifacts/production-outputs.json, or the platform deployment's own where no deploy ran.
    private sealed class AzureChannelHost : Puck.Cli.Channel.ReleaseChannelHost {
        public override string Describe => "the Azure deployment's official content (releases/)";
        public override Task<System.Security.Cryptography.ECDsa> LoadSigningKeyAsync(CancellationToken cancellationToken) => AzureCommand.LoadReleaseChannelKeyAsync();
        public override Task UploadAsync(string tree, IReadOnlyList<string> objects, IReadOnlyList<string> documents, CancellationToken cancellationToken) =>
            AzureCommand.UploadReleaseChannelAsync(documents: documents, tree: tree);
    }

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
