using Puck.Abstractions;
using Puck.Confinement;


namespace Puck.Cli;

// Export a generated image without treating an existing image as an oracle.
public static class GeneratedArtifact {
    public static bool Write(string path, byte[] bytes, string verb) {
        var hash = ContentDigest.HexOf(content: bytes);
        var displayPath = Puck.Abstractions.PuckPaths.Relative(path: path, relativeTo: Environment.CurrentDirectory);

        AtomicFile.WriteAllBytes(bytes: bytes, path: path);

        Console.Out.WriteLine(value: $"{verb}: wrote {displayPath} ({bytes.Length} bytes; SHA-256 {hash}).");
        return true;
    }
}
