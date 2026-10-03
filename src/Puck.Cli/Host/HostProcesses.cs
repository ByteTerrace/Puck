using System.Text.RegularExpressions;

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
/// <c>Puck.Vulkan.Tests</c>, <c>Puck.World.Tests</c>).</item>
/// </list>
/// </summary>
internal static partial class HostProcesses {
    private static readonly string[] DeviceTestAssemblies = ["Puck.DirectX.Tests", "Puck.Vulkan.Tests", "Puck.World.Tests"];
    private static readonly string[] NeverWork = ["powershell", "pwsh", "bash", "sh", "cmd", "grep", "conhost", "MSBuild", "csc", "VBCSCompiler"];

    // A build's own processes: a dotnet build verb, MSBuild and its nodes, and the compiler and its server, which run as
    // `dotnet csc.dll` (their command lines name the assembly they compile, Puck.World.dll included).
    [GeneratedRegex(pattern: @"(?:^|[\s""'])(?:dotnet(?:\.exe)?""?\s+)?(?:build|restore|msbuild|publish|pack)(?:\s|$)|MSBuild\.dll|/nodemode:|[\\/]\s*(?:csc|VBCSCompiler)\.(?:dll|exe)\b", options: RegexOptions.IgnoreCase)]
    private static partial Regex BuildCommand();
    [GeneratedRegex(pattern: @"(?:Puck\.Cli\.dll|(?:^|[\\/\s""'])puck(?:\.exe)?)""?\s+(?:canary|parity|counters)(?:\s|$)", options: RegexOptions.IgnoreCase)]
    private static partial Regex GpuVerb();
    [GeneratedRegex(pattern: @"(?:^|[\\/\s""'])Puck\.World\.dll(?:[\s""']|$)", options: RegexOptions.IgnoreCase)]
    private static partial Regex WorldAssembly();
    [GeneratedRegex(pattern: @"nodeReuse:true", options: RegexOptions.IgnoreCase)]
    private static partial Regex ReuseNode();

    /// <summary>Whether a process is GPU work.</summary>
    /// <param name="name">The process name, without extension.</param>
    /// <param name="commandLine">Its command line, or empty when it could not be read.</param>
    /// <returns><see langword="true"/> for the World, a GPU verb, or a device-law test host.</returns>
    public static bool IsGpuWork(string name, string commandLine) {
        if (NeverWork.Contains(value: name, comparer: StringComparer.OrdinalIgnoreCase) || BuildCommand().IsMatch(input: commandLine)) {
            return false;
        }
        if (
            name.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck.World") ||
            WorldAssembly().IsMatch(input: commandLine) ||
            GpuVerb().IsMatch(input: commandLine)
        ) {
            return true;
        }

        // A test host is GPU work only for the device-law assemblies: their own executable, or a testhost that loads one.
        return DeviceTestAssemblies.Any(predicate: assembly => (
            name.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: assembly) ||
            (name.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "testhost") && commandLine.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: $"{assembly}.dll"))
        ));
    }
    /// <summary>Whether a process is an MSBuild node left running for reuse.</summary>
    /// <param name="name">The process name, without extension.</param>
    /// <param name="commandLine">Its command line.</param>
    /// <returns><see langword="true"/> for a <c>dotnet</c> or MSBuild process started with <c>nodeReuse:true</c>.</returns>
    public static bool IsReuseNode(string name, string commandLine) =>
        ((name.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "dotnet") || name.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "MSBuild")) &&
        ReuseNode().IsMatch(input: commandLine));
}
