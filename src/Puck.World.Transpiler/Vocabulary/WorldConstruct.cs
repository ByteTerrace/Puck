namespace Puck.World.Transpiler.Vocabulary;

/// <summary>One construct of the <c>puck.world.definition.v1</c> vocabulary, described once for every reader:
/// its keyword, its members, the document member it lowers to, and what the printer requires before it may print a
/// document node as this construct.</summary>
/// <param name="DocumentMember">The document member the construct lowers to, as a path from the document's own
/// root: <c>host</c>, <c>shapes[]</c>, <c>state.world[]</c>. A trailing <c>[]</c> marks one element of an array. A
/// construct legal in more than one place names its primary home.</param>
/// <param name="Enclosing">The keyword of the innermost construct whose body admits this one, or
/// <see langword="null"/> at the document's own root. <c>rule</c> stands for a rule body wherever one appears — a
/// <c>rule</c>, an <c>option</c>, a <c>step</c>, or an <c>if</c>/<c>transaction</c> branch.</param>
/// <param name="Grammar">The construct's whole spelling on one line, square brackets marking what is
/// optional.</param>
/// <param name="Keyword">The word at the start of the line, which is what a reader, a diagnostic and this table
/// key on.</param>
/// <param name="Members">Every member the construct describes, in the order the manual and a hover card list
/// them.</param>
/// <param name="RootArm">The arm the lowering's and the printer's root dispatch take for this construct, required
/// of a construct written at the document's own root and <see langword="null"/> for one written inside
/// another.</param>
/// <param name="Shape">The construct's grammatical shape.</param>
/// <param name="Snippet">The body a completion inserts, in the LSP snippet grammar.</param>
/// <param name="Sugar">What the printer requires before it may print a node as this construct.</param>
/// <param name="Summary">One sentence saying what the construct declares.</param>
public sealed record WorldConstruct(
    string Keyword,
    WorldConstructShape Shape,
    string DocumentMember,
    string Grammar,
    string Summary,
    WorldConstructSugar Sugar,
    IReadOnlyList<WorldConstructMember>? Members = null,
    string? Enclosing = null,
    string? Snippet = null,
    WorldRootArm? RootArm = null
) {
    private static IReadOnlyList<string> Keys(IEnumerable<WorldConstructMember> members, IEnumerable<string> extra) =>
        [.. members.SelectMany(selector: static member => member.DocumentKeys).Concat(second: extra).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal)];
    private static IReadOnlyList<string> Names(IEnumerable<WorldConstructMember> members) =>
        [.. members.Select(selector: static member => member.Name).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal)];
    // A member whose keys sit on the construct's own document node, written in its header or body rather than in a
    // cell entry.
    private static bool OwnNode(WorldConstructMember member) => (
        (member.DocumentNode is null) &&
        (member.Position != WorldMemberPosition.Cell)
    );

    /// <summary>Gets every member the construct describes.</summary>
    public IReadOnlyList<WorldConstructMember> Members { get; init; } = (Members ?? []);
    /// <summary>Gets every key the spelling can carry on the construct's own document node: the described members'
    /// keys and the keys the emitter adds.</summary>
    /// <remarks>A node carrying a key outside this set has no spelling and takes
    /// <see cref="WorldConstructSugar.Fallback"/>. Members written in a cell body are excluded — their keys sit on
    /// the cell, not on the node; see <see cref="CellKeys"/>.</remarks>
    public IReadOnlyList<string> NodeKeys => Keys(
        extra: Sugar.AdmittedKeys,
        members: Members.Where(predicate: OwnNode)
    );
    /// <summary>Gets every key the spelling can carry on one entry of the construct's cell body.</summary>
    public IReadOnlyList<string> CellKeys => Keys(
        extra: [],
        members: Members.Where(predicate: static member => (member.Position == WorldMemberPosition.Cell))
    );
    /// <summary>Gets every key a document node must carry before the spelling applies.</summary>
    /// <remarks>A required member naming more than one key names alternatives — a <c>push</c>'s right-hand side is
    /// <c>fromState</c> or <c>expression</c>, never both — so its keys are admitted without being required.</remarks>
    public IReadOnlyList<string> RequiredKeys => Keys(
        extra: Sugar.RequiredKeys,
        members: Members.Where(predicate: static member => (
            member.Required &&
            (member.DocumentKeys.Count == 1) &&
            OwnNode(member: member)
        ))
    );
    /// <summary>Gets every document member one of this construct's statements writes to, its own first.</summary>
    public IReadOnlyList<string> DocumentMembers => [DocumentMember, .. Members
        .Select(selector: static member => member.DocumentNode)
        .OfType<string>()
        .Where(predicate: node => !string.Equals(
        a: node,
        b: DocumentMember,
        comparisonType: StringComparison.Ordinal
    ))
        .Distinct(comparer: StringComparer.Ordinal)
        .Order(comparer: StringComparer.Ordinal)];
    /// <summary>Gets the names of the <c>name(args)</c> modifiers the construct's header admits.</summary>
    public IReadOnlyList<string> ModifierNames => Names(members: Members.Where(predicate: static member => (member.Position == WorldMemberPosition.Modifier)));
    /// <summary>Gets the names of the modifiers one entry of the construct's cell body admits — its optional cell
    /// members, the required ones being the entry's own key and value.</summary>
    public IReadOnlyList<string> CellModifierNames => Names(members: Members.Where(predicate: static member => ((member.Position == WorldMemberPosition.Cell) && !member.Required)));

    /// <summary>Returns the member written as <paramref name="name"/>, the header's before a body's.</summary>
    /// <param name="name">The member's authored spelling.</param>
    /// <param name="member">The member, when one is described.</param>
    /// <returns><see langword="true"/> when the construct describes a member of that name.</returns>
    /// <remarks>A name is unique per <see cref="WorldMemberPosition"/>, not per construct: a
    /// <c>table</c> describes <c>advance</c> both as a row modifier and as a cell's own.</remarks>
    public bool TryGetMember(string name, out WorldConstructMember? member) {
        member = Members
            .Where(predicate: candidate => string.Equals(
            a: candidate.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ))
            .OrderBy(keySelector: static candidate => candidate.Position)
            .FirstOrDefault();

        return (member is not null);
    }
    /// <summary>Returns the document value the printer may elide <paramref name="key"/> against.</summary>
    /// <param name="key">A key on the construct's document node.</param>
    /// <returns>The default as canonical JSON text, or <see langword="null"/> when the key carries none.</returns>
    public string? DefaultFor(string key) => Members
        .Where(predicate: member => ((member.Default is not null) && member.DocumentKeys.Contains(value: key, comparer: StringComparer.Ordinal)))
        .Select(selector: static member => member.Default)
        .FirstOrDefault();
}
