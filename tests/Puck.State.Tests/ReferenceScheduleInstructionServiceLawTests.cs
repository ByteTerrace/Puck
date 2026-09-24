using Xunit;

namespace Puck.State.Tests;

public sealed class ReferenceScheduleInstructionServiceLawTests {
    private static long Ceiling(long numerator, long denominator) => ((numerator + (denominator - 1L)) / denominator);

    [Fact]
    public void TheServiceRuleIsTheSerializedMaximumOfLatencyAndReciprocalThroughput() {
        Assert.Equal(
            "q(i) = max(1, ceil(L(i)), ceil(T(i)))",
            ReferenceScheduleManifest.InstructionServiceRule
        );
        foreach (var (family, forms) in ReferenceScheduleManifest.InstructionForms) {
            Assert.NotEmpty(collection: forms);
            foreach (var form in forms) {
                Assert.False(condition: string.IsNullOrWhiteSpace(value: form.Form));
                foreach (var (target, service) in form.Service) {
                    Assert.True(
                        condition: (service.ReciprocalThroughputDenominator > 0L),
                        userMessage: $"{family}/{target}/{form.Form}"
                    );
                    Assert.True(
                        condition: (service.ReciprocalThroughputNumerator >= 0L),
                        userMessage: $"{family}/{target}/{form.Form}"
                    );
                    Assert.True(
                        condition: (service.Latency >= 0L),
                        userMessage: $"{family}/{target}/{form.Form}"
                    );
                    Assert.Equal(
                        Math.Max(
                            val1: 1L,
                            val2: Math.Max(
                                val1: service.Latency,
                                val2: Ceiling(
                                    denominator: service.ReciprocalThroughputDenominator,
                                    numerator: service.ReciprocalThroughputNumerator
                                )
                            )
                        ),
                        service.Service
                    );
                }
            }
        }
    }
    [Fact]
    public void EveryFormCarriesAMeasurementFromEveryTargetOfItsFamily() {
        foreach (var (family, forms) in ReferenceScheduleManifest.InstructionForms) {
            var expected = ReferenceScheduleManifest.Targets
                .Where(predicate: target => string.Equals(
                    a: target.Family,
                    b: family,
                    comparisonType: StringComparison.Ordinal
                ))
                .Select(selector: target => target.Id)
                .ToHashSet(comparer: StringComparer.Ordinal);

            Assert.NotEmpty(collection: expected);
            foreach (var form in forms) {
                Assert.Equal(
                    expected.OrderBy(keySelector: id => id, comparer: StringComparer.Ordinal),
                    form.Service.Keys.OrderBy(keySelector: id => id, comparer: StringComparer.Ordinal)
                );
            }
            Assert.Equal(
                forms.Count,
                forms.Select(selector: form => form.Form).Distinct(comparer: StringComparer.Ordinal).Count()
            );
        }
    }
    [Fact]
    public void EveryIsaFamilyInTheCohortCarriesItsOwnInstructionTable() {
        foreach (var family in ReferenceScheduleManifest.Targets.Select(selector: target => target.Family).Distinct(comparer: StringComparer.Ordinal)) {
            Assert.True(
                condition: ReferenceScheduleManifest.InstructionForms.ContainsKey(key: family),
                userMessage: family
            );
        }
        foreach (var family in ReferenceScheduleManifest.InstructionForms.Keys) {
            Assert.Contains(
                collection: ReferenceScheduleManifest.Targets.Select(selector: target => target.Family),
                filter: recorded => string.Equals(
                    a: recorded,
                    b: family,
                    comparisonType: StringComparison.Ordinal
                )
            );
        }
    }
    [Fact]
    public void TheServiceRestsOnLinkedPublishedMeasurements() {
        Assert.NotEmpty(collection: ReferenceScheduleManifest.PublishedSources);
        foreach (var source in ReferenceScheduleManifest.PublishedSources) {
            Assert.False(condition: string.IsNullOrWhiteSpace(value: source.Name));
            Assert.True(
                condition: (Uri.TryCreate(
                    result: out var url,
                    uriKind: UriKind.Absolute,
                    uriString: source.Url
                ) && (url.Scheme == Uri.UriSchemeHttps)),
                userMessage: source.Name
            );
            if (source.Redistribution is { } terms) {
                Assert.False(condition: string.IsNullOrWhiteSpace(value: terms));
            }
        }
    }
}
