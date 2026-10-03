using System.CommandLine.Parsing;

using Puck.Cli.Counters;
using Puck.Cli.Gate;
using Puck.Cli.Parity;

namespace Puck.Cli.Host;

/// <summary>
/// Classifies a running process from its name and command line, with no machine reads of its own, so the rules are
/// laws:
/// <list type="bullet">
/// <item>shells and builds are never GPU work: <c>powershell</c>, <c>pwsh</c>, <c>bash</c>, <c>sh</c>, <c>cmd</c>,
/// <c>grep</c>, <c>conhost</c>, MSBuild and its nodes, the compiler and its server, and any <c>dotnet build</c>,
/// <c>restore</c>, <c>msbuild</c>, <c>publish</c> or <c>pack</c>, whatever project they name;</item>
/// <item>GPU work is the World (<c>Puck.World</c> or <c>Puck.World.dll</c>), a <c>canary</c>, <c>parity</c> or
/// <c>counters</c> verb of the CLI, or a test host running the device-law assemblies (<c>Puck.DirectX.Tests</c>,
/// <c>Puck.Vulkan.Tests</c>, <c>Puck.World.Tests</c>, <c>Puck.Platform.Windows.Tests</c>).</item>
/// </list>
/// </summary>
internal static class HostProcesses {
    private static readonly string[] DeviceTestAssemblies = [.. GatePlan.Steps.Where(predicate: step => (step.Kind == GateStepKind.DeviceSuite)).Select(selector: step => step.Name)];
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
    private static string EntryPoint(string[] arguments, out int next, out bool deviceTestHost) {
        next = 1;
        deviceTestHost = false;

        if (!ProgramName(path: arguments[0]).Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "dotnet")) { return ProgramName(path: arguments[0]); }
        if ((next < arguments.Length) && (arguments[next] == "exec")) { next++; }

        while ((next < arguments.Length) && arguments[next].StartsWith(comparisonType: StringComparison.Ordinal, value: "--")) {
            var token = arguments[next++];
            var separator = token.IndexOfAny(anyOf: ['=', ':']);
            var option = ((separator < 0) ? token : token[..separator]);

            if (!ExecValueOptions.Contains(value: option, comparer: StringComparer.Ordinal) || ((separator < 0) && (next == arguments.Length))) { return string.Empty; }

            var value = ((separator < 0) ? arguments[next++] : token[(separator + 1)..]);

            if ((option == "--runtimeconfig") && FileName(path: value).EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".runtimeconfig.json")) {
                deviceTestHost = IsDeviceTest(program: FileName(path: value)[..^19]);
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
    private static bool RunningGpuVerb(string[] arguments, int start) {
        if (start == arguments.Length) { return false; }
        if (arguments.Skip(count: start).Any(predicate: static token => (token is "--help" or "-h" or "-?"))) { return false; }
        if (arguments[start] == "canary") {
            return (!Enabled(arguments: arguments, flag: "--list", start: (start + 1)) && !Enabled(arguments: arguments, flag: "--plan", start: (start + 1)));
        }

        // The existing grammar distinguishes each root action from compare, including option values named compare.
        var command = arguments[start] switch {
            "parity" => ParityCommand.Create(),
            "counters" => CountersCommand.Create(),
            _ => null,
        };

        if (command is null) { return false; }

        var result = command.Parse(args: arguments[(start + 1)..]);

        return ((result.Errors.Count == 0) && (result.CommandResult.Command == command));
    }

    /// <summary>Whether a process is GPU work.</summary>
    /// <param name="name">The process name, without extension.</param>
    /// <param name="commandLine">Its Windows command line or Linux NUL-delimited argv; empty when unreadable.</param>
    /// <returns><see langword="true"/> for the World, a GPU verb, or a device-law test host.</returns>
    public static bool IsGpuWork(string name, string commandLine) {
        var program = ProgramName(path: name);

        if (NeverWork.Contains(value: program, comparer: StringComparer.OrdinalIgnoreCase)) { return false; }
        if (program.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck.World") || IsDeviceTest(program: program)) { return true; }

        var arguments = Arguments(commandLine: commandLine);

        if (arguments.Length == 0) { return false; }

        var entry = EntryPoint(arguments: arguments, deviceTestHost: out var deviceTestHost, next: out var next);

        if (entry.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck.World") || IsDeviceTest(program: entry)) { return true; }
        if (entry.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "testhost")) {
            return (deviceTestHost || arguments.Skip(count: next).Any(predicate: static argument => IsDeviceTest(program: ProgramName(path: argument))));
        }

        return ((entry.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "puck") || entry.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck.Cli")) && RunningGpuVerb(arguments: arguments, start: next));
    }
    /// <summary>Whether a process is an MSBuild node left running for reuse.</summary>
    /// <param name="name">The process name, without extension.</param>
    /// <param name="commandLine">Its command line.</param>
    /// <returns><see langword="true"/> for a <c>dotnet</c> or MSBuild process started with <c>nodeReuse:true</c>.</returns>
    public static bool IsReuseNode(string name, string commandLine) {
        if (!name.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "dotnet") && !name.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "MSBuild")) { return false; }

        var arguments = Arguments(commandLine: commandLine);

        return ((arguments.Length != 0) && EntryPoint(arguments: arguments, deviceTestHost: out _, next: out _).Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "MSBuild") &&
            arguments.Any(predicate: static argument => argument.TrimStart(trimChars: ['/', '-']).Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "nodemode:1")) &&
            arguments.Any(predicate: static argument => argument.TrimStart(trimChars: ['/', '-']).Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "nodeReuse:true")));
    }
}
