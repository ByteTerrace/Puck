using System.Numerics;
using Xunit;

namespace Puck.State.Tests;

public sealed class ReferenceScheduleKernelPriceLawTests {
    private static (BigInteger Numerator, BigInteger Denominator) Median(IReadOnlyList<(BigInteger Numerator, BigInteger Denominator)> values) {
        var ordered = values
            .Order(comparer: Comparer<(BigInteger Numerator, BigInteger Denominator)>.Create(comparison: (left, right) => BigInteger.Compare(
                left: (left.Numerator * right.Denominator),
                right: (right.Numerator * left.Denominator)
            )))
            .ToList();
        var middle = (ordered.Count / 2);

        if ((ordered.Count % 2) == 1) {
            return ordered[middle];
        }
        var left = ordered[(middle - 1)];
        var right = ordered[middle];

        return Reduce(
            denominator: (2 * left.Denominator * right.Denominator),
            numerator: ((left.Numerator * right.Denominator) + (right.Numerator * left.Denominator))
        );
    }
    private static (BigInteger Numerator, BigInteger Denominator) Reduce(BigInteger numerator, BigInteger denominator) {
        var divisor = BigInteger.GreatestCommonDivisor(
            left: numerator,
            right: denominator
        );

        return (
            (numerator / divisor),
            (denominator / divisor)
        );
    }

    [Fact]
    public void TheAggregationRuleIsPinnedInTheManifest() {
        Assert.Equal(
            "an equal-weight median within each ISA family, then an equal-weight median of the family results, rounded upward",
            ReferenceScheduleManifest.RepresentativeServiceRule
        );
        Assert.Equal(
            "the exact arithmetic mean of the middle two values",
            ReferenceScheduleManifest.EvenSizedMedianRule
        );
        Assert.False(condition: string.IsNullOrWhiteSpace(value: ReferenceScheduleManifest.RepresentativeServiceNormalization));
    }
    [Fact]
    public void EveryKernelNamesItsEntryPointFormulaAndSealedSource() {
        Assert.NotEmpty(collection: ReferenceScheduleManifest.Kernels);
        foreach (var kernel in ReferenceScheduleManifest.Kernels) {
            Assert.False(condition: string.IsNullOrWhiteSpace(value: kernel.Id));
            Assert.False(condition: string.IsNullOrWhiteSpace(value: kernel.EntryPoint));
            Assert.False(condition: string.IsNullOrWhiteSpace(value: kernel.Symbol));
            Assert.False(condition: string.IsNullOrWhiteSpace(value: kernel.Formula));
            Assert.NotEmpty(collection: kernel.Sources);
            foreach (var source in kernel.Sources) {
                Assert.False(condition: string.IsNullOrWhiteSpace(value: source.Path));
                Assert.Matches(
                    actualString: source.Sha256,
                    expectedRegexPattern: "^[0-9a-f]{64}$"
                );
            }
            foreach (var loop in kernel.LoopBounds) {
                Assert.True(condition: (loop.Iterations > 0L));
                Assert.False(condition: string.IsNullOrWhiteSpace(value: loop.Symbol));
                Assert.False(condition: string.IsNullOrWhiteSpace(value: loop.Contract));
            }
        }
        Assert.Equal(
            ReferenceScheduleManifest.Kernels.Count,
            ReferenceScheduleManifest.Kernels.Select(selector: kernel => kernel.Id).Distinct(comparer: StringComparer.Ordinal).Count()
        );
    }
    [Fact]
    public void EveryKernelIsPricedOnEveryTargetOrNamesTheTargetThatLeftItUnresolved() {
        var targets = ReferenceScheduleManifest.Targets.Select(selector: target => target.Id).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var kernel in ReferenceScheduleManifest.Kernels) {
            Assert.Equal(
                targets.OrderBy(keySelector: id => id, comparer: StringComparer.Ordinal),
                kernel.Targets.Keys.OrderBy(keySelector: id => id, comparer: StringComparer.Ordinal)
            );

            var unresolved = kernel.Targets.Values.Where(predicate: price => (price.Unresolved.Count > 0)).ToList();

            foreach (var price in unresolved) {
                Assert.All(
                    action: reason => Assert.False(condition: string.IsNullOrWhiteSpace(value: reason)),
                    collection: price.Unresolved
                );
            }
            Assert.Equal(
                (unresolved.Count > 0),
                kernel.RepresentativeService.IsUnmodeled
            );
            if (kernel.RepresentativeService.IsUnmodeled) {
                Assert.False(condition: string.IsNullOrWhiteSpace(value: kernel.RepresentativeService.Reason));
                continue;
            }
            foreach (var price in kernel.Targets.Values) {
                Assert.True(condition: (price.Cycles > 0L));
                Assert.True(condition: (price.MaximumPathInstructions > 0L));
                Assert.NotNull(@object: price.Simulation);
                Assert.False(condition: string.IsNullOrWhiteSpace(value: price.Simulation!.TotalCycles));
                Assert.False(condition: string.IsNullOrWhiteSpace(value: price.Simulation.BlockReciprocalThroughput));
                Assert.NotEmpty(collection: price.Simulation.ResourcePressure);
            }
        }
    }
    [Fact]
    public void EveryPricedKernelNormalizesToItsTargetsScalarIntegerAddAndAggregatesByTheFamilyBalancedMedian() {
        var targets = ReferenceScheduleManifest.Targets.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: target => target,
            keySelector: target => target.Id
        );

        foreach (var kernel in ReferenceScheduleManifest.Kernels) {
            if (!kernel.RepresentativeService.IsKnown) {
                Assert.Empty(collection: kernel.FamilyMedians);
                continue;
            }
            var families = new Dictionary<string, List<(BigInteger, BigInteger)>>(comparer: StringComparer.Ordinal);

            foreach (var (id, price) in kernel.Targets) {
                var target = targets[id];

                Assert.Equal(
                    Reduce(
                        denominator: target.ScalarIntegerAddService.Service,
                        numerator: price.Cycles
                    ),
                    Reduce(
                        denominator: price.NormalizedDenominator,
                        numerator: price.NormalizedNumerator
                    )
                );
                if (!families.TryGetValue(
                    key: target.Family,
                    value: out var samples
                )) {
                    families[target.Family] = samples = [];
                }
                samples.Add(item: (price.NormalizedNumerator, price.NormalizedDenominator));
            }

            var medians = new List<(BigInteger, BigInteger)>();

            foreach (var (family, samples) in families) {
                var median = Median(values: samples);

                Assert.Equal(
                    Reduce(
                        denominator: kernel.FamilyMedians[family].Denominator,
                        numerator: kernel.FamilyMedians[family].Numerator
                    ),
                    median
                );
                medians.Add(item: median);
            }
            Assert.Equal(
                families.Count,
                kernel.FamilyMedians.Count
            );

            var overall = Median(values: medians);

            Assert.Equal(
                ((long)BigInteger.Divide(
                    dividend: ((overall.Numerator + overall.Denominator) - 1),
                    divisor: overall.Denominator
                )),
                kernel.RepresentativeService.Cycles
            );
        }
    }
}
