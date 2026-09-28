namespace Puck.Cli;

/// <summary>Creates the SDK-pinned scratch projects CLI verbs and fixtures build outside the checkout, and owns the
/// best-effort cleanup their runners share.</summary>
public static class CliScratchDirectories {
    /// <summary>Creates a fresh, prefixed directory under the temp root holding a copy of the checkout's
    /// <c>global.json</c>, so the SDK version and roll-forward policy that select the SDK there are the checkout's own.
    /// <para>The .NET host selects the SDK from the working directory's nearest <c>global.json</c>, and MSBuild resolves
    /// a project's SDK from the project's; an SDK command against a scratch project runs from this directory or a
    /// descendant so both agree on the pin.</para></summary>
    /// <param name="prefix">The directory-name prefix that identifies the owning runner.</param>
    /// <returns>The created directory's absolute path; the caller owns its removal.</returns>
    public static string CreateProject(string prefix) {
        var pin = RepositoryPaths.Resolve(relativePath: "global.json");
        var directory = Directory.CreateTempSubdirectory(prefix: prefix).FullName;

        try {
            File.Copy(sourceFileName: pin, destFileName: Path.Combine(path1: directory, path2: "global.json"));
            return directory;
        } catch {
            TryDelete(path: directory);
            throw;
        }
    }
    /// <summary>Removes a directory this run owns when no open file or permission prevents deletion.</summary>
    /// <param name="path">The directory to remove recursively.</param>
    public static void TryDelete(string path) {
        try {
            if (Directory.Exists(path: path)) {
                Directory.Delete(
                    path: path,
                    recursive: true
                );
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // A file still held open (an antivirus scan, a lingering child) is swept on a later run.
        }
    }
    /// <summary>Removes, best-effort, this runner's temp directories created more than six hours ago.</summary>
    /// <param name="scratchPrefix">The prefix identifying this runner's directories.</param>
    public static void SweepScratch(string scratchPrefix) {
        var threshold = DateTime.UtcNow.AddHours(value: -6);

        try {
            foreach (var directory in Directory.EnumerateDirectories(
                path: Path.GetTempPath(),
                searchPattern: $"{scratchPrefix}*",
                searchOption: SearchOption.TopDirectoryOnly
            )) {
                try {
                    if (Directory.GetCreationTimeUtc(path: directory) < threshold) {
                        Directory.Delete(
                            path: directory,
                            recursive: true
                        );
                    }
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    // A best-effort age-bounded sweep never makes this run fail or touches a fresh sibling run.
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // Enumerating temp is best-effort for the same reason as deleting an old entry.
        }
    }
}
