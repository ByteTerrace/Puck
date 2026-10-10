using System.CommandLine;

namespace Puck.Cli.Gate;

/// <summary>What the gate's verbs (<c>puck affected</c>, <c>puck gate</c>, <c>puck host</c>) read from verbs the same
/// root composes beside them. The root hands it in, so this assembly references none of those verbs: a change to one of
/// them reaches the gate's laws only where it changes what the root composes.</summary>
/// <param name="SchemaSourceTypes">The types a repository-relative file <c>puck schema</c> writes is generated from, so
/// that a change to the file is placed through the sources declaring them; empty for a file it does not write.</param>
/// <param name="GpuVerbGrammars">By verb name, the grammar of each verb whose own arguments decide whether a running
/// process is GPU work: its root action runs a World or a device, and a comparison of saved results does not.</param>
public sealed record GateComposition(
    Func<string, IReadOnlyList<Type>> SchemaSourceTypes,
    IReadOnlyDictionary<string, Func<Command>> GpuVerbGrammars
);
