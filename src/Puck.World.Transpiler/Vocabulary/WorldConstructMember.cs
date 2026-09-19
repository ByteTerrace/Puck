namespace Puck.World.Transpiler.Vocabulary;

/// <summary>One parameter of a <see cref="WorldMemberKind.Parameters"/> member: the name the construct's body reads
/// it by, and the type word an argument must spell.</summary>
/// <param name="Name">The parameter's authored name.</param>
/// <param name="Required">Whether an invocation is refused without an argument for it.</param>
/// <param name="Summary">One sentence, in the register a hover card and a manual row both read.</param>
/// <param name="Type">The type word the argument spells, which the owning member's
/// <see cref="WorldConstructMember.Choices"/> admits.</param>
public sealed record WorldConstructParameter(
    string Name,
    string Type,
    string Summary,
    bool Required = true
);

/// <summary>One member of a described construct: how it is written, what it lowers to, and the default the
/// printer elides it against.</summary>
/// <param name="AdmittedKinds">The cell kinds the construct's own <c>kind</c> must be one of before this member is
/// admitted, empty when every kind the construct admits carries it.</param>
/// <param name="Choices">The admitted words when <paramref name="Kind"/> is
/// <see cref="WorldMemberKind.Enumeration"/>, empty otherwise.</param>
/// <param name="Default">The document value the printer may elide this member against, as the canonical JSON text
/// of that value, <see cref="RowIndexDefault"/> for the one positional default, or <see langword="null"/> when the
/// member carries no default and always prints.</param>
/// <param name="DocumentKeys">The keys this member fills — more than one for a modifier standing for several
/// (<c>bounds</c>), empty for a member the document carries nothing for.</param>
/// <param name="DocumentNode">The document member the keys sit on when it is not the construct's own, as
/// <see cref="WorldConstruct.DocumentMember"/> spells it: a <c>grid</c>'s shape modifiers land on the topology it
/// mints, not on its row.</param>
/// <param name="Kind">What the member's value is written as.</param>
/// <param name="Lowering">How the lowering consumes a member that fills no key, or <see langword="null"/> when
/// <paramref name="DocumentKeys"/> is the whole answer. A member naming no key and carrying no classification is
/// inert — described, admitted, and dropped — which is what a description must never be, so the construct table's
/// laws refuse it.</param>
/// <param name="Name">The authored spelling.</param>
/// <param name="Parameters">The parameters a <see cref="WorldMemberKind.Parameters"/> member pairs with their
/// type words, in the order they are written; empty for every other kind. <paramref name="Choices"/> says which
/// type words the list admits, which one parameter's own <see cref="WorldConstructParameter.Type"/> cannot.</param>
/// <param name="Position">Where the member is written relative to the keyword.</param>
/// <param name="Required">Whether the construct is refused without this member.</param>
/// <param name="Summary">One sentence, in the register a hover card and a manual row both read.</param>
public sealed record WorldConstructMember(
    string Name,
    WorldMemberPosition Position,
    WorldMemberKind Kind,
    IReadOnlyList<string> DocumentKeys,
    string Summary,
    string? Default = null,
    bool Required = false,
    IReadOnlyList<string>? AdmittedKinds = null,
    IReadOnlyList<string>? Choices = null,
    string? DocumentNode = null,
    string? Lowering = null,
    IReadOnlyList<WorldConstructParameter>? Parameters = null
) {
    /// <summary>The one default that is positional rather than constant: a <c>shape</c>'s <c>id</c> is its own
    /// 0-based position in the <c>shapes</c> array.</summary>
    public const string RowIndexDefault = "$index";

    /// <summary>Gets the cell kinds the construct's own <c>kind</c> must be one of before this member is
    /// admitted, empty when the member's admission does not turn on it.</summary>
    public IReadOnlyList<string> AdmittedKinds { get; init; } = (AdmittedKinds ?? []);
    /// <summary>Gets the admitted words for an <see cref="WorldMemberKind.Enumeration"/> member.</summary>
    public IReadOnlyList<string> Choices { get; init; } = (Choices ?? []);
    /// <summary>Gets the parameters this member pairs with their type words, in written order.</summary>
    public IReadOnlyList<WorldConstructParameter> Parameters { get; init; } = (Parameters ?? []);

    /// <summary>Returns the member's spelling as the manual and a hover card print it.</summary>
    /// <returns>The spelling, with the member's own punctuation.</returns>
    public string Spelling() => ((Kind == WorldMemberKind.Parameters)
        ? $"{Name}({string.Join(
            separator: ", ",
            values: Parameters.Select(selector: static parameter => $"{parameter.Name}: {parameter.Type}")
        )})"
        : Position switch {
        WorldMemberPosition.Modifier => $"{Name}(…)",
        WorldMemberPosition.Property => $"{Name}:",
        WorldMemberPosition.Cell => Name,
        WorldMemberPosition.Body => Name,
        _ => Name,
    });
}
