using System.Collections.Concurrent;

namespace Puck;

// Linked into the CLI, the verification projects, and file apps. One run's directory under the temporary root, and
// the one policy every per-run directory follows: a uniquely named directory whose prefix names its owner; a run that
// passes deletes it (retrying briefly while handles close); a run that fails keeps it and names its absolute path in
// a "run directory kept:" line, so the evidence survives; and the first creation for a prefix in a process removes the
// same-prefix directories older than StaleAge, which a crashed or failed earlier run left behind.
internal sealed class RunDirectory : IDisposable {
    /// <summary>How old a same-prefix directory must be before a later run removes it.</summary>
    public static readonly TimeSpan StaleAge = TimeSpan.FromHours(value: 6);

    // How many times a deletion tries, and how long it waits between tries, in milliseconds.
    private const int DeleteAttempts = 20;
    private const int DeleteRetryMilliseconds = 50;

    // The prefixes this process has already swept, so each is swept once however many directories it creates.
    private static readonly ConcurrentDictionary<string, byte> Swept = new(comparer: StringComparer.Ordinal);

    private readonly bool m_keepOnFailure;
    private readonly TextWriter m_report;

    private bool m_concluded;

    private RunDirectory(string path, bool keepOnFailure, TextWriter report) {
        m_keepOnFailure = keepOnFailure;
        m_report = report;
        Path = path;
    }

    /// <summary>Gets the directory's absolute path.</summary>
    public string Path { get; }

    /// <summary>Creates a run directory whose outcome decides whether it survives.</summary>
    /// <param name="prefix">The directory-name prefix that identifies the owning runner.</param>
    /// <param name="report">Where the kept line is written; standard error when omitted.</param>
    /// <param name="keepOnFailure">Whether a failing run keeps the directory; <see langword="false"/> for a directory
    /// that holds no evidence or holds credentials, which is deleted whatever the outcome.</param>
    /// <returns>The run directory; dispose it, or conclude it with the run's outcome.</returns>
    public static RunDirectory Create(string prefix, TextWriter? report = null, bool keepOnFailure = true) => new(
        keepOnFailure: keepOnFailure,
        path: CreatePath(prefix: prefix),
        report: (report ?? Console.Error)
    );
    /// <summary>Creates a fresh, uniquely named directory under the temporary root, first sweeping this prefix's stale
    /// directories when this process has not yet swept it.</summary>
    /// <param name="prefix">The directory-name prefix that identifies the owning runner.</param>
    /// <returns>The created directory's absolute path.</returns>
    public static string CreatePath(string prefix) {
        if (Swept.TryAdd(
            key: prefix,
            value: 0
        )) {
            Sweep(
                age: StaleAge,
                prefix: prefix
            );
        }

        return Directory.CreateTempSubdirectory(prefix: prefix).FullName;
    }
    /// <summary>Concludes the run with its exit code: zero passes, anything else fails.</summary>
    /// <param name="exitCode">The run's exit code.</param>
    /// <returns><paramref name="exitCode"/>, so a verb can return what it concludes with.</returns>
    public int Conclude(int exitCode) {
        Conclude(passed: (exitCode == 0));

        return exitCode;
    }
    /// <summary>Concludes the run once: deletes the directory when it passed, or keeps it and writes the kept line
    /// when it failed. Later conclusions do nothing.</summary>
    /// <param name="passed">Whether the run passed.</param>
    public void Conclude(bool passed) {
        if (m_concluded) {
            return;
        }

        m_concluded = true;
        Conclude(
            passed: (passed || !m_keepOnFailure),
            path: Path,
            report: m_report
        );
    }
    /// <summary>Concludes a run that ended without concluding — an exception, an early refusal — as a failure.</summary>
    public void Dispose() => Conclude(passed: false);
    /// <summary>Applies the policy to a directory a caller tracks by path: deletes it when its run passed, or writes
    /// the kept line when it failed and the directory exists.</summary>
    /// <param name="path">The run's directory.</param>
    /// <param name="passed">Whether the run passed.</param>
    /// <param name="report">Where the kept line is written.</param>
    public static void Conclude(string path, bool passed, TextWriter report) {
        if (passed) {
            TryDelete(path: path);
        } else if (Directory.Exists(path: path)) {
            report.WriteLine(value: KeptLine(path: path));
        }
    }
    /// <summary>The line that names a kept run directory.</summary>
    /// <param name="path">The kept directory's absolute path.</param>
    /// <returns>The line, without a terminator.</returns>
    public static string KeptLine(string path) => $"run directory kept: {path}";
    /// <summary>Deletes a directory once, letting the failure propagate: for an owner whose law is that nothing under
    /// the directory is still open.</summary>
    /// <param name="path">The directory to remove recursively.</param>
    public static void Delete(string path) {
        if (Directory.Exists(path: path)) {
            Directory.Delete(
                path: path,
                recursive: true
            );
        }
    }
    /// <summary>Deletes a directory best-effort, retrying for a short while as handles under it close and clearing
    /// read-only attributes that refuse a delete.</summary>
    /// <param name="path">The directory to remove recursively.</param>
    /// <returns>Whether the directory is gone.</returns>
    public static bool TryDelete(string path) {
        for (var attempt = 0; (attempt < DeleteAttempts); attempt++) {
            try {
                Delete(path: path);

                return true;
            } catch (IOException) {
                // A handle under the directory has not closed yet.
            } catch (UnauthorizedAccessException) {
                // A read-only file, or one still held open for deletion.
                ClearReadOnly(path: path);
            }

            Thread.Sleep(millisecondsTimeout: DeleteRetryMilliseconds);
        }

        return !Directory.Exists(path: path);
    }
    /// <summary>Removes, best-effort, the directories under the temporary root whose names start with
    /// <paramref name="prefix"/> and that were created longer than <paramref name="age"/> ago.</summary>
    /// <param name="prefix">The prefix identifying one runner's directories.</param>
    /// <param name="age">How old a directory must be to be removed.</param>
    /// <returns>How many directories were removed.</returns>
    public static int Sweep(string prefix, TimeSpan age) {
        var threshold = (DateTime.UtcNow - age);
        var removed = 0;

        try {
            foreach (var directory in Directory.EnumerateDirectories(
                path: System.IO.Path.GetTempPath(),
                searchPattern: $"{prefix}*",
                searchOption: SearchOption.TopDirectoryOnly
            )) {
                // The pattern also matches 8.3 short names; only a long name that starts with the prefix is this runner's.
                if (!System.IO.Path.GetFileName(path: directory).StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: prefix
                )) {
                    continue;
                }

                try {
                    if (Directory.GetCreationTimeUtc(path: directory) < threshold) {
                        Delete(path: directory);
                        removed++;
                    }
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    // A best-effort, age-bounded sweep never fails this run or touches a fresh sibling run.
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // Enumerating the temporary root is best-effort for the same reason as deleting an old entry.
        }

        return removed;
    }

    private static void ClearReadOnly(string path) {
        try {
            foreach (var file in Directory.EnumerateFiles(
                path: path,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            )) {
                var attributes = File.GetAttributes(path: file);

                if ((attributes & FileAttributes.ReadOnly) != 0) {
                    File.SetAttributes(
                        fileAttributes: attributes & ~FileAttributes.ReadOnly,
                        path: file
                    );
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // The next attempt reports what still refuses.
        }
    }
}
