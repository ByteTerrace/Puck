using System.Security.Cryptography;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    // The deployment's release channels live under the official-content prefix, the one the publishing identity may write
    // and Front Door serves at <officialContentBaseUrl>/releases.
    private const string ReleaseChannelPrefix = "public/puck/official/releases";
    // The channels' root key, a deployment-vault secret created on first use like the world signing keys.
    private const string ReleaseChannelKeySecret = "release-channel-root-key";

    /// <summary>Loads the release channels' root key from the deployment's key vault (<c>deploymentKeyVaultName</c>),
    /// creating it there on first use. The runner must already be admitted to the vault (<c>puck azure vault-access</c>).</summary>
    /// <returns>The key, owned by the caller.</returns>
    internal static async Task<ECDsa> LoadReleaseChannelKeyAsync() {
        var vault = Text(value: Value(key: "deploymentKeyVaultName", outputs: Outputs()));
        var secrets = await AzJsonAsync("keyvault", "secret", "list", "--vault-name", vault, "--query", "[].name", "-o", "json").ConfigureAwait(continueOnCapturedContext: false);
        // The scratch can hold the key while it is created, so it is deleted however the step ends.
        using var scratch = RunDirectory.Create(keepOnFailure: false, prefix: "puck-channel-key-");
        var encoded = await LoadSigningKeyAsync(secret: ReleaseChannelKeySecret, secrets: secrets.AsArray(), temporary: scratch.Path, vault: vault).ConfigureAwait(continueOnCapturedContext: false);
        var key = ECDsa.Create();

        key.ImportPkcs8PrivateKey(bytesRead: out _, source: Convert.FromBase64String(s: encoded));
        return key;
    }
    /// <summary>Writes a signed release-source tree into the official-content container: every object, immutable and
    /// skipped where the blob exists, then each document, uncached, in order.</summary>
    /// <param name="tree">The tree's root.</param>
    /// <param name="documents">The documents' slashed paths under the tree, manifests last.</param>
    /// <returns>A task that completes when every blob is written.</returns>
    internal static async Task UploadReleaseChannelAsync(string tree, IReadOnlyList<string> documents) {
        var outputs = Outputs();
        var container = Text(value: Value(key: "officialContentContainerName", outputs: outputs));
        var endpoint = new Uri(uriString: Text(value: Value(key: "staticSiteEndpoint", outputs: outputs))).GetLeftPart(part: UriPartial.Authority);
        var destination = $"{endpoint}/{container}/{ReleaseChannelPrefix}";
        var commit = (Environment.GetEnvironmentVariable(variable: "GITHUB_SHA") ?? "local");
        var objects = Path.Combine(path1: tree, path2: "objects");

        if (Directory.Exists(path: objects)) {
            await CopyBlobsAsync(commit: commit, contentType: "application/octet-stream", destination: $"{destination}/objects", immutable: true, source: LocalPath(path: objects)).ConfigureAwait(continueOnCapturedContext: false);
        }
        foreach (var document in documents) {
            await CopyBlobsAsync(
                commit: commit,
                contentType: (document.EndsWith(value: ".json", comparisonType: StringComparison.Ordinal) ? "application/json" : "application/octet-stream"),
                destination: $"{destination}/{document}",
                source: LocalPath(path: Path.Combine(path1: tree, path2: document))
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        Console.Out.WriteLine(value: $"azure: release channel written under {Text(value: Value(key: "officialContentBaseUrl", outputs: outputs))}/releases.");
    }
}
