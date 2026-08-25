namespace Puck.Launcher.Tests.Release;

/// <summary>A per-test cache-root directory (staging area, applied-current tree, or rollout install-id file),
/// cleaned up whole on dispose.</summary>
internal sealed class TempStagingRoot : IDisposable {
    private readonly string m_root = Directory.CreateDirectory(path: Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-launcher-{Guid.NewGuid():n}"
    )).FullName;

    /// <summary>Gets the directory's own path.</summary>
    public string RootPath => m_root;

    public void Dispose() {
        try {
            Directory.Delete(
                path: m_root,
                recursive: true
            );
        } catch (IOException) {
        }
    }
}
