using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

namespace Puck.Cli;

/// <summary>
/// The invocation every <c>puck</c> root shares: a root composes its verbs through <see cref="Compose"/>, and the process
/// entry point and in-process invocations both go through <see cref="Invoke(string[], RootCommand)"/> or
/// <see cref="InvokeAsync(string[], RootCommand)"/>.
/// </summary>
public static class CliRoot {
    /// <summary>Composes a root command over <paramref name="verbs"/>.</summary>
    /// <param name="description">The root's description.</param>
    /// <param name="verbs">The verbs the root holds.</param>
    /// <returns>The root command: its verbs in ordinal name order, its help naming the tool <see cref="CliHelp.ToolName"/>,
    /// and every action guarded by <see cref="CliExit.Guard"/>.</returns>
    public static RootCommand Compose(string description, IEnumerable<Command> verbs) {
        var root = new RootCommand(description: description);

        // The listing reads in name order however the caller keeps its list.
        foreach (var verb in verbs.OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: static verb => verb.Name
        )) {
            root.Subcommands.Add(item: verb);
        }

        CliHelp.Install(root: root);
        CliExit.Guard(command: root);

        return root;
    }
    /// <summary>Parses and runs <paramref name="args"/> against <paramref name="root"/>.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="root">The root command.</param>
    /// <returns>The verb's exit code, or <see cref="CliExit.Refused"/> for a usage error.</returns>
    public static int Invoke(string[] args, RootCommand root) => (Parse(
        args: args,
        root: root
    )?.Invoke(configuration: Invocation()) ?? CliExit.Refused);
    /// <summary>Parses and runs <paramref name="args"/> against <paramref name="root"/>.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="root">The root command.</param>
    /// <returns>The verb's exit code, or <see cref="CliExit.Refused"/> for a usage error.</returns>
    public static async Task<int> InvokeAsync(string[] args, RootCommand root) => ((Parse(
        args: args,
        root: root
    ) is { } result)
        ? await result.InvokeAsync(configuration: Invocation())
        : CliExit.Refused
    );
    // Ctrl+C and SIGTERM cancel the verb's token and the process waits for the verb: a host's own shutdown
    // lifecycle (the silo allows ShutdownSeconds + 5) is the only deadline, never the parser's two-second default.
    // CliExit.Guard maps every exception an action throws, so the parser's own handler never sees one.
    /// <summary>The invocation configuration every root runs its verbs under.</summary>
    /// <returns>A configuration with the parser's exception handler off and no process-termination deadline.</returns>
    public static InvocationConfiguration Invocation() => new() { EnableDefaultExceptionHandler = false, ProcessTerminationTimeout = Timeout.InfiniteTimeSpan };

    // The parser hands a misspelled option to whichever value slot is still open, so a token that
    // looks like an option is refused unless it follows "--", is a number, or reaches an argument
    // that forwards its tokens to another tool (CliOptions.Forwarded).
    private static IEnumerable<string> OptionLikeValues(ParseResult result) {
        var escaped = result.Tokens.SkipWhile(predicate: token => (token.Type != TokenType.DoubleDash)).Skip(count: 1).Select(selector: token => token.Value).ToList();
        var values = result.CommandResult.Children.Where(predicate: static child => (child is not ArgumentResult { Argument: CliForwardedArgument })).SelectMany(selector: child => child.Tokens).Select(selector: token => token.Value)
            .Where(predicate: value => (value.StartsWith(value: '-') && (value.Length > 1) && !double.TryParse(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            result: out _,
            s: value,
            style: System.Globalization.NumberStyles.Float
        )));

        foreach (var value in values) {
            if (!escaped.Remove(item: value)) { yield return value; }
        }
    }

    // A usage error exits 2, so verbs keep 1 for a failed check and 0 for success.
    /// <summary>Parses <paramref name="args"/>, printing every usage error and the help hint for the command it reached.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="root">The root command.</param>
    /// <returns>The parse result, or <see langword="null"/> when the arguments are a usage error.</returns>
    public static ParseResult? Parse(string[] args, RootCommand root) {
        var result = root.Parse(args: args);
        var errors = result.Errors.Select(selector: error => error.Message).Concat(second: OptionLikeValues(result: result).Select(selector: token => $"Unrecognized option '{token}'.")).ToArray();

        if (
            (result.Action is not ParseErrorAction) &&
            (errors.Length == 0)
        ) { return result; }
        foreach (var error in errors) { Console.Error.WriteLine(value: error); }
        var path = new List<string>();

        for (var command = result.CommandResult; (command.Parent is CommandResult parent); command = parent) {
            path.Insert(
                index: 0,
                item: command.Command.Name
            );
        }
        Console.Error.WriteLine(value: $"Run '{CliHelp.ToolName} {string.Concat(values: path.Select(selector: name => (name + " ")))}--help' for usage.");
        return null;
    }
}
