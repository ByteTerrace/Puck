using System.Diagnostics;
using System.Globalization;

namespace Puck.Cli;

/// <summary>Serializes lane work under a shared checkout's lease directory.</summary>
public sealed class CliLease : IDisposable {
    private readonly FileStream m_gate;
    private readonly string m_directory;

    private bool m_disposed;

    private CliLease(FileStream gate, string directory) {
        m_gate = gate;
        m_directory = directory;
    }

    /// <summary>Acquires a named lease, waiting at most the supplied interval.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="name">The resource: build (one World store build at a time), gpu or heavy.</param>
    /// <param name="holder">The lane name.</param>
    /// <param name="wait">The maximum wait.</param>
    /// <param name="clock">The deadline and stale-age clock.</param>
    /// <param name="cancellationToken">Cancels acquisition.</param>
    /// <param name="ancestor">Recognizes a live ancestor holder by PID and start ticks, allowing a serialized child lease.</param>
    /// <returns>The held lease.</returns>
    /// <exception cref="ArgumentException">The name, holder or wait is invalid.</exception>
    /// <exception cref="TimeoutException">The lease remains held at the deadline.</exception>
    public static async Task<CliLease> AcquireAsync(string root, string name, string holder, TimeSpan wait, TimeProvider clock, CancellationToken cancellationToken = default, Func<int, long, bool>? ancestor = null) {
        if ((name is not ("build" or "gpu" or "heavy")) || string.IsNullOrWhiteSpace(value: holder) || holder.Contains(value: '\n') || holder.Contains(value: '\r') || (wait < TimeSpan.Zero)) {
            throw new ArgumentException(message: "A lease requires build, gpu or heavy, a single-line holder, and a nonnegative wait.");
        }
        Directory.CreateDirectory(path: root);
        var directory = Path.Combine(path1: root, path2: name);
        var started = clock.GetTimestamp();
        string? reported = null;
        string? ticket = null;

        try {
            do {
                cancellationToken.ThrowIfCancellationRequested();
                // An outer `puck lease` still owns the machine lease. Its descendants compete for one
                // child lease instead of waiting on their parent; siblings never borrow each other's hold.
                var metadata = ReadHolder(directory: directory);

                if ((metadata is { Length: 4 }) && int.TryParse(s: metadata[2], result: out var pid) && long.TryParse(s: metadata[3], result: out var ticks) && (ancestor?.Invoke(pid, ticks) == true)) {
                    Leave(ticket: ticket);
                    ticket = null;
                    directory = Path.Combine(path1: directory, path2: "child");
                    continue;
                }
                // Admission is first come, first served: the lease goes to the oldest live ticket beside it.
                var queue = (directory + ".queue");

                if ((ticket is null) || !File.Exists(path: ticket)) { ticket = Enqueue(clock: clock, holder: holder, queue: queue); }
                var ahead = Ahead(now: clock.GetUtcNow(), ticket: ticket);

                if (ahead == 0) {
                    var lease = TryAcquire(clock: clock, directory: directory, holder: holder);

                    if (lease is not null) { return lease; }
                }
                var remaining = (wait - clock.GetElapsedTime(startingTimestamp: started));

                if (remaining <= TimeSpan.Zero) { throw new TimeoutException(message: $"{name} lease is held or queued at {CliPaths.ToDisplay(fullPath: directory)}; use --wait <seconds> to wait."); }
                var waitingOn = ((metadata is { Length: > 0 }) ? metadata[0] : null);
                var report = $"lease: {holder} waiting for {name} lease{((waitingOn is null) ? ((ahead == 0) ? " held by another holder" : string.Empty) : $" held by {waitingOn}")}{((ahead == 0) ? string.Empty : $"; {ahead} queued ahead")}.";

                if (reported != report) {
                    Console.Error.WriteLine(value: report);
                    reported = report;
                }
                await Task.Delay(delay: ((remaining < TimeSpan.FromMilliseconds(milliseconds: 100)) ? remaining : TimeSpan.FromMilliseconds(milliseconds: 100)), timeProvider: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            } while (true);
        } finally {
            Leave(ticket: ticket);
        }
    }

    /// <summary>Takes the next ticket in a queue directory: a file named by a zero-padded sequence, holding the same four holder lines.</summary>
    private static string Enqueue(string queue, string holder, TimeProvider clock) {
        Directory.CreateDirectory(path: queue);
        using var process = Process.GetCurrentProcess();
        string[] lines = [
            holder,
            clock.GetUtcNow().ToString(format: "O", formatProvider: CultureInfo.InvariantCulture),
            Environment.ProcessId.ToString(provider: CultureInfo.InvariantCulture),
            process.StartTime.ToUniversalTime().Ticks.ToString(provider: CultureInfo.InvariantCulture),
        ];

        while (true) {
            var next = 1L;

            foreach (var file in Directory.EnumerateFiles(path: queue)) {
                if (long.TryParse(s: Path.GetFileName(path: file), provider: CultureInfo.InvariantCulture, result: out var sequence) && (sequence >= next)) { next = (sequence + 1); }
            }
            var path = Path.Combine(path1: queue, path2: next.ToString(format: "D12", provider: CultureInfo.InvariantCulture));

            try {
                // CreateNew is the atomic claim: a racing newcomer that computed the same sequence fails here and recomputes.
                using var stream = new FileStream(access: FileAccess.Write, mode: FileMode.CreateNew, path: path, share: FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream: stream);

                foreach (var line in lines) { writer.WriteLine(value: line); }
                return path;
            } catch (IOException) when (File.Exists(path: path)) { }
        }
    }
    /// <summary>Counts the live tickets older than this one, dropping each whose process is gone.</summary>
    private static int Ahead(string ticket, DateTimeOffset now) {
        var ahead = 0;
        var mine = Path.GetFileName(path: ticket);

        foreach (var file in Directory.EnumerateFiles(path: Path.GetDirectoryName(path: ticket)!).Order(comparer: StringComparer.Ordinal)) {
            if (string.CompareOrdinal(strA: Path.GetFileName(path: file), strB: mine) >= 0) { break; }
            string[]? lines;

            try {
                using var stream = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: file, share: FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream: stream);

                lines = reader.ReadToEnd().Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: '\n');
            } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
                // Vanished, or being written: a ticket we cannot read yet is counted, never skipped.
                ahead += (File.Exists(path: file) ? 1 : 0);
                continue;
            }
            // A ticket whose metadata never became recognizable (its writer died mid-write) is dropped once it is old.
            var gone = (ProcessGone(lines: lines, now: now) ?? ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path: file)) > TimeSpan.FromSeconds(seconds: 10)));

            if (gone) {
                try { File.Delete(path: file); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { ahead++; }
            } else {
                ahead++;
            }
        }
        return ahead;
    }
    private static void Leave(string? ticket) {
        if (ticket is null) { return; }
        try { File.Delete(path: ticket); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static string[]? ReadHolder(string directory) {
        try {
            // A diagnostic reader must not prevent the holder from releasing its directory on Windows.
            using var stream = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: Path.Combine(path1: directory, path2: "holder"), share: FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream: stream);

            return reader.ReadToEnd().Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: '\n');
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { return null; }
    }

    /// <summary>Resolves the shared lease directory through the worktree's common Git directory.</summary>
    /// <param name="checkout">The working checkout.</param>
    /// <param name="cancellationToken">Cancels Git discovery.</param>
    /// <returns>The main checkout's lease directory.</returns>
    public static async Task<string> RootAsync(string checkout, CancellationToken cancellationToken = default) {
        var common = (await CliGit.CaptureAsync(arguments: ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken: cancellationToken, repository: checkout).ConfigureAwait(continueOnCapturedContext: false)).Trim();

        return Path.Combine(path1: Path.GetDirectoryName(path: common)!, path2: ".claude/leases");
    }

    private static CliLease? TryAcquire(string directory, string holder, TimeProvider clock) {
        FileStream gate;

        try {
            // Keep the guard inode: unlinking it lets two processes lock different files at the same path.
            gate = new FileStream(access: FileAccess.ReadWrite, mode: FileMode.OpenOrCreate, path: (directory + ".guard"), share: FileShare.None);
        } catch (IOException exception) when (((exception.HResult & 0xffff) is 11 or 32 or 33)) {
            return null;
        }
        try {
            // The exclusive guard is the ownership proof. Leftover metadata can name a still-live adapter
            // whose World exited while a reader delayed cleanup; it must not strand the released lease.
            Directory.CreateDirectory(path: directory);
            try {
                using var process = Process.GetCurrentProcess();

                File.WriteAllLines(path: Path.Combine(path1: directory, path2: "holder"), contents: [
                    holder,
                    clock.GetUtcNow().ToString(format: "O", formatProvider: CultureInfo.InvariantCulture),
                    Environment.ProcessId.ToString(provider: CultureInfo.InvariantCulture),
                    process.StartTime.ToUniversalTime().Ticks.ToString(provider: CultureInfo.InvariantCulture),
                ]);
                return new CliLease(directory: directory, gate: gate);
            } catch {
                try { Directory.Delete(path: directory, recursive: true); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { }
                throw;
            }
        } catch {
            gate.Dispose();
            throw;
        }
    }
    /// <summary>Decides from a queued ticket's metadata whether its process is gone; null when the metadata is unrecognized.</summary>
    private static bool? ProcessGone(string[] lines, DateTimeOffset now) {
        if ((lines.Length != 4) || !int.TryParse(s: lines[2], result: out var pid) || (pid <= 0) || !long.TryParse(s: lines[3], result: out var ticks)) { return null; }
        try {
            using var process = Process.GetProcessById(processId: pid);

            return (process.HasExited || (process.StartTime.ToUniversalTime().Ticks != ticks));
        } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } catch (System.ComponentModel.Win32Exception) {
            // The guard is exclusively ours, but the OS could not answer who owns the metadata. Only then use age.
            return (DateTimeOffset.TryParse(input: lines[1], formatProvider: CultureInfo.InvariantCulture, styles: DateTimeStyles.RoundtripKind, result: out var acquired) && ((now - acquired) > TimeSpan.FromHours(hours: 1)));
        }
    }

    /// <summary>Removes this holder's directory and releases the exclusive guard.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        try { Directory.Delete(path: m_directory, recursive: true); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { } finally { m_gate.Dispose(); }
    }
}
