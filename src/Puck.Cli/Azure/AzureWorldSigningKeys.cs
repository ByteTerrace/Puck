using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static async Task<string> LoadWorldSigningKeyAsync(string vault, string secret, JsonArray secrets, string temporary) {
        if (!secrets.Any(value => Text(value) == secret)) {
            using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var encoded = Convert.ToBase64String(generated.ExportPkcs8PrivateKey());
            CliGitHub.Mask(encoded);
            var file = Path.Combine(temporary, "key.txt");
            File.WriteAllText(file, encoded);
            await AzAsync("keyvault", "secret", "set", "--vault-name", vault, "--name", secret, "--file", file, "-o", "none").ConfigureAwait(false);
            secrets.Add(secret);
        }
        var retained = await AzAsync("keyvault", "secret", "show", "--vault-name", vault, "--name", secret, "--query", "value", "-o", "tsv").ConfigureAwait(false);
        CliGitHub.Mask(retained);
        using var key = ECDsa.Create();
        var bytes = Convert.FromBase64String(retained);
        key.ImportPkcs8PrivateKey(bytes, out var consumed);
        if (consumed != bytes.Length) { throw new InvalidDataException("world signing key contains trailing bytes"); }
        return retained;
    }
}
