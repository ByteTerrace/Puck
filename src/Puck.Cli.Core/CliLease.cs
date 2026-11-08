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
    private readonly List<CliLease> m_children = [];

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
    /// <exception cref="LeaseProtocolException">The lease directory records a newer lease protocol than <see cref="Protocol"/>.</exception>
    public static Task<CliLease> AcquireAsync(string root, string name, string holder, TimeSpan wait, TimeProvider clock, string checkout, CancellationToken cancellationToken = default, Func<int, long, bool>? ancestor = null, string? lane = null) {
        if (name is not ("build" or "gpu" or "heavy")) { throw new ArgumentException(message: "Use AcquireQuietAsync for a measurement reservation.", paramName: nameof(name)); }
        return AcquireCoreAsync(ancestor: ancestor, cancellationToken: cancellationToken, checkout: checkout, clock: clock, holder: holder, lane: lane, name: name, quiet: null, root: root, wait: wait);
    }
    /// <summary>Queues a priority measurement window, then drains and holds all three resources. One deadline bounds acquisition;
    /// current work is never killed. The returned lease releases the resources before releasing the priority reservation.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="holder">The measurement holder.</param>
    /// <param name="wait">The total acquisition bound.</param>
    /// <param name="clock">The deadline clock.</param>
    /// <param name="checkout">The checkout the measurement runs in, recorded beside each lease it holds.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="ancestor">Recognizes nested measurement processes.</param>
    /// <param name="lane">The lane, or null for the holder's first word.</param>
    /// <returns>The priority reservation holding heavy, build and gpu.</returns>
    public static async Task<CliLease> AcquireQuietAsync(string root, string holder, TimeSpan wait, TimeProvider clock, string checkout, CancellationToken cancellationToken = default, Func<int, long, bool>? ancestor = null, string? lane = null) {
        var started = clock.GetTimestamp();
        var quiet = await AcquireCoreAsync(ancestor: ancestor, cancellationToken: cancellationToken, checkout: checkout, clock: clock, holder: holder, lane: lane, name: "quiet", quiet: null, root: root, wait: wait).ConfigureAwait(continueOnCapturedContext: false);

        try {
            foreach (var name in LeaseCommand.QuietOrder) {
                var remaining = (wait - clock.GetElapsedTime(startingTimestamp: started));

                quiet.m_children.Add(item: await AcquireCoreAsync(ancestor: ancestor, cancellationToken: cancellationToken, checkout: checkout, clock: clock, holder: holder, lane: lane, name: name, quiet: quiet, root: root, wait: ((remaining > TimeSpan.Zero) ? remaining : TimeSpan.Zero)).ConfigureAwait(continueOnCapturedContext: false));
            }
            return quiet;
        } catch { quiet.Dispose(); throw; }
    }

    private static async Task<CliLease> AcquireCoreAsync(string root, string name, string holder, TimeSpan wait, TimeProvider clock, string checkout, CancellationToken cancellationToken, Func<int, long, bool>? ancestor, string? lane, CliLease? quiet) {
        if ((name is not ("build" or "gpu" or "heavy" or "quiet")) || string.IsNullOrWhiteSpace(value: holder) || holder.Contains(value: '\n') || holder.Contains(value: '\r') || (wait < TimeSpan.Zero)
            || ((lane is not null) && (string.IsNullOrWhiteSpace(value: lane) || lane.Contains(value: '\n') || lane.Contains(value: '\r')))
            || string.IsNullOrWhiteSpace(value: checkout) || checkout.Contains(value: '\n') || checkout.Contains(value: '\r')) {
            throw new ArgumentException(message: "A lease requires build, gpu or heavy, a single-line holder, lane and checkout, and a nonnegative wait.");
        }
        lane = (lane?.Trim() ?? DefaultLane(holder: holder));
        checkout = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: checkout)).Replace(newChar: '/', oldChar: '\\');
        Directory.CreateDirectory(path: root);
        var request = new Request(Ancestor: Remembered(ancestor: ancestor), Checkout: checkout, Clock: clock, Holder: holder, Lane: lane, Name: name, Quiet: quiet, Root: root);
        var started = clock.GetTimestamp();
        string? reported = null;
        var reportedAt = started;
        Ticket? ticket = null;

        try {
            do {
                cancellationToken.ThrowIfCancellationRequested();
                var pass = await PassAsync(cancellationToken: cancellationToken, request: request, ticket: ticket).ConfigureAwait(continueOnCapturedContext: false);

                ticket = pass.Ticket;
                if (pass.Lease is { } lease) { return lease; }
                var remaining = (wait - clock.GetElapsedTime(startingTimestamp: started));

                if (remaining <= TimeSpan.Zero) { throw new TimeoutException(message: $"{name} lease is held or queued at {CliPaths.ToDisplay(fullPath: (pass.Directory ?? Path.Combine(path1: root, path2: name)))}; use --wait <seconds> to wait."); }
                var ahead = pass.Place.Ahead;
                var report = $"lease: {holder} waiting for {name} lease{((pass.Directory is null) ? " behind the admission lock" : (pass.Measurement ? " for the measurement window" : ((pass.HeldBy is null) ? ((ahead == 0) ? " held by another holder" : string.Empty) : $" held by {pass.HeldBy}")))}{((ahead == 0) ? string.Empty : $"; {ahead} queued ahead, lane {lane} is {pass.Place.Position} of {pass.Place.Lanes} in the round")}.";

                if ((reported != report) || (clock.GetElapsedTime(startingTimestamp: reportedAt) >= Heartbeat)) {
                    var waited = clock.GetElapsedTime(startingTimestamp: started);

                    Console.Error.WriteLine(value: ((waited < Heartbeat) ? report : $"{report[..^1]} (waited {waited.TotalMinutes.ToString(format: "0", provider: CultureInfo.InvariantCulture)} min)."));
                    reported = report;
                    reportedAt = clock.GetTimestamp();
                }
                await Task.Delay(delay: ((remaining < TimeSpan.FromMilliseconds(milliseconds: 100)) ? remaining : TimeSpan.FromMilliseconds(milliseconds: 100)), timeProvider: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            } while (true);
        } finally {
            Leave(ticket: ticket?.Path);
        }
    }

    /// <summary>What one acquisition asks for, fixed for its whole wait.</summary>
    private sealed record Request(string Root, string Name, string Holder, string Lane, string Checkout, TimeProvider Clock, CliLease? Quiet, Func<int, long, bool>? Ancestor);
    /// <summary>A waiter's ticket: its file and the queue directory it was taken in.</summary>
    private sealed record Ticket(string Path, string Queue);
    /// <summary>What one pass through the grant door decided: the lease when granted, the ticket the waiter holds now, and
    /// what a wait line names. <see cref="Directory"/> is null when the admission lock stayed busy past its bound.</summary>
    private readonly record struct Pass(CliLease? Lease, Ticket? Ticket, string? Directory, Place Place, string? HeldBy, bool Measurement);
    /// <summary>How an acquisition reaches its grant. A <see cref="Root"/> acquisition takes the lease itself and waits while a
    /// quiet reservation is queued or held. A <see cref="Nested"/> one runs under a live ancestor that holds the lease, proved by
    /// that holder's guard and its process identity, and takes a child lease that quiet never blocks: a current holder's
    /// process tree always finishes. A <see cref="Quiet"/> one is the measurement reservation itself or a resource it drains.</summary>
    private enum Route { Root, Nested, Quiet }

    /// <summary>The one decision of how an acquisition is granted, made afresh under the admission lock on every pass: an ancestor
    /// that held the lease when the waiter queued may have released it since, so a route is never carried over.</summary>
    private static (Route Route, string Directory) Decide(Request request, DateTimeOffset now) {
        var directory = Path.Combine(path1: request.Root, path2: request.Name);
        var nested = false;

        // An outer `puck lease` still owns the machine lease. Its descendants compete for one child lease beside it instead of
        // waiting on their parent; siblings never borrow each other's hold. The proof is the parent's guard, held now, and a
        // holder identity (process id and start ticks) that is this process's ancestor; metadata alone proves nothing.
        while ((request.Ancestor is { } ancestor) && (ReadHolder(directory: directory) is { } holder) && (Identity(lines: holder) is { } identity) && GuardHeld(directory: directory, holder: holder, now: now) && ancestor(identity.Pid, identity.Ticks)) {
            directory += ChildSuffix;
            nested = true;
        }
        return ((nested ? Route.Nested : (((request.Quiet is not null) || (request.Name == "quiet")) ? Route.Quiet : Route.Root)), directory);
    }

    /// <summary>A child lease's directory sits beside its parent's, never inside it, so a parent's release cannot delete a
    /// descendant's ticket, rotation or holder.</summary>
    private const string ChildSuffix = ".child";
    /// <summary>The lock every ticket registration, protocol stamp and grant is made under. It is held for one pass and never
    /// across a wait.</summary>
    private const string AdmissionGuard = "admission.guard";

    /// <summary>How long a pass waits for <see cref="AdmissionGuard"/>, which another process holds only for its own pass; a
    /// pass that cannot take it counts as a pass without a grant, so even <c>--wait 0</c> is decided on the lease itself.</summary>
    private static readonly TimeSpan AdmissionLockBound = TimeSpan.FromSeconds(seconds: 30);

    /// <summary>Remembers an ancestry verdict per holder identity for one acquisition, whose passes run one at a time, so the
    /// process-tree walk stays out of the admission lock after the first pass that meets a holder: a process identified by id
    /// and start ticks is this process's ancestor for its whole life or never, and the guard check beside the verdict on every
    /// pass says whether that holder still holds.</summary>
    private static Func<int, long, bool>? Remembered(Func<int, long, bool>? ancestor) {
        if (ancestor is null) { return null; }
        var verdicts = new Dictionary<(int, long), bool>();

        return (pid, ticks) => {
            if (!verdicts.TryGetValue(key: (pid, ticks), value: out var verdict)) { verdicts[(pid, ticks)] = verdict = ancestor(pid, ticks); }
            return verdict;
        };
    }
    /// <summary>The process a holder file or ticket names, by id and start ticks; null when its metadata is unrecognized.</summary>
    private static (int Pid, long Ticks)? Identity(string[] lines) =>
        (((lines.Length is (4 or 5)) && int.TryParse(s: lines[2], provider: CultureInfo.InvariantCulture, result: out var pid) && (pid > 0) && long.TryParse(s: lines[3], provider: CultureInfo.InvariantCulture, result: out var ticks)) ? (pid, ticks) : null);
    /// <summary>Takes the admission lock, bounded, and passes through the grant door once.</summary>
    private static async Task<Pass> PassAsync(Request request, Ticket? ticket, CancellationToken cancellationToken) {
        var asked = Stopwatch.GetTimestamp();

        using var admission = await FileLock.AcquireAsync(cancellationToken: cancellationToken, expired: () => (Stopwatch.GetElapsedTime(startingTimestamp: asked) >= AdmissionLockBound), path: Path.Combine(path1: request.Root, path2: AdmissionGuard)).ConfigureAwait(continueOnCapturedContext: false);

        return ((admission is null) ? new Pass(Directory: null, HeldBy: null, Lease: null, Measurement: false, Place: default, Ticket: ticket) : TryAcquire(request: request, ticket: ticket));
    }

    /// <summary>The lease protocol this CLI speaks. Every CLI copy on a machine shares the lease root, so two copies must agree on
    /// what its files mean: holders, tickets and sidecars, rotation, the quiet reservation, the child layout, readiness marks,
    /// and how host admission classifies the processes it waits on (<c>HostProcesses</c>). Raise it in any change to one of
    /// those meanings: a copy built before the change then refuses by name instead of deadlocking against a newer one.</summary>
    public const int Protocol = 2;

    private const string ProtocolFile = "protocol";

    /// <summary>Reads the lease protocol a lease root records: the highest any CLI that queued there spoke; zero when none has
    /// recorded one.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <returns>The recorded protocol.</returns>
    public static int RecordedProtocol(string root) =>
        (((ReadLines(path: Path.Combine(path1: root, path2: ProtocolFile)) is [var line, ..]) && int.TryParse(s: line, provider: CultureInfo.InvariantCulture, result: out var recorded)) ? recorded : 0);

    /// <summary>Refuses a lease root a newer CLI has stamped, and raises an older stamp to this CLI's protocol. Called only under
    /// <see cref="AdmissionGuard"/>, so a raise and every grant under the old protocol are ordered.</summary>
    private static void AgreeOnProtocol(string root) {
        var recorded = RecordedProtocol(root: root);

        if (recorded > Protocol) { throw new LeaseProtocolException(recorded: recorded, root: root, spoken: Protocol); }
        if (recorded < Protocol) { WriteMetadata(contents: (Protocol.ToString(provider: CultureInfo.InvariantCulture) + "\n"), path: Path.Combine(path1: root, path2: ProtocolFile)); }
    }
    /// <summary>Whether a quiet reservation is queued or held: a live quiet ticket, or the quiet guard taken.</summary>
    private static bool QuietPending(string root, DateTimeOffset now) {
        var directory = Path.Combine(path1: root, path2: "quiet");
        var queue = (directory + ".queue");

        PruneQuiet(directory: directory);

        // Placing a ticket name no waiter holds counts every live ticket ahead of it.
        return ((Directory.Exists(path: queue) && (Admission(now: now, rotation: (directory + ".rotation"), ticket: Path.Combine(path1: queue, path2: "probe")).Ahead > 0))
            || GuardHeld(directory: directory, holder: (ReadHolder(directory: directory) ?? []), now: now));
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

    /// <summary>Inspects a lease without taking a ticket, removing dead tickets or writing rotation. An unlocked quiet
    /// holder's metadata is pruned under its exclusive guard. Other holders' liveness is read with a momentary shared hold.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="name">The resource: build, gpu or heavy.</param>
    /// <param name="now">The time the metadata of a process the OS cannot name is judged against.</param>
    /// <returns>The holder and the live waiters; an absent directory reads as an idle lease.</returns>
    public static LeaseState Inspect(string root, string name, DateTimeOffset now) {
        var directory = Path.Combine(path1: root, path2: name);

        if (name == "quiet") { PruneQuiet(directory: directory); }
        var holder = ReadHolder(directory: directory);
        string? holderName = null, holderLane = null, holderCheckout = null;
        var alive = false;

        if (holder is { Length: > 0 }) {
            holderName = holder[0];
            holderLane = ((holder.Length == 5) ? holder[4] : DefaultLane(holder: holder[0]));
            alive = GuardHeld(directory: directory, holder: holder, now: now);
            holderCheckout = CheckoutOf(owner: holder, sidecar: Path.Combine(path1: directory, path2: CheckoutFile));
        }
        var queue = (directory + ".queue");
        var tickets = new List<LeaseTicket>();

        if (Directory.Exists(path: queue)) {
            foreach (var file in Directory.EnumerateFiles(path: queue).Order(comparer: StringComparer.Ordinal)) {
                if ((ReadLines(path: file) is not { } lines) || (ProcessGone(lines: lines, now: now) != false) || (lines.Length is not (4 or 5))) { continue; }
                var place = Admission(drop: false, now: now, rotation: (directory + ".rotation"), ticket: file);

                tickets.Add(item: new LeaseTicket(
                    Ahead: place.Ahead,
                    Checkout: CheckoutOf(owner: lines, sidecar: TicketCheckoutPath(ticket: file)),
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

    // The checkout is recorded beside the holder file and each ticket rather than as a metadata line, which keeps a holder or
    // ticket at four or five lines. A holder's checkout is `checkout` in the holder directory; a ticket's is a file of the
    // ticket's name under `<name>.checkouts`. Both hold the owner's process id, start ticks and checkout, and are read only
    // for the owner they name, so a file a crashed holder or waiter left is never read for the next one.
    private const string CheckoutFile = "checkout";

    /// <summary>The contents of a checkout sidecar: the owner's process id and start ticks, then the checkout.</summary>
    private static string Sidecar(string checkout, string[] metadata) => $"{metadata[2]}\n{metadata[3]}\n{checkout}\n";
    /// <summary>The checkout a sidecar records, when the sidecar names the process the holder or ticket metadata names.</summary>
    private static string? CheckoutOf(string sidecar, string[] owner) =>
        (((owner.Length >= 4) && (ReadLines(path: sidecar) is [var pid, var ticks, var checkout]) && (pid == owner[2]) && (ticks == owner[3])) ? checkout : null);
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
    // The same exclusive guard used by acquisition proves there is no holder and prevents a replacement racing
    // cleanup. Never unlink the guard itself, nor infer death from metadata age or a process inventory.
    private static void PruneQuiet(string directory) {
        if (!Directory.Exists(path: directory)) { return; }
        try {
            using var held = FileLock.TryAcquire(path: (directory + ".guard"));

            if (held is not null) { ConfinedDirectory.RemoveTree(path: directory); }
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) { }
    }
    private static string TicketCheckoutPath(string ticket) {
        var queue = Path.GetDirectoryName(path: ticket)!;

        return Path.Combine(path1: (queue[..^".queue".Length] + ".checkouts"), path2: Path.GetFileName(path: ticket));
    }
    /// <summary>Records a ticket's checkout, and drops sidecars whose ticket is gone.</summary>
    private static void RecordTicketCheckout(string ticket, string[] metadata, string checkout) {
        var path = TicketCheckoutPath(ticket: ticket);
        var directory = Path.GetDirectoryName(path: path)!;
        var queue = Path.GetDirectoryName(path: ticket)!;

        try {
            Directory.CreateDirectory(path: directory);
            AtomicFile.WriteAllText(path: path, contents: Sidecar(checkout: checkout, metadata: metadata));
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
        try { return (long.TryParse(s: ReadLines(path: path)?.FirstOrDefault(), provider: CultureInfo.InvariantCulture, result: out var serial) ? serial : 0); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { return 0; }
    }
    /// <summary>Gives a lane that has no serial yet the highest serial any lane has had, as it first queues, so a lane that has
    /// never held the lease ranks by when it arrived instead of ahead of every lane that has waited: it still ranks behind
    /// each lane served longer ago, and ties with the lane served last, which the older ticket wins. The serial is created
    /// whole and never replaces one a holder has written.</summary>
    private static void Stamp(string rotation, string lane) {
        var path = LanePath(lane: lane, rotation: rotation);

        try {
            if (File.Exists(path: path)) { return; }
            Directory.CreateDirectory(path: rotation);
            var serial = Directory.EnumerateFiles(path: rotation).Where(predicate: file => (Path.GetExtension(path: file) != ".tmp")).Select(selector: Served).DefaultIfEmpty().Max();

            using var pending = AtomicFile.StageAllText(path: path, contents: serial.ToString(provider: CultureInfo.InvariantCulture));

            pending.Commit(overwrite: false);
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            // A holder wrote this lane's serial first, or the directory refuses: the lane ranks as it did.
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
        if (!CliGit.TryResolveMainCheckout(mainCheckout: out var mainCheckout, repository: checkout)) { throw new InvalidOperationException(message: $"git names no common directory for {checkout}."); }
        return Task.FromResult(result: Path.Combine(path1: mainCheckout, path2: ".claude/leases"));
    }

    /// <summary>
    /// The single grant door, entered only under <see cref="AdmissionGuard"/>. It agrees on the protocol, decides the route
    /// (<see cref="Decide"/>), moves the waiter's ticket into that route's queue, and grants when the route allows it: a root
    /// acquisition never while a quiet reservation is queued or held, a nested or quiet one whatever quiet holds; every route
    /// but a quiet reservation's drains in rotation order; and only to the process that takes the exclusive guard. Every check
    /// is made again on every pass, under the same lock as the grant it decides.
    /// </summary>
    private static Pass TryAcquire(Request request, Ticket? ticket) {
        AgreeOnProtocol(root: request.Root);
        var now = request.Clock.GetUtcNow();

        var (route, directory) = Decide(now: now, request: request);
        var queue = (directory + ".queue");
        var rotation = (directory + ".rotation");

        if ((ticket is null) || (ticket.Queue != queue) || !File.Exists(path: ticket.Path)) {
            Leave(ticket: ticket?.Path);
            Stamp(lane: request.Lane, rotation: rotation);
            ticket = new Ticket(Path: Enqueue(checkout: request.Checkout, clock: request.Clock, holder: request.Holder, lane: request.Lane, queue: queue), Queue: queue);
        }
        // Admission rotates between lanes: the lease goes to the oldest ticket of the lane served least recently, and within a
        // lane first come, first served.
        var place = Admission(now: now, rotation: rotation, ticket: ticket.Path);
        var heldBy = (((ReadHolder(directory: directory) is { Length: > 0 } holder) && GuardHeld(directory: directory, holder: holder, now: now)) ? holder[0] : null);
        var waiting = new Pass(Directory: directory, HeldBy: heldBy, Lease: null, Measurement: false, Place: place, Ticket: ticket);

        if ((route == Route.Root) && QuietPending(now: now, root: request.Root)) { return (waiting with { Measurement = true }); }
        if ((place.Ahead > 0) && (request.Quiet is null)) { return waiting; }
        // Keep the guard inode: unlinking it lets two processes lock different files at the same path.
        if (FileLock.TryAcquire(path: (directory + ".guard")) is not { } gate) {
            return waiting;
        }
        try {
            // The exclusive guard is the ownership proof. Leftover metadata can name a still-live adapter
            // whose World exited while a reader delayed cleanup; it must not strand the released lease.
            Directory.CreateDirectory(path: directory);
            try {
                var metadata = Metadata(clock: request.Clock, holder: request.Holder, lane: request.Lane);

                // The checkout lands first, so a reader that sees the new holder sees its checkout.
                WriteMetadata(path: Path.Combine(path1: directory, path2: CheckoutFile), contents: Sidecar(checkout: request.Checkout, metadata: metadata));
                WriteMetadata(path: Path.Combine(path1: directory, path2: "holder"), contents: string.Concat(values: metadata.Select(selector: static line => (line + "\n"))));
                Serve(lane: request.Lane, rotation: rotation);
                return (waiting with { Lease = new CliLease(directory: directory, gate: gate) });
            } catch {
                try { ConfinedDirectory.RemoveTree(path: directory); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) { }
                throw;
            }
        } catch {
            gate.Dispose();
            throw;
        }
    }
    /// <summary>Publishes diagnostic metadata while holding the exclusive guard. A reader that denies deletion of an
    /// old metadata file must not keep the released guard: overwrite that file in place when replacement is refused.
    /// Such a reader can observe incomplete metadata, which is not authority; the guard alone excludes holders.</summary>
    private static void WriteMetadata(string path, string contents) {
        try {
            AtomicFile.WriteAllText(contents: contents, path: path);
        } catch (IOException) when (File.Exists(path: path)) {
            File.WriteAllText(contents: contents, path: path);
        }
    }
    /// <summary>Decides from a queued ticket's metadata whether its process is gone; null when the metadata is unrecognized.</summary>
    private static bool? ProcessGone(string[] lines, DateTimeOffset now) {
        if (Identity(lines: lines) is not { } identity) { return null; }
        try {
            using var process = Process.GetProcessById(processId: identity.Pid);

            return (process.HasExited || (process.StartTime.ToUniversalTime().Ticks != identity.Ticks));
        } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } catch (System.ComponentModel.Win32Exception) {
            // If the OS cannot inspect this queued process, fall back to the ticket's age.
            return (DateTimeOffset.TryParse(input: lines[1], formatProvider: CultureInfo.InvariantCulture, styles: DateTimeStyles.RoundtripKind, result: out var acquired) && ((now - acquired) > TimeSpan.FromHours(hours: 1)));
        }
    }

    /// <summary>Releases the exclusive guard, attempting to remove metadata whose deletion a reader may delay.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        for (var index = (m_children.Count - 1); (index >= 0); index--) { m_children[index].Dispose(); }
        try { ConfinedDirectory.RemoveTree(path: m_directory); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) { } finally { m_gate.Dispose(); }
    }
}
