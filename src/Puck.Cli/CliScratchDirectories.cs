namespace Puck.Cli;

// The scratch-directory lifecycle a proof runner needs: a fresh randomly-named run directory under the temp root,
// and a best-effort age-bounded sweep of stale siblings left by an earlier run that never got to clean up after
// itself (a crash, a killed process). Shared by every runner that stamps its scratch directories with its own prefix.
internal static class CliScratchDirectories {
    // A fresh, empty, randomly-named directory under the temp root, prefixed for SweepScratch to find later.
    public static string CreateRunDirectory(string scratchPrefix) {
        var temp = Path.GetTempPath();

        for (var attempt = 0; (attempt < 8); attempt++) {
            var path = Path.Combine(path1: temp, path2: $"{scratchPrefix}{Guid.NewGuid():N}");

            if (Directory.Exists(path: path)) {
                continue;
            }

            Directory.CreateDirectory(path: path);

            return path;
        }

        throw new IOException(message: $"Could not create a fresh random '{scratchPrefix}' scratch directory after 8 attempts.");
    }
    public static void SweepScratch(string scratchPrefix) {
        var threshold = DateTime.UtcNow.AddHours(value: -6);

        try {
            foreach (var directory in Directory.EnumerateDirectories(path: Path.GetTempPath(), searchPattern: $"{scratchPrefix}*", searchOption: SearchOption.TopDirectoryOnly)) {
                try {
                    if (Directory.GetCreationTimeUtc(path: directory) < threshold) {
                        Directory.Delete(path: directory, recursive: true);
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
