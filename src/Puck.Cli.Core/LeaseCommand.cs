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
    /// <summary>The leases <c>puck lease quiet</c> holds, in the one order it takes them. A run that holds one lease and then takes
    /// another (a heavy suite that builds the World, a build that boots it) already goes heavy, then build, then gpu, so the quiet
    /// order is the same and the verb cannot deadlock against them; it releases in the reverse order.</summary>
    public static readonly IReadOnlyList<string> QuietOrder = ["heavy", "build", "gpu"];

    private static async Task<int> RunAsync(ParseResult parse, string verb, IReadOnlyList<string> names, Option<string> holder, Option<string?> lane, Option<int> wait, Argument<string[]> arguments, TimeProvider clock, string? leaseRoot, Option<int>? settle, LeaseSettle? settleWait, HostAdmit? admit, CancellationToken cancellationToken) {
        var seconds = parse.GetValue(option: wait);

        if (seconds < 0) { return CliExit.Refuse(verb: verb, what: "--wait", why: "must be nonnegative."); }

        var settled = ((settle is null) ? 0 : parse.GetValue(option: settle));

        if (settled < 0) { return CliExit.Refuse(verb: verb, what: "--settle", why: "must be nonnegative."); }

        var directory = leaseRoot;

        if (directory is null) {
            directory = await CliLease.RootAsync(checkout: RepositoryPaths.RequireRoot(), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }

        var named = parse.GetValue(option: lane);

        if ((named is not null) && (string.IsNullOrWhiteSpace(value: named) || named.Contains(value: '\n') || named.Contains(value: '\r'))) { return CliExit.Refuse(verb: verb, what: "--lane", why: "must be a single non-empty line."); }

        var bound = TimeSpan.FromSeconds(seconds: seconds);
        var started = clock.GetTimestamp();
        var held = new List<CliLease>();

        try {
            // One wait bounds the whole acquisition: each lease gets what the earlier ones left.
            foreach (var name in names) {
                var remaining = (bound - clock.GetElapsedTime(startingTimestamp: started));

                held.Add(item: await CliLease.AcquireAsync(ancestor: ProcessInspection.IsAncestor, lane: named, root: directory, name: name, holder: parse.GetValue(option: holder)!, wait: ((remaining > TimeSpan.Zero) ? remaining : TimeSpan.Zero), clock: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false));

                // Admission follows the lease, in the queue's order: the one holder of a heavy or GPU lease is the only run
                // judging the machine, so two starts never read the same headroom and both take it.
                if (admit is not null) {
                    var left = (bound - clock.GetElapsedTime(startingTimestamp: started));
                    var admitted = await admit(checkout: Environment.CurrentDirectory, step: $"{verb} {parse.GetValue(option: holder)}", device: (name == "gpu"), heavySuite: (name == "heavy"), kind: ((name == "gpu") ? HostStartKind.WindowedWorld : ((name == "heavy") ? HostStartKind.HeavySuite : HostStartKind.Step)), bound: ((left > TimeSpan.Zero) ? left : TimeSpan.Zero), progress: Console.Error, stage: null, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                    if (!admitted) { return CliExit.Refuse(verb: verb, what: "host admission", why: "the machine had no memory, commit or disk headroom (or its GPU stayed busy) within --wait, so the command did not start and the lease is released."); }
                }
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
    /// <returns>One line per lease (build, gpu, heavy), each followed by a line per waiter in admission order.</returns>
    public static string Render(TimeProvider clock, string root) {
        var now = clock.GetUtcNow();
        var text = new System.Text.StringBuilder();

        foreach (var name in new[] { "build", "gpu", "heavy" }) {
            var state = CliLease.Inspect(name: name, now: now, root: root);

            if (state.Holder is null) {
                _ = text.Append(value: name).Append(value: ": idle");
            } else {
                _ = text.Append(value: name).Append(value: ": held by ").Append(value: state.Holder).Append(value: " (lane ").Append(value: state.Lane).Append(value: ") for ").Append(value: Age(since: state.Since, now: now));
                if (!state.HolderAlive) { _ = text.Append(value: ", stale: its process has exited"); }
            }
            _ = text.Append(value: ((state.Queue.Count == 0) ? "\n" : $"; {state.Queue.Count} queued\n"));
            foreach (var (ticket, index) in state.Queue.OrderBy(keySelector: static ticket => ticket.Ahead).ThenBy(keySelector: static ticket => ticket.Id, comparer: StringComparer.Ordinal).Select(selector: static (ticket, index) => (ticket, index))) {
                _ = text.Append(value: $"  {(index + 1)}. {ticket.Holder} (lane {ticket.Lane}) waiting {Age(since: ticket.Since, now: now)}; lane {ticket.Position} of {ticket.Lanes} in the round, {ticket.Ahead} ahead\n");
            }
        }
        return text.ToString();
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
    /// <param name="admit">The host admission <c>puck lease heavy</c> and <c>puck lease gpu</c> wait on once they hold their lease and before they run the command; null admits at once.</param>
    /// <returns>The lease command.</returns>
    public static Command Create(TimeProvider clock, string? leaseRoot = null, LeaseSettle? settleWait = null, HostAdmit? admit = null) {
        var root = new Command(description: "Run a command under an exclusive World-build, GPU or heavy-work lease, or all three once the machine is idle.", name: "lease");

        foreach (var name in new[] { "build", "gpu", "heavy" }) {
            var holder = new Option<string>(name: "--holder") { Description = "Holder name recorded with the holder process and acquisition time; its first word is the lane unless --lane names one.", Required = true };
            var lane = new Option<string?>(name: "--lane") { Description = "The lane admission rotates between; defaults to the first word of --holder." };
            var wait = new Option<int>(name: "--wait") { Description = "Seconds to wait for the lease; zero refuses immediately (default)." };
            var arguments = CliOptions.Forwarded(description: "Executable and arguments after --; no shell expansion is performed.", name: "command");

            arguments.Arity = ArgumentArity.OneOrMore;
            var command = new Command(description: $"Hold the {name} lease until the command exits or is cancelled.", name: name) { holder, lane, wait, arguments };

            command.SetAction(action: (parse, cancellationToken) => RunAsync(arguments: arguments, cancellationToken: cancellationToken, clock: clock, holder: holder, lane: lane, leaseRoot: leaseRoot, names: [name], parse: parse, settle: null, settleWait: null, admit: ((name is "heavy" or "gpu") ? admit : null), verb: ("lease " + name), wait: wait));
            command.Detail(detail: (("Use: puck lease " + name) + " --holder <lane> [--lane <lane>] [--wait <seconds>] -- <executable> <arguments...>\nThe shared main checkout owns .claude/leases/. Waiters take tickets in .claude/leases/<name>.queue and are admitted by rotation between lanes (the first word of --holder, or --lane): the oldest ticket of the lane that held the lease least recently, first come, first served within a lane; a ticket of an exited process is dropped. A lease older than one hour is reclaimed only when its holder process is gone." + ((name is "heavy" or "gpu") ? " Once the lease is held, the run also waits for host headroom, the admission `puck gate` uses (free memory, a commit charge under its share of the commit limit, commit headroom for the start's expected peak, free disk" + ((name == "gpu") ? ", an idle GPU" : ", no other heavy test run") + "), within what --wait has left, and is refused (exit 2, lease released) when it does not return; the wait lines go to standard error." : string.Empty) + " Child failure preserves its exit code; cancellation kills the child tree and releases the lease."));
            root.Subcommands.Add(item: command);
        }

        var list = new Command(description: "Print each lease with its holder, lane and hold time, and its queue in admission order with each waiter's wait.", name: "list");

        list.SetAction(action: async (parse, cancellationToken) => {
            var directory = (leaseRoot ?? await CliLease.RootAsync(checkout: RepositoryPaths.RequireRoot(), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false));

            Console.Out.Write(value: Render(clock: clock, root: directory));
            return 0;
        });
        list.Detail(detail: "Use: puck lease list\nReads .claude/leases/ without taking a ticket, dropping a dead ticket or writing a rotation serial. Each of build, gpu and heavy prints idle, or its holder (lane, how long it has held the lease) and then its live waiters in admission order: the rotation puts the oldest ticket of the lane served least recently first, so the order is the order the lease will be granted in. Each waiter shows its holder, lane, how long it has waited, and its lane's turn among the queued lanes. A holder file whose process has exited prints as stale; the next acquisition replaces it. A lease a holder's descendants hold as a child of it is not shown.");
        root.Subcommands.Add(item: list);

        if (settleWait is not null) {
            var holder = new Option<string>(name: "--holder") { Description = "Holder name recorded with all three leases; its first word is the lane unless --lane names one.", Required = true };
            var lane = new Option<string?>(name: "--lane") { Description = "The lane admission rotates between; defaults to the first word of --holder." };
            var wait = new Option<int>(name: "--wait") { Description = "Seconds to wait, in total, for the three leases, and again beyond --settle for the machine to go idle; zero refuses at once (default)." };
            var settle = new Option<int>(name: "--settle") { DefaultValueFactory = _ => 30, Description = "Seconds the machine must stay idle after the leases are held, before the command runs; zero skips the wait (default 30)." };
            var arguments = CliOptions.Forwarded(description: "Executable and arguments after --; no shell expansion is performed.", name: "command");

            arguments.Arity = ArgumentArity.OneOrMore;
            var command = new Command(description: "Hold the heavy, build and gpu leases together, wait for an idle machine, then run the command: a settle window for timing measurements.", name: "quiet") { holder, lane, wait, settle, arguments };

            command.SetAction(action: (parse, cancellationToken) => RunAsync(arguments: arguments, cancellationToken: cancellationToken, clock: clock, holder: holder, lane: lane, leaseRoot: leaseRoot, names: QuietOrder, parse: parse, settle: settle, settleWait: settleWait, admit: null, verb: "lease quiet", wait: wait));
            command.Detail(detail: "Use: puck lease quiet --holder <lane> [--lane <lane>] [--wait <seconds>] [--settle <seconds>] -- <executable> <arguments...>\nTakes the heavy, build and gpu leases in that one order (the order a heavy suite, a World build and a World boot already nest in, so it cannot deadlock against them), then samples the machine with the `puck host load` sampler until it has been idle for --settle seconds: no GPU work, no heavy test run, and CAPACITY by the host-load thresholds (CPU mean and free memory). What the wait is on is printed to standard error whenever it changes. Then it runs the command and releases all three leases in reverse on every exit path. A run under the command that takes any of the three leases nests (the quiet holder is its ancestor) instead of waiting on it, so `puck sweep`, a World start and a World build run inside. --wait bounds the lease acquisition in total and, added to --settle, the wait for idle; a machine that never settles in time is refused (exit 2) and the leases are released. Child failure preserves its exit code; cancellation kills the child tree and releases the leases.");
            root.Subcommands.Add(item: command);
        }

        return root;
    }
}
