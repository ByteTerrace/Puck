using System.CommandLine;

namespace Puck.Cli;

/// <summary>Runs one command while holding a shared machine-work lease.</summary>
public static class LeaseCommand {
    /// <summary>Creates the gpu and heavy lease commands.</summary>
    /// <param name="clock">The CLI clock.</param>
    /// <param name="leaseRoot">An explicit lease directory, or null for the main checkout's shared directory.</param>
    /// <returns>The lease command.</returns>
    public static Command Create(TimeProvider clock, string? leaseRoot = null) {
        var root = new Command(description: "Run a command under an exclusive GPU or heavy-work lease.", name: "lease");

        foreach (var name in new[] { "gpu", "heavy" }) {
            var holder = new Option<string>(name: "--holder") { Description = "Lane name recorded with the holder process and acquisition time.", Required = true };
            var wait = new Option<int>(name: "--wait") { Description = "Seconds to wait for the lease; zero refuses immediately (default)." };
            var arguments = CliOptions.Forwarded(description: "Executable and arguments after --; no shell expansion is performed.", name: "command");

            arguments.Arity = ArgumentArity.OneOrMore;
            var command = new Command(description: $"Hold the {name} lease until the command exits or is cancelled.", name: name) { holder, wait, arguments };

            command.SetAction(action: async (parse, cancellationToken) => {
                var seconds = parse.GetValue(option: wait);

                if (seconds < 0) { return CliExit.Refuse(verb: ("lease " + name), what: "--wait", why: "must be nonnegative."); }
                var directory = leaseRoot;

                if (directory is null) {
                    directory = await CliLease.RootAsync(checkout: RepositoryPaths.RequireRoot(), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                }
                using var lease = await CliLease.AcquireAsync(root: directory, name: name, holder: parse.GetValue(option: holder)!, wait: TimeSpan.FromSeconds(seconds: seconds), clock: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                var tokens = parse.GetValue(argument: arguments)!;
                var run = await CliProcess.RunAsync(fileName: tokens[0], arguments: tokens.Skip(count: 1), capture: false, clock: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                return run.ExitCode;
            });
            command.Detail(detail: (("Use: puck lease " + name) + " --holder <lane> [--wait <seconds>] -- <executable> <arguments...>\nThe shared main checkout owns .claude/leases/. A lease older than one hour is reclaimed only when its holder process is gone. Child failure preserves its exit code; cancellation kills the child tree and releases the lease."));
            root.Subcommands.Add(item: command);
        }
        return root;
    }
}
