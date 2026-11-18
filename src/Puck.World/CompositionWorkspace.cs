using System.Text.Json;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Confinement;

namespace Puck.World;

/// <summary>Publishes immutable sibling documents together, then owns only this boot's writable overlays.</summary>
internal sealed class CompositionWorkspace : IDisposable {
    private bool m_owns = true;
    internal string DirectoryPath { get; }

    internal CompositionWorkspace(string cache) {
        DirectoryPath = Path.Combine(cache, "compositions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    private string Portable => Path.Combine(DirectoryPath, "portable");
    internal void AddPortable(string name, byte[] bytes) {
        Directory.CreateDirectory(Portable);
        File.WriteAllBytes(Path.Combine(Portable, WorldDocumentName.DocumentFile(name)), bytes);
    }

    internal string Publish() {
        var files = Directory.EnumerateFiles(Portable, "*", SearchOption.AllDirectories)
            .Select(file => (Name: PuckPaths.Relative(Portable, file), Pin: ContentPin.Compute(File.ReadAllBytes(file)).Hex))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => new[] { file.Name, file.Pin });
        var identity = ContentPin.Compute(JsonSerializer.SerializeToUtf8Bytes(files)).Hex;
        var shared = Path.Combine(Path.GetDirectoryName(DirectoryPath)!, identity);
        try { Directory.Move(Portable, shared); }
        catch (IOException) when (Directory.Exists(shared)) { ConfinedDirectory.RemoveTree(Portable); }
        return shared;
    }

    internal IDisposable Transfer() {
        m_owns = false;
        return new Cleanup(DirectoryPath);
    }

    private sealed class Cleanup(string directory) : IDisposable {
        public void Dispose() => ConfinedDirectory.RemoveTree(directory);
    }

    public void Dispose() {
        if (m_owns) { ConfinedDirectory.RemoveTree(DirectoryPath); }
    }
}
