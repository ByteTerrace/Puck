using System.Reflection;
using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// The tally of known bypasses. Several cases in this suite assert what the analyzer does today rather than what it
/// ought to do, each marked with a <c>// BYPASS:</c> comment naming the correct behaviour and a
/// <c>[Trait("bypass", …)]</c> naming the defect. This gate pins the set: a repair that closes one makes its case
/// fail, and the case is flipped to the right assertion and struck from this list in the same change. Nothing here
/// may be quietly deleted, and nothing may be quietly added.
/// </summary>
public sealed class BypassLedgerTests {
    /// <summary>The defects this suite currently records as present, each named by what goes wrong.</summary>
    private static readonly string[] Known = [];

    private static string[] Recorded() =>
        typeof(BypassLedgerTests).Assembly
            .GetTypes()
            .SelectMany(selector: type => type.GetMethods(bindingAttr: BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static))
            .SelectMany(selector: method => method.GetCustomAttributesData())
            .Where(predicate: attribute =>
                (string.Equals(a: attribute.AttributeType.FullName, b: "Xunit.TraitAttribute", comparisonType: StringComparison.Ordinal)
                && (attribute.ConstructorArguments.Count == 2)
                && string.Equals(a: (attribute.ConstructorArguments[0].Value as string), b: "bypass", comparisonType: StringComparison.Ordinal)))
            .Select(selector: attribute => ((attribute.ConstructorArguments[1].Value as string) ?? string.Empty))
            .Distinct(comparer: StringComparer.Ordinal)
            .OrderBy(keySelector: id => id, comparer: StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void EveryKnownBypassIsStillRecordedByACase() {
        var recorded = Recorded();

        Assert.Equal(expected: Known.OrderBy(keySelector: id => id, comparer: StringComparer.Ordinal).ToArray(), actual: recorded);
    }
    [Fact]
    public void TallyOfKnownBypassesIsWhatThisSuiteSays() {
        const int Tally = 0;

        var known = Known.Length;
        var recorded = Recorded().Length;

        Assert.Equal(actual: known, expected: Tally);
        Assert.Equal(actual: recorded, expected: Tally);
    }
}
