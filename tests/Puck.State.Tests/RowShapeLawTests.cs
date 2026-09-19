using System.Reflection;

using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="RowShapes.FromDomain"/> is the one derivation from the authored
/// <see cref="StateDomain"/> to <see cref="RowShape"/>, it answers for every declared case, and a row's own
/// <see cref="StateRow.Shape"/> reads it over the row's effective domain — including the domain an unauthored row
/// infers.</summary>
public sealed class RowShapeLawTests {
    private static StateRow Row(StateDomain? domain, int? capacity = null, IReadOnlyList<StateCell>? cells = null) => new(
        Name: CellName.Parse(candidate: "row"),
        Kind: CellKind.Int,
        Capacity: capacity,
        Cells: cells,
        Domain: domain
    );

    [Fact]
    public void EveryDeclaredDomainCaseDerivesAShape() {
        var cases = typeof(StateDomain)
            .GetNestedTypes(bindingAttr: BindingFlags.Public)
            .Where(predicate: static type => typeof(StateDomain).IsAssignableFrom(c: type))
            .ToArray();

        Assert.NotEmpty(collection: cases);

        var derived = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var declared in cases) {
            derived.Add(item: declared.Name);
        }

        Assert.Equal(
            expected: new HashSet<string>(
                collection: ["Slot", "Keys", "KeysOf", "CellsOf", "Ring"],
                comparer: StringComparer.Ordinal
            ),
            actual: derived
        );
    }
    [Fact]
    public void TheDerivationAnswersOneShapePerDomain() {
        Assert.Equal(
            expected: RowShape.Slot,
            actual: RowShapes.FromDomain(domain: StateDomain.Slot.Instance)
        );
        Assert.Equal(
            expected: RowShape.Keyed,
            actual: RowShapes.FromDomain(domain: StateDomain.Keys.Instance)
        );
        Assert.Equal(
            expected: RowShape.Keyed,
            actual: RowShapes.FromDomain(domain: new StateDomain.KeysOf(Row: CellName.Parse(candidate: "deck")))
        );
        Assert.Equal(
            expected: RowShape.Ordered,
            actual: RowShapes.FromDomain(domain: new StateDomain.KeysOf(
                Row: CellName.Parse(candidate: "deck"),
                Ordered: true
            ))
        );
        Assert.Equal(
            expected: RowShape.Lattice,
            actual: RowShapes.FromDomain(domain: new StateDomain.CellsOf(Topology: "board"))
        );
        Assert.Equal(
            expected: RowShape.Ring,
            actual: RowShapes.FromDomain(domain: new StateDomain.Ring(Capacity: 8))
        );
    }
    [Fact]
    public void ARowsShapeIsItsEffectiveDomainsShape() {
        Assert.Equal(
            expected: RowShape.Slot,
            actual: Row(domain: null).Shape
        );
        Assert.Equal(
            expected: RowShape.Keyed,
            actual: Row(
                capacity: 4,
                domain: null
            ).Shape
        );
        Assert.Equal(
            expected: RowShape.Ring,
            actual: Row(domain: new StateDomain.Ring(Capacity: 4)).Shape
        );
        Assert.Equal(
            expected: Row(domain: new StateDomain.CellsOf(Topology: "board")).Shape,
            actual: RowShapes.FromDomain(domain: Row(domain: new StateDomain.CellsOf(Topology: "board")).EffectiveDomain)
        );
    }
    [Fact]
    public void TheDerivationRefusesANullDomain() {
        Assert.Throws<ArgumentNullException>(testCode: () => RowShapes.FromDomain(domain: null!));
    }
}
