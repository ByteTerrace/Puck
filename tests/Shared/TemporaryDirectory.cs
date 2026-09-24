namespace Puck.Testing;

/// <summary>A directory under the temporary root that one law owns: created on construction and deleted whole on
/// dispose. A deletion failure fails the law rather than masking a handle the code under test left open. Names are
/// relative to <see cref="RootPath"/> and may be forward-slashed; a write creates any subdirectory its name
/// names.</summary>
/// <param name="prefix">The temp-directory name prefix — kept distinct per caller so a directory that survives an
/// aborted run (a killed process, a debugger break) still names which law left it behind.</param>
internal sealed class TemporaryDirectory(string prefix = "puck-test-") : IDisposable {
    /// <summary>Gets the directory's absolute path.</summary>
    public string RootPath { get; } = Directory.CreateTempSubdirectory(prefix: prefix).FullName;

    public void Dispose() => Directory.Delete(
        path: RootPath,
        recursive: true
    );
    /// <summary>Returns the absolute path of <paramref name="name"/> under this directory.</summary>
    /// <param name="name">The relative path.</param>
    /// <returns>The absolute path; nothing is created.</returns>
    public string PathOf(string name) => Path.Combine(
        path1: RootPath,
        path2: name
    );
    /// <summary>Writes <paramref name="bytes"/> to <paramref name="name"/> under this directory.</summary>
    /// <param name="name">The relative path.</param>
    /// <param name="bytes">The file's contents.</param>
    /// <returns>The absolute path written.</returns>
    public string WriteBytes(string name, ReadOnlySpan<byte> bytes) {
        var path = PathOf(name: name);

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
        File.WriteAllBytes(
            bytes: bytes,
            path: path
        );

        return path;
    }
    /// <summary>Writes <paramref name="text"/> as UTF-8 without a byte-order mark to <paramref name="name"/> under
    /// this directory.</summary>
    /// <param name="name">The relative path.</param>
    /// <param name="text">The file's contents.</param>
    /// <returns>The absolute path written.</returns>
    public string WriteText(string name, string text) => WriteBytes(
        bytes: System.Text.Encoding.UTF8.GetBytes(s: text),
        name: name
    );
}
