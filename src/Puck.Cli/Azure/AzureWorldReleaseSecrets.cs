using System.IO.Compression;
using System.Text;
using Puck.Assets;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Keeps credential-bearing bootstrap inputs in Key Vault and returns a pinned secret version URI.</summary>
    private sealed class AzureWorldReleaseSecretVersions(string vault, string prefix) : IWorldReleaseSecretVersions {
        private void ValidateVersion(string version) {
            if (
                !Uri.TryCreate(
                result: out var uri,
                uriKind: UriKind.Absolute,
                uriString: version
            ) ||
                (uri.Scheme != Uri.UriSchemeHttps) ||
                (uri.Host != (vault + ".vault.azure.net")) ||
                !uri.IsDefaultPort ||
                (uri.Query.Length != 0) ||
                (uri.Fragment.Length != 0) ||
                (uri.UserInfo.Length != 0)
            ) {
                throw new InvalidDataException(message: "retained deployment must reference a secret version in the configured vault");
            }
            var parts = uri.AbsolutePath.Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: '/'
            );

            if (
                (parts.Length != 3) ||
                (parts[0] != "secrets") ||
                !parts[1].StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: (prefix + "-")
            ) ||
                !ContentPin.TryParseHex(
                hex: parts[1].AsSpan(start: (prefix.Length + 1)),
                pin: out _
            ) ||
                (parts[2].Length != 32) ||
                parts[2].Any(predicate: character => !Uri.IsHexDigit(character: character))
            ) {
                throw new InvalidDataException(message: "retained deployment requires a full immutable release secret version");
            }
        }

        public async Task<ReadOnlyMemory<byte>> ReadAsync(string version, CancellationToken cancellationToken) {
            ValidateVersion(version: version);
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = await AzAsync(
                "keyvault",
                "secret",
                "show",
                "--id",
                version,
                "--query",
                "value",
                "-o",
                "tsv"
            ).ConfigureAwait(continueOnCapturedContext: false);

            CliGitHub.Mask(value: encoded);
            if (encoded.Length > 25000) { throw new InvalidDataException(message: "retained deployment secret exceeds its encoded budget"); }
            using var compressed = new MemoryStream(buffer: Convert.FromBase64String(s: encoded));
            using var gzip = new GZipStream(
                mode: CompressionMode.Decompress,
                stream: compressed
            );
            var bytes = new byte[((1024 * 1024) + 1)];
            var count = 0;

            while (count < bytes.Length) {
                var read = await gzip.ReadAsync(
                    buffer: bytes.AsMemory(start: count),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (read == 0) {
                    return bytes.AsMemory(
                        length: count,
                        start: 0
                    );
                }
                count += read;
            }
            throw new InvalidDataException(message: "retained deployment secret exceeds its decoded budget");
        }
        public async Task<string> WriteAsync(string release, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) {
            if (!ContentPin.TryParse(
                pin: out var releasePin,
                text: release
            )) {
                throw new InvalidDataException(message: "deployment secret requires a full release identity");
            }
            var name = ((prefix + "-") + releasePin.Hex);

            if (
                (name.Length > 127) ||
                name.Any(predicate: character => (!char.IsAsciiLetterOrDigit(c: character) && (character != '-')))
            ) {
                throw new InvalidDataException(message: "deployment secret prefix cannot form a valid retained Key Vault name");
            }
            using var compressed = new MemoryStream();

            using (var gzip = new GZipStream(
                compressed,
                CompressionLevel.SmallestSize,
                leaveOpen: true
            )) {
                await gzip.WriteAsync(
                    buffer: content,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
            }
            var encoded = Convert.ToBase64String(inArray: compressed.ToArray());

            if (Encoding.UTF8.GetByteCount(s: encoded) > 25000) { throw new InvalidDataException(message: "compressed deployment inputs exceed the Key Vault secret budget"); }
            CliGitHub.Mask(value: encoded);
            var file = Path.Combine(
                path1: Path.GetTempPath(),
                path2: (("puck-release-secret-" + Guid.NewGuid().ToString(format: "N")) + ".txt")
            );

            try {
                await File.WriteAllTextAsync(
                    cancellationToken: cancellationToken,
                    contents: encoded,
                    path: file
                ).ConfigureAwait(continueOnCapturedContext: false);
                cancellationToken.ThrowIfCancellationRequested();
                var version = await AzAsync(
                    "keyvault",
                    "secret",
                    "set",
                    "--vault-name",
                    vault,
                    "--name",
                    name,
                    "--file",
                    file,
                    "--query",
                    "id",
                    "-o",
                    "tsv"
                ).ConfigureAwait(continueOnCapturedContext: false);

                ValidateVersion(version: version);
                return version;
            } finally { File.Delete(path: file); }
        }
    }
}
