using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static async Task<string> PrepareWorldReleaseFixtureAsync(WorldReleaseContext context, AzureWorldReleaseDeployment? source,
        WorldReleaseManifest bootstrap, Guid requestId, CancellationToken token) {
        WorldReleaseFixtureManifest? snapshot = null;
        if (source is not null) {
            var workers = await StableWorkersAsync(context.ResourceGroup, context.Group).ConfigureAwait(false);
            if (workers.Length != 1) { throw new InvalidOperationException("qualification export requires exactly one active source worker"); }
            var worker = Text(workers[0]!["name"]);
            var status = await WorldGuestJsonAsync(context.ResourceGroup,
                "docker inspect --format '{\"image\":{{json .Config.Image}},\"running\":{{.State.Running}}}' puck-world", worker).ConfigureAwait(false);
            if (status["image"]?.GetValue<string>() != source.Image || status["running"]?.GetValue<bool>() != true) {
                throw new InvalidOperationException("qualification export found another image or a stopped source");
            }
            var exported = await WorldGuestJsonAsync(context.ResourceGroup,
                $"curl --fail --silent --show-error --max-time {source.ShutdownSeconds} -X POST http://127.0.0.1:{source.HealthPort}/release/fixture/{requestId:D}", worker).ConfigureAwait(false);
            snapshot = await context.Fixtures.LoadAsync(requestId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("worker did not retain a complete qualification capture");
            if (exported["requestId"]?.GetValue<Guid>() != requestId || exported["identity"]?.GetValue<string>() != snapshot.Identity ||
                exported["release"]?.GetValue<string>() != source.Manifest.Identity || snapshot.Group != context.Group || snapshot.Release != source.Manifest.Identity) {
                throw new InvalidDataException("worker qualification response does not match the retained source capture");
            }
        }
        var output = Path.GetFullPath(Path.Combine("artifacts", "world-release-fixtures", Guid.NewGuid().ToString("N")));
        await new WorldReleaseFixtureBuilder(context.Blobs, context.Archive, context.Fixtures)
            .BuildAsync(source?.Manifest ?? bootstrap, context.Owner, snapshot, output, token).ConfigureAwait(false);
        Console.WriteLine($"Qualification fixture: {output}");
        return output;
    }
}
