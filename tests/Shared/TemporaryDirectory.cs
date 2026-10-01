namespace Puck.Testing;

/// <summary>A directory under the temporary root that one law owns: created on construction, under a name no other
/// directory takes, and deleted whole on dispose. A deletion failure fails the law rather than masking a handle the code
/// under test left open, unless the law asks for a best-effort delete. Names are relative to <see cref="RootPath"/> and
/// may be forward-slashed; a write creates any subdirectory its name names.</summary>
/// <param name="prefix">The temp-directory name prefix — kept distinct per caller so a directory that survives an
/// aborted run (a killed process, a debugger break) still names which law left it behind.</param>
/// <param name="bestEffortDelete">Whether disposal waits a moment for handles still closing under the directory and,
/// when one stays open, leaves the directory to the temporary root rather than failing the law: for a law that composes a
/// host whose background work may still hold a file there as it is disposed, where what the law proves is not the
/// host's file handling.</param>
internal sealed class TemporaryDirectory(string prefix = "puck-test-", bool bestEffortDelete = false) : IDisposable {
    // How many times a best-effort delete tries, and how long it waits between tries, in milliseconds.
    private const int DeleteAttempts = 20;
    private const int DeleteRetryMilliseconds = 50;

    /// <summary>Gets the directory's absolute path.</summary>
    public string RootPath { get; } = Directory.CreateTempSubdirectory(prefix: prefix).FullName;

    public void Dispose() {
        if (!bestEffortDelete) {
            Directory.Delete(
                path: RootPath,
                recursive: true
            );

            return;
        }

        for (var attempt = 0; ((attempt < DeleteAttempts) && Directory.Exists(path: RootPath)); attempt++) {
            try {
                Directory.Delete(
                    path: RootPath,
                    recursive: true
                );

                return;
            } catch (IOException) {
                // A handle under the directory has not closed yet.
            } catch (UnauthorizedAccessException) {
                // Nor has one holding a file open for deletion.
            }

            Thread.Sleep(millisecondsTimeout: DeleteRetryMilliseconds);
        }
    }
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
