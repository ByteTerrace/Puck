using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: an ordered zone's arrangement rank answers the same off its document row
/// (<see cref="StateReader.ArrangementRank(IReadOnlyList{StateRow}?, StateRow)"/>) as off a live arena
/// (<see cref="RuleReads.ArrangementRank"/>), within 0..k!-1 for k tokens, for every arrangement of every subset of
/// its domain, and a zone that holds its whole domain ranks the same as the <c>arrangementRank</c> expression
/// function over its nibble-packed domain ordinals.</summary>
public sealed class ArrangementRankLawTests {
    private static readonly string[] Domain = ["a", "b", "c", "d", "e", "f"];

    private static IEnumerable<string[]> Arrangements(int length) {
        if (length == 0) {
            yield return [];

            yield break;
        }

        foreach (var prefix in Arrangements(length: (length - 1))) {
            foreach (var token in Domain) {
                if (!prefix.Contains(value: token)) {
                    yield return [.. prefix, token];
                }
            }
        }
    }
    private static StateSection Section(IEnumerable<string> zone) => new(Rows: [
        new StateRow(
            Name: TransformFixture.Name(value: "tokens"),
            Kind: CellKind.Int,
            Capacity: Domain.Length,
            Cells: [.. Domain.Select(selector: (key, index) => TransformFixture.Cell(
                    key: key,
                    value: index
                ))]
        ),
        new StateRow(
            Name: TransformFixture.Name(value: "pile"),
            Kind: CellKind.Int,
            Capacity: Domain.Length,
            Domain: new StateDomain.KeysOf(
                Ordered: true,
                Row: TransformFixture.Name(value: "tokens")
            ),
            Cells: [.. zone.Select(selector: key => TransformFixture.Cell(
                    key: key,
                    value: 0L
                ))]
        ),
    ]);
    private static (long Document, long Arena) Ranks(StateSection section) {
        var context = TransformFixture.Context(section: section);
        var host = TransformFixture.Host(
            context: context,
            section: section
        );

        return (StateReader.ArrangementRank(
            rows: section.Rows,
            zone: section.Rows![1]
        ), RuleReads.ArrangementRank(
            domainOrdinal: TransformFixture.Ordinal(
                context: context,
                name: "tokens"
            ),
            reader: host,
            rowOrdinal: TransformFixture.Ordinal(
                context: context,
                name: "pile"
            )
        ));
    }

    [Fact]
    public void EveryArrangementRanksAlikeOffTheDocumentAndTheArena() {
        for (var length = 0; (length <= Domain.Length); length++) {
            var orders = ((long)Puck.Maths.Combinatorics.Factorial(n: length));

            foreach (var zone in Arrangements(length: length)) {
                var (document, arena) = Ranks(section: Section(zone: zone));

                Assert.Equal(
                    actual: arena,
                    expected: document
                );
                Assert.InRange(
                    actual: arena,
                    high: (orders - 1L),
                    low: 0L
                );

                if (length == Domain.Length) {
                    var packed = 0L;

                    for (var position = 0; (position < length); position++) {
                        packed |= (((long)Array.IndexOf(
                            array: Domain,
                            value: zone[position]
                        )) << (4 * position));
                    }

                    Assert.True(condition: ExpressionArithmetic.TryFunction(
                        arguments: [length, packed],
                        kind: CellKind.Int,
                        operation: ExpressionOp.ArrangementRank,
                        value: out var expression
                    ));
                    Assert.Equal(
                        actual: expression,
                        expected: arena
                    );
                }
            }
        }
    }
    [Fact]
    public void AZoneHoldingATokenItsDomainDoesNotDeclareRanksMinusOneBothWays() {
        var (document, arena) = Ranks(section: Section(zone: ["a", "z"]));

        Assert.Equal(
            actual: document,
            expected: -1L
        );
        Assert.Equal(
            actual: arena,
            expected: -1L
        );
    }
}
