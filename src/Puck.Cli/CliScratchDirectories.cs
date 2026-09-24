namespace Puck.Cli;

// The scratch-directory lifecycle a proof runner needs beyond Directory.CreateTempSubdirectory, which makes each
// fresh, prefixed run directory under the temp root: best-effort removal of a directory a run is done with, and a
// best-effort age-bounded sweep of stale siblings left by an earlier run that never got to clean up after itself (a
// crash, a killed process). Shared by every runner that stamps its scratch directories with its own prefix.
internal static class CliScratchDirectories {
    // Best-effort removal of a directory this run created; what survives is left to SweepScratch's age bound or to the
    // owner's own later cleanup.
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
