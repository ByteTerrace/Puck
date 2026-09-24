using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

using Puck.Cli.Analysis;
using Puck.Cli.Affected;
using Puck.Cli.Architecture;
using Puck.Cli.Automation;
using Puck.Cli.Azure;
using Puck.Cli.Baselines;
using Puck.Cli.Branding;
using Puck.Cli.Bench;
using Puck.Cli.Canary;
using Puck.Cli.CartridgeCost;
using Puck.Cli.Counters;
using Puck.Cli.Creation;
using Puck.Cli.Docs;
using Puck.Cli.Firmware;
using Puck.Cli.FontAtlas;
using Puck.Cli.Format;
using Puck.Cli.Landing;
using Puck.Cli.Mcp;
using Puck.Cli.NuGet;
using Puck.Cli.Official;
using Puck.Cli.Packaging;
using Puck.Cli.Parity;
using Puck.Cli.PublishRelease;
using Puck.Cli.PullRequest;
using Puck.Cli.Qualification;
using Puck.Cli.Ratchets;
using Puck.Cli.Registry;
using Puck.Cli.Scan;
using Puck.Cli.Schema;
using Puck.Cli.Search;
using Puck.Cli.Shaders;
using Puck.Cli.Test;
using Puck.Cli.Transpiler;
using Puck.Cli.Vocabulary;
using Puck.Cli.WasmStdlib;
using Puck.Cli.WorktreeBase;

namespace Puck.Cli;

/// <summary>
/// The <c>puck</c> command tree: every verb hangs off one root, and the process entry point and
/// in-process self-invocations both go through <see cref="InvokeAsync(string[])"/>.
/// </summary>
internal static class PuckRootCommand {
    internal static int Invoke(string[] args, RootCommand root) => (Parse(
        args: args,
        root: root
    )?.Invoke(configuration: Invocation()) ?? CliExit.Refused);
    internal static async Task<int> InvokeAsync(string[] args, RootCommand root) => ((Parse(
        args: args,
        root: root
    ) is { } result)
        ? await result.InvokeAsync(configuration: Invocation())
        : CliExit.Refused
    );
    // Ctrl+C and SIGTERM cancel the verb's token and the process waits for the verb: a host's own shutdown
    // lifecycle (the silo allows ShutdownSeconds + 5) is the only deadline, never the parser's two-second default.
    // CliExit.Guard maps every exception an action throws, so the parser's own handler never sees one.
    internal static InvocationConfiguration Invocation() => new() { EnableDefaultExceptionHandler = false, ProcessTerminationTimeout = Timeout.InfiniteTimeSpan };

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
    internal static ParseResult? Parse(string[] args, RootCommand root) {
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

    /// <summary>Creates the root command. <paramref name="clock"/> is the CLI host's one clock: every deadline a verb
    /// puts on a process, a connection, a lease, or a request runs on it.</summary>
    /// <param name="clock">The CLI host's clock; <see cref="TimeProvider.System"/> for a real invocation.</param>
    /// <returns>The root command: its verbs in ordinal name order, its help naming the tool <see cref="CliHelp.ToolName"/>,
    /// and every action guarded by <see cref="CliExit.Guard"/>.</returns>
    public static RootCommand Create(TimeProvider clock) {
        Command[] verbs = [
            AffectedCommand.Create(),
            ArchitectureCommand.Create(),
            ArtifactsCommand.Create(),
            AzureCommand.Create(clock: clock),
            BaselinesCommand.Create(),
            BenchRunner.Create(clock: clock),
            BrandingCommand.Create(),
            BundleCommand.Create(),
            CanaryCommand.Create(),
            CartridgeCostCommand.Create(),
            RatchetCommand.CreateCommentSmells(),
            CompileCommand.Create(),
            CountersCommand.Create(),
            CreationCommand.Create(),
            DecompileCommand.Create(),
            DeclarationsCommand.Create(),
            DocsCommand.Create(),
            EmbedCommand.Create(),
            FirmwareCommand.Create(),
            FontAtlasCommand.Create(),
            FormatCommand.Create(),
            LandingCommand.Create(),
            RatchetCommand.CreateLengths(),
            LintCommand.Create(),
            LspCommand.Create(),
            McpCommand.Create(),
            NuGetCommand.Create(),
            OfficialCommand.Create(),
            PackagesCommand.Create(),
            ParityCommand.Create(),
            PuckMigrateCommand.Create(),
            PublishCommand.Create(),
            PullRequestCommand.Create(),
            QualifyCommand.Create(),
            ReferencesCommand.Create(),
            RegistryCommand.Create(),
            ScanCommand.Create(),
            SchemaCommand.Create(),
            SearchCommand.Create(),
            ShadersCommand.Create(),
            TestCommand.Create(),
            VocabularyCommand.Create(),
            WasmBuildCommand.Create(),
            WasmStdlibCommand.Create(),
            WorktreeBaseCommand.Create(),
            WorldCommand.Create(clock: clock),
        ];
        var root = new RootCommand(description: "The Puck developer CLI: every repository operation is a verb here.");

        // The listing reads in name order however the list above is kept.
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
    public static int Invoke(string[] args) => Invoke(
        args: args,
        root: Create(clock: TimeProvider.System)
    );
    public static Task<int> InvokeAsync(string[] args) => InvokeAsync(
        args: args,
        root: Create(clock: TimeProvider.System)
    );
}
