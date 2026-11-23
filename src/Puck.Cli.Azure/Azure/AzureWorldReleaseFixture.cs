using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    // Exports the source's current state and builds the qualification fixture from it, or from bootstrap's definitions
    // without gameplay when there is no source. When successor is given and changes a definition outside its metadata, or
    // the source's state does not decode under this build, the fixture is successor's fresh one instead and Reset names
    // why (WorldReleaseFixtureBuilder.BuildAsync).
    private static async Task<(string Directory, string? Reset)> PrepareWorldReleaseFixtureAsync(WorldReleaseContext context, AzureWorldReleaseDeployment? source,
        WorldReleaseManifest bootstrap, Guid requestId, CancellationToken token, WorldReleaseManifest? successor = null) {
        WorldReleaseFixtureManifest? snapshot = null;

        if (source is not null) {
            var workers = await StableWorkersAsync(
                clock: context.Clock,
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
                script: WorldControlRequest(
                    deployment: source,
                    method: "POST",
                    path: $"/release/fixture/{requestId:D}"
                ),
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

        var reset = await new WorldReleaseFixtureBuilder(
            context.Blobs,
            context.Archive,
            context.Fixtures,
            LocalWorldsDirectory
        )
            .BuildAsync(
            (source?.Manifest ?? bootstrap),
            context.Owner,
            snapshot,
            output,
            token,
            successor
        ).ConfigureAwait(continueOnCapturedContext: false);

        Console.WriteLine(value: $"Qualification fixture: {output}");
        if (reset is not null) { Console.WriteLine(value: $"Hosted state reset: {reset}"); }
        return (output, reset);
    }
}
