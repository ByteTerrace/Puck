using System.Security.Cryptography;

namespace Puck.Cli.Firmware;

// Generation is an explicit operation. Verification never creates a directory or rewrites a stale artifact.
internal static class FirmwareArtifact {
    public static bool WriteOrVerify(string path, byte[] bytes, bool verify, string machine) {
        var hash = Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes));
        var displayPath = Path.GetRelativePath(relativeTo: Environment.CurrentDirectory, path: path);

        if (verify) {
            if (!File.Exists(path: path)) {
                Console.Error.WriteLine(value: $"firmware {machine}: missing {displayPath}; expected SHA-256 {hash}.");
                return false;
            }
            if (!File.ReadAllBytes(path: path).AsSpan().SequenceEqual(other: bytes)) {
                Console.Error.WriteLine(value: $"firmware {machine}: drift in {displayPath}; expected SHA-256 {hash}.");
                return false;
            }
        } else {
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
            var temporary = path + $".{Guid.NewGuid():N}.tmp";

            try {
                File.WriteAllBytes(path: temporary, bytes: bytes);
                File.Move(sourceFileName: temporary, destFileName: path, overwrite: true);
            } finally {
                File.Delete(path: temporary);
            }
        }

        Console.Out.WriteLine(value: $"firmware {machine}: {(verify ? "verified" : "wrote")} {displayPath} ({bytes.Length} bytes; SHA-256 {hash}).");
        return true;
    }
}
