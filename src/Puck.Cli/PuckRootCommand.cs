using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

using Puck.Cli.Analysis;
using Puck.Cli.Architecture;
using Puck.Cli.Automation;
using Puck.Cli.Azure;
using Puck.Cli.Bench;
using Puck.Cli.Canary;
using Puck.Cli.Citations;
using Puck.Cli.Creation;
using Puck.Cli.DocLinks;
using Puck.Cli.FontAtlas;
using Puck.Cli.Format;
using Puck.Cli.Landing;
using Puck.Cli.Lengths;
using Puck.Cli.Mcp;
using Puck.Cli.NuGet;
using Puck.Cli.Official;
using Puck.Cli.Packaging;
using Puck.Cli.Parity;
using Puck.Cli.PublishRelease;
using Puck.Cli.Registry;
using Puck.Cli.Scan;
using Puck.Cli.Schema;
using Puck.Cli.Search;
using Puck.Cli.Shaders;
using Puck.Cli.Transpiler;
using Puck.Cli.WasmStdlib;
using Puck.Cli.WorktreeBase;

namespace Puck.Cli;

/// <summary>
/// The <c>puck</c> command tree: every verb hangs off one root, and the process entry point and
/// in-process self-invocations both go through <see cref="InvokeAsync(string[])"/>.
/// </summary>
internal static class PuckRootCommand {
    public static RootCommand Create() =>
        new(description: "The Puck developer CLI.") {
            ArchitectureCommand.Create(),
            ArtifactsCommand.Create(),
            AzureCommand.Create(),
            BenchRunner.Create(),
            BundleCommand.Create(),
            CanaryCommand.Create(),
            CitationsCommand.Create(),
            CompileCommand.Create(),
            CreationCommand.Create(),
            DecompileCommand.Create(),
            DeclarationsCommand.Create(),
            DocLinksCommand.Create(),
            DocsBuildCommand.Create(),
            FontAtlasCommand.Create(),
            FormatCommand.Create(),
            LandingCommand.Create(),
            LengthsCommand.Create(),
            LintCommand.Create(),
            LspCommand.Create(),
            McpCommand.Create(),
            NuGetCommand.Create(),
            OfficialCommand.Create(),
            PackagesCommand.Create(),
            ParityCommand.Create(),
            PuckFmtCommand.Create(),
            PublishCommand.Create(),
            ReferencesCommand.Create(),
            RegistryCommand.Create(),
            ScanCommand.Create(),
            SchemaCommand.Create(),
            SearchCommand.Create(),
            ShadersCommand.Create(),
            WasmBuildCommand.Create(),
            WasmStdlibCommand.Create(),
            WorktreeBaseCommand.Create(),
            WorldCommand.Create(),
        };
    public static int Invoke(string[] args) => Invoke(args: args, root: Create());
    public static Task<int> InvokeAsync(string[] args) => InvokeAsync(args: args, root: Create());

    internal static int Invoke(string[] args, RootCommand root) => (Parse(args: args, root: root)?.Invoke(configuration: Invocation()) ?? 2);
    internal static async Task<int> InvokeAsync(string[] args, RootCommand root) => ((Parse(args: args, root: root) is { } result) ? await result.InvokeAsync(configuration: Invocation()) : 2);

    // Ctrl+C and SIGTERM cancel the verb's token and the process waits for the verb: a host's own shutdown
    // lifecycle (the silo allows ShutdownSeconds + 5) is the only deadline, never the parser's two-second default.
    private static InvocationConfiguration Invocation() => new() { ProcessTerminationTimeout = Timeout.InfiniteTimeSpan };
    // A usage error exits 2, so verbs keep 1 for a failed check and 0 for success.
    private static ParseResult? Parse(string[] args, RootCommand root) {
        var result = root.Parse(args: args);
        var errors = result.Errors.Select(selector: error => error.Message).Concat(second: OptionLikeValues(result: result).Select(selector: token => $"Unrecognized option '{token}'.")).ToArray();

        if ((result.Action is not ParseErrorAction) && (errors.Length == 0)) { return result; }
        foreach (var error in errors) { Console.Error.WriteLine(value: error); }
        var path = new List<string>();

        for (var command = result.CommandResult; (command.Parent is CommandResult parent); command = parent) { path.Insert(index: 0, item: command.Command.Name); }
        Console.Error.WriteLine(value: $"Run 'puck {string.Concat(values: path.Select(selector: name => (name + " ")))}--help' for usage.");
        return null;
    }
    // The parser hands a misspelled option to whichever value slot is still open, so a token that
    // looks like an option is refused unless it follows "--", is a number, or reaches a command
    // that forwards its unmatched tokens.
    private static IEnumerable<string> OptionLikeValues(ParseResult result) {
        if (!result.CommandResult.Command.TreatUnmatchedTokensAsErrors) { yield break; }
        var escaped = result.Tokens.SkipWhile(predicate: token => (token.Type != TokenType.DoubleDash)).Skip(count: 1).Select(selector: token => token.Value).ToList();
        var values = result.CommandResult.Children.SelectMany(selector: child => child.Tokens).Select(selector: token => token.Value)
            .Where(predicate: value => (value.StartsWith(value: '-') && (value.Length > 1) && !double.TryParse(provider: System.Globalization.CultureInfo.InvariantCulture, result: out _, s: value, style: System.Globalization.NumberStyles.Float)));

        foreach (var value in values) {
            if (!escaped.Remove(item: value)) { yield return value; }
        }
    }
}
