using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// The CLI conventions (docs/reference/cli.md), checked by walking the real command tree. Every violation is either
/// fixed or recorded, by command path and rule, in <c>CliConventionExemptions.json</c> beside this file. That ledger
/// only shrinks: a recorded row whose command no longer breaks its rule fails the law until the row is deleted, so the
/// ledger can never describe a tree that no longer exists.
/// </summary>
public sealed partial class CliConventionLawTests {
    private const string LedgerPath = "tests/Puck.Cli.Tests/CliConventionExemptions.json";

    // The packages whose verbs may still carry recorded violations. Everything else is held to every rule outright.
    private static readonly HashSet<string> LedgerPackages = new(comparer: StringComparer.Ordinal) { "B", "C", "D", "E" };
    // Loaded by any verb's help, these mean a verb built something heavy while building its command: a compiler, a
    // build engine, a benchmark harness, or a GPU backend.
    private static readonly string[] HelpForbiddenAssemblies = [
        "BenchmarkDotNet", "Microsoft.Build", "Microsoft.CodeAnalysis", "Puck.DirectX", "Puck.SdfVm", "Puck.Shaders",
        "Puck.Vulkan",
    ];

    private static IReadOnlySet<CliViolation> Ledger() {
        using var document = JsonDocument.Parse(json: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: LedgerPath)));
        var rows = new HashSet<CliViolation>();

        Assert.Equal(
            actual: document.RootElement.GetProperty(propertyName: "format").GetInt32(),
            expected: 1
        );

        foreach (var row in document.RootElement.GetProperty(propertyName: "exemptions").EnumerateArray()) {
            var command = row.GetProperty(propertyName: "command").GetString()!;
            var rule = row.GetProperty(propertyName: "rule").GetString()!;
            var package = row.GetProperty(propertyName: "package").GetString()!;

            Assert.True(
                condition: LedgerPackages.Contains(item: package),
                userMessage: $"{command} [{rule}] is recorded for package '{package}'; only B, C, D and E may carry exemptions."
            );
            Assert.True(
                condition: CliConventions.Rules.Contains(value: rule),
                userMessage: $"{command} [{rule}] names no rule this law checks."
            );
            Assert.True(
                condition: rows.Add(item: new CliViolation(
                    Command: command,
                    Rule: rule
                )),
                userMessage: $"{command} [{rule}] is recorded twice."
            );
        }

        return rows;
    }

    [Fact]
    public void EveryViolationIsRecordedAndEveryRecordStillViolates() {
        var actual = CliConventions.Violations(root: PuckRootCommand.Create(clock: TimeProvider.System)).Select(selector: static violation => violation.Key).ToHashSet();
        var recorded = Ledger();
        var unrecorded = actual.Except(second: recorded).Order().ToList();
        var stale = recorded.Except(second: actual).Order().ToList();

        Assert.True(
            condition: (unrecorded.Count == 0),
            userMessage: $"These commands break a CLI convention; fix them (docs/reference/cli.md):{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: unrecorded)}"
        );
        Assert.True(
            condition: (stale.Count == 0),
            userMessage: $"These exemptions no longer apply; delete their rows from {LedgerPath}:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: stale)}"
        );
    }
    // The reference's verb table names every root verb exactly once, in the root listing's ordinal order, and nothing
    // the root does not carry.
    [Fact]
    public void TheReferenceVerbTableListsEveryRootVerbInListingOrder() {
        var lines = File.ReadAllLines(path: RepositoryPaths.Resolve(relativePath: "docs/reference/cli.md"));
        var table = lines.SkipWhile(predicate: static line => (line != "## Verbs")).SkipWhile(predicate: static line => !line.StartsWith(comparisonType: StringComparison.Ordinal, value: "| Verb ")).Skip(count: 2).TakeWhile(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "|"));
        var listed = table.Select(selector: static row => VerbCell().Match(input: row)).Select(selector: static match => (match.Success
            ? match.Groups[1].Value
            : "(unreadable row)")).ToList();
        var verbs = PuckRootCommand.Create(clock: TimeProvider.System).Subcommands.Select(selector: static verb => verb.Name).ToList();

        Assert.Equal(
            actual: listed,
            expected: verbs
        );
    }

    [GeneratedRegex(pattern: "\\A\\| \\[?`puck ([a-z-]+)`")]
    private static partial Regex VerbCell();

    // Each rule, broken once on a tree of its own, is caught under its own name, and the same tree built correctly
    // is clean: the checker neither misses a rule nor flags everything.
    [Fact]
    public void EachRuleCatchesItsOwnViolation() {
        Assert.Empty(collection: CliConventions.Violations(root: MutationTree.Clean()));

        foreach (var rule in CliConventions.Rules) {
            var violations = CliConventions.Violations(root: MutationTree.Breaking(rule: rule)).Select(selector: static violation => violation.Key.Rule).ToHashSet();

            Assert.True(
                condition: violations.Contains(item: rule),
                userMessage: $"a tree breaking {rule} was not caught (found: {string.Join(separator: ", ", values: violations)})."
            );
        }
    }
    // Help is printed from a CLI loaded into a context of its own, so the assemblies it loads are counted apart from
    // everything this test process has already loaded.
    [Fact]
    public void HelpLoadsNoCompilerBuildEngineBenchmarkOrGpuAssembly() {
        var directory = AppContext.BaseDirectory;
        var context = new IsolatedCliContext(directory: directory);
        var cli = context.LoadFromAssemblyPath(assemblyPath: Path.Combine(
            path1: directory,
            path2: "Puck.Cli.dll"
        ));
        var commandLine = context.LoadFromAssemblyName(assemblyName: new AssemblyName(assemblyName: "System.CommandLine"));
        var root = cli.GetType(name: "Puck.Cli.PuckRootCommand", throwOnError: true)!
            .GetMethod(bindingAttr: BindingFlags.Public | BindingFlags.Static, name: "Create")!
            .Invoke(obj: null, parameters: [TimeProvider.System])!;
        var commandType = commandLine.GetType(name: "System.CommandLine.Command", throwOnError: true)!;
        var configurationType = commandLine.GetType(name: "System.CommandLine.InvocationConfiguration", throwOnError: true)!;
        var parse = commandType.GetMethod(
            name: "Parse",
            types: [typeof(IReadOnlyList<string>), commandLine.GetType(name: "System.CommandLine.ParserConfiguration", throwOnError: true)!]
        )!;
        var names = new List<string> { string.Empty };

        foreach (var subcommand in ((System.Collections.IEnumerable)commandType.GetProperty(name: "Subcommands")!.GetValue(obj: root)!)) {
            names.Add(item: ((string)subcommand.GetType().GetProperty(name: "Name")!.GetValue(obj: subcommand)!));
        }

        foreach (var name in names) {
            string[] arguments = ((name.Length == 0)
                ? ["--help"]
                : [name, "--help"]);
            var configuration = Activator.CreateInstance(type: configurationType)!;

            configurationType.GetProperty(name: "Output")!.SetValue(obj: configuration, value: TextWriter.Null);
            configurationType.GetProperty(name: "Error")!.SetValue(obj: configuration, value: TextWriter.Null);

            var result = parse.Invoke(obj: root, parameters: [arguments, null])!;
            var code = ((int)result.GetType().GetMethod(name: "Invoke", types: [configurationType])!.Invoke(obj: result, parameters: [configuration])!);

            Assert.Equal(
                actual: code,
                expected: 0
            );

            var forbidden = context.Assemblies.Select(selector: static assembly => assembly.GetName().Name!).Where(predicate: static loaded => HelpForbiddenAssemblies.Any(predicate: prefix => loaded.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: prefix
            ))).Order(comparer: StringComparer.Ordinal).ToList();

            Assert.True(
                condition: (forbidden.Count == 0),
                userMessage: $"'puck {string.Join(separator: ' ', values: arguments)}' loaded {string.Join(separator: ", ", values: forbidden)}."
            );
        }
    }

    // Resolves every assembly the CLI's output directory carries from that directory, so what the CLI loads lands here
    // rather than in the test's own default context; the shared framework still comes from the default context.
    private sealed class IsolatedCliContext(string directory) : AssemblyLoadContext(isCollectible: false) {
        protected override Assembly? Load(AssemblyName assemblyName) {
            var path = Path.Combine(
                path1: directory,
                path2: (assemblyName.Name + ".dll")
            );

            return (File.Exists(path: path)
                ? LoadFromAssemblyPath(assemblyPath: path)
                : null
            );
        }
    }
    // Trees that break exactly one rule, and one that breaks none, for the mutation proof.
    private static class MutationTree {
        private static Command Verb(string name, string description = "Do one thing.") {
            var command = new Command(
                description: description,
                name: name
            );

            command.SetAction(action: static _ => 0);

            return command;
        }
        private static RootCommand Root(params Command[] verbs) {
            var root = new RootCommand(description: "A tree for the convention law.");

            foreach (var verb in verbs) {
                root.Subcommands.Add(item: verb);
            }

            return root;
        }
        private static RootCommand Finish(RootCommand root, bool guard = true, bool help = true) {
            if (help) {
                CliHelp.Install(root: root);
            }

            if (guard) {
                CliExit.Guard(command: root);
            }

            return root;
        }

        public static RootCommand Clean() {
            var check = Verb(name: "check-tree");

            check.Options.Add(item: CliOptions.Check(description: "Write nothing."));

            var family = new Command(
                description: "Group two operations.",
                name: "family"
            ) { Verb(name: "one"), Verb(name: "two") };

            return Finish(root: Root(check, family));
        }
        public static RootCommand Breaking(string rule) {
            switch (rule) {
                case CliConventions.UsageName:
                    return Finish(
                        help: false,
                        root: Root(Verb(name: "one"))
                    );
                case CliConventions.KebabCase:
                    return Finish(root: Root(Verb(name: "Bad_Name")));
                case CliConventions.GnuLongName: {
                        var verb = Verb(name: "one");

                        verb.Options.Add(item: new Option<string>(name: "-Only") { Description = "Pick one." });

                        return Finish(root: Root(verb));
                    }
                case CliConventions.NoAlias: {
                        var verb = Verb(name: "one");

                        verb.Options.Add(item: new Option<string>(
                            aliases: ["-o"],
                            name: "--only"
                        ) { Description = "Pick one." });

                        return Finish(root: Root(verb));
                    }
                case CliConventions.Described: {
                        var verb = Verb(name: "one");

                        verb.Options.Add(item: new Option<string>(name: "--only"));

                        return Finish(root: Root(verb));
                    }
                case CliConventions.CheckIsBool: {
                        var verb = Verb(name: "one");

                        verb.Options.Add(item: new Option<string>(name: "--check") { Description = "Compare." });

                        return Finish(root: Root(verb));
                    }
                case CliConventions.BannedName: {
                        var verb = Verb(name: "one");

                        verb.Options.Add(item: new Option<bool>(name: "--verify") { Description = "Write nothing." });

                        return Finish(root: Root(verb));
                    }
                case CliConventions.Alphabetical:
                    return Finish(root: Root(Verb(name: "two"), Verb(name: "one")));
                case CliConventions.OneLineDescription:
                    return Finish(root: Root(Verb(
                        description: "Do one thing.\n\nThen explain it at length.",
                        name: "one"
                    )));
                case CliConventions.NoArgumentWithSubcommands: {
                        var family = new Command(
                            description: "Group two operations.",
                            name: "family"
                        ) { Verb(name: "one") };

                        family.Arguments.Add(item: new Argument<string>(name: "path") { Description = "A path." });

                        return Finish(root: Root(family));
                    }
                case CliConventions.BalancedUsage: {
                        // System.CommandLine renders a command that keeps unmatched tokens with one closing bracket too
                        // many: "[[--] <additional arguments>...]]".
                        var verb = Verb(name: "one");

                        verb.TreatUnmatchedTokensAsErrors = false;

                        return Finish(root: Root(verb));
                    }
                case CliConventions.PositionalOutput: {
                        var verb = Verb(name: "one");

                        verb.Arguments.Add(item: new Argument<string>(name: "output-directory") { Description = "Where to write." });

                        return Finish(root: Root(verb));
                    }
                case CliConventions.ExitGuarded:
                    return Finish(
                        guard: false,
                        root: Root(Verb(name: "one"))
                    );
                default:
                    throw new ArgumentException(message: $"No mutation breaks '{rule}'.");
            }
        }
    }
}

/// <summary>One command breaking one rule: the command path as a caller types it after <c>puck</c>, or
/// <c>(root)</c>, and the rule's name.</summary>
internal readonly record struct CliViolation(string Command, string Rule) : IComparable<CliViolation> {
    public int CompareTo(CliViolation other) {
        var command = string.CompareOrdinal(
            strA: Command,
            strB: other.Command
        );

        return ((command != 0)
            ? command
            : string.CompareOrdinal(
                strA: Rule,
                strB: other.Rule
            ));
    }
    public override string ToString() => $"{Command} [{Rule}]";
}
/// <summary>The mechanical half of the CLI conventions: each rule, and the walk that reports every command breaking
/// one, with what it found.</summary>
internal static partial class CliConventions {
    public const string Alphabetical = "alphabetical";
    public const string BalancedUsage = "balanced-usage";
    public const string BannedName = "banned-name";
    public const string CheckIsBool = "check-is-bool";
    public const string Described = "described";
    public const string ExitGuarded = "exit-guarded";
    public const string GnuLongName = "gnu-long-name";
    public const string KebabCase = "kebab-case";
    public const string NoAlias = "no-alias";
    public const string NoArgumentWithSubcommands = "no-argument-with-subcommands";
    public const string OneLineDescription = "one-line-description";
    public const string PositionalOutput = "positional-output";
    public const string UsageName = "usage-name";

    public static readonly string[] Rules = [
        Alphabetical, BalancedUsage, BannedName, CheckIsBool, Described, ExitGuarded, GnuLongName, KebabCase, NoAlias,
        NoArgumentWithSubcommands, OneLineDescription, PositionalOutput, UsageName,
    ];

    // Option names, without their leading dashes, that a verb must not use: each has one replacement every verb
    // shares, or names a style knob canonical output does not have.
    private static readonly Dictionary<string, string> BannedOptions = new(comparer: StringComparer.Ordinal) {
        ["files"] = "--file-list",
        ["indent-size"] = "no style knob",
        ["out"] = "--output",
        ["out-dir"] = "--output",
        ["output-directory"] = "--output",
        ["output-file"] = "--output",
        ["stdout"] = "--output",
        ["tabs"] = "no style knob",
        ["verify"] = "--check",
        ["what-if"] = "--check",
    };
    // Positional argument names that name the path a verb writes: that path is always `--output`, never a position.
    private static readonly HashSet<string> OutputArguments = new(comparer: StringComparer.Ordinal) {
        "destination", "out", "out-dir", "output", "output-directory", "output-file",
    };
    // Verb names that abbreviate or duplicate another verb.
    private static readonly Dictionary<string, string> BannedCommands = new(comparer: StringComparer.Ordinal) {
        ["fmt"] = "format",
    };
    // A verb that mirrors a named external tool keeps that tool's short flags and mode names: `search` is ripgrep's
    // shape, short flags and its --files list mode included.
    private static readonly HashSet<string> ExternalToolMirrors = new(comparer: StringComparer.Ordinal) { "search" };

    [GeneratedRegex(pattern: "\\A[a-z][a-z0-9]*(-[a-z0-9]+)*\\z")]
    private static partial Regex KebabName();
    [GeneratedRegex(pattern: "\\A-[A-Za-z?]\\z")]
    private static partial Regex ShortFlag();
    private static string PathOf(IReadOnlyList<string> path) => ((path.Count == 0)
        ? "(root)"
        : string.Join(
            separator: ' ',
            values: path
        ));
    // Whether every bracket the usage line opens it also closes, in order.
    private static bool Balanced(string usage) {
        var open = new Stack<char>();

        foreach (var character in usage) {
            if (character is '[' or '<') {
                open.Push(item: character);
            } else if (character is ']' or '>') {
                if (
                    !open.TryPop(result: out var opened) ||
                    (opened != ((character == ']')
                    ? '['
                    : '<'))
                ) {
                    return false;
                }
            }
        }

        return (open.Count == 0);
    }
    private static string Help(RootCommand root, IReadOnlyList<string> path) {
        using var output = new StringWriter();

        _ = root.Parse(args: [.. path, "--help"]).Invoke(configuration: new InvocationConfiguration { Error = TextWriter.Null, Output = output });

        return output.ToString();
    }
    private static void Walk(RootCommand root, Command command, List<string> path, List<KeyValuePair<CliViolation, string>> found) {
        var name = PathOf(path: path);
        var mirror = ((path.Count > 0) && ExternalToolMirrors.Contains(item: path[0]));

        void Report(string rule, string detail) => found.Add(item: new KeyValuePair<CliViolation, string>(
            key: new CliViolation(
                Command: name,
                Rule: rule
            ),
            value: detail
        ));

        if (path.Count > 0) {
            if (!KebabName().IsMatch(input: command.Name)) {
                Report(rule: KebabCase, detail: command.Name);
            }

            if (BannedCommands.TryGetValue(key: command.Name, value: out var commandReplacement)) {
                Report(rule: BannedName, detail: $"{command.Name} -> {commandReplacement}");
            }

            if (command.Aliases.Count > 0) {
                Report(rule: NoAlias, detail: string.Join(separator: ", ", values: command.Aliases));
            }

            if (string.IsNullOrWhiteSpace(value: command.Description)) {
                Report(detail: "command", rule: Described);
            }
        }

        var usage = (Help(path: path, root: root).ReplaceLineEndings(replacementText: "\n").Split('\n').SkipWhile(predicate: static line => (line != "Usage:")).Skip(count: 1).FirstOrDefault() ?? string.Empty);

        if (!usage.StartsWith(comparisonType: StringComparison.Ordinal, value: $"  {CliHelp.ToolName}{((path.Count == 0) ? string.Empty : (" " + string.Join(separator: ' ', values: path)))}")) {
            Report(rule: UsageName, detail: usage.Trim());
        }

        if (!Balanced(usage: usage)) {
            Report(rule: BalancedUsage, detail: usage.Trim());
        }

        if (!CliExit.IsGuarded(command: command)) {
            Report(rule: ExitGuarded, detail: command.Action!.GetType().Name);
        }

        if (
            (command.Subcommands.Count > 0) &&
            (command.Arguments.Count > 0)
        ) {
            Report(rule: NoArgumentWithSubcommands, detail: string.Join(separator: ", ", values: command.Arguments.Select(selector: static argument => argument.Name)));
        }

        foreach (var argument in command.Arguments.Where(predicate: static argument => !argument.Hidden)) {
            if (string.IsNullOrWhiteSpace(value: argument.Description)) {
                Report(rule: Described, detail: $"<{argument.Name}>");
            }

            if (OutputArguments.Contains(item: argument.Name)) {
                Report(rule: PositionalOutput, detail: $"<{argument.Name}> -> --output");
            }
        }

        foreach (var option in command.Options.Where(predicate: static option => ((option is not HelpOption and not VersionOption) && !option.Hidden))) {
            if (!option.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "--")) {
                Report(rule: GnuLongName, detail: option.Name);
            } else if (!KebabName().IsMatch(input: option.Name[2..])) {
                Report(rule: KebabCase, detail: option.Name);
            }

            foreach (var alias in option.Aliases.Where(predicate: alias => !(mirror && ShortFlag().IsMatch(input: alias)))) {
                Report(rule: NoAlias, detail: $"{option.Name} {alias}");
            }

            if (string.IsNullOrWhiteSpace(value: option.Description)) {
                Report(rule: Described, detail: option.Name);
            }

            if (
                (option.Name == "--check") &&
                (option.ValueType != typeof(bool))
            ) {
                Report(rule: CheckIsBool, detail: option.ValueType.Name);
            }

            foreach (var spelling in option.Aliases.Prepend(element: option.Name).Where(predicate: static spelling => spelling.StartsWith(comparisonType: StringComparison.Ordinal, value: "--"))) {
                if (
                    BannedOptions.TryGetValue(key: spelling[2..], value: out var replacement) &&
                    !(mirror && (spelling == "--files"))
                ) {
                    Report(detail: $"{spelling} -> {replacement}", rule: BannedName);
                }
            }
        }

        if (path.Count == 0) {
            var names = command.Subcommands.Select(selector: static subcommand => subcommand.Name).ToList();

            if (!names.SequenceEqual(second: names.Order(comparer: StringComparer.Ordinal))) {
                Report(rule: Alphabetical, detail: string.Join(separator: ", ", values: names));
            }
        }

        foreach (var subcommand in command.Subcommands) {
            if (
                (path.Count == 0) &&
                (subcommand.Description?.Contains(value: '\n') == true)
            ) {
                found.Add(item: new KeyValuePair<CliViolation, string>(
                    key: new CliViolation(
                        Command: subcommand.Name,
                        Rule: OneLineDescription
                    ),
                    value: subcommand.Description.Split('\n')[0]
                ));
            }

            path.Add(item: subcommand.Name);
            Walk(command: subcommand, found: found, path: path, root: root);
            path.RemoveAt(index: (path.Count - 1));
        }
    }

    /// <summary>Returns every command in <paramref name="root"/>'s tree that breaks a rule, one entry per command and
    /// rule, with a detail naming what broke it.</summary>
    public static IReadOnlyDictionary<CliViolation, string> Violations(RootCommand root) {
        var found = new List<KeyValuePair<CliViolation, string>>();

        Walk(command: root, found: found, path: [], root: root);

        return found.GroupBy(keySelector: static entry => entry.Key).ToDictionary(
            elementSelector: static group => string.Join(separator: "; ", values: group.Select(selector: static entry => entry.Value)),
            keySelector: static group => group.Key
        );
    }
}
