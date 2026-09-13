using System.Text.Json.Nodes;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    // A pipeline retry must not push through a release's registry write lock. Reuse an existing commit tag
    // only after its immutable manifest matches the locally built image identity.
    private static async Task<string?> ReusePublishedContainerAsync(string registry, string repository, string commit, string server) {
        var repositories = await AzJsonAsync("acr", "repository", "list", "--name", registry, "-o", "json").ConfigureAwait(false);
        if (!repositories.AsArray().Any(row => Text(row) == repository)) { return null; }
        var tags = await AzJsonAsync("acr", "repository", "show-tags", "--name", registry, "--repository", repository, "-o", "json").ConfigureAwait(false);
        if (!tags.AsArray().Any(row => Text(row) == commit)) { return null; }
        var metadata = await AzJsonAsync("acr", "repository", "show", "--name", registry, "--image", repository + ":" + commit, "-o", "json").ConfigureAwait(false);
        var digest = Text(metadata["digest"]);
        _ = ParseWorldReleaseImage(server + "/" + repository + "@" + digest);
        var reference = server + "/" + repository + "@" + digest;
        var retained = JsonNode.Parse(await DockerAsync("manifest", "inspect", reference).ConfigureAwait(false))!;
        var built = JsonNode.Parse(await DockerAsync("image", "inspect", $"puck/{repository}:{commit}").ConfigureAwait(false))!;
        if (!MatchesPublishedContainer(digest, built[0]!, retained)) {
            throw new InvalidDataException("the published commit names different image bytes; a release tag cannot be replaced on retry");
        }
        return reference;
    }

    internal static bool MatchesPublishedContainer(string digest, JsonNode built, JsonNode retained) {
        // Containerd's Id is the OCI index digest, while the classic Docker store exposes a config digest.
        // Never compare an index to a config, or weaken an available full descriptor to a platform subset.
        if (built["Descriptor"]?["digest"]?.GetValue<string>() is { } descriptor) { return descriptor == digest; }
        return retained["config"]?["digest"]?.GetValue<string>() is { } configuration && built["Id"]?.GetValue<string>() == configuration;
    }
}
