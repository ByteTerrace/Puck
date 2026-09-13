using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static async Task<string> LoadWorldSigningKeyAsync(string vault, string secret, JsonArray secrets, string temporary) {
        if (!secrets.Any(predicate: value => (Text(value: value) == secret))) {
            using var generated = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
            var encoded = Convert.ToBase64String(inArray: generated.ExportPkcs8PrivateKey());

            CliGitHub.Mask(value: encoded);
            var file = Path.Combine(
                path1: temporary,
                path2: "key.txt"
            );

            File.WriteAllText(
                contents: encoded,
                path: file
            );
            await AzAsync(
                "keyvault",
                "secret",
                "set",
                "--vault-name",
                vault,
                "--name",
                secret,
                "--file",
                file,
                "-o",
                "none"
            ).ConfigureAwait(continueOnCapturedContext: false);
            secrets.Add(value: secret);
        }
        var retained = await AzAsync(
            "keyvault",
            "secret",
            "show",
            "--vault-name",
            vault,
            "--name",
            secret,
            "--query",
            "value",
            "-o",
            "tsv"
        ).ConfigureAwait(continueOnCapturedContext: false);

        CliGitHub.Mask(value: retained);
        using var key = ECDsa.Create();
        var bytes = Convert.FromBase64String(s: retained);

        key.ImportPkcs8PrivateKey(
            bytesRead: out var consumed,
            source: bytes
        );
        if (consumed != bytes.Length) { throw new InvalidDataException(message: "world signing key contains trailing bytes"); }
        return retained;
    }
}
