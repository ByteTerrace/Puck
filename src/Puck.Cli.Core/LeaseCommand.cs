using System.CommandLine;

namespace Puck.Cli;

/// <summary>Waits for the machine to be idle and stay idle.</summary>
/// <param name="settle">How long the machine must stay idle without a break.</param>
/// <param name="timeout">How long to wait, at most, for that.</param>
/// <param name="progress">Receives a line naming what the wait is on whenever that changes.</param>
/// <param name="cancellationToken">Stops the wait.</param>
/// <returns><see langword="null"/> once the machine has been idle for <paramref name="settle"/>; otherwise why it was not.</returns>
public delegate Task<string?> LeaseSettle(TimeSpan settle, TimeSpan timeout, TextWriter progress, CancellationToken cancellationToken);

/// <summary>Runs one command while holding a shared machine-work lease.</summary>
public static class LeaseCommand {
    /// <summary>The resources a priority quiet reservation drains and holds, releasing in reverse order.</summary>
    public static readonly IReadOnlyList<string> QuietOrder = ["heavy", "build", "gpu"];

    private static async Task<int> RunAsync(ParseResult parse, string verb, IReadOnlyList<string> names, Option<string> holder, Option<string> lane, Option<int> wait, Option<HostStartKind?>? kind, Argument<string[]> arguments, TimeProvider clock, string? leaseRoot, Option<int>? settle, LeaseSettle? settleWait, HostAdmit? admit, CancellationToken cancellationToken, Option<bool>? exclusive = null) {
        var seconds = parse.GetValue(option: wait);
        var alone = ((exclusive is not null) && parse.GetValue(option: exclusive));
        var settled = ((settle is null) ? 0 : parse.GetValue(option: settle));
        var selectedKind = ((kind is null) ? null : parse.GetValue(option: kind));
        if (selectedKind is { } selected && !Enum.IsDefined(value: selected)) { return CliExit.Refuse(verb: verb, what: "--kind", why: "must name a declared HostStartKind."); }

        var directory = leaseRoot;

        if (directory is null) {
            directory = await CliLease.RootAsync(checkout: RepositoryPaths.RequireRoot(), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }

        var named = parse.GetValue(option: lane);
        var bound = TimeSpan.FromSeconds(seconds: seconds);
        var started = clock.GetTimestamp();
        var held = new List<IDisposable>();

        try {
            if (settleWait is not null) {
                held.Add(await CliLease.AcquireQuietAsync(root: directory, holder: parse.GetValue(holder)!, wait: bound, clock: clock, checkout: (RepositoryPaths.FindRoot() ?? Environment.CurrentDirectory), cancellationToken: cancellationToken, ancestor: ProcessInspection.IsAncestor, lane: named).ConfigureAwait(false));
            }
            // One wait bounds the whole acquisition: each lease gets what the earlier ones left.
            foreach (var name in (settleWait is null ? names : [])) {
                var remaining = TimeSpan.FromTicks( Math.Max(val1: 0, val2: (bound - clock.GetElapsedTime(startingTimestamp: started)).Ticks));
                held.Add(item: await LeasedAdmission.AcquireAsync(
                    root: directory, name: name, holder: parse.GetValue(option: holder)!, lane: named,
                    admit: admit, checkout: (RepositoryPaths.FindRoot() ?? Environment.CurrentDirectory), step: $"{verb} {parse.GetValue(option: holder)}", device: name == "gpu", heavySuite: name == "heavy",
                    kind: (selectedKind ?? (name == "gpu" ? HostStartKind.WindowedWorld : name == "heavy" ? HostStartKind.HeavySuite : HostStartKind.Step)),
                    clock: clock, bound: remaining, progress: Console.Error, stage: null, cancellationToken: cancellationToken, exclusive: alone).ConfigureAwait(false));
            }

            if ((settleWait is not null) && (settled > 0)) {
                var timeout = (bound + TimeSpan.FromSeconds(seconds: settled));

                if (await settleWait(TimeSpan.FromSeconds(seconds: settled), timeout, Console.Error, cancellationToken).ConfigureAwait(continueOnCapturedContext: false) is { } why) {
                    return CliExit.Refuse(verb: verb, what: "--settle", why: why);
                }
            }

            var tokens = parse.GetValue(argument: arguments)!;
            var run = await CliProcess.RunAsync(fileName: tokens[0], arguments: tokens.Skip(count: 1), capture: false, clock: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            return run.ExitCode;
        } finally {
            for (var index = (held.Count - 1); (index >= 0); index--) {
                held[index].Dispose();
            }
        }
    }

    /// <summary>Renders every lease's holder and queue, as <c>puck lease list</c> prints them.</summary>
    /// <param name="clock">The clock hold and wait times are measured against.</param>
    /// <param name="root">The shared lease directory.</param>
    /// <returns>One line per lease (build, gpu, heavy), each followed by a line per waiter in admission order, then a line
    /// per idle start (<see cref="StartIdleness"/>).</returns>
    public static string Render(TimeProvider clock, string root) {
        var now = clock.GetUtcNow();
        var text = new System.Text.StringBuilder();

        if (CliLease.RecordedProtocol(root: root) is var recorded && (recorded > CliLease.Protocol)) {
            _ = text.Append(value: $"protocol: the lease directory records lease protocol {recorded}, and this CLI speaks {CliLease.Protocol}; rebuild this CLI before queuing\n");
        }
        foreach (var name in new[] { "quiet", "build", "gpu", "heavy" }) {
            var state = CliLease.Inspect(name: name, now: now, root: root);

            _ = text.Append(value: name).Append(value: ((state.Holders.Count == 0) ? ": idle" : ": held by "));
            foreach (var (held, index) in state.Holders.Select(selector: static (held, index) => (held, index))) {
                _ = text.Append(value: ((index == 0) ? string.Empty : "; and by ")).Append(value: held.Holder).Append(value: " (lane ").Append(value: held.Lane).Append(value: ") for ").Append(value: Age(since: held.Since, now: now));
                if (held.Checkout is { } checkout) { _ = text.Append(value: " from ").Append(value: checkout); }
                if (held.Exclusive && (name == "heavy")) { _ = text.Append(value: ", exclusive"); }
                if (!held.Alive) { _ = text.Append(value: ", stale: no process holds its lock"); }
            }
            _ = text.Append(value: ((state.Queue.Count == 0) ? "\n" : $"; {state.Queue.Count} queued\n"));
            foreach (var (ticket, index) in state.Queue.OrderBy(keySelector: static ticket => ticket.Ahead).ThenBy(keySelector: static ticket => ticket.Id, comparer: StringComparer.Ordinal).Select(selector: static (ticket, index) => (ticket, index))) {
                _ = text.Append(value: $"  {(index + 1)}. {ticket.Holder} (lane {ticket.Lane}){((ticket.Checkout is null) ? string.Empty : $" from {ticket.Checkout}")} waiting {Age(since: ticket.Since, now: now)}; lane {ticket.Position} of {ticket.Lanes} in the round, {ticket.Ahead} ahead{(ticket.AdmissionBlocked ? "; waiting for host capacity" : string.Empty)}\n");
            }
        }
        foreach (var start in IdleStarts(root: root)) {
            _ = text.Append(value: $"idle start: {start.Kind} PID {start.ProcessId} has used no CPU for {Age(since: start.Since, now: now)}; admission no longer waits for it to grow or exit\n");
        }
        return text.ToString();
    }
    // The tracked starts running that have gone idle, oldest first; reading them removes nothing, so listing stays a read.
    private static IEnumerable<IdleStart> IdleStarts(string root) =>
        StartIdleness.Among(root: root, running: new Dictionary<int, long>(), prune: false).Values.OrderBy(keySelector: static start => start.Since).ThenBy(keySelector: static start => start.ProcessId);

    /// <summary>Writes every lease's holder and queue as <c>puck lease list --json</c> does: one record per lease, in the
    /// order <see cref="Render"/> prints them.</summary>
    /// <param name="clock">The clock hold and wait times are measured against.</param>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="output">The JSON-lines output the records go to.</param>
    public static void WriteRecords(TimeProvider clock, string root, CliJsonLines output) {
        var now = clock.GetUtcNow();

        foreach (var name in new[] { "quiet", "build", "gpu", "heavy" }) {
            var state = CliLease.Inspect(name: name, now: now, root: root);
            var queue = new System.Text.Json.Nodes.JsonArray();

            foreach (var ticket in state.Queue.OrderBy(keySelector: static ticket => ticket.Ahead).ThenBy(keySelector: static ticket => ticket.Id, comparer: StringComparer.Ordinal)) {
                queue.Add(item: new System.Text.Json.Nodes.JsonObject {
                    ["holder"] = ticket.Holder,
                    ["lane"] = ticket.Lane,
                    ["checkout"] = ticket.Checkout,
                    ["since"] = ticket.Since,
                    ["ahead"] = ticket.Ahead,
                    ["position"] = ticket.Position,
                    ["lanes"] = ticket.Lanes,
                    ["admissionBlocked"] = ticket.AdmissionBlocked,
                });
            }

            var holders = new System.Text.Json.Nodes.JsonArray();

            foreach (var held in state.Holders) {
                holders.Add(item: new System.Text.Json.Nodes.JsonObject {
                    ["holder"] = held.Holder,
                    ["lane"] = held.Lane,
                    ["checkout"] = held.Checkout,
                    ["since"] = held.Since,
                    ["alive"] = held.Alive,
                    ["exclusive"] = held.Exclusive,
                });
            }
            output.Write(record: new System.Text.Json.Nodes.JsonObject {
                ["lease"] = name,
                ["holder"] = state.Holder,
                ["lane"] = state.Lane,
                ["checkout"] = state.Checkout,
                ["since"] = state.Since,
                ["alive"] = ((state.Holder is null) ? null : state.HolderAlive),
                ["holders"] = holders,
                ["queue"] = queue,
                ["recordedProtocol"] = CliLease.RecordedProtocol(root: root),
                ["protocol"] = CliLease.Protocol,
            });
        }
        foreach (var start in IdleStarts(root: root)) {
            output.Write(record: new System.Text.Json.Nodes.JsonObject {
                ["idleStart"] = start.Kind.ToString(),
                ["processId"] = start.ProcessId,
                ["since"] = start.Since,
            });
        }
    }
    private static string Age(DateTimeOffset? since, DateTimeOffset now) {
        if (since is not { } at) { return "an unknown time"; }
        var age = (now - at);

        if (age < TimeSpan.Zero) { age = TimeSpan.Zero; }
        return ((age.TotalHours >= 1) ? $"{((int)age.TotalHours)}h {age.Minutes}m" : ((age.TotalMinutes >= 1) ? $"{age.Minutes}m {age.Seconds}s" : $"{age.Seconds}s"));
    }

    /// <summary>Creates the build, gpu, heavy, list and quiet lease commands.</summary>
    /// <param name="clock">The CLI clock.</param>
    /// <param name="leaseRoot">An explicit lease directory, or null for the main checkout's shared directory.</param>
    /// <param name="settleWait">How <c>puck lease quiet</c> tells the machine is idle (the host load sampler, which lives above this assembly); null leaves the quiet command out.</param>
    /// <param name="admit">The host admission <c>puck lease heavy</c> and <c>puck lease gpu</c> wait on through <see cref="LeasedAdmission"/> before they run the command; null admits at once.</param>
    /// <returns>The lease command.</returns>
    public static Command Create(TimeProvider clock, string? leaseRoot = null, LeaseSettle? settleWait = null, HostAdmit? admit = null) {
        var root = new Command(description: "Run a command under a World-build, GPU or heavy-work lease (heavy holders share it while host headroom covers each), or all three once the machine is idle.", name: "lease");

        foreach (var name in new[] { "build", "gpu", "heavy" }) {
            var holder = CliOptions.Name(description: "Holder name recorded with the holder process and acquisition time; its first word is the lane unless --lane names one.", name: "--holder", required: true);
            var lane = CliOptions.Name(description: "The lane admission rotates between; defaults to the first word of --holder.", name: "--lane");
            var wait = CliOptions.Count(defaultValue: 0, description: "Seconds to wait for the lease; zero refuses immediately (default).", minimum: 0, name: "--wait");
            var kind = ((name is "gpu" or "heavy") ? new Option<HostStartKind?>(name: "--kind") { Description = "Expected peak class of the child command (default WindowedWorld for gpu, HeavySuite for heavy); Step for a bounded focused check. Changes the memory estimate, not lease ownership or admission thresholds." } : null);
            var exclusive = ((name == "heavy") ? new Option<bool>(name: "--exclusive") { Description = "Run alone: wait for every other heavy holder to finish, and keep the lease from sharing while the command runs." } : null);
            var arguments = CliOptions.ForwardedCommand(description: "Executable and arguments after --; no shell expansion is performed.");
            var command = new Command(description: $"Hold the {name} lease until the command exits or is cancelled.", name: name) { holder, lane, wait, arguments };
            if (kind is not null) { command.Options.Add(item: kind); }
            if (exclusive is not null) { command.Options.Add(item: exclusive); }

            command.SetAction(action: (parse, cancellationToken) => RunAsync(arguments: arguments, cancellationToken: cancellationToken, clock: clock, holder: holder, kind: kind, lane: lane, leaseRoot: leaseRoot, names: [name], parse: parse, settle: null, settleWait: null, admit: ((name is "heavy" or "gpu") ? admit : null), verb: ("lease " + name), wait: wait, exclusive: exclusive));
            command.Detail(detail: (("Use: puck lease " + name) + ((name == "heavy") ? " --holder <lane> [--lane <lane>] [--wait <seconds>] [--kind <kind>] [--exclusive]" : (name == "gpu") ? " --holder <lane> [--lane <lane>] [--wait <seconds>] [--kind <kind>]" : " --holder <lane> [--lane <lane>] [--wait <seconds>]") + " -- <executable> <arguments...>\nThe shared main checkout owns .claude/leases/. Waiters take tickets in .claude/leases/<name>.queue and are admitted by rotation between lanes (the first word of --holder, or --lane): the oldest ticket of the lane that held the lease least recently, first come, first served within a lane; a ticket of an exited process is dropped. An exclusive guard per slot proves ownership; stale metadata never prevents acquisition." + ((name is "heavy" or "gpu") ? " The ticket waits without a hold for host headroom, the admission `puck gate` uses (free memory, a commit charge under its share of the commit limit, commit headroom for the start's expected peak plus what every running start and every other admitted hold still has to grow, free disk" + ((name == "gpu") ? ", an idle GPU" : ", and CPU under the capacity threshold while another heavy holder runs") + "), within what --wait has left, and is refused (exit 2, ticket withdrawn) when it does not return. A fresh nonwaiting reservation under the grant guard parks the same ticket again if headroom changed, without serving its lane; the wait lines go to standard error. The hold records its --kind and whether it opens a device beside its holder, so other processes' admission counts its growth from the grant." : string.Empty) + ((name == "heavy") ? $" Heavy holders share the lease, up to {CliLease.SharedSlots} at once, while admission covers each new one; --exclusive waits for every other holder to finish and keeps the lease to itself, and a quiet window drains every holder." : string.Empty) + " Child failure preserves its exit code; cancellation kills the child tree and releases the lease."));
            root.Subcommands.Add(item: command);
        }

        var listJson = CliOptions.Json(description: "Write one JSON object per lease: lease, holder, lane, checkout, since and alive of its first holder, holders (each holder's holder, lane, checkout, since, alive and exclusive), queue (each waiter's holder, lane, checkout, since, ahead, position and lanes), recordedProtocol and protocol.");
        var list = new Command(description: "Print each lease with its holder, lane and hold time, and its queue in admission order with each waiter's wait.", name: "list") { listJson };

        list.SetAction(action: async (parse, cancellationToken) => {
            var directory = (leaseRoot ?? await CliLease.RootAsync(checkout: RepositoryPaths.RequireRoot(), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false));

            using var json = CliJsonLines.Open(json: parse.GetValue(option: listJson));

            if (json is null) {
                Console.Out.Write(value: Render(clock: clock, root: directory));
            } else {
                WriteRecords(clock: clock, output: json, root: directory);
            }

            return 0;
        });
        list.Detail(detail: "Use: puck lease list [--json]\nInspects .claude/leases/ without taking a ticket, dropping a dead ticket or writing rotation. Prunes stale quiet-holder metadata under its free exclusive guard, never unlinking the guard. Each of build, gpu and heavy prints idle, or its holders (lane, how long each has held the lease; heavy may have several) and then its live waiters in admission order: the rotation puts the oldest ticket of the lane served least recently first, so the order is the order the lease will be granted in. Each holder and waiter shows its holder, lane and the checkout it works in (when its CLI recorded one), and each waiter how long it has waited and its lane's turn among the queued lanes. Whether the holder is alive is read from the lease's guard lock, the lock that decides ownership: a holder file no process holds the lock for prints as stale, and the next acquisition replaces it. A lease a holder's descendants hold as a child of it is not shown.");
        root.Subcommands.Add(item: list);

        if (settleWait is not null) {
            var holder = CliOptions.Name(description: "Holder name recorded with all three leases; its first word is the lane unless --lane names one.", name: "--holder", required: true);
            var lane = CliOptions.Name(description: "The lane admission rotates between; defaults to the first word of --holder.", name: "--lane");
            var wait = CliOptions.Count(defaultValue: 0, description: "Seconds to wait, in total, for the three leases, and again beyond --settle for the machine to go idle; zero refuses at once (default).", minimum: 0, name: "--wait");
            var settle = CliOptions.Count(defaultValue: 30, description: "Seconds the machine must stay idle after the leases are held, before the command runs; zero skips the wait (default 30).", minimum: 0, name: "--settle");
            var arguments = CliOptions.ForwardedCommand(description: "Executable and arguments after --; no shell expansion is performed.");
            var command = new Command(description: "Hold the heavy, build and gpu leases together, wait for an idle machine, then run the command: a settle window for timing measurements.", name: "quiet") { holder, lane, wait, settle, arguments };

            command.SetAction(action: (parse, cancellationToken) => RunAsync(arguments: arguments, cancellationToken: cancellationToken, clock: clock, holder: holder, kind: null, lane: lane, leaseRoot: leaseRoot, names: QuietOrder, parse: parse, settle: settle, settleWait: settleWait, admit: null, verb: "lease quiet", wait: wait));
            command.Detail(detail: "Use: puck lease quiet --holder <lane> [--lane <lane>] [--wait <seconds>] [--settle <seconds>] -- <executable> <arguments...>\nQueues a priority measurement reservation before draining heavy (every holder of its shared slots), build and gpu. Once queued, no new grant is issued except to the quiet owner or a validated descendant: current holders finish without being stopped, and other waiters say they are waiting for the measurement window. CliLease.TryAcquire enforces this under admission.guard, the same lock used to register tickets; ordinary work retains lane rotation. --wait bounds acquisition in total, including current holders that do not finish; a timeout releases the reservation and every partial hold. Once all three are held, the `puck host load` sampler waits for --settle idle seconds (no GPU work, no heavy test run and host capacity), bounded by --wait plus --settle after collecting the initial CPU sample window. --wait buys only extra time for load to clear; the full idle settle is available with --wait 0. Then the command runs. Nested sweep, World and build grants run inside the window. Every exit releases resources in reverse before the reservation; failure preserves the child's exit code and cancellation kills its tree.");
            root.Subcommands.Add(item: command);
        }

        return root;
    }
}
