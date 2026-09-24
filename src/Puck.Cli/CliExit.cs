using System.CommandLine;
using System.CommandLine.Invocation;

namespace Puck.Cli;

/// <summary>
/// The one exit-code contract every <c>puck</c> verb shares, and the one place an action's failure becomes a code.
/// <para>
/// A verb returns <see cref="Success"/> when it did its job, <see cref="Failed"/> when the check or proof it ran
/// observed a failure or drift (or, for a query verb, found nothing), and <see cref="Refused"/> for a usage error, a
/// refusal, or an infrastructure failure. <see cref="Guard"/> wraps every action in the tree so an exception that
/// escapes a verb is reported on one named line and exits <see cref="Refused"/>, and a cancellation exits
/// <see cref="Cancelled"/>, instead of reaching the parser's default handler and its stack trace.
/// </para>
/// </summary>
internal static class CliExit {
    /// <summary>The verb did its job: a check found nothing to report, a query found a result, a write landed.</summary>
    public const int Success = 0;
    /// <summary>A check or proof observed a failure or drift; a query verb found nothing.</summary>
    public const int Failed = 1;
    /// <summary>A usage error, a refusal, or an infrastructure failure.</summary>
    public const int Refused = 2;
    /// <summary>The invocation was cancelled (Ctrl+C or SIGTERM), the shell's own code for an interrupt.</summary>
    public const int Cancelled = 130;

    /// <summary>Writes a refusal as one line on standard error and returns <see cref="Refused"/>.</summary>
    /// <param name="verb">The command path that refused, such as <c>format</c> or <c>pull-request format</c>.</param>
    /// <param name="what">What was refused: a path, a value, an operation.</param>
    /// <param name="why">Why it was refused.</param>
    /// <returns><see cref="Refused"/>.</returns>
    public static int Refuse(string verb, string what, string why) {
        Console.Error.WriteLine(value: $"puck {verb}: {what}: {why}");

        return Refused;
    }
    /// <summary>Wraps the action of <paramref name="command"/> and of every command beneath it in the shared
    /// exception and cancellation mapping. A command with no action is left alone, and a command already guarded is
    /// not wrapped twice.</summary>
    /// <param name="command">The root of the tree to guard.</param>
    public static void Guard(Command command) {
        if (
            (command.Action is { } action) &&
            (action is not GuardedSynchronousAction and not GuardedAsynchronousAction)
        ) {
            var path = Path(command: command);

            command.Action = action switch {
                SynchronousCommandLineAction synchronous => new GuardedSynchronousAction(
                    inner: synchronous,
                    path: path
                ),
                AsynchronousCommandLineAction asynchronous => new GuardedAsynchronousAction(
                    inner: asynchronous,
                    path: path
                ),
                _ => action,
            };
        }

        foreach (var subcommand in command.Subcommands) {
            Guard(command: subcommand);
        }
    }
    /// <summary>Indicates whether <paramref name="command"/>'s action runs through <see cref="Guard"/>.</summary>
    /// <param name="command">The command to inspect.</param>
    /// <returns><see langword="true"/> when the command has no action or a guarded one.</returns>
    public static bool IsGuarded(Command command) =>
        (command.Action is null or GuardedSynchronousAction or GuardedAsynchronousAction);

    // The command path below the root, as a caller types it: "format", "pull-request format".
    private static string Path(Command command) {
        var names = new List<string>();

        for (Symbol? symbol = command; ((symbol is Command { } current) && (current is not RootCommand)); symbol = current.Parents.FirstOrDefault()) {
            names.Insert(
                index: 0,
                item: current.Name
            );
        }

        return string.Join(
            separator: ' ',
            values: names
        );
    }
    private static int Report(string path, Exception exception) {
        var message = exception.Message.ReplaceLineEndings(replacementText: " ");

        Console.Error.WriteLine(value: ((path.Length == 0)
            ? $"puck: {message}"
            : $"puck {path}: {message}"
        ));

        return Refused;
    }

    private sealed class GuardedSynchronousAction(SynchronousCommandLineAction inner, string path) : SynchronousCommandLineAction {
        public override int Invoke(ParseResult parseResult) {
            // A synchronous action has no token to be cancelled through, so every exception it throws is a failure.
            try {
                return inner.Invoke(parseResult: parseResult);
            } catch (Exception exception) {
                return Report(
                    exception: exception,
                    path: path
                );
            }
        }
    }
    private sealed class GuardedAsynchronousAction(AsynchronousCommandLineAction inner, string path) : AsynchronousCommandLineAction {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) {
            try {
                return await inner.InvokeAsync(
                    cancellationToken: cancellationToken,
                    parseResult: parseResult
                ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                return Cancelled;
            } catch (Exception exception) {
                return Report(
                    exception: exception,
                    path: path
                );
            }
        }
    }
}
