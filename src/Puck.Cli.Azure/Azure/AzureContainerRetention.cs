using System.Text.Json.Nodes;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    // A pipeline retry must not push through a release's registry write lock. Reuse an existing commit tag
    // only after its immutable manifest matches the locally built image identity.
    // The publishing identity holds repository-scoped rights only and is deliberately denied the registry catalog, so
    // the reuse check asks for the one known tag and treats only a not-found answer as "not published"; any other
    // refusal (authentication, throttling) still fails the deploy by name.
    private static async Task<string?> ReusePublishedContainerAsync(string registry, string repository, string commit, string server) {
        JsonNode metadata;

        try {
            metadata = await AzJsonAsync(
                "acr",
                "repository",
                "show",
                "--name",
                registry,
                "--image",
                ((repository + ":") + commit),
                "-o",
                "json"
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (InvalidOperationException exception) when (IsRegistryNotFound(message: exception.Message)) {
            return null;
        }
        var digest = Text(value: metadata["digest"]);

        _ = ParseWorldReleaseImage(image: ((((server + "/") + repository) + "@") + digest));
        var reference = ((((server + "/") + repository) + "@") + digest);
        var retained = JsonNode.Parse(await DockerAsync(
            "manifest",
            "inspect",
            reference
        ).ConfigureAwait(continueOnCapturedContext: false))!;
        var built = JsonNode.Parse(await DockerAsync(
            "image",
            "inspect",
            $"puck/{repository}:{commit}"
        ).ConfigureAwait(continueOnCapturedContext: false))!;

        if (!MatchesPublishedContainer(
            digest,
            built[0]!,
            retained
        )) {
            throw new InvalidDataException(message: "the published commit names different image bytes; a release tag cannot be replaced on retry");
        }
        return reference;
    }

    /// <summary>Reports whether an <c>az acr</c> failure says the asked repository or tag does not exist, as opposed to a refusal of the request.</summary>
    /// <param name="message">The failure text, carrying the command's standard error.</param>
    /// <returns><see langword="true"/> when the registry answered that the image is unknown.</returns>
    public static bool IsRegistryNotFound(string message) =>
        message.Contains(value: "MANIFEST_UNKNOWN", comparisonType: StringComparison.OrdinalIgnoreCase)
        || message.Contains(value: "NAME_UNKNOWN", comparisonType: StringComparison.OrdinalIgnoreCase)
        || message.Contains(value: "manifest unknown", comparisonType: StringComparison.OrdinalIgnoreCase)
        || message.Contains(value: "does not exist", comparisonType: StringComparison.OrdinalIgnoreCase)
        || message.Contains(value: "not found", comparisonType: StringComparison.OrdinalIgnoreCase);

    public static bool MatchesPublishedContainer(string digest, JsonNode built, JsonNode retained) {
        // Containerd's Id is the OCI index digest, while the classic Docker store exposes a config digest.
        // Never compare an index to a config, or weaken an available full descriptor to a platform subset.
        if (built["Descriptor"]?["digest"]?.GetValue<string>() is { } descriptor) { return (descriptor == digest); }
        return (
            (retained["config"]?["digest"]?.GetValue<string>() is { } configuration) &&
            (built["Id"]?.GetValue<string>() == configuration)
        );
    }
}
