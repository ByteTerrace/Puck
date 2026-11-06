using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions;
using Puck.Confinement;

namespace Puck.Cli;

/// <summary>Serializes lane work under a shared checkout's lease directory, rotating admission between lanes.</summary>
public sealed class CliLease : IDisposable {
    /// <summary>How long a run waits in a machine-wide lease queue before it is refused, when its caller names no other.
    /// Holders run long (a heavy suite can take half an hour under load, a GPU leg is time-boxed to twenty minutes) and
    /// queues are several deep, so refusing sooner throws away work that was only waiting its turn. A wait is never part
    /// of what a run's own timeout measures: the lease wait and the work under the lease have separate limits.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromHours(hours: 2);

    /// <summary>How often a wait that has said nothing new repeats where it stands, so a long queue is seen to be alive.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(minutes: 5);

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
    /// <param name="checkout">The checkout the holder works in, recorded beside its holder file and ticket so a reader
    /// (<c>puck lanes</c>) can tell which worktree holds or waits for the lease whatever the holder is called.</param>
    /// <param name="cancellationToken">Cancels acquisition.</param>
    /// <param name="ancestor">Recognizes a live ancestor holder by PID and start ticks, allowing a serialized child lease.</param>
    /// <param name="lane">The lane the holder works for, which admission rotates between; null takes the holder's first word.</param>
    /// <returns>The held lease.</returns>
    /// <exception cref="ArgumentException">The name, holder, lane, checkout or wait is invalid.</exception>
    /// <exception cref="TimeoutException">The lease remains held at the deadline.</exception>
    public static async Task<CliLease> AcquireAsync(string root, string name, string holder, TimeSpan wait, TimeProvider clock, string checkout, CancellationToken cancellationToken = default, Func<int, long, bool>? ancestor = null, string? lane = null) {
        if ((name is not ("build" or "gpu" or "heavy")) || string.IsNullOrWhiteSpace(value: holder) || holder.Contains(value: '\n') || holder.Contains(value: '\r') || (wait < TimeSpan.Zero)
            || ((lane is not null) && (string.IsNullOrWhiteSpace(value: lane) || lane.Contains(value: '\n') || lane.Contains(value: '\r')))
            || string.IsNullOrWhiteSpace(value: checkout) || checkout.Contains(value: '\n') || checkout.Contains(value: '\r')) {
            throw new ArgumentException(message: "A lease requires build, gpu or heavy, a single-line holder, lane and checkout, and a nonnegative wait.");
        }
        lane = (lane?.Trim() ?? DefaultLane(holder: holder));
        checkout = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: checkout)).Replace(oldChar: '\\', newChar: '/');
        Directory.CreateDirectory(path: root);
        var directory = Path.Combine(path1: root, path2: name);
        var started = clock.GetTimestamp();
        string? reported = null;
        var reportedAt = started;
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

                if ((ticket is null) || !File.Exists(path: ticket)) {
                    Stamp(lane: lane, rotation: (directory + ".rotation"));
                    ticket = Enqueue(checkout: checkout, clock: clock, holder: holder, lane: lane, queue: queue);
                }
                var place = Admission(now: clock.GetUtcNow(), rotation: (directory + ".rotation"), ticket: ticket);
                var ahead = place.Ahead;

                if (ahead == 0) {
                    var lease = TryAcquire(checkout: checkout, clock: clock, directory: directory, holder: holder, lane: lane);

                    if (lease is not null) { return lease; }
                }
                var remaining = (wait - clock.GetElapsedTime(startingTimestamp: started));

                if (remaining <= TimeSpan.Zero) { throw new TimeoutException(message: $"{name} lease is held or queued at {CliPaths.ToDisplay(fullPath: directory)}; use --wait <seconds> to wait."); }
                var waitingOn = ((metadata is { Length: > 0 }) ? metadata[0] : null);
                var report = $"lease: {holder} waiting for {name} lease{((waitingOn is null) ? ((ahead == 0) ? " held by another holder" : string.Empty) : $" held by {waitingOn}")}{((ahead == 0) ? string.Empty : $"; {ahead} queued ahead, lane {lane} is {place.Position} of {place.Lanes} in the round")}.";

                if ((reported != report) || (clock.GetElapsedTime(startingTimestamp: reportedAt) >= Heartbeat)) {
                    var waited = clock.GetElapsedTime(startingTimestamp: started);

                    Console.Error.WriteLine(value: ((waited < Heartbeat) ? report : $"{report[..^1]} (waited {waited.TotalMinutes.ToString(format: "0", provider: CultureInfo.InvariantCulture)} min)."));
                    reported = report;
                    reportedAt = clock.GetTimestamp();
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
    /// <param name="Since">When the waiter took the ticket, or null when its time is unreadable.</param>
    /// <param name="Checkout">The checkout the waiter works in, or null when its writer recorded none.</param>
    public sealed record LeaseTicket(string Id, string Holder, string Lane, int Ahead, int Position, int Lanes, DateTimeOffset? Since = null, string? Checkout = null);
    /// <summary>What a lease directory shows at one moment.</summary>
    /// <param name="Holder">The holder name, or null when nothing holds the lease.</param>
    /// <param name="Lane">The holder's lane, or null when nothing holds the lease.</param>
    /// <param name="HolderAlive">Whether a process holds the lease's guard lock, the lock that decides ownership; false
    /// for a holder file no process holds the lock for.</param>
    /// <param name="Queue">The live waiters in queue order.</param>
    /// <param name="Since">When the holder acquired the lease, or null when nothing holds it or its time is unreadable.</param>
    /// <param name="Checkout">The checkout the holder works in, or null when nothing holds the lease or its writer recorded
    /// none.</param>
    public sealed record LeaseState(string? Holder, string? Lane, bool HolderAlive, IReadOnlyList<LeaseTicket> Queue, DateTimeOffset? Since = null, string? Checkout = null);

    /// <summary>Reads a lease without changing it: no ticket is taken, no dead ticket or stale holder is removed, no rotation
    /// serial is written. Whether the holder is alive is read from its guard lock through a momentary shared hold, which an
    /// acquirer racing it retries past.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="name">The resource: build, gpu or heavy.</param>
    /// <param name="now">The time the metadata of a process the OS cannot name is judged against.</param>
    /// <returns>The holder and the live waiters; an absent directory reads as an idle lease.</returns>
    public static LeaseState Inspect(string root, string name, DateTimeOffset now) {
        var directory = Path.Combine(path1: root, path2: name);
        var holder = ReadHolder(directory: directory);
        string? holderName = null, holderLane = null, holderCheckout = null;
        var alive = false;

        if (holder is { Length: > 0 }) {
            holderName = holder[0];
            holderLane = ((holder.Length == 5) ? holder[4] : DefaultLane(holder: holder[0]));
            alive = GuardHeld(directory: directory, holder: holder, now: now);
            holderCheckout = ReadLines(path: Path.Combine(path1: directory, path2: CheckoutFile))?.FirstOrDefault();
        }
        var queue = (directory + ".queue");
        var tickets = new List<LeaseTicket>();

        if (Directory.Exists(path: queue)) {
            foreach (var file in Directory.EnumerateFiles(path: queue).Order(comparer: StringComparer.Ordinal)) {
                if ((ReadLines(path: file) is not { } lines) || (ProcessGone(lines: lines, now: now) != false) || (lines.Length is not (4 or 5))) { continue; }
                var place = Admission(drop: false, now: now, rotation: (directory + ".rotation"), ticket: file);

                tickets.Add(item: new LeaseTicket(
                    Ahead: place.Ahead,
                    Checkout: TicketCheckout(ticket: file, lines: lines),
                    Holder: lines[0],
                    Id: Path.GetFileName(path: file).TrimStart(trimChar: '0'),
                    Lane: ((lines.Length == 5) ? lines[4] : DefaultLane(holder: lines[0])),
                    Lanes: place.Lanes,
                    Position: place.Position,
                    Since: Stamped(lines: lines)
                ));
            }
        }
        return new LeaseState(Checkout: holderCheckout, Holder: holderName, HolderAlive: alive, Lane: holderLane, Queue: tickets, Since: ((holder is { Length: > 0 }) ? Stamped(lines: holder) : null));
    }

    // The checkout is recorded beside the holder file and each ticket rather than as a metadata line: CLIs built before
    // it count a holder's or ticket's lines (four or five), and one that read a sixth would drop the ticket and refuse
    // to nest a child under the holder. A holder's checkout is `checkout` in the holder directory; a ticket's is a file
    // of the ticket's name under `<name>.checkouts`, holding the ticket's process id, start ticks and checkout, so a
    // file left by a crashed waiter is never read for a later ticket that reuses its number.
    private const string CheckoutFile = "checkout";

    /// <summary>Whether a process holds a lease's guard: a momentary shared hold succeeds only when no exclusive holder has
    /// it. A guard that cannot be probed for another reason falls back to the holder file's process identity.</summary>
    private static bool GuardHeld(string directory, string[] holder, DateTimeOffset now) {
        var guard = (directory + ".guard");

        if (!File.Exists(path: guard)) { return false; }
        try {
            using var probe = FileLock.TryAcquireShared(path: guard);

            return (probe is null);
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            return (ProcessGone(lines: holder, now: now) == false);
        }
    }
    private static string TicketCheckoutPath(string ticket) {
        var queue = Path.GetDirectoryName(path: ticket)!;

        return Path.Combine(path1: (queue[..^".queue".Length] + ".checkouts"), path2: Path.GetFileName(path: ticket));
    }
    /// <summary>The checkout a ticket's sidecar records, when the sidecar names the ticket's own process.</summary>
    private static string? TicketCheckout(string ticket, string[] lines) =>
        ((ReadLines(path: TicketCheckoutPath(ticket: ticket)) is [var pid, var ticks, var checkout]) && (pid == lines[2]) && (ticks == lines[3])) ? checkout : null;
    /// <summary>Records a ticket's checkout, and drops sidecars whose ticket is gone.</summary>
    private static void RecordTicketCheckout(string ticket, string[] metadata, string checkout) {
        var path = TicketCheckoutPath(ticket: ticket);
        var directory = Path.GetDirectoryName(path: path)!;
        var queue = Path.GetDirectoryName(path: ticket)!;

        try {
            Directory.CreateDirectory(path: directory);
            AtomicFile.WriteAllText(path: path, contents: $"{metadata[2]}\n{metadata[3]}\n{checkout}\n");
            foreach (var sidecar in Directory.EnumerateFiles(path: directory)) {
                if ((Path.GetExtension(path: sidecar).Length == 0) && !File.Exists(path: Path.Combine(path1: queue, path2: Path.GetFileName(path: sidecar)))) {
                    try { File.Delete(path: sidecar); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    /// <summary>Reads a small metadata file's non-empty trimmed lines without stopping its writer from replacing or deleting
    /// it; null when it is absent or unreadable.</summary>
    private static string[]? ReadLines(string path) {
        try {
            using var stream = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: path, share: FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream: stream);

            return reader.ReadToEnd().Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: '\n');
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { return null; }
    }

    /// <summary>The acquisition or ticket time a holder file or ticket records on its second line.</summary>
    private static DateTimeOffset? Stamped(string[] lines) => (((lines.Length > 1) && DateTimeOffset.TryParse(input: lines[1], formatProvider: CultureInfo.InvariantCulture, styles: DateTimeStyles.RoundtripKind, result: out var at)) ? at : null);
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
    private static string Enqueue(string queue, string holder, string lane, string checkout, TimeProvider clock) {
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
                using (var stream = new FileStream(access: FileAccess.Write, mode: FileMode.CreateNew, path: path, share: FileShare.ReadWrite | FileShare.Delete))
                using (var writer = new StreamWriter(stream: stream)) {
                    foreach (var line in lines) { writer.WriteLine(value: line); }
                }
                RecordTicketCheckout(checkout: checkout, metadata: lines, ticket: path);
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
    /// <summary>Gives a lane that has no serial yet the highest serial any lane has had, as it first queues, so a lane that has
    /// never held the lease ranks by when it arrived instead of ahead of every lane that has waited: it still ranks behind
    /// each lane served longer ago, and ties with the lane served last, which the older ticket wins. The serial is created
    /// whole and never replaces one a holder has written.</summary>
    private static void Stamp(string rotation, string lane) {
        var path = LanePath(lane: lane, rotation: rotation);
        string? temporary = null;

        try {
            if (File.Exists(path: path)) { return; }
            Directory.CreateDirectory(path: rotation);
            var serial = Directory.EnumerateFiles(path: rotation).Where(predicate: file => (Path.GetExtension(path: file) != ".tmp")).Select(selector: Served).DefaultIfEmpty().Max();

            temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(path: temporary, contents: serial.ToString(provider: CultureInfo.InvariantCulture));
            File.Move(destFileName: path, overwrite: false, sourceFileName: temporary);
            temporary = null;
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            // A holder wrote this lane's serial first, or the directory refuses: the lane ranks as it did.
        } finally {
            if (temporary is not null) { try { File.Delete(path: temporary); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { } }
        }
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
        try { File.Delete(path: TicketCheckoutPath(ticket: ticket)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    // A diagnostic reader must not prevent the holder from releasing its directory on Windows, which ReadLines's sharing
    // allows.
    private static string[]? ReadHolder(string directory) => ReadLines(path: Path.Combine(path1: directory, path2: "holder"));

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
    public static Task<string> RootAsync(string checkout, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CliGit.TryResolveMainCheckout(repository: checkout, mainCheckout: out var mainCheckout)) { throw new InvalidOperationException(message: $"git names no common directory for {checkout}."); }
        return Task.FromResult(result: Path.Combine(path1: mainCheckout, path2: ".claude/leases"));
    }

    private static CliLease? TryAcquire(string directory, string holder, string lane, string checkout, TimeProvider clock) {
        // Keep the guard inode: unlinking it lets two processes lock different files at the same path.
        if (FileLock.TryAcquire(path: (directory + ".guard")) is not { } gate) {
            return null;
        }
        try {
            // The exclusive guard is the ownership proof. Leftover metadata can name a still-live adapter
            // whose World exited while a reader delayed cleanup; it must not strand the released lease.
            Directory.CreateDirectory(path: directory);
            try {
                // The checkout lands first, so a reader that sees the new holder sees its checkout.
                WriteMetadata(path: Path.Combine(path1: directory, path2: CheckoutFile), contents: (checkout + "\n"));
                WriteMetadata(path: Path.Combine(path1: directory, path2: "holder"), contents: string.Concat(values: Metadata(clock: clock, holder: holder, lane: lane).Select(selector: static line => (line + "\n"))));
                Serve(lane: lane, rotation: (directory + ".rotation"));
                return new CliLease(directory: directory, gate: gate);
            } catch {
                try { ConfinedDirectory.RemoveTree(path: directory); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) { }
                throw;
            }
        } catch {
            gate.Dispose();
            throw;
        }
    }
    /// <summary>Writes a guard owner's metadata file whole, so a reader never sees it half written. A leftover file that a
    /// reader holds open without delete sharing cannot be renamed over; only the guard owner writes here, so that file is
    /// overwritten in place, which such a reader permits.</summary>
    private static void WriteMetadata(string path, string contents) {
        try {
            AtomicFile.WriteAllText(path: path, contents: contents);
        } catch (IOException) when (File.Exists(path: path)) {
            File.WriteAllText(path: path, contents: contents);
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
        try { ConfinedDirectory.RemoveTree(path: m_directory); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) { } finally { m_gate.Dispose(); }
    }
}
