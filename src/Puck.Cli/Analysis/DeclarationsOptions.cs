namespace Puck.Cli.Analysis;

// The parsed `puck declarations` command line. Kinds is empty when --kind was absent, which means "every type
// kind" plus the members --members asks for.
internal sealed record DeclarationsOptions {
    public required string? Attribute { get; init; }
    public required string? Base { get; init; }
    public required bool Doc { get; init; }
    public required IReadOnlyList<CliGlob> Exclude { get; init; }
    public required IReadOnlyList<CliGlob> Include { get; init; }
    public required bool Json { get; init; }
    public required IReadOnlySet<string> Kinds { get; init; }
    public required bool Members { get; init; }
    public required string? Name { get; init; }
    public required bool Quiet { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }

    // A type declaration is emitted when --kind was absent or names a type kind.
    public bool WantTypes =>
        ((Kinds.Count == 0) || Kinds.Overlaps(other: DeclarationsWalker.TypeKinds));

    // Members are emitted when asked for by --members, or implied by a member kind in --kind (asking for
    // methods and getting nothing because --members was missing would be a trap).
    public bool WantMembers =>
        (Members || Kinds.Overlaps(other: DeclarationsWalker.MemberKinds));
}
