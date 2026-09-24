using System.Globalization;
using System.Text;

namespace Puck.World.Transpiler.Vocabulary;

/// <summary>A set of described constructs, and the projections its readers take: the keyword lookup the parser and
/// the printer dispatch on, the hover card, and the manual's vocabulary tables.</summary>
/// <remarks>Readers take a table rather than reaching for <see cref="WorldConstructs.Table"/>, so a law can hand
/// every reader one description it made up and watch all of them follow.</remarks>
public sealed class WorldConstructTable {
    private readonly Dictionary<(string Enclosing, string Keyword), WorldConstruct> m_byKey;
    // The lowering and the parser ask these per statement, so each is answered once, here, rather than by a scan
    // of the whole table at every ask.
    private readonly Dictionary<string, IReadOnlyList<WorldConstruct>> m_inside;
    private readonly Dictionary<string, WorldConstruct> m_byKeyword;
    private readonly Dictionary<string, WorldConstruct> m_rootByKeyword;
    private readonly HashSet<string> m_embeddedLanguages;

    /// <summary>Initializes a table over <paramref name="constructs"/>.</summary>
    /// <param name="constructs">The described constructs; no two may share both an enclosing construct and a
    /// keyword.</param>
    /// <param name="excluded">The constructs the table deliberately carries no row for.</param>
    /// <exception cref="ArgumentException">A keyword is described more than once in the same enclosing
    /// construct.</exception>
    /// <remarks>A construct's identity is where it is written and the word it opens with, so one keyword may mean
    /// two constructs in two places.</remarks>
    public WorldConstructTable(IReadOnlyList<WorldConstruct> constructs, IReadOnlyList<WorldConstructExclusion>? excluded = null) {
        ArgumentNullException.ThrowIfNull(constructs);

        Excluded = [.. (excluded ?? []).OrderBy(keySelector: static exclusion => exclusion.Keyword, comparer: StringComparer.Ordinal)];
        m_byKey = [];
        foreach (var construct in constructs) {
            if (!m_byKey.TryAdd(
                key: ((construct.Enclosing ?? ""), construct.Keyword),
                value: construct
            )) {
                throw new ArgumentException(
                    message: $"'{construct.Keyword}' is described more than once inside {((construct.Enclosing is null)
                        ? "the document"
                        : $"'{construct.Enclosing}'")}.",
                    paramName: nameof(constructs)
                );
            }
        }
        Constructs = [.. constructs
            .OrderBy(keySelector: static construct => (construct.Enclosing ?? ""), comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: static construct => construct.Keyword, comparer: StringComparer.Ordinal)];
        m_inside = Constructs
            .GroupBy(keySelector: static construct => (construct.Enclosing ?? ""), comparer: StringComparer.Ordinal)
            .ToDictionary(
                comparer: StringComparer.Ordinal,
                elementSelector: static group => ((IReadOnlyList<WorldConstruct>)Array.AsReadOnly(array: group.ToArray())),
                keySelector: static group => group.Key
            );
        // A keyword names the construct written at the root when one is, and otherwise the one construct that
        // spells it; a keyword several nested constructs share and the root does not names none of them.
        m_byKeyword = [];
        foreach (var group in Constructs.GroupBy(keySelector: static construct => construct.Keyword, comparer: StringComparer.Ordinal)) {
            var matches = group.ToArray();
            var chosen = (matches.FirstOrDefault(predicate: static candidate => (candidate.Enclosing is null)) ?? ((matches.Length == 1)
                ? matches[0]
                : null
            ));

            if (chosen is not null) {
                m_byKeyword[group.Key] = chosen;
            }
        }
        m_embeddedLanguages = new(
            collection: Constructs
                .Where(predicate: static construct => (construct.Shape == WorldConstructShape.EmbeddedLanguage))
                .Select(selector: static construct => construct.Keyword),
            comparer: StringComparer.OrdinalIgnoreCase
        );
        m_rootByKeyword = new(comparer: StringComparer.OrdinalIgnoreCase);
        foreach (var construct in Inside(enclosing: null)) {
            _ = m_rootByKeyword.TryAdd(key: construct.Keyword, value: construct);
        }
    }

    private static string Cell(string text) => ((text.Length == 0)
        ? "—"
        : text
    );
    private static string Code(string text) => $"`{text.Replace(
        comparisonType: StringComparison.Ordinal,
        newValue: "`` ` ``",
        oldValue: "`"
    )}`";
    private static string CodeList(IEnumerable<string> names) => Cell(text: string.Join(
        separator: ", ",
        values: names.Select(selector: Code)
    ));
    private static string MemberCard(WorldConstruct construct, WorldConstructMember member) =>
        $"**`{member.Spelling()}`**\n\n{member.Summary} A `{construct.Keyword}` member{((member.DocumentKeys.Count == 0)
            ? ((member.Lowering is null)
                ? ""
                : $", lowering to {member.Lowering}")
            : $", lowering to {CodeList(names: member.DocumentKeys)}")}.{((member.Default is null)
                ? ""
                : $" Defaults to `{member.Default}`.")}";
    private static string Shape(WorldConstructShape shape) => shape switch {
        WorldConstructShape.EmbeddedLanguage => "embedded language",
        _ => shape.ToString().ToLowerInvariant(),
    };
    private static void RenderConstruct(StringBuilder output, WorldConstruct construct) {
        _ = output.AppendLine().AppendLine(
            CultureInfo.InvariantCulture,
            $"### `{construct.Keyword}`"
        ).AppendLine();
        _ = output.AppendLine(
            CultureInfo.InvariantCulture,
            $"```puck\n{construct.Grammar}\n```"
        ).AppendLine();

        if (construct.Members.Count > 0) {
            _ = output.AppendLine(value: "| Member | Written | Kind | Admitted on | Lowers to | Default | Means |");
            _ = output.AppendLine(value: "|---|---|---|---|---|---|---|");
            foreach (var member in construct.Members) {
                _ = output.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| {Code(text: member.Spelling())}{(member.Required
                        ? " (required)"
                        : "")} | {member.Position.ToString().ToLowerInvariant()} | {member.Kind.ToString().ToLowerInvariant()}{((member.Choices.Count > 0)
                            ? $" ({CodeList(names: member.Choices)})"
                            : "")} | {((member.AdmittedKinds.Count == 0)
                                ? "—"
                                : CodeList(names: member.AdmittedKinds))} | {((member.DocumentKeys.Count == 0)
                                ? Cell(text: (member.Lowering ?? ""))
                                : CodeList(names: member.DocumentKeys))} | {((member.Default is null)
                                ? "—"
                                : Code(text: member.Default))} | {member.Summary} |"
                );
            }
            _ = output.AppendLine();
        }

        if (!construct.Sugar.Printed) {
            _ = output.AppendLine(
                CultureInfo.InvariantCulture,
                $"Never printed back; the printer produces {construct.Sugar.Fallback} in its place."
            );

            return;
        }

        if (construct.Sugar.FromRows) {
            _ = output.AppendLine(
                CultureInfo.InvariantCulture,
                $"Read back as `{construct.Keyword}` from the rows it generated when {construct.Sugar.Condition}; otherwise {construct.Sugar.Fallback}."
            );

            return;
        }

        var requirements = new List<string>();

        if (construct.RequiredKeys.Count > 0) {
            requirements.Add(item: $"the node carries {CodeList(names: construct.RequiredKeys)}");
        }
        if (!construct.Sugar.Open) {
            requirements.Add(item: $"it carries no key outside {CodeList(names: construct.NodeKeys)}");
        }
        if (construct.Sugar.Condition is { } condition) {
            requirements.Add(item: condition);
        }
        _ = output.AppendLine(
            CultureInfo.InvariantCulture,
            $"Printed back as `{construct.Keyword}`{((requirements.Count == 0)
                ? ""
                : $" when {string.Join(
                    separator: ", and ",
                    values: requirements
                )}")}; otherwise {construct.Sugar.Fallback}.{(construct.Sugar.Open
                    ? " Every field of the node the description does not name prints as an ordinary property."
                    : "")}"
        );

        if (construct.CellKeys.Count > 0) {
            _ = output.AppendLine().AppendLine(
                CultureInfo.InvariantCulture,
                $"One body entry carries {CodeList(names: construct.CellKeys)} and nothing else."
            );
        }
        if (construct.DocumentMembers.Count > 1) {
            _ = output.AppendLine().AppendLine(
                CultureInfo.InvariantCulture,
                $"One statement writes {CodeList(names: construct.DocumentMembers)}."
            );
        }
    }

    /// <summary>Gets every described construct, ordered by the construct it is written in and then by
    /// keyword.</summary>
    public IReadOnlyList<WorldConstruct> Constructs { get; }
    /// <summary>Gets the constructs the table carries no row for, each with its reason, in ordinal order.</summary>
    public IReadOnlyList<WorldConstructExclusion> Excluded { get; }
    /// <summary>Gets every described keyword, in ordinal order, once each however many constructs spell
    /// it.</summary>
    public IReadOnlyList<string> Keywords => [.. m_byKey.Keys
        .Select(selector: static key => key.Keyword)
        .Distinct(comparer: StringComparer.Ordinal)
        .Order(comparer: StringComparer.Ordinal)];

    /// <summary>Returns the constructs written directly inside <paramref name="enclosing"/>.</summary>
    /// <param name="enclosing">An enclosing construct's keyword, or <see langword="null"/> for the document's own
    /// root.</param>
    /// <returns>The constructs legal there, in table order.</returns>
    public IReadOnlyList<WorldConstruct> Inside(string? enclosing) => m_inside.GetValueOrDefault(
        defaultValue: [],
        key: (enclosing ?? "")
    );
    /// <summary>Returns the root dispatch arm of the construct written as <paramref name="keyword"/> at the
    /// document's own root.</summary>
    /// <param name="keyword">The keyword as written.</param>
    /// <returns>The arm, or <see langword="null"/> when no construct of that keyword is written at the
    /// root.</returns>
    /// <remarks>The lowering's own dispatch: a block whose identifier names no root construct takes the generic
    /// path, which is what a block nested inside another construct is. The keyword is matched without regard to
    /// case, matching how a block's identifier reaches the lowering.</remarks>
    public WorldRootArm? RootArmOf(string keyword) => m_rootByKeyword.GetValueOrDefault(key: keyword)?.RootArm;
    /// <summary>Returns the root dispatch arm of the constructs that write <paramref name="documentKey"/>, the
    /// document's own root key.</summary>
    /// <param name="documentKey">A key on the document's root object, as the document spells it.</param>
    /// <returns>The arm every root construct writing there names, or <see langword="null"/> when none writes there
    /// or they disagree.</returns>
    /// <remarks>The printer's own dispatch: it reads a key off the document and has no keyword in hand. Two
    /// spellings may write one key — <c>state</c> and <c>sql</c> both write <c>state</c> — and both must name the
    /// same arm, since the printer has nothing to tell them apart by.</remarks>
    public WorldRootArm? RootArmWriting(string documentKey) {
        var array = $"{documentKey}[]";
        var arms = Inside(enclosing: null)
            .Where(predicate: construct => (string.Equals(
            a: construct.DocumentMember,
            b: documentKey,
            comparisonType: StringComparison.Ordinal
        ) || string.Equals(
            a: construct.DocumentMember,
            b: array,
            comparisonType: StringComparison.Ordinal
        )))
            .Select(selector: static construct => construct.RootArm)
            .Distinct()
            .ToArray();

        return ((arms.Length == 1)
            ? arms[0]
            : null
        );
    }
    /// <summary>Returns a value indicating whether <paramref name="identifier"/> opens an embedded language
    /// block.</summary>
    /// <param name="identifier">A block identifier as written.</param>
    /// <returns><see langword="true"/> when a described construct of that keyword is an embedded language.</returns>
    /// <remarks>An embedded language's own name is matched without regard to case, matching how the block's
    /// lowering finds its dialect; every other keyword is matched exactly.</remarks>
    public bool IsEmbeddedLanguage(string identifier) => m_embeddedLanguages.Contains(item: identifier);
    /// <summary>Returns the one construct whose keyword is <paramref name="keyword"/>, wherever it is
    /// written.</summary>
    /// <param name="keyword">The keyword as written.</param>
    /// <param name="construct">The described construct, when exactly one carries that keyword.</param>
    /// <returns><see langword="true"/> when one root construct, or exactly one construct anywhere, carries that
    /// keyword.</returns>
    /// <remarks>The root spelling wins when a keyword is also meaningful in a nested construct. A caller that
    /// knows the nesting takes the contextual overload.</remarks>
    public bool TryGet(string keyword, out WorldConstruct? construct) => m_byKeyword.TryGetValue(
        key: keyword,
        value: out construct
    );
    /// <summary>Returns the construct written as <paramref name="keyword"/> inside
    /// <paramref name="enclosing"/>.</summary>
    /// <param name="enclosing">The enclosing construct's keyword, or <see langword="null"/> for the document's own
    /// root.</param>
    /// <param name="keyword">The keyword as written.</param>
    /// <param name="construct">The described construct, when one is described there.</param>
    /// <returns><see langword="true"/> when the table describes that keyword in that place.</returns>
    public bool TryGet(string? enclosing, string keyword, out WorldConstruct? construct) => m_byKey.TryGetValue(
        key: ((enclosing ?? ""), keyword),
        value: out construct
    );
    /// <summary>Returns the root construct whose rows <paramref name="word"/> is the array spelling of.</summary>
    /// <param name="word">A word as written, which may be the plural array name of a row construct.</param>
    /// <param name="construct">The construct whose rows the array carries, when exactly one writes there.</param>
    /// <returns><see langword="true"/> when the word is the array spelling of exactly one root construct's
    /// rows.</returns>
    /// <remarks>A keyword resolves as itself first: <c>prototypes</c> is a construct of its own as well as the
    /// array its <c>prototype</c> rows sit in.</remarks>
    public bool TryGetByArraySpelling(string word, out WorldConstruct? construct) {
        var array = $"{word}[]";
        var matches = Inside(enclosing: null).Where(predicate: candidate => string.Equals(
            a: candidate.DocumentMember,
            b: array,
            comparisonType: StringComparison.Ordinal
        )).ToArray();

        construct = ((matches.Length == 1)
            ? matches[0]
            : null
        );

        return (construct is not null);
    }
    /// <summary>Returns the constructs describing a member written as <paramref name="name"/>, in table
    /// order.</summary>
    /// <param name="name">A member's authored spelling.</param>
    /// <returns>Every construct that describes a member of that name.</returns>
    /// <remarks>A member's identity is (construct, name), not the bare name: sixteen names are owned by more than
    /// one construct, and <c>name</c> by twenty-five, so a reader that resolves a member by name alone attributes
    /// it to whichever construct it meets first.</remarks>
    public IReadOnlyList<WorldConstruct> Owners(string name) => [.. Constructs.Where(predicate: construct => construct.TryGetMember(
        member: out _,
        name: name
    ))];
    /// <summary>Returns the member written as <paramref name="name"/> and the construct that owns it, resolved from
    /// the construct the cursor sits in.</summary>
    /// <param name="enclosing">The keyword of the construct whose statement <paramref name="name"/> is written in,
    /// or <see langword="null"/> when the caller has no context.</param>
    /// <param name="name">The member's authored spelling.</param>
    /// <param name="owner">The construct the member belongs to, when it resolves to exactly one.</param>
    /// <param name="member">The member, when it resolves to exactly one.</param>
    /// <returns><see langword="true"/> when the name resolves to one construct's member.</returns>
    public bool TryGetMember(string? enclosing, string name, out WorldConstruct? owner, out WorldConstructMember? member) {
        if (
            (enclosing is not null) &&
            TryGet(
            construct: out var enclosingConstruct,
            keyword: enclosing
        ) &&
            enclosingConstruct!.TryGetMember(
            member: out member,
            name: name
        )
        ) {
            owner = enclosingConstruct;

            return true;
        }

        var owners = Owners(name: name);

        owner = ((owners.Count == 1)
            ? owners[0]
            : null
        );
        member = null;

        return ((owner is not null) && owner.TryGetMember(
            member: out member,
            name: name
        ));
    }
    /// <summary>Returns the hover card for a word, whether it names a construct or one of its members.</summary>
    /// <param name="enclosing">The keyword of the construct whose statement the word is written in, or
    /// <see langword="null"/> when the caller has no context.</param>
    /// <param name="word">The word under the cursor.</param>
    /// <param name="card">The card as Markdown.</param>
    /// <returns><see langword="true"/> when the table describes that word.</returns>
    /// <remarks>A word is read as the enclosing construct's member first, since that is what the cursor is in: a
    /// <c>slot</c>'s <c>space(…)</c> modifier is that modifier, not the <c>space</c> declaration of the same name.
    /// With no context, a name several constructs own is described as each of them rather than as the first.</remarks>
    public bool TryDescribe(string? enclosing, string word, out string? card) {
        if (TryGetMember(
            enclosing: enclosing,
            member: out var owned,
            name: word,
            owner: out var owner
        ) && (enclosing is not null)) {
            card = MemberCard(
                construct: owner!,
                member: owned!
            );

            return true;
        }
        if (TryGet(
            construct: out var construct,
            keyword: word
        )) {
            var members = ((construct!.Members.Count == 0)
                ? ""
                : $"\n\n{string.Join(
                    separator: "\n",
                    values: construct.Members.Select(selector: static member => $"- `{member.Spelling()}` — {member.Summary}")
                )}"
            );

            card = $"**`{construct.Grammar}`**\n\n{construct.Summary} Lowers to `{construct.DocumentMember}`.{members}";

            return true;
        }

        // The plural array spelling of a row construct — `materials [ … ]` beside `material { … }` — is the same
        // rows written as an array, so it takes the construct's own card rather than none.
        if (TryGetByArraySpelling(
            construct: out var array,
            word: word
        )) {
            card = $"**`{word} [ … ]`**\n\nThe array form of `{array!.Keyword}`, which writes the same `{array.DocumentMember}` rows. {array.Summary}";

            return true;
        }

        var owners = Owners(name: word);

        if (owners.Count == 0) {
            card = null;

            return false;
        }
        card = string.Join(
            separator: "\n\n",
            values: owners.Select(selector: candidate => {
                _ = candidate.TryGetMember(
                    member: out var member,
                    name: word
                );

                return MemberCard(
                    construct: candidate,
                    member: member!
                );
            })
        );

        return true;
    }
    /// <summary>Returns the manual's vocabulary tables for this table.</summary>
    /// <returns>The whole document, newline-terminated, with <c>\n</c> line endings.</returns>
    public string Render() {
        var output = new StringBuilder();

        _ = output.AppendLine(value: "# World vocabulary");
        _ = output.AppendLine();
        _ = output.AppendLine(value: "Generated by `puck vocabulary` from `WorldConstructTable`; do not hand-edit.");
        _ = output.AppendLine(value: "`puck vocabulary --check` fails when this file disagrees with the table.");
        _ = output.AppendLine();
        _ = output.AppendLine(value: "Every construct of `puck.world.definition.v1`'s authoring vocabulary: the word at the start of");
        _ = output.AppendLine(value: "the line, the members it carries, the document member it lowers to, and what the printer needs");
        _ = output.AppendLine(value: "before it may print a document node back as that construct. What a document field means is");
        _ = output.AppendLine(value: "`puck schema`'s (`src/Puck.World/Assets/worlds/schema/`), and which fields carry names is");
        _ = output.AppendLine(value: "`puck registry`'s ([the name registry](../world-name-registry.md)); this table is the surface and");
        _ = output.AppendLine(value: "its mapping onto them.");
        _ = output.AppendLine();
        _ = output.AppendLine(value: "A `Lowers to` path is read from the document's own root, and a trailing `[]` marks one element of");
        _ = output.AppendLine(value: "an array. `Written in` names the innermost construct whose body admits the row; `rule` there means");
        _ = output.AppendLine(value: "a rule body wherever one appears — a `rule`, an `option`, a `step`, or an `if`/`transaction`");
        _ = output.AppendLine(value: "branch. `Admitted on` is the cell kinds the row's own `kind` must be one of before the member is");
        _ = output.AppendLine(value: "admitted — a range and a per-second accumulation are defined over the numeric kinds alone, and a");
        _ = output.AppendLine(value: "row of any other kind refuses them by name — and is empty for a member whose admission turns on no");
        _ = output.AppendLine(value: "kind.");
        _ = output.AppendLine();
        _ = output.AppendLine(value: "## Constructs");
        _ = output.AppendLine();
        _ = output.AppendLine(value: "| Keyword | Written in | Shape | Lowers to | Declares |");
        _ = output.AppendLine(value: "|---|---|---|---|---|");
        foreach (var construct in Constructs) {
            _ = output.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {Code(text: construct.Keyword)} | {((construct.Enclosing is null)
                    ? "the document"
                    : Code(text: construct.Enclosing))} | {Shape(shape: construct.Shape)} | {Code(text: construct.DocumentMember)} | {construct.Summary} |"
            );
        }
        _ = output.AppendLine();
        _ = output.AppendLine(value: "## Members, and what the printer requires");

        foreach (var construct in Constructs) {
            RenderConstruct(
                construct: construct,
                output: output
            );
        }
        if (Excluded.Count > 0) {
            _ = output.AppendLine();
            _ = output.AppendLine(value: "## Not described, and why");
            _ = output.AppendLine();
            _ = output.AppendLine(value: "| Construct | Why |");
            _ = output.AppendLine(value: "|---|---|");
            foreach (var exclusion in Excluded) {
                _ = output.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| {Code(text: exclusion.Keyword)} | {exclusion.Reason} |"
                );
            }
        }
        _ = output.AppendLine();
        _ = output.AppendLine(value: "---");
        _ = output.AppendLine();
        _ = output.AppendLine(value: "[Reference](README.md) · [The language core](dsl.md) ·");
        _ = output.AppendLine(value: "[The world vocabulary](../../src/Puck.World.Transpiler/README.md)");

        return output.ToString().ReplaceLineEndings(replacementText: "\n");
    }
}
