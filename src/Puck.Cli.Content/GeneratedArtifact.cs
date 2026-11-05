using System.Security.Cryptography;
using Puck.Abstractions;

namespace Puck.Cli;

// Export a generated image without treating an existing image as an oracle.
public static class GeneratedArtifact {
    public static bool Write(string path, byte[] bytes, string verb) {
        var hash = Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes));
        var displayPath = Path.GetRelativePath(
            relativeTo: Environment.CurrentDirectory,
            path: path
        ).Replace(
            newChar: '/',
            oldChar: Path.DirectorySeparatorChar
        );

        AtomicFile.WriteAllBytes(bytes: bytes, path: path);

        Console.Out.WriteLine(value: $"{verb}: wrote {displayPath} ({bytes.Length} bytes; SHA-256 {hash}).");
        return true;
    }
}
