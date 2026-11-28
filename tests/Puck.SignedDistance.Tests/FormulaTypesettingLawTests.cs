using Puck.Text;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: every field formula's readable lowering (<see cref="SdfFormulaTypesetting"/>) is well-formed math markup and
/// names every parameter the formula reads. A console that shows the engine's maths reads it through this lowering, and a
/// term it taps or scrubs is found by name, so a line that does not parse or a parameter the markup leaves out is a panel
/// that cannot show or select it. Running a world cannot show this for the formulas no exhibit shows.
/// </summary>
public sealed class FormulaTypesettingLawTests {
    [Fact]
    public void EveryTypesetFormulaParsesAndNamesEveryParameter() {
        var formulas = SdfFormulaTypesetting.All;

        Assert.NotEmpty(collection: formulas);
        foreach (var expected in new[] { "shape:Sphere", "shape:Box", "shape:Torus", "blend:SmoothUnion", "blend:CubicSmoothUnion", "blend:Subtraction" }) {
            Assert.True(condition: SdfFormulaTypesetting.TryFind(formula: out _, reference: expected), userMessage: $"{expected} has no typeset formula.");
        }

        foreach (var formula in formulas) {
            Assert.NotEmpty(collection: formula.Equation);

            var named = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var line in formula.Lines()) {
                Assert.True(condition: MathMarkup.TryParse(error: out var error, markup: line, root: out _), userMessage: $"{formula.Reference}: '{line}' {error}");
                named.UnionWith(other: MathMarkup.Terms(markup: line));
            }
            foreach (var term in formula.Terms) {
                Assert.True(condition: named.Contains(item: term.Name), userMessage: $"{formula.Reference} reads '{term.Name}', which its markup never names.");
                Assert.True(condition: MathMarkup.TryParse(error: out var error, markup: term.Symbol, root: out _), userMessage: $"{formula.Reference}: symbol '{term.Symbol}' {error}");
            }
            Assert.Subset(new HashSet<string>(collection: formula.Terms.Select(selector: static term => term.Name), comparer: StringComparer.Ordinal), named);
        }
    }
}
