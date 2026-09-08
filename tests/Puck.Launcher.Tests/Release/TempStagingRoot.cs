namespace Puck.Launcher.Tests.Release;

/// <summary>A per-test cache-root directory path (staging area, applied-current tree, or rollout install-id file)
/// that does not exist until the tested code creates it — the absent-root scenario is part of what these tests
/// exercise. Cleanup deletes the whole tree on dispose and lets a deletion failure fail the test rather than
/// masking a lifetime defect.</summary>
internal sealed class TempStagingRoot : IDisposable {
    private readonly string m_root = Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-launcher-{Guid.NewGuid():n}"
    );

    /// <summary>Gets the directory's own path.</summary>
    public string RootPath => m_root;

    public void Dispose() {
        if (Directory.Exists(path: m_root)) {
            Directory.Delete(
                path: m_root,
                recursive: true
            );
        }
    }
}
