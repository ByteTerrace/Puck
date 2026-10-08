using System.CommandLine;
using System.CommandLine.Parsing;

using Puck.Cli.Affected;
using Puck.Cli.Gate;

namespace Puck.Cli.Host;

/// <summary>
/// Classifies a running process from its name and command line, with no machine reads of its own, so the rules are
/// laws:
/// <list type="bullet">
/// <item>shells and builds are never GPU work: <c>powershell</c>, <c>pwsh</c>, <c>bash</c>, <c>sh</c>, <c>cmd</c>,
/// <c>grep</c>, <c>conhost</c>, MSBuild and its nodes, the compiler and its server, and any <c>dotnet build</c>,
/// <c>restore</c>, <c>msbuild</c>, <c>publish</c> or <c>pack</c>, whatever project they name;</item>
/// <item>GPU work is the World (<c>Puck.World</c> or <c>Puck.World.dll</c>), a <c>canary</c> verb of the CLI, a CLI verb
/// whose grammar the composing root names (<see cref="GateComposition.GpuVerbGrammars"/>: <c>parity</c> and
/// <c>counters</c>) parsed as its root action, or a test host running the device-law assemblies (<c>Puck.DirectX.Tests</c>,
/// <c>Puck.Vulkan.Tests</c>, <c>Puck.World.Tests</c>, <c>Puck.Platform.Windows.Tests</c>) whose arguments can select a
/// <c>Gpu</c>-trait test: a run carrying the CPU selection (<see cref="AffectedCommand.CpuSelection"/>) opens no
/// device.</item>
/// </list>
/// </summary>
public static class HostProcesses {
    private static readonly string[] DeviceTestAssemblies = [.. GatePlan.Steps.Where(predicate: step => (step.Kind == GateStepKind.DeviceSuite)).Select(selector: step => step.Name)];
    private static readonly string[] HeavyTestAssemblies = ["Puck.World.Presentation.Tests", "Puck.World.Tests"];
    private static readonly string[] NeverWork = ["powershell", "pwsh", "bash", "sh", "cmd", "grep", "conhost", "MSBuild", "csc", "VBCSCompiler"];
    private static readonly string[] ExecValueOptions = ["--depsfile", "--runtimeconfig", "--additionalprobingpath", "--additional-deps", "--fx-version", "--roll-forward", "--roll-forward-on-no-candidate-fx"];

    // /proc supplies argv, not shell text: retain NUL boundaries when an argument contains spaces or quotes.
    private static string[] Arguments(string commandLine) => (commandLine.Contains(value: '\0')
        ? commandLine.TrimEnd(trimChar: '\0').Split(separator: '\0')
        : [.. CommandLineParser.SplitCommandLine(commandLine: commandLine)]);
    private static string FileName(string path) => path[(path.LastIndexOfAny(anyOf: ['/', '\\']) + 1)..];
    private static string ProgramName(string path) {
        var file = FileName(path: path);

        return ((file.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".exe") || file.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".dll")) ? file[..^4] : file);
    }
    private static bool IsDeviceTest(string program) => DeviceTestAssemblies.Contains(value: program, comparer: StringComparer.OrdinalIgnoreCase);
    // Only dotnet's entry assembly is executable work. Build/run/test/tool wrappers are not. A managed testhost's
    // runtimeconfig identifies its test assembly rather than a build's project path.
    private static string EntryPoint(string[] arguments, out int next, out string? hostAssembly) {
        next = 1;
        hostAssembly = null;

        if (!ProgramName(path: arguments[0]).Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "dotnet")) { return ProgramName(path: arguments[0]); }
        if ((next < arguments.Length) && (arguments[next] == "exec")) { next++; }

        while ((next < arguments.Length) && arguments[next].StartsWith(comparisonType: StringComparison.Ordinal, value: "--")) {
            var token = arguments[next++];
            var separator = token.IndexOfAny(anyOf: ['=', ':']);
            var option = ((separator < 0) ? token : token[..separator]);

            if (!ExecValueOptions.Contains(value: option, comparer: StringComparer.Ordinal) || ((separator < 0) && (next == arguments.Length))) { return string.Empty; }

            var value = ((separator < 0) ? arguments[next++] : token[(separator + 1)..]);

            if ((option == "--runtimeconfig") && FileName(path: value).EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".runtimeconfig.json")) {
                hostAssembly = FileName(path: value)[..^19];
            }
        }
        if (next == arguments.Length) { return string.Empty; }

        var assembly = FileName(path: arguments[next++]);

        return (assembly.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".dll") ? assembly[..^4] : string.Empty);
    }
    private static bool Enabled(string[] arguments, int start, string flag) {
        for (var index = start; (index < arguments.Length); index++) {
            var token = arguments[index];

            if (token == "--") { break; }
            if (token == flag) {
                return (((index + 1) >= arguments.Length) || !bool.TryParse(value: arguments[(index + 1)], result: out var value) || value);
            }
            if (token.StartsWith(comparisonType: StringComparison.Ordinal, value: (flag + "=")) || token.StartsWith(comparisonType: StringComparison.Ordinal, value: (flag + ":"))) {
                return (!bool.TryParse(value: token[(flag.Length + 1)..], result: out var value) || value);
            }
        }

        return false;
    }
    // A device-law test application runs GPU work only when its arguments can select a Gpu-trait test. The test
    // framework's simple filters narrow one another, so the CPU selection leaves every device law out whatever else the
    // run names. A response file's arguments are not on the command line, so a run naming one can select them.
    private static bool SelectsGpuTests(string[] arguments, int start) {
        var (option, value) = (AffectedCommand.CpuSelection[0], AffectedCommand.CpuSelection[1]);
        var excluded = false;

        for (var index = start; (index < arguments.Length); index++) {
            var token = arguments[index];

            if (token.StartsWith(value: '@')) { return true; }

            excluded |= (((token == option) && ((index + 1) < arguments.Length) && (arguments[(index + 1)] == value)) ||
                (token == $"{option}={value}") || (token == $"{option}:{value}"));
        }

        return !excluded;
    }
    private static bool RunningGpuVerb(string[] arguments, int start, IReadOnlyDictionary<string, Func<Command>> grammars) {
        if (start == arguments.Length) { return false; }
        if (arguments.Skip(count: start).Any(predicate: static token => (token is "--help" or "-h" or "-?"))) { return false; }
        if (arguments[start] == "canary") {
            return (!Enabled(arguments: arguments, flag: "--list", start: (start + 1)) && !Enabled(arguments: arguments, flag: "--plan", start: (start + 1)));
        }

        // The verb's own grammar distinguishes each root action from compare, including option values named compare.
        if (!grammars.TryGetValue(key: arguments[start], value: out var grammar)) { return false; }

        var command = grammar();

        var result = command.Parse(args: arguments[(start + 1)..]);

        return ((result.Errors.Count == 0) && (result.CommandResult.Command == command));
    }

    /// <summary>Whether a process is GPU work.</summary>
    /// <param name="name">The process name, without extension.</param>
    /// <param name="commandLine">Its Windows command line or Linux NUL-delimited argv; empty when unreadable.</param>
    /// <param name="grammars">The grammar of each verb whose own arguments decide whether its run is GPU work, by name.</param>
    /// <returns><see langword="true"/> for the World, a GPU verb, or a device-law test host whose arguments can select a
    /// device law.</returns>
    public static bool IsGpuWork(string name, string commandLine, IReadOnlyDictionary<string, Func<Command>> grammars) {
        var program = ProgramName(path: name);

        if (NeverWork.Contains(value: program, comparer: StringComparer.OrdinalIgnoreCase)) { return false; }
        if (program.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck.World")) { return true; }

        var arguments = Arguments(commandLine: commandLine);

        // An unreadable command line cannot show a CPU selection.
        if (IsDeviceTest(program: program)) { return ((arguments.Length == 0) || SelectsGpuTests(arguments: arguments, start: 1)); }
        if (arguments.Length == 0) { return false; }

        var entry = EntryPoint(arguments: arguments, hostAssembly: out var hostAssembly, next: out var next);

        if (entry.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck.World")) { return true; }
        if (IsDeviceTest(program: entry)) { return SelectsGpuTests(arguments: arguments, start: next); }
        if (entry.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "testhost")) {
            return (((hostAssembly is not null) && IsDeviceTest(program: hostAssembly)) || arguments.Skip(count: next).Any(predicate: static argument => IsDeviceTest(program: ProgramName(path: argument))));
        }

        return ((entry.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "puck") || entry.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck.Cli")) && RunningGpuVerb(arguments: arguments, grammars: grammars, start: next));
    }
    /// <summary>Whether a process is an MSBuild node left running for reuse.</summary>
    /// <param name="name">The process name, without extension.</param>
    /// <param name="commandLine">Its command line.</param>
    /// <returns><see langword="true"/> for a <c>dotnet</c> or MSBuild process started with <c>nodeReuse:true</c>.</returns>
    public static bool IsReuseNode(string name, string commandLine) {
        if (!name.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "dotnet") && !name.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "MSBuild")) { return false; }

        var arguments = Arguments(commandLine: commandLine);

        return ((arguments.Length != 0) && EntryPoint(arguments: arguments, hostAssembly: out _, next: out _).Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "MSBuild") &&
            arguments.Any(predicate: static argument => argument.TrimStart(trimChars: ['/', '-']).Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "nodemode:1")) &&
            arguments.Any(predicate: static argument => argument.TrimStart(trimChars: ['/', '-']).Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "nodeReuse:true")));
    }
    /// <summary>Whether a test assembly's full run is heavy: the machine takes one at a time, whoever starts it.</summary>
    /// <param name="assembly">The test assembly's name, without extension.</param>
    /// <returns><see langword="true"/> for <c>Puck.World.Tests</c> and <c>Puck.World.Presentation.Tests</c>, the suites that hold full
    /// hosts and scene probes.</returns>
    public static bool IsHeavyTestAssembly(string assembly) => HeavyTestAssemblies.Contains(value: assembly, comparer: StringComparer.OrdinalIgnoreCase);
    /// <summary>Whether a process runs a heavy test assembly (<see cref="IsHeavyTestAssembly"/>), whatever its filter:
    /// its apphost, <c>dotnet exec</c> of its assembly, or a test host running it. A build, a <c>dotnet test</c> driver
    /// or a shell naming it runs no test.</summary>
    /// <param name="name">The process name, without extension.</param>
    /// <param name="commandLine">Its Windows command line or Linux NUL-delimited argv; empty when unreadable.</param>
    /// <returns><see langword="true"/> for a running heavy test assembly.</returns>
    public static bool IsHeavyTest(string name, string commandLine) {
        var program = ProgramName(path: name);

        if (IsHeavyTestAssembly(assembly: program)) { return true; }
        if (NeverWork.Contains(value: program, comparer: StringComparer.OrdinalIgnoreCase)) { return false; }

        var arguments = Arguments(commandLine: commandLine);

        if (arguments.Length == 0) { return false; }

        var entry = EntryPoint(arguments: arguments, hostAssembly: out var hostAssembly, next: out var next);

        if (IsHeavyTestAssembly(assembly: entry)) { return true; }

        return (entry.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "testhost") &&
            (((hostAssembly is not null) && IsHeavyTestAssembly(assembly: hostAssembly)) || arguments.Skip(count: next).Any(predicate: static argument => IsHeavyTestAssembly(assembly: ProgramName(path: argument)))));
    }
    /// <summary>Returns the processes descended from <paramref name="root"/>: its children, theirs, and so on. A process
    /// is a child only when it started no earlier than its parent, so a recycled parent id never adopts an older
    /// process.</summary>
    /// <param name="root">The process whose descendants are wanted.</param>
    /// <param name="processes">Each running process's parent id and start time; a start time that cannot be read is
    /// <see cref="DateTime.MinValue"/>, which no process is ever started after.</param>
    /// <returns>The descendants' ids, without <paramref name="root"/>.</returns>
    public static IReadOnlySet<int> Descendants(int root, IReadOnlyDictionary<int, (int Parent, DateTime Started)> processes) {
        var found = new HashSet<int>();
        var frontier = new Queue<int>(collection: [root]);

        while (frontier.TryDequeue(result: out var parent)) {
            var started = (processes.TryGetValue(key: parent, value: out var entry) ? entry.Started : DateTime.MinValue);

            foreach (var (id, process) in processes) {
                if ((process.Parent == parent) && (id != root) && (process.Started >= started) && found.Add(item: id)) {
                    frontier.Enqueue(item: id);
                }
            }
        }

        return found;
    }
}
