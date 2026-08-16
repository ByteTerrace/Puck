namespace Puck.World.Tests;

/// <summary>A per-test directory so relative <c>basis</c> spellings resolve exactly the way the shipped assets' do
/// (against the referring file's own directory), cleaned up whole.</summary>
internal sealed class TempWorldDirectory : IDisposable {
    private readonly string m_root = Directory.CreateDirectory(path: Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-world-tests-basis-{Guid.NewGuid():N}"
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
    /// <summary>Writes raw bytes under this directory, creating any subdirectory <paramref name="name"/> names (a
    /// basis link deliberately lives one directory down, in <c>basis/</c>).</summary>
    public string WriteBytes(string name, byte[] bytes) {
        var path = Path.Combine(
            path1: m_root,
            path2: name
        );

        Directory.CreateDirectory(path: (Path.GetDirectoryName(path: path) ?? m_root));
        File.WriteAllBytes(
            bytes: bytes,
            path: path
        );

        return path;
    }
    public string WriteFlatDocument(string name) {
        return WriteBytes(
            bytes: Fixtures.DefaultWorldBytes(),
            name: name
        );
    }
    public string WriteText(string name, string text) {
        var path = Path.Combine(
            path1: m_root,
            path2: name
        );

        Directory.CreateDirectory(path: (Path.GetDirectoryName(path: path) ?? m_root));
        File.WriteAllText(
            contents: text,
            path: path
        );

        return path;
    }
}
