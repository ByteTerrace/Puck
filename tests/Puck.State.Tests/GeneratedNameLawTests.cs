using Puck.Testing;
using Xunit;

namespace Puck.State.Tests;

/// <summary>The generated-name laws at the state layer, each with its mutation proof: <see cref="GeneratedName"/>
/// joins every set of parts into its reserved form and back out of it; no name the one identifier rule admits bare,
/// and no name an author door admits quoted, is in that form; and the state catalog mints every row a pool generates in
/// it. The compiler's own minting sites and author doors are held to the same laws in
/// <c>tests/Puck.World.Transpiler.Tests/GeneratedNameLawTests.cs</c>.</summary>
public sealed class GeneratedNameLawTests {
    private static readonly IReadOnlyList<string> Corpus = IdentifierCorpus.Names(count: 4000);

    // The parts a minting site joins: names that could come from an author, which carry neither joiner.
    private static IEnumerable<string> Parts(char joiner) => Corpus.Where(predicate: name => ((name.Length > 0) && !name.Contains(value: joiner)));
    // Every pair of parts a join spells, the join's name for it, and the parts the name read back as.
    private static List<string> JoinViolations(Func<string, string, string> join, Func<string, bool> isGenerated, char joiner) {
        var violations = new List<string>();
        var parts = Parts(joiner: joiner).Take(count: 60).ToArray();
        var joined = new Dictionary<string, (string, string)>(comparer: StringComparer.Ordinal);

        foreach (var left in parts) {
            foreach (var right in parts) {
                var name = join(arg1: left, arg2: right);

                if (!isGenerated(arg: name)) {
                    violations.Add(item: $"'{left}' + '{right}' joined as '{name}', which is not in the reserved form");
                }
                if (joined.TryGetValue(key: name, value: out var earlier) && (earlier != (left, right))) {
                    violations.Add(item: $"'{left}' + '{right}' and '{earlier.Item1}' + '{earlier.Item2}' both joined as '{name}'");
                }

                joined[name] = (left, right);
            }
        }

        return violations;
    }
    private static List<string> AuthorViolations(Func<string, bool> admitsBare, Func<string, bool> admitsAuthored) {
        var violations = new List<string>();

        foreach (var name in Corpus) {
            if (admitsBare(arg: name) && GeneratedName.IsGenerated(name: name)) {
                violations.Add(item: $"'{name}' is written bare and is in the reserved form");
            }
            if (admitsAuthored(arg: name) == GeneratedName.IsReserved(name: name)) {
                violations.Add(item: $"'{name}' is {(GeneratedName.IsReserved(name: name) ? "in" : "not in")} a reserved form and an author door {(admitsAuthored(arg: name) ? "admits" : "refuses")} it");
            }
        }

        return violations;
    }
    private static bool AuthorAdmits(string name) => GeneratedName.TryValidateAuthored(
        name: name,
        reason: out _
    );
    private static void AssertNone(List<string> violations) => Assert.True(
        condition: (violations.Count == 0),
        userMessage: $"{violations.Count} violation(s):{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: violations.Take(count: 25))}"
    );
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateSection PoolSection(string pool, string field) => new(
        PairPools: [new StatePairPool(LeftPool: Name(value: pool), Name: Name(value: "links"), Record: Name(value: "Link"), MaxLive: 2, RightPool: Name(value: pool))],
        Pools: [new StatePool(Capacity: 2, Name: Name(value: pool), Record: Name(value: "Piece"))],
        Records: [
            new StateRecord(Fields: [new StatePoolField(Name: Name(value: field))], Name: Name(value: "Piece")),
            new StateRecord(Fields: [new StatePoolField(Name: Name(value: "weight"))], Name: Name(value: "Link")),
        ]
    );

    [Fact]
    public void EveryJoinSpellsItsPartsInTheReservedFormAndNoTwoPartListsShareAName() {
        AssertNone(violations: JoinViolations(
            isGenerated: static name => GeneratedName.IsGenerated(name: name),
            join: static (left, right) => GeneratedName.Join(left, right),
            joiner: GeneratedName.Joiner
        ));
        AssertNone(violations: JoinViolations(
            isGenerated: static name => GeneratedName.IsGenerated(name: name),
            join: static (left, right) => GeneratedName.Append(name: left, part: right),
            joiner: GeneratedName.Joiner
        ));
        AssertNone(violations: JoinViolations(
            isGenerated: static name => GeneratedName.IsGeneratedFile(name: name),
            join: static (left, right) => GeneratedName.JoinFile(left, right),
            joiner: GeneratedName.FileJoiner
        ));
        AssertNone(violations: JoinViolations(
            isGenerated: static name => GeneratedName.IsGeneratedFile(name: name),
            join: static (left, right) => GeneratedName.AppendFile(name: left, part: right),
            joiner: GeneratedName.FileJoiner
        ));
    }
    // The mutation proof: a join spelled with an underscore, a character any author name may carry, neither reads as
    // generated nor keeps two part lists apart.
    [Fact]
    public void TheJoinLawCatchesAJoinThatSpellsWithAnAuthorCharacter() => Assert.NotEmpty(collection: JoinViolations(
        isGenerated: static name => GeneratedName.IsGenerated(name: name),
        join: static (left, right) => $"{left}_{right}",
        joiner: GeneratedName.Joiner
    ));
    [Fact]
    public void AJoinRefusesAPartThatWouldBlurWhereOnePartEnds() {
        _ = Assert.Throws<ArgumentException>(testCode: static () => GeneratedName.Join("turn", "a$b"));
        _ = Assert.Throws<ArgumentException>(testCode: static () => GeneratedName.Join("turn", ""));
        _ = Assert.Throws<ArgumentException>(testCode: static () => GeneratedName.Join("turn"));
        _ = Assert.Throws<ArgumentException>(testCode: static () => GeneratedName.Append(name: "turn", part: "a$b"));
        _ = Assert.Throws<ArgumentException>(testCode: static () => GeneratedName.JoinFile("rulepush", "a~b"));
        _ = Assert.Throws<ArgumentException>(testCode: static () => GeneratedName.AppendFile(name: "rulepush", part: "a~b"));
        Assert.Equal(actual: GeneratedName.Join("$pool", "pieces", "live"), expected: "$pool$pieces$live");
        Assert.True(condition: GeneratedName.TryStripHead(head: "turn", name: "turn$east", rest: out var rest));
        Assert.Equal(actual: rest, expected: "east");
        Assert.False(condition: GeneratedName.TryStripHead(head: "turn", name: "turn_east", rest: out _));
        Assert.False(condition: GeneratedName.TryStripHead(head: "turn", name: "turn$", rest: out _));
    }
    [Fact]
    public void NoNameAnAuthorWritesBareOrQuotedIsInTheReservedForm() => AssertNone(violations: AuthorViolations(
        admitsAuthored: AuthorAdmits,
        admitsBare: static name => IdentifierSpelling.IsName(text: name)
    ));
    // The mutation proofs: a bare rule that lets the sigil stand anywhere, and an author door that checks only the
    // front of a name, each admit a generated name.
    [Fact]
    public void TheAuthorLawCatchesARuleOrADoorThatAdmitsTheSigilInside() {
        Assert.NotEmpty(collection: AuthorViolations(
            admitsAuthored: AuthorAdmits,
            admitsBare: static name => ((name.Length > 0) && name.All(predicate: static character => (IdentifierSpelling.IsPart(character: character) || (character == IdentifierSpelling.Sigil))))
        ));
        Assert.NotEmpty(collection: AuthorViolations(
            admitsAuthored: static name => !name.StartsWith(value: GeneratedName.Joiner),
            admitsBare: static name => IdentifierSpelling.IsName(text: name)
        ));
        Assert.NotEmpty(collection: AuthorViolations(
            admitsAuthored: static name => !GeneratedName.IsGenerated(name: name),
            admitsBare: static name => IdentifierSpelling.IsName(text: name)
        ));
    }
    [Fact]
    public void EveryFileBackedAuthorDoorRefusesTheFileJoiner() {
        foreach (var name in Corpus.Where(predicate: static name => (name.Length > 0))) {
            Assert.Equal(
                actual: GeneratedName.TryValidateAuthoredFile(
                    name: name,
                    reason: out var reason
                ),
                expected: !GeneratedName.IsGeneratedFile(name: name)
            );

            if (GeneratedName.IsGeneratedFile(name: name)) {
                Assert.Contains(actualString: reason, expectedSubstring: "reserves");
            }
        }

        Assert.False(condition: GeneratedName.TryValidateAuthored(name: "turn$east", reason: out var refusal));
        Assert.Contains(actualString: refusal, expectedSubstring: "reserves");
        Assert.False(condition: GeneratedName.TryValidateAuthored(name: "turn~east", reason: out var fileRefusal));
        Assert.Contains(actualString: fileRefusal, expectedSubstring: "reserves");
    }

    // Every name a document may hold (no '~'), spelled file-backed, and the names that spelled alike.
    private static List<string> ToFileViolations(Func<string, string> toFile) {
        var violations = new List<string>();
        var spelled = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var name in Corpus.Where(predicate: static name => ((name.Length > 0) && GeneratedName.TryValidateDocument(name: name, reason: out _)))) {
            var file = toFile(arg: name);

            if (file.Contains(value: GeneratedName.Joiner)) {
                violations.Add(item: $"'{name}' spells '{file}', which still carries '{GeneratedName.Joiner}'");
            }
            if (spelled.TryGetValue(key: file, value: out var earlier)) {
                violations.Add(item: $"'{name}' and '{earlier}' both spell '{file}'");
            }

            spelled[file] = name;
        }

        return violations;
    }

    [Fact]
    public void ToFileSpellsEveryDocumentNameWithoutTheJoinerAndKeepsEveryTwoApart() {
        AssertNone(violations: ToFileViolations(toFile: GeneratedName.ToFile));
        Assert.Equal(actual: GeneratedName.ToFile(name: "link$west"), expected: "link~west");
        Assert.Equal(actual: GeneratedName.ToFile(name: "plain"), expected: "plain");

        var refused = Assert.Throws<ArgumentException>(testCode: static () => GeneratedName.ToFile(name: "link~west"));

        Assert.Contains(actualString: refused.Message, expectedSubstring: "reserves");
        _ = Assert.Throws<ArgumentException>(testCode: static () => GeneratedName.ToFile(name: ""));
    }
    // The mutation proofs: a spelling that leaves the joiner in place, and one that folds two characters onto one —
    // the map a name carrying '~' would need, which is why a document name may not carry one.
    [Fact]
    public void TheToFileLawCatchesASpellingThatKeepsTheJoinerOrFoldsTwoNamesTogether() {
        Assert.NotEmpty(collection: ToFileViolations(toFile: static name => name));
        Assert.NotEmpty(collection: ToFileViolations(toFile: static name => name.Replace(newChar: GeneratedName.FileJoiner, oldChar: GeneratedName.Joiner).Replace(newChar: GeneratedName.FileJoiner, oldChar: '-')));
    }
    [Fact]
    public void TheCatalogMintsEveryRowAPoolGeneratesInTheReservedForm() {
        var generated = StateCatalog.ExpandRows(section: PoolSection(field: "hp", pool: "pieces")).Where(predicate: static row => row.Generated).Select(selector: static row => row.Name.Value).ToArray();

        Assert.NotEmpty(collection: generated);
        Assert.All(collection: generated, action: static name => Assert.True(condition: GeneratedName.IsGenerated(name: name), userMessage: name));
        Assert.Contains(collection: generated, expected: "$pool$pieces$live");
        Assert.Contains(collection: generated, expected: "$pool$pieces$generation");
        Assert.Contains(collection: generated, expected: "$pool$pieces$field$hp");
        Assert.Contains(collection: generated, expected: "$pool$links$field$weight");
    }
    // A field named like a pool part and a pool named like a field part generate distinct rows, which the separator
    // an author could also write never kept apart.
    [Fact]
    public void APoolAndAFieldNamedAlikeGenerateDistinctRows() {
        var rows = StateCatalog.ExpandRows(section: PoolSection(field: "live", pool: "pieces")).Where(predicate: static row => row.Generated).Select(selector: static row => row.Name.Value).ToArray();

        Assert.Equal(actual: rows.Distinct(comparer: StringComparer.Ordinal).Count(), expected: rows.Length);
        Assert.Contains(collection: rows, expected: "$pool$pieces$live");
        Assert.Contains(collection: rows, expected: "$pool$pieces$field$live");
    }
    // A module instance's pool is itself a generated name, `left$pieces`, and generates its rows under it; a record
    // field is always authored, so one carrying the joiner is refused.
    [Fact]
    public void AnInstancesPoolGeneratesItsRowsAndAFieldCarryingTheJoinerIsRefused() {
        var rows = StateCatalog.ExpandRows(section: PoolSection(field: "hp", pool: "left$pieces")).Where(predicate: static row => row.Generated).Select(selector: static row => row.Name.Value).ToArray();
        var field = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.ExpandRows(section: PoolSection(field: "a$b", pool: "pieces")));

        Assert.Contains(collection: rows, expected: "$pool$left$pieces$live");
        Assert.Contains(collection: rows, expected: "$pool$left$pieces$field$hp");
        Assert.Contains(actualString: field.Message, expectedSubstring: "may not carry '$'");
    }
}
