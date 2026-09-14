using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Protects exact registry manifests and pulls both engines before any source drain.
    /// Docker credentials are isolated from the operator's existing configuration.</summary>
    private static async Task RetainWorldReleaseImagesAsync(IEnumerable<string> images, CancellationToken cancellationToken) {
        var inventory = images.Distinct(comparer: StringComparer.Ordinal).Select(selector: ParseWorldReleaseImage).ToArray();
        var registryName = ResourceName(
            "containerRegistry",
            "name"
        );
        var registryServer = await AzAsync(
            "acr",
            "show",
            "--name",
            registryName,
            "--query",
            "loginServer",
            "-o",
            "tsv"
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (inventory.Any(predicate: image => (image.Server != registryServer))) { throw new InvalidDataException(message: "release images must belong to the configured official registry"); }
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-release-registry-");

        try {
            foreach (var registry in inventory.GroupBy(
                image => image.Server,
                StringComparer.Ordinal
            )) {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var image in registry) {
                    await AzAsync(
                        "acr",
                        "repository",
                        "update",
                        "--name",
                        registryName,
                        "--image",
                        image.Reference,
                        "--delete-enabled",
                        "false",
                        "--write-enabled",
                        "false",
                        "-o",
                        "none"
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    var retained = await AzJsonAsync(
                        "acr",
                        "repository",
                        "show",
                        "--name",
                        registryName,
                        "--image",
                        image.Reference,
                        "-o",
                        "json"
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    ValidateRetainedWorldReleaseImage(
                        digest: image.Digest,
                        retained: retained
                    );
                }
                var server = registry.Key;
                var tenant = await AzAsync(
                    "account",
                    "show",
                    "--query",
                    "tenantId",
                    "-o",
                    "tsv"
                ).ConfigureAwait(continueOnCapturedContext: false);
                var token = await TokenAsync(resource: "https://containerregistry.azure.net").ConfigureAwait(continueOnCapturedContext: false);
                using var form = new FormUrlEncodedContent(nameValueCollection: new Dictionary<string, string> {
                    ["grant_type"] = "access_token",
                    ["service"] = server,
                    ["tenant"] = tenant,
                    ["access_token"] = token,
                });
                using var response = await Http.PostAsync(
                    cancellationToken: cancellationToken,
                    content: form,
                    requestUri: $"https://{server}/oauth2/exchange"
                ).ConfigureAwait(continueOnCapturedContext: false);

                response.EnsureSuccessStatusCode();
                var refresh = Text(value: JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false))!["refresh_token"]);

                CliGitHub.Mask(value: refresh);
                await RunAsync(
                    "docker",
                    ["--config", directory.FullName, "login", server, "--username", "00000000-0000-0000-0000-000000000000", "--password-stdin"],
                    input: (refresh + "\n")
                ).ConfigureAwait(continueOnCapturedContext: false);
                foreach (var image in registry) {
                    await RunAsync(
                        "docker",
                        ["--config", directory.FullName, "pull", ((server + "/") + image.Reference)]
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }
            }
        } finally { directory.Delete(recursive: true); }
    }

    internal static (string Server, string Reference, string Digest) ParseWorldReleaseImage(string image) {
        var match = Regex.Match(
            input: image,
            pattern: @"\A(?<server>[a-z0-9][a-z0-9-]{3,61}[a-z0-9]\.azurecr\.io)/(?<repository>[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*)@(?<digest>sha256:[a-f0-9]{64})\z"
        );

        if (!match.Success) { throw new InvalidDataException(message: "managed world releases require an exact Azure Container Registry repository digest"); }
        var digest = match.Groups["digest"].Value;

        return (match.Groups["server"].Value, ((match.Groups["repository"].Value + "@") + digest), digest);
    }
    internal static void ValidateRetainedWorldReleaseImage(string digest, JsonNode retained) {
        if (
            (retained["digest"]?.GetValue<string>() != digest) ||
            (retained["changeableAttributes"]?["deleteEnabled"]?.GetValue<bool>() != false) ||
            (retained["changeableAttributes"]?["writeEnabled"]?.GetValue<bool>() != false) ||
            (retained["changeableAttributes"]?["readEnabled"]?.GetValue<bool>() != true)
        ) {
            throw new InvalidDataException(message: "registry did not confirm readable, write-protected and deletion-protected release bytes");
        }
    }
}
