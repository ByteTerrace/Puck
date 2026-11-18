using System.Text.Json;
using Puck.Abstractions;
using Puck.Abstractions.Waiting;
using Puck.Assets;
using Puck.Confinement;

namespace Puck.World;

/// <summary>Publishes immutable sibling documents together, then owns only this boot's writable overlays.</summary>
internal sealed class CompositionWorkspace : IDisposable {
    private readonly string m_root;
    private readonly IDisposable m_boot;
    private IDisposable? m_basis;
    private bool m_transferred;
    internal string DirectoryPath { get; }

    internal CompositionWorkspace(string cache) {
        m_root = Path.Combine(cache, "compositions");
        DirectoryPath = Path.Combine(m_root, Guid.NewGuid().ToString("N"));
        using var gate = LockDirectory();
        Sweep();
        Directory.CreateDirectory(DirectoryPath);
        m_boot = Hold(DirectoryPath);
    }

    // Serialize pin acquisition and last-reader cleanup across processes. Only staging and retirement take this lock.
    private string LockPath => Path.Combine(m_root, "staging.lock");
    private FileStream LockDirectory() {
        var deadline = WaitDeadline.In(TimeSpan.FromSeconds(5));
        return FileLock.Acquire(LockPath, expired: () => deadline.HasPassed)
            ?? throw new IOException($"CompositionStagingBusy: timed out waiting for '{PuckPaths.Normalize(LockPath)}'.");
    }
    private static IDisposable Hold(string directory) =>
        CacheRetention.HoldSet(directory) ?? throw new IOException($"Could not hold composition directory '{directory}'.");

    private void Sweep() {
        foreach (var directory in Directory.EnumerateDirectories(m_root)) {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out _) && !ContentPin.TryParseHex(name, out _)) { continue; }
            var lease = directory + ".lease";
            try {
                using var unused = FileLock.TryAcquireForDeletion(lease);
                if (unused is not null || !File.Exists(lease)) { ConfinedDirectory.RemoveTree(directory); }
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                // A process can hold a file in stale staging without holding its lease. Retry on the next sweep.
            }
        }
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
        using var gate = LockDirectory();
        if (Directory.Exists(shared)) { ConfinedDirectory.RemoveTree(Portable); }
        else { Directory.Move(Portable, shared); }
        m_basis = Hold(shared);
        return shared;
    }

    internal IDisposable Transfer() {
        m_transferred = true;
        return new Cleanup(this);
    }

    private sealed class Cleanup(CompositionWorkspace workspace) : IDisposable {
        public void Dispose() => workspace.Release();
    }

    private void Release() {
        m_basis?.Dispose();
        m_boot.Dispose();
        using var gate = FileLock.TryAcquire(LockPath);
        if (gate is not null) { Sweep(); }
    }

    public void Dispose() {
        if (!m_transferred) { Release(); }
    }
}
