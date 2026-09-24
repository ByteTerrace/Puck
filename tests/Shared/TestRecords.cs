namespace Puck.Testing;

/// <summary>Where a test run writes the fresh copy of a committed record it compares against: under
/// <c>records/&lt;artifact&gt;</c> beside the test assembly, never in the checkout. <c>puck baselines</c> runs the test
/// and promotes these files over the committed ones, and its <c>--check</c> compares them; the artifact names and the
/// <c>records</c> directory are the two spellings its <c>BaselinesCommand</c> shares with this type.</summary>
internal static class TestRecords {
    /// <summary>Returns the directory a run writes one artifact's records to.</summary>
    /// <param name="artifact">The artifact name <c>puck baselines</c> takes, for example <c>state</c>.</param>
    /// <returns>The absolute directory path, which may not exist yet.</returns>
    public static string DirectoryOf(string artifact) => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "records",
        path3: artifact
    );
    /// <summary>Writes one record, creating its directory, and returns where it went.</summary>
    /// <param name="artifact">The artifact name <c>puck baselines</c> takes.</param>
    /// <param name="fileName">The record's file name, the same as its committed file's.</param>
    /// <param name="bytes">The record's exact bytes.</param>
    /// <returns>The absolute path written.</returns>
    public static string Write(string artifact, string fileName, byte[] bytes) {
        var directory = DirectoryOf(artifact: artifact);
        var path = Path.Combine(
            path1: directory,
            path2: fileName
        );

        _ = Directory.CreateDirectory(path: directory);
        File.WriteAllBytes(
            bytes: bytes,
            path: path
        );

        return path;
    }
}
