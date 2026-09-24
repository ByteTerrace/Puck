using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Runtime.CompilerServices;

namespace Puck.Cli;

/// <summary>
/// The help every <c>puck</c> command prints: System.CommandLine's own rendering, with usage naming the tool
/// <see cref="ToolName"/>, and a command's detail (its modes, exit codes, and examples) printed only in that
/// command's own help, so the root listing keeps one line per verb.
/// </summary>
internal static class CliHelp {
    /// <summary>The name usage and help print for the tool, whatever file the process was started from.</summary>
    public const string ToolName = "puck";

    private static readonly ConditionalWeakTable<Command, string> Details = [];

    /// <summary>Attaches the detail <paramref name="command"/>'s own help prints after its options, under
    /// <c>Details:</c>.</summary>
    /// <param name="command">The command the detail belongs to.</param>
    /// <param name="detail">The detail text; its lines print indented by two spaces.</param>
    /// <returns><paramref name="command"/>.</returns>
    public static Command Detail(this Command command, string detail) {
        Details.AddOrUpdate(
            key: command,
            value: detail
        );

        return command;
    }
    /// <summary>Gets the detail attached to <paramref name="command"/>, or <see langword="null"/>.</summary>
    /// <param name="command">The command to read.</param>
    /// <returns>The detail text, or <see langword="null"/> when none was attached.</returns>
    public static string? DetailOf(Command command) => (Details.TryGetValue(
        key: command,
        value: out var detail
    )
        ? detail
        : null
    );
    /// <summary>Replaces the help action of <paramref name="root"/>'s help option with one that names the tool
    /// <see cref="ToolName"/> and prints attached detail.</summary>
    /// <param name="root">The root command.</param>
    public static void Install(RootCommand root) {
        foreach (var option in root.Options) {
            if (option.Action is HelpAction help) {
                option.Action = new ToolNamedHelpAction(inner: help);
            }
        }
    }

    // System.CommandLine names the root after the file the process started from, so `dotnet Puck.Cli.dll` and the
    // tool shim print `Puck.Cli` where a caller types `puck`, and RootCommand's name cannot be set. The default help
    // renders into a buffer and each usage line's leading executable name is replaced by the tool's.
    private sealed class ToolNamedHelpAction(HelpAction inner) : SynchronousCommandLineAction {
        public override bool ClearsParseErrors => inner.ClearsParseErrors;

        public override int Invoke(ParseResult parseResult) {
            var configuration = parseResult.InvocationConfiguration;
            var output = configuration.Output;
            var buffer = new StringWriter();

            configuration.Output = buffer;

            int code;

            try {
                code = inner.Invoke(parseResult: parseResult);
            } finally {
                configuration.Output = output;
            }

            var prefix = ("  " + RootCommand.ExecutableName);
            var lines = buffer.ToString().ReplaceLineEndings(replacementText: "\n").TrimEnd(trimChar: '\n').Split('\n');
            var inUsage = false;

            foreach (var line in lines) {
                var text = line;

                if (
                    (text.Length == 0) ||
                    !text.StartsWith(value: ' ')
                ) {
                    inUsage = string.Equals(
                        a: text,
                        b: "Usage:",
                        comparisonType: StringComparison.Ordinal
                    );
                } else if (
                    inUsage &&
                    text.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: prefix
                ) &&
                    ((text.Length == prefix.Length) || (text[prefix.Length] == ' '))
                ) {
                    text = (("  " + ToolName) + text[prefix.Length..]);
                }

                output.Write(value: (text + "\n"));
            }

            if (DetailOf(command: parseResult.CommandResult.Command) is { } detail) {
                output.Write(value: "\nDetails:\n");

                foreach (var line in detail.ReplaceLineEndings(replacementText: "\n").TrimEnd(trimChar: '\n').Split('\n')) {
                    output.Write(value: ((line.Length == 0)
                        ? "\n"
                        : (("  " + line) + "\n")
                    ));
                }
            }

            output.Write(value: "\n");

            return code;
        }
    }
}
