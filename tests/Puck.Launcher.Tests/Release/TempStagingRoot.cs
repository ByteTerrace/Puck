using Puck.Testing;

namespace Puck.Launcher.Tests.Release;

/// <summary>A per-test cache-root directory path (staging area, applied-current tree, or rollout install-id file)
/// that does not exist until the tested code creates it — the absent-root scenario is part of what these tests
/// exercise. The path lies under a <see cref="TemporaryDirectory"/> that the law's verdict resolves on dispose: a
/// passing law deletes the whole tree and lets a deletion failure fail the law rather than masking a lifetime
/// defect, and a failing law keeps it.</summary>
internal sealed class TempStagingRoot : IDisposable {
    private readonly TemporaryDirectory m_directory = new(prefix: "puck-launcher-");

    /// <summary>Gets the root's own path, which nothing has created until the tested code does.</summary>
    public string RootPath => m_directory.PathOf(name: "root");

    public void Dispose() => m_directory.Dispose();
}
