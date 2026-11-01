using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions;
using Puck.Confinement;

namespace Puck.Cli;

/// <summary>Serializes lane work under a shared checkout's lease directory, rotating admission between lanes.</summary>
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
    /// <param name="lane">The lane the holder works for, which admission rotates between; null takes the holder's first word.</param>
    /// <returns>The held lease.</returns>
    /// <exception cref="ArgumentException">The name, holder, lane or wait is invalid.</exception>
    /// <exception cref="TimeoutException">The lease remains held at the deadline.</exception>
    public static async Task<CliLease> AcquireAsync(string root, string name, string holder, TimeSpan wait, TimeProvider clock, CancellationToken cancellationToken = default, Func<int, long, bool>? ancestor = null, string? lane = null) {
        if ((name is not ("build" or "gpu" or "heavy")) || string.IsNullOrWhiteSpace(value: holder) || holder.Contains(value: '\n') || holder.Contains(value: '\r') || (wait < TimeSpan.Zero)
            || ((lane is not null) && (string.IsNullOrWhiteSpace(value: lane) || lane.Contains(value: '\n') || lane.Contains(value: '\r')))) {
            throw new ArgumentException(message: "A lease requires build, gpu or heavy, a single-line holder and lane, and a nonnegative wait.");
        }
        lane = (lane?.Trim() ?? DefaultLane(holder: holder));
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

                if ((metadata is { Length: (4 or 5) }) && int.TryParse(s: metadata[2], result: out var pid) && long.TryParse(s: metadata[3], result: out var ticks) && (ancestor?.Invoke(pid, ticks) == true)) {
                    Leave(ticket: ticket);
                    ticket = null;
                    directory = Path.Combine(path1: directory, path2: "child");
                    continue;
                }
                // Admission rotates between lanes: the lease goes to the oldest ticket of the lane served least recently,
                // and within a lane first come, first served.
                var queue = (directory + ".queue");

                if ((ticket is null) || !File.Exists(path: ticket)) { ticket = Enqueue(clock: clock, holder: holder, lane: lane, queue: queue); }
                var place = Admission(now: clock.GetUtcNow(), rotation: (directory + ".rotation"), ticket: ticket);
                var ahead = place.Ahead;

                if (ahead == 0) {
                    var lease = TryAcquire(clock: clock, directory: directory, holder: holder, lane: lane);

                    if (lease is not null) { return lease; }
                }
                var remaining = (wait - clock.GetElapsedTime(startingTimestamp: started));

                if (remaining <= TimeSpan.Zero) { throw new TimeoutException(message: $"{name} lease is held or queued at {CliPaths.ToDisplay(fullPath: directory)}; use --wait <seconds> to wait."); }
                var waitingOn = ((metadata is { Length: > 0 }) ? metadata[0] : null);
                var report = $"lease: {holder} waiting for {name} lease{((waitingOn is null) ? ((ahead == 0) ? " held by another holder" : string.Empty) : $" held by {waitingOn}")}{((ahead == 0) ? string.Empty : $"; {ahead} queued ahead, lane {lane} is {place.Position} of {place.Lanes} in the round")}.";

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

    /// <summary>One live waiter in a lease queue.</summary>
    /// <param name="Id">The ticket's sequence number, without padding.</param>
    /// <param name="Holder">The holder name the waiter gave.</param>
    /// <param name="Lane">The lane the waiter works for.</param>
    /// <param name="Ahead">How many admissions come before this ticket's.</param>
    /// <param name="Position">The ticket's lane's turn among the queued lanes in the current round.</param>
    /// <param name="Lanes">How many lanes are queued.</param>
    public sealed record LeaseTicket(string Id, string Holder, string Lane, int Ahead, int Position, int Lanes);
    /// <summary>What a lease directory shows at one moment.</summary>
    /// <param name="Holder">The holder name, or null when nothing holds the lease.</param>
    /// <param name="Lane">The holder's lane, or null when nothing holds the lease.</param>
    /// <param name="HolderAlive">Whether the holder's process still runs; false for a holder file left by an exited process.</param>
    /// <param name="Queue">The live waiters in queue order.</param>
    public sealed record LeaseState(string? Holder, string? Lane, bool HolderAlive, IReadOnlyList<LeaseTicket> Queue);

    /// <summary>Reads a lease without changing it: no ticket is taken, no dead ticket or stale holder is removed, no rotation serial is written.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="name">The resource: build, gpu or heavy.</param>
    /// <param name="now">The time the metadata of a process the OS cannot name is judged against.</param>
    /// <returns>The holder and the live waiters; an absent directory reads as an idle lease.</returns>
    public static LeaseState Inspect(string root, string name, DateTimeOffset now) {
        var directory = Path.Combine(path1: root, path2: name);
        var holder = ReadHolder(directory: directory);
        string? holderName = null, holderLane = null;
        var alive = false;

        if (holder is { Length: > 0 }) {
            holderName = holder[0];
            holderLane = ((holder.Length == 5) ? holder[4] : DefaultLane(holder: holder[0]));
            alive = (ProcessGone(lines: holder, now: now) == false);
        }
        var queue = (directory + ".queue");
        var tickets = new List<LeaseTicket>();

        if (Directory.Exists(path: queue)) {
            foreach (var file in Directory.EnumerateFiles(path: queue).Order(comparer: StringComparer.Ordinal)) {
                string[] lines;

                try {
                    using var stream = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: file, share: FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream: stream);

                    lines = reader.ReadToEnd().Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: '\n');
                } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { continue; }
                if ((ProcessGone(lines: lines, now: now) != false) || (lines.Length is not (4 or 5))) { continue; }
                var place = Admission(drop: false, now: now, rotation: (directory + ".rotation"), ticket: file);

                tickets.Add(item: new LeaseTicket(
                    Ahead: place.Ahead,
                    Holder: lines[0],
                    Id: Path.GetFileName(path: file).TrimStart(trimChar: '0'),
                    Lane: ((lines.Length == 5) ? lines[4] : DefaultLane(holder: lines[0])),
                    Lanes: place.Lanes,
                    Position: place.Position
                ));
            }
        }
        return new LeaseState(Holder: holderName, HolderAlive: alive, Lane: holderLane, Queue: tickets);
    }

    /// <summary>The lane a holder works for when none is named: the first word of its name.</summary>
    private static string DefaultLane(string holder) => holder.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ((char[]?)null))[0];
    /// <summary>The metadata a holder file and a ticket share: holder, UTC time, process id and process start ticks, then the lane
    /// only when it is not the holder's first word.</summary>
    private static string[] Metadata(string holder, string lane, TimeProvider clock) {
        using var process = Process.GetCurrentProcess();
        string[] lines = [
            holder,
            clock.GetUtcNow().ToString(format: "O", formatProvider: CultureInfo.InvariantCulture),
            Environment.ProcessId.ToString(provider: CultureInfo.InvariantCulture),
            process.StartTime.ToUniversalTime().Ticks.ToString(provider: CultureInfo.InvariantCulture),
        ];

        return ((lane == DefaultLane(holder: holder)) ? lines : [.. lines, lane]);
    }
    /// <summary>Takes the next ticket in a queue directory: a file named by a zero-padded sequence, holding the same lines as a holder.</summary>
    private static string Enqueue(string queue, string holder, string lane, TimeProvider clock) {
        Directory.CreateDirectory(path: queue);
        var lines = Metadata(clock: clock, holder: holder, lane: lane);
        var floor = 1L;
        var denied = 0;

        while (true) {
            var next = floor;

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
            // Windows refuses a name whose deleted file is still held open elsewhere, and the listing may not show it:
            // take the next sequence, and give up only when the directory itself refuses.
            catch (UnauthorizedAccessException) when ((++denied <= 64)) { floor = (next + 1); }
        }
    }

    /// <summary>Where a ticket stands in the admission order: tickets ahead, its lane's turn among the queued lanes, and how many lanes are queued.</summary>
    private readonly record struct Place(int Ahead, int Position, int Lanes);

    /// <summary>
    /// Replays the admission order over the live tickets, skipping each whose process is gone and deleting its file only when
    /// <c>drop</c> is set, so a read-only inspection leaves the queue as it found it. Each round admits the oldest
    /// ticket of the lane served least recently, then counts that lane as the most recently served; a ticket whose lane is
    /// not yet readable is a lane of its own that has never been served.
    /// </summary>
    private static Place Admission(string ticket, string rotation, DateTimeOffset now, bool drop = true) {
        var mine = Path.GetFileName(path: ticket);
        var queues = new Dictionary<string, Queue<string>>(comparer: StringComparer.Ordinal);
        var live = 0;
        string? mineLane = null;

        foreach (var file in Directory.EnumerateFiles(path: Path.GetDirectoryName(path: ticket)!).Order(comparer: StringComparer.Ordinal)) {
            var id = Path.GetFileName(path: file);
            string[]? lines = null;
            var recognized = false;

            try {
                using var stream = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: file, share: FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream: stream);

                lines = reader.ReadToEnd().Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: '\n');
            } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
                // Vanished, being written, or deleted but still held open (Windows answers access denied): a ticket we
                // cannot read yet is counted, never skipped.
                if (!File.Exists(path: file)) { continue; }
            }
            if (lines is not null) {
                var exited = ProcessGone(lines: lines, now: now);

                recognized = (exited is not null);
                // A ticket whose metadata never became recognizable (its writer died mid-write) is dropped once it is old.
                if (exited ?? ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path: file)) > TimeSpan.FromSeconds(seconds: 10))) {
                    if (!drop) { continue; }
                    // A ticket the OS will not let go of yet (Windows holds a deleted file open elsewhere) stays counted.
                    try { File.Delete(path: file); continue; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            var key = (recognized ? ("lane:" + ((lines!.Length == 5) ? lines[4] : DefaultLane(holder: lines[0]))) : ("ticket:" + id));

            if (!queues.TryGetValue(key: key, value: out var queue)) { queues[key] = queue = new Queue<string>(); }
            queue.Enqueue(item: id);
            live++;
            if (id == mine) { mineLane = key; }
        }
        if (mineLane is null) { return new Place(Ahead: live, Lanes: queues.Count, Position: queues.Count); }
        var served = queues.Keys.ToDictionary(keySelector: key => key, elementSelector: key => (key.StartsWith(comparisonType: StringComparison.Ordinal, value: "lane:") ? Served(path: LanePath(lane: key[5..], rotation: rotation)) : 0L), comparer: StringComparer.Ordinal);
        var next = (served.Values.DefaultIfEmpty().Max() + 1);
        var ahead = 0;
        var lanes = queues.Count;
        var position = 0;

        for (var round = 0; ; round++) {
            var order = queues.Keys.OrderBy(keySelector: key => served[key]).ThenBy(keySelector: key => queues[key].Peek(), comparer: StringComparer.Ordinal).ToList();

            if (round == 0) { position = (order.IndexOf(item: mineLane) + 1); }
            var turn = order[0];

            if (queues[turn].Peek() == mine) { return new Place(Ahead: ahead, Lanes: lanes, Position: position); }
            ahead++;
            queues[turn].Dequeue();
            served[turn] = next++;
            if (queues[turn].Count == 0) { queues.Remove(key: turn); }
        }
    }
    private static string LanePath(string lane, string rotation) => Path.Combine(path1: rotation, path2: Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: lane))));
    /// <summary>Reads a lane's serial: the order in which it last held the lease; zero when it never has.</summary>
    private static long Served(string path) {
        try { return (long.TryParse(s: File.ReadLines(path: path).FirstOrDefault(), provider: CultureInfo.InvariantCulture, result: out var serial) ? serial : 0); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { return 0; }
    }
    /// <summary>Records that a lane holds the lease now, as the highest serial any lane has had. Only the guard's owner writes.</summary>
    private static void Serve(string rotation, string lane) {
        try {
            Directory.CreateDirectory(path: rotation);
            var next = (Directory.EnumerateFiles(path: rotation).Where(predicate: file => (Path.GetExtension(path: file) != ".tmp")).Select(selector: Served).DefaultIfEmpty().Max() + 1);
            var path = LanePath(lane: lane, rotation: rotation);

            // Replaced whole, so a waiter reading the serial never sees it half written.
            AtomicFile.WriteAllText(path: path, contents: next.ToString(provider: CultureInfo.InvariantCulture));
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
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

    /// <summary>Names the lane a checkout's own runs take the lease for: its directory name, one lane per worktree.</summary>
    /// <param name="checkout">The working checkout.</param>
    /// <returns>The checkout directory's name, or <c>checkout</c> for a root directory.</returns>
    public static string LaneOf(string checkout) {
        var name = Path.GetFileName(path: Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: checkout))).Trim();

        return ((name.Length == 0) ? "checkout" : name);
    }
    /// <summary>Resolves the shared lease directory through the worktree's common Git directory.</summary>
    /// <param name="checkout">The working checkout.</param>
    /// <param name="cancellationToken">Cancels Git discovery.</param>
    /// <returns>The main checkout's lease directory.</returns>
    public static async Task<string> RootAsync(string checkout, CancellationToken cancellationToken = default) {
        var common = (await CliGit.CaptureAsync(arguments: ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken: cancellationToken, repository: checkout).ConfigureAwait(continueOnCapturedContext: false)).Trim();

        return Path.Combine(path1: Path.GetDirectoryName(path: common)!, path2: ".claude/leases");
    }

    private static CliLease? TryAcquire(string directory, string holder, string lane, TimeProvider clock) {
        // Keep the guard inode: unlinking it lets two processes lock different files at the same path.
        if (FileLock.TryAcquire(path: (directory + ".guard")) is not { } gate) {
            return null;
        }
        try {
            // The exclusive guard is the ownership proof. Leftover metadata can name a still-live adapter
            // whose World exited while a reader delayed cleanup; it must not strand the released lease.
            Directory.CreateDirectory(path: directory);
            try {
                File.WriteAllLines(path: Path.Combine(path1: directory, path2: "holder"), contents: Metadata(clock: clock, holder: holder, lane: lane));
                Serve(lane: lane, rotation: (directory + ".rotation"));
                return new CliLease(directory: directory, gate: gate);
            } catch {
                try { ConfinedDirectory.RemoveTree(path: directory); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { }
                throw;
            }
        } catch {
            gate.Dispose();
            throw;
        }
    }
    /// <summary>Decides from a queued ticket's metadata whether its process is gone; null when the metadata is unrecognized.</summary>
    private static bool? ProcessGone(string[] lines, DateTimeOffset now) {
        if ((lines.Length is not (4 or 5)) || !int.TryParse(s: lines[2], result: out var pid) || (pid <= 0) || !long.TryParse(s: lines[3], result: out var ticks)) { return null; }
        try {
            using var process = Process.GetProcessById(processId: pid);

            return (process.HasExited || (process.StartTime.ToUniversalTime().Ticks != ticks));
        } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } catch (System.ComponentModel.Win32Exception) {
            // If the OS cannot inspect this queued process, fall back to the ticket's age.
            return (DateTimeOffset.TryParse(input: lines[1], formatProvider: CultureInfo.InvariantCulture, styles: DateTimeStyles.RoundtripKind, result: out var acquired) && ((now - acquired) > TimeSpan.FromHours(hours: 1)));
        }
    }

    /// <summary>Releases the exclusive guard, attempting to remove metadata whose deletion a reader may delay.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        try { ConfinedDirectory.RemoveTree(path: m_directory); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { } finally { m_gate.Dispose(); }
    }
}
