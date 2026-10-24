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

        do {
            cancellationToken.ThrowIfCancellationRequested();
            // An outer `puck lease` still owns the machine lease. Its descendants compete for one
            // child lease instead of waiting on their parent; siblings never borrow each other's hold.
            var metadata = ReadHolder(directory: directory);

            if ((metadata is { Length: 4 }) && int.TryParse(s: metadata[2], result: out var pid) && long.TryParse(s: metadata[3], result: out var ticks) && (ancestor?.Invoke(pid, ticks) == true)) {
                directory = Path.Combine(path1: directory, path2: "child");
                continue;
            }
            var lease = TryAcquire(clock: clock, directory: directory, holder: holder);

            if (lease is not null) { return lease; }
            var remaining = (wait - clock.GetElapsedTime(startingTimestamp: started));

            if (remaining <= TimeSpan.Zero) { throw new TimeoutException(message: $"{name} lease is held at {CliPaths.ToDisplay(fullPath: directory)}; use --wait <seconds> to wait."); }
            var waitingOn = ((metadata is { Length: > 0 }) ? metadata[0] : "another holder");

            if (reported != waitingOn) {
                Console.Error.WriteLine(value: $"lease: {holder} waiting for {name} lease held by {waitingOn}.");
                reported = waitingOn;
            }
            await Task.Delay(delay: ((remaining < TimeSpan.FromMilliseconds(milliseconds: 100)) ? remaining : TimeSpan.FromMilliseconds(milliseconds: 100)), timeProvider: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        } while (true);
    }

    private static string[]? ReadHolder(string directory) {
        try { return File.ReadAllLines(path: Path.Combine(path1: directory, path2: "holder")); } catch (IOException) { return null; }
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
            if (Directory.Exists(path: directory)) {
                if (!IsStale(directory: directory, now: clock.GetUtcNow())) { gate.Dispose(); return null; }
                Directory.Delete(path: directory, recursive: true);
            }
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
                Directory.Delete(path: directory, recursive: true);
                throw;
            }
        } catch {
            gate.Dispose();
            throw;
        }
    }
    private static bool IsStale(string directory, DateTimeOffset now) {
        var holder = Path.Combine(path1: directory, path2: "holder");
        // Missing or unrecognized metadata cannot prove that a holder is gone.
        if (!File.Exists(path: holder)) { return false; }
        var lines = File.ReadAllLines(path: holder);

        if ((lines.Length != 4) || !int.TryParse(s: lines[2], result: out var pid) || (pid <= 0) || !long.TryParse(s: lines[3], result: out var ticks)) { return false; }
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
        try { Directory.Delete(path: m_directory, recursive: true); } finally { m_gate.Dispose(); }
    }
}
