using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Protects exact registry manifests and pulls both engines before any source drain.
    /// Docker credentials are isolated from the operator's existing configuration.</summary>
    private static async Task RetainWorldReleaseImagesAsync(IEnumerable<string> images, CancellationToken cancellationToken) {
        var inventory = images.Distinct(StringComparer.Ordinal).Select(ParseWorldReleaseImage).ToArray();
        var registryName = ResourceName("containerRegistry", "name");
        var registryServer = await AzAsync("acr", "show", "--name", registryName, "--query", "loginServer", "-o", "tsv").ConfigureAwait(false);
        if (inventory.Any(image => image.Server != registryServer)) { throw new InvalidDataException("release images must belong to the configured official registry"); }
        var directory = Directory.CreateTempSubdirectory("puck-release-registry-");
        try {
            foreach (var registry in inventory.GroupBy(image => image.Server, StringComparer.Ordinal)) {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var image in registry) {
                    await AzAsync("acr", "repository", "update", "--name", registryName, "--image", image.Reference,
                        "--delete-enabled", "false", "--write-enabled", "false", "-o", "none").ConfigureAwait(false);
                    var retained = await AzJsonAsync("acr", "repository", "show", "--name", registryName, "--image", image.Reference, "-o", "json").ConfigureAwait(false);
                    ValidateRetainedWorldReleaseImage(image.Digest, retained);
                }
                var server = registry.Key;
                var tenant = await AzAsync("account", "show", "--query", "tenantId", "-o", "tsv").ConfigureAwait(false);
                var token = await TokenAsync("https://containerregistry.azure.net").ConfigureAwait(false);
                using var form = new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["grant_type"] = "access_token", ["service"] = server, ["tenant"] = tenant, ["access_token"] = token,
                });
                using var response = await Http.PostAsync($"https://{server}/oauth2/exchange", form, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var refresh = Text(JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!["refresh_token"]);
                CliGitHub.Mask(refresh);
                await RunAsync("docker", ["--config", directory.FullName, "login", server, "--username", "00000000-0000-0000-0000-000000000000", "--password-stdin"], input: refresh + "\n").ConfigureAwait(false);
                foreach (var image in registry) {
                    await RunAsync("docker", ["--config", directory.FullName, "pull", server + "/" + image.Reference]).ConfigureAwait(false);
                }
            }
        } finally { directory.Delete(recursive: true); }
    }

    internal static (string Server, string Reference, string Digest) ParseWorldReleaseImage(string image) {
        var match = Regex.Match(image, @"\A(?<server>[a-z0-9][a-z0-9-]{3,61}[a-z0-9]\.azurecr\.io)/(?<repository>[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*)@(?<digest>sha256:[a-f0-9]{64})\z");
        if (!match.Success) { throw new InvalidDataException("managed world releases require an exact Azure Container Registry repository digest"); }
        var digest = match.Groups["digest"].Value;
        return (match.Groups["server"].Value, match.Groups["repository"].Value + "@" + digest, digest);
    }

    internal static void ValidateRetainedWorldReleaseImage(string digest, JsonNode retained) {
        if (retained["digest"]?.GetValue<string>() != digest ||
            retained["changeableAttributes"]?["deleteEnabled"]?.GetValue<bool>() != false ||
            retained["changeableAttributes"]?["writeEnabled"]?.GetValue<bool>() != false ||
            retained["changeableAttributes"]?["readEnabled"]?.GetValue<bool>() != true) {
            throw new InvalidDataException("registry did not confirm readable, write-protected and deletion-protected release bytes");
        }
    }
}
