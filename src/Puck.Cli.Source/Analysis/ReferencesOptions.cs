using Microsoft.CodeAnalysis;

namespace Puck.Cli.Analysis;

// What `puck references` was asked for. Every query starts by matching declarations by name; the mode decides
// what is reported about each one.
internal enum ReferencesMode {
    // Every reference to the declaration, solution-wide (the default).
    References,

    // The declarations themselves and nothing else.
    Declarations,

    // Implementations of an interface or of an interface member.
    Implementers,

    // Overrides of a virtual or abstract member.
    Overrides,

    // Derived types.
    Derived,
}
// The parsed `puck references` command line. Parse is the sole constructor path.
internal sealed record ReferencesOptions {
    public required bool AllowPartial { get; init; }
    public required string Configuration { get; init; }
    public required string? Containing { get; init; }
    public required bool Contains { get; init; }
    public required SymbolFilter Filter { get; init; }
    public required bool IgnoreCase { get; init; }
    public required bool Json { get; init; }
    public required bool Metadata { get; init; }
    public required ReferencesMode Mode { get; init; }
    public required string Name { get; init; }
    public required bool NoDoc { get; init; }
    public required string? ProjectPath { get; init; }
    public required bool Quiet { get; init; }
    public required string? SolutionPath { get; init; }
    public required bool Strict { get; init; }
}
