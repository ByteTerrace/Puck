namespace Puck.Analyzers;

/// <summary>
/// The one list of files that may call <c>RuleExpressions.EvaluateDetached</c> (<see cref="OutcomeAnalyzer"/>,
/// OUTCOME003): the consumers that are not a firing, each stating what its <c>Faulted</c> arm does. A caller outside the
/// list is a firing's evaluation or a new consumer; the first goes through the interpreter's frame, and the second is
/// added here with its arm.
/// </summary>
public static class OutcomeDetachedAllowlist {
    /// <summary>Gets every allowlisted file.</summary>
    public static IReadOnlyList<OutcomeDetachedEntry> Entries { get; } = [
        new(
            Assembly: "Puck.State.Rules",
            Path: "src/Puck.State.Rules/Compiler/RuleCompiler.Expressions.cs",
            Reason: "The constant folder: a faulted or absent subtree stays unfolded, so the interpreter answers it at run time."
        ),
        new(
            Assembly: "Puck.World.Server",
            Path: "src/Puck.World.Server/WorldRuleHost.FlockAffinities.cs",
            Reason: "The flock affinity is a perception, not a firing: a non-finite outcome is a counted AffinityFailures and a weight of zero."
        ),
        new(
            Assembly: "Puck.World.Browser",
            Path: "src/Puck.World.Browser/Engine/BrowserSession.cs",
            Reason: "The browser read-back writes no state: a non-finite outcome refuses the read with the fault's description."
        ),
    ];

    /// <summary>Returns whether a file may call the detached evaluator.</summary>
    /// <param name="assembly">The compiling assembly's name.</param>
    /// <param name="path">The source file's path as the compiler was given it, spelled with either separator.</param>
    /// <returns><see langword="true"/> when an entry names the assembly and the file.</returns>
    public static bool Admits(string assembly, string path) => Entries.Any(predicate: entry =>
        AnalyzerAllowlistPath.Matches(assembly: assembly, path: path, expectedAssembly: entry.Assembly, expectedPath: entry.Path));
}
/// <summary>One file allowed to evaluate detached.</summary>
/// <param name="Assembly">The assembly that compiles the file.</param>
/// <param name="Path">The source file's repository-relative path, spelled with <c>/</c>.</param>
/// <param name="Reason">What the consumer is and what its <c>Faulted</c> arm does.</param>
public sealed record OutcomeDetachedEntry(string Assembly, string Path, string Reason);
