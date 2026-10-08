using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Puck;

// Linked into the CLI, the verification projects, and file apps. One run's directory under the temporary root, and
// the one policy every per-run directory follows. A directory is named <kind><owner>-<token>: its kind, which starts
// with "puck-" and names the runner; the process id of the run that owns it; and a unique token. A run that passes
// deletes its directory (retrying briefly while handles close). A run that fails keeps it and names its absolute path
// in a "run directory kept:" line, so the evidence survives, and then trims its kind's kept directories: of those
// whose owner has finished, the newest KeptPerKind stay and the rest go. The first directory a process creates sweeps
// every kind the same way and also removes any finished owner's directory older than StaleAge, which clears a killed
// run's leftovers and old evidence of kinds that never run again. A directory whose owner still runs is never removed,
// and a family directory named with a token alone names no owner, so it goes once older than StaleAge.
internal sealed partial class RunDirectory : IDisposable {
    /// <summary>The prefix every run directory's name starts with.</summary>
    public const string Family = "puck-";
    /// <summary>How many kept directories of one kind, owned by finished runs, a sweep leaves.</summary>
    public const int KeptPerKind = 4;

    /// <summary>How old a finished run's directory must be before a sweep removes it whatever its kind keeps.</summary>
    public static readonly TimeSpan StaleAge = TimeSpan.FromHours(value: 6);

    // How many times a deletion tries, and how long it waits between tries, in milliseconds.
    private const int DeleteAttempts = 20;
    private const int DeleteRetryMilliseconds = 50;

    // A process that started this long after a directory was created cannot be the run that created it: the id was
    // reused. The margin absorbs the coarse start times some platforms report.
    private static readonly TimeSpan ReuseMargin = TimeSpan.FromSeconds(value: 1);

    // Whether this process has swept every kind yet: its first created directory does.
    private static int Swept;

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

    // <kind><owner>-<token>, where the kind ends in '-' and the token, a random file name, holds none.
    [GeneratedRegex(pattern: @"^(?<kind>puck-.*-)(?<owner>[0-9]{1,10})-(?<token>[^-]+)$", options: RegexOptions.CultureInvariant)]
    private static partial Regex OwnedName();
    // <kind><token> with the random token alone, which names no owner.
    [GeneratedRegex(pattern: @"^(?<kind>puck-.*-)[a-z0-9]{8}\.[a-z0-9]{3}$", options: RegexOptions.CultureInvariant)]
    private static partial Regex OwnerlessName();

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
    /// <summary>Creates a fresh, uniquely named directory of <paramref name="prefix"/>'s kind under the temporary root,
    /// owned by this process, first sweeping every kind when this process has not swept yet.</summary>
    /// <param name="prefix">The directory-name prefix that identifies the owning runner; <see cref="Family"/> is
    /// prepended when it does not start with it.</param>
    /// <returns>The created directory's absolute path.</returns>
    public static string CreatePath(string prefix) {
        var kind = KindOf(prefix: prefix);

        if (Interlocked.Exchange(location1: ref Swept, value: 1) == 0) {
            _ = Sweep();
        }

        return Directory.CreateTempSubdirectory(prefix: $"{kind}{Environment.ProcessId.ToString(provider: CultureInfo.InvariantCulture)}-").FullName;
    }
    /// <summary>The kind a prefix names: the prefix itself, with <see cref="Family"/> prepended when it lacks it and a
    /// hyphen appended when it does not end in one, so the owner that follows is always its own name segment.</summary>
    /// <param name="prefix">The directory-name prefix.</param>
    /// <returns>The kind.</returns>
    public static string KindOf(string prefix) {
        ArgumentException.ThrowIfNullOrEmpty(argument: prefix);

        var kind = (prefix.StartsWith(comparisonType: StringComparison.Ordinal, value: Family) ? prefix : (Family + prefix));

        return (kind.EndsWith(value: '-') ? kind : (kind + "-"));
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
    /// the kept line and trims its kind's kept directories (<see cref="Trim"/>) when it failed and the directory
    /// exists.</summary>
    /// <param name="path">The run's directory.</param>
    /// <param name="passed">Whether the run passed.</param>
    /// <param name="report">Where the kept line is written.</param>
    public static void Conclude(string path, bool passed, TextWriter report) {
        if (passed) {
            TryDelete(path: path);
        } else if (Directory.Exists(path: path)) {
            report.WriteLine(value: KeptLine(path: path));
            _ = Trim(kept: path);
        }
    }
    /// <summary>The line that names a kept run directory.</summary>
    /// <param name="path">The kept directory's absolute path.</param>
    /// <returns>The line, without a terminator.</returns>
    public static string KeptLine(string path) => $"run directory kept: {path}";
    /// <summary>Trims the kind of a directory just kept: sweeps that kind (<see cref="Sweep(string, TimeSpan)"/>). A
    /// directory whose name is not a run directory's trims nothing.</summary>
    /// <param name="kept">The kept directory.</param>
    /// <returns>How many directories were removed.</returns>
    public static int Trim(string kept) {
        var match = OwnedName().Match(input: System.IO.Path.GetFileName(path: System.IO.Path.TrimEndingDirectorySeparator(path: kept)));

        return (match.Success ? SweepKinds(age: StaleAge, kind: match.Groups["kind"].Value) : 0);
    }
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
    /// <summary>Removes, best-effort, the run directories of every kind under the temporary root whose owners have
    /// finished, keeping the newest <see cref="KeptPerKind"/> of each kind that are younger than <see cref="StaleAge"/>.
    /// A directory whose owner still runs, or whose owner cannot be told, is never removed.</summary>
    /// <returns>How many directories were removed.</returns>
    public static int Sweep() => SweepKinds(age: StaleAge, kind: null);
    /// <summary>Removes, best-effort, the run directories of <paramref name="prefix"/>'s kind whose owners have finished,
    /// keeping the newest <see cref="KeptPerKind"/> that are younger than <paramref name="age"/>. A directory whose owner
    /// still runs, or whose owner cannot be told, is never removed.</summary>
    /// <param name="prefix">The prefix naming the kind (<see cref="KindOf"/>).</param>
    /// <param name="age">How old a finished run's directory must be to go whatever its kind keeps.</param>
    /// <returns>How many directories were removed.</returns>
    public static int Sweep(string prefix, TimeSpan age) => SweepKinds(age: age, kind: KindOf(prefix: prefix));

    // Sweeps one kind, or every kind when kind is null. A family directory named with a token alone, which names no
    // owner to ask, goes once it is older than the age.
    private static int SweepKinds(string? kind, TimeSpan age) {
        var threshold = (DateTime.UtcNow - age);
        var finished = new Dictionary<string, List<(string Path, DateTime Created)>>(comparer: StringComparer.Ordinal);
        var removed = 0;

        try {
            foreach (var directory in Directory.EnumerateDirectories(
                path: System.IO.Path.GetTempPath(),
                searchPattern: $"{(kind ?? Family)}*",
                searchOption: SearchOption.TopDirectoryOnly
            )) {
                // The pattern also matches 8.3 short names; only a long name of a run directory's shape counts.
                var name = System.IO.Path.GetFileName(path: directory);
                var match = OwnedName().Match(input: name);

                try {
                    if (match.Success) {
                        var group = match.Groups["kind"].Value;
                        var created = Directory.GetCreationTimeUtc(path: directory);

                        if (((kind is null) || string.Equals(a: group, b: kind, comparisonType: StringComparison.Ordinal)) && OwnerFinished(created: created, owner: match.Groups["owner"].Value)) {
                            if (!finished.TryGetValue(key: group, value: out var list)) {
                                finished[group] = list = [];
                            }

                            list.Add(item: (directory, created));
                        }
                    } else if (OwnerlessName().IsMatch(input: name) && ((kind is null) || OwnerlessName().Match(input: name).Groups["kind"].Value.Equals(comparisonType: StringComparison.Ordinal, value: kind)) && (Directory.GetCreationTimeUtc(path: directory) < threshold)) {
                        Delete(path: directory);
                        removed++;
                    }
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    // A directory that vanished, cannot be read or is still held is left to a later sweep.
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // Enumerating the temporary root is best-effort: a sweep never fails the run that started it.
        }

        foreach (var list in finished.Values) {
            list.Sort(comparison: static (left, right) => {
                var order = right.Created.CompareTo(value: left.Created);

                return ((order != 0) ? order : string.CompareOrdinal(strA: left.Path, strB: right.Path));
            });

            for (var index = 0; (index < list.Count); index++) {
                if ((index < KeptPerKind) && (list[index].Created >= threshold)) {
                    continue;
                }

                try {
                    Delete(path: list[index].Path);
                    removed++;
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    // Something still holds a file there; a later sweep tries again.
                }
            }
        }

        return removed;
    }    // Whether the run that created a directory at the given time has finished: no process holds its id, or the process
    // holding it started after the directory was created, so the id was reused. A process that cannot be read may be
    // the owner, so it counts as running.
    private static bool OwnerFinished(string owner, DateTime created) {
        if (!int.TryParse(s: owner, style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var id)) {
            return false;
        }

        try {
            using var process = Process.GetProcessById(processId: id);

            return (process.StartTime.ToUniversalTime() > (created + ReuseMargin));
        } catch (ArgumentException) {
            // No process has the id.
            return true;
        } catch (InvalidOperationException) {
            // The process exited while it was read.
            return true;
        } catch (Exception exception) when ((exception is System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException)) {
            // Another user's process, or a platform that will not say: it may be the owner, so it is.
            return false;
        }
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
