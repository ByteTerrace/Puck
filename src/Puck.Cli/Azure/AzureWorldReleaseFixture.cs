using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static async Task<string> PrepareWorldReleaseFixtureAsync(WorldReleaseContext context, AzureWorldReleaseDeployment? source,
        WorldReleaseManifest bootstrap, Guid requestId, CancellationToken token) {
        WorldReleaseFixtureManifest? snapshot = null;

        if (source is not null) {
            var workers = await StableWorkersAsync(
                group: context.ResourceGroup,
                scaleSet: context.Group
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (workers.Length != 1) { throw new InvalidOperationException(message: "qualification export requires exactly one active source worker"); }
            var worker = Text(value: workers[0]!["name"]);
            var status = await WorldGuestJsonAsync(
                group: context.ResourceGroup,
                script: "docker inspect --format '{\"image\":{{json .Config.Image}},\"running\":{{.State.Running}}}' puck-world",
                worker: worker
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (status["image"]?.GetValue<string>() != source.Image) ||
                (status["running"]?.GetValue<bool>() != true)
            ) {
                throw new InvalidOperationException(message: "qualification export found another image or a stopped source");
            }
            var exported = await WorldGuestJsonAsync(
                group: context.ResourceGroup,
                script: $"curl --fail --silent --show-error --max-time {source.ShutdownSeconds} -X POST http://127.0.0.1:{source.HealthPort}/release/fixture/{requestId:D}",
                worker: worker
            ).ConfigureAwait(continueOnCapturedContext: false);

            snapshot = (await context.Fixtures.LoadAsync(
                cancellationToken: token,
                requestId: requestId
            ).ConfigureAwait(continueOnCapturedContext: false)
                ?? throw new InvalidDataException(message: "worker did not retain a complete qualification capture"));
            if (
                (exported["requestId"]?.GetValue<Guid>() != requestId) ||
                (exported["identity"]?.GetValue<string>() != snapshot.Identity) ||
                (exported["release"]?.GetValue<string>() != source.Manifest.Identity) ||
                (snapshot.Group != context.Group) ||
                (snapshot.Release != source.Manifest.Identity)
            ) {
                throw new InvalidDataException(message: "worker qualification response does not match the retained source capture");
            }
        }
        var output = Path.GetFullPath(path: Path.Combine(
            path1: "artifacts",
            path2: "world-release-fixtures",
            path3: Guid.NewGuid().ToString(format: "N")
        ));

        await new WorldReleaseFixtureBuilder(
            context.Blobs,
            context.Archive,
            context.Fixtures
        )
            .BuildAsync(
            (source?.Manifest ?? bootstrap),
            context.Owner,
            snapshot,
            output,
            token
        ).ConfigureAwait(continueOnCapturedContext: false);
        Console.WriteLine(value: $"Qualification fixture: {output}");
        return output;
    }
}
