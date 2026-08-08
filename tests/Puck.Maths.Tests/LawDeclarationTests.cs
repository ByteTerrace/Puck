using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// The declaration/case parity gate. <see cref="LawDeclarations.All"/> (the authored <c>laws/*.json</c> rows) and
/// <see cref="LawRegistry.All"/> (the run-bound cases <see cref="LawRegistry"/> builds from them) must name exactly the
/// same set of ids, one case per id. This is the runtime replacement for the guarantee the old inline
/// <c>Case(id, tier, members, legs, run)</c> calls gave for free at compile time — a case could not exist without its
/// tier, members and legs beside it. Now that those live in data and a case is <c>Case(id, run)</c>, nothing stops a
/// registry entry from naming an id with no declaration, or a declaration from never being instantiated as a case; this
/// gate is what still catches both.
/// </summary>
public sealed class LawDeclarationTests {
    [Fact]
    [Trait(name: "tier", value: "Default")]
    public void EveryDeclarationHasExactlyOneCase() {
        var declaredIds = LawDeclarations.All.Keys.ToHashSet(comparer: StringComparer.Ordinal);
        var caseIds = LawRegistry.All.Select(selector: lawCase => lawCase.Id).ToList();
        var caseIdCounts = caseIds
            .GroupBy(keySelector: id => id, comparer: StringComparer.Ordinal)
            .ToDictionary(keySelector: group => group.Key, elementSelector: group => group.Count(), comparer: StringComparer.Ordinal);

        var undeclared = caseIdCounts.Keys.Where(predicate: id => !declaredIds.Contains(item: id)).OrderBy(keySelector: id => id, comparer: StringComparer.Ordinal).ToList();
        var uninstantiated = declaredIds.Where(predicate: id => !caseIdCounts.ContainsKey(key: id)).OrderBy(keySelector: id => id, comparer: StringComparer.Ordinal).ToList();
        var duplicated = caseIdCounts.Where(predicate: pair => (pair.Value > 1)).Select(selector: pair => pair.Key).OrderBy(keySelector: id => id, comparer: StringComparer.Ordinal).ToList();

        Assert.True(condition: (undeclared.Count == 0), userMessage: $"{undeclared.Count} case id(s) with no declaration under tests/Puck.Maths.Tests/laws/: {string.Join(separator: ", ", values: undeclared.Take(count: 20))}");
        Assert.True(condition: (uninstantiated.Count == 0), userMessage: $"{uninstantiated.Count} declared id(s) with no case in LawRegistry.All: {string.Join(separator: ", ", values: uninstantiated.Take(count: 20))}");
        Assert.True(condition: (duplicated.Count == 0), userMessage: $"{duplicated.Count} case id(s) declared more than once in LawRegistry.All: {string.Join(separator: ", ", values: duplicated.Take(count: 20))}");
    }
}
