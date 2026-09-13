using System.IO.Compression;
using System.Text;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Keeps credential-bearing bootstrap inputs in Key Vault and returns a pinned secret version URI.</summary>
    private sealed class AzureWorldReleaseSecretVersions(string vault, string prefix) : IWorldReleaseSecretVersions {
        public async Task<string> WriteAsync(string release, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) {
            if (release.Length != 71 || !release.StartsWith("sha256/", StringComparison.Ordinal) ||
                release.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0) {
                throw new InvalidDataException("deployment secret requires a full release identity");
            }
            var name = prefix + "-" + release[7..];
            if (name.Length > 127 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) {
                throw new InvalidDataException("deployment secret prefix cannot form a valid retained Key Vault name");
            }
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) {
                await gzip.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }
            var encoded = Convert.ToBase64String(compressed.ToArray());
            if (Encoding.UTF8.GetByteCount(encoded) > 25000) { throw new InvalidDataException("compressed deployment inputs exceed the Key Vault secret budget"); }
            CliGitHub.Mask(encoded);
            var file = Path.Combine(Path.GetTempPath(), "puck-release-secret-" + Guid.NewGuid().ToString("N") + ".txt");
            try {
                await File.WriteAllTextAsync(file, encoded, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var version = await AzAsync("keyvault", "secret", "set", "--vault-name", vault, "--name", name,
                    "--file", file, "--query", "id", "-o", "tsv").ConfigureAwait(false);
                ValidateVersion(version);
                return version;
            } finally { File.Delete(file); }
        }

        public async Task<ReadOnlyMemory<byte>> ReadAsync(string version, CancellationToken cancellationToken) {
            ValidateVersion(version);
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = await AzAsync("keyvault", "secret", "show", "--id", version, "--query", "value", "-o", "tsv").ConfigureAwait(false);
            CliGitHub.Mask(encoded);
            if (encoded.Length > 25000) { throw new InvalidDataException("retained deployment secret exceeds its encoded budget"); }
            using var compressed = new MemoryStream(Convert.FromBase64String(encoded));
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            var bytes = new byte[1024 * 1024 + 1];
            var count = 0;
            while (count < bytes.Length) {
                var read = await gzip.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) { return bytes.AsMemory(0, count); }
                count += read;
            }
            throw new InvalidDataException("retained deployment secret exceeds its decoded budget");
        }

        private void ValidateVersion(string version) {
            if (!Uri.TryCreate(version, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                uri.Host != vault + ".vault.azure.net" || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0) {
                throw new InvalidDataException("retained deployment must reference a secret version in the configured vault");
            }
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[0] != "secrets" || !parts[1].StartsWith(prefix + "-", StringComparison.Ordinal) ||
                parts[1].Length != prefix.Length + 65 || parts[1].AsSpan(prefix.Length + 1).IndexOfAnyExcept("0123456789abcdef") >= 0 ||
                parts[2].Length != 32 || parts[2].Any(character => !Uri.IsHexDigit(character))) {
                throw new InvalidDataException("retained deployment requires a full immutable release secret version");
            }
        }
    }
}
