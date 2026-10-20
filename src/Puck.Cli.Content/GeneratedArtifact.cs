using System.Security.Cryptography;
using Puck.Assets;

namespace Puck.Cli;

// A checked-in binary a generator verb owns. Generation is an explicit operation; verification never creates a
// directory or rewrites a stale artifact.
public static class GeneratedArtifact {
    public static bool WriteOrVerify(string path, byte[] bytes, bool verify, string verb) {
        var hash = Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes));
        var displayPath = Path.GetRelativePath(
            relativeTo: Environment.CurrentDirectory,
            path: path
        ).Replace(
            newChar: '/',
            oldChar: Path.DirectorySeparatorChar
        );

        if (verify) {
            if (!File.Exists(path: path)) {
                Console.Error.WriteLine(value: $"{verb}: missing {displayPath}; expected SHA-256 {hash}.");
                return false;
            }
            if (!File.ReadAllBytes(path: path).AsSpan().SequenceEqual(other: bytes)) {
                Console.Error.WriteLine(value: $"{verb}: drift in {displayPath}; expected SHA-256 {hash}.");
                return false;
            }
        } else {
            AtomicFile.WriteAllBytes(
                bytes: bytes,
                path: path
            );
        }

        Console.Out.WriteLine(value: $"{verb}: {(verify
            ? "verified"
            : "wrote")} {displayPath} ({bytes.Length} bytes; SHA-256 {hash}).");
        return true;
    }
}
