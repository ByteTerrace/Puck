using System.Text.Json;
using Xunit;

namespace Puck.State.Tests;

public sealed class ReferenceScheduleTargetLawTests {
    private static (string Version, string RollForward) CheckoutSdk() {
        using var document = JsonDocument.Parse(json: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "global.json")));

        var sdk = document.RootElement.GetProperty(propertyName: "sdk");

        return (
            (sdk.GetProperty(propertyName: "version").GetString() ?? string.Empty),
            (sdk.GetProperty(propertyName: "rollForward").GetString() ?? string.Empty)
        );
    }

    [Fact]
    public void EveryPinnedTargetRecordsACompleteReferenceBuild() {
        Assert.NotEmpty(collection: ReferenceScheduleManifest.Targets);
        foreach (var target in ReferenceScheduleManifest.Targets) {
            foreach (var field in new[] {
                target.Id,
                target.Family,
                target.Build.Compilation,
                target.Build.Configuration,
                target.Build.GlobalJsonRollForward,
                target.Build.IlCompiler,
                target.Build.InstructionSet,
                target.Build.MethodBodyFolding,
                target.Build.Optimization,
                target.Build.ProfileGuidedOptimization,
                target.Build.RuntimeIdentifier,
                target.Build.RuntimePack,
                target.Build.Sdk,
                target.Analysis.CpuModel,
                target.Analysis.Disassembler,
                target.Analysis.Scheduler,
                target.Analysis.Triple,
                target.ScalarIntegerAddForm,
            }) {
                Assert.False(condition: string.IsNullOrWhiteSpace(value: field));
            }
        }
        Assert.Equal(
            ReferenceScheduleManifest.Targets.Count,
            ReferenceScheduleManifest.Targets.Select(selector: target => target.Id).Distinct(comparer: StringComparer.Ordinal).Count()
        );
    }
    [Fact]
    public void EveryPinnedTargetTargetsAnExplicitInstructionSetWithoutProfileData() {
        foreach (var target in ReferenceScheduleManifest.Targets) {
            Assert.False(condition: string.Equals(
                a: target.Build.InstructionSet,
                b: "native",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ));
            Assert.Equal(
                "none",
                target.Build.ProfileGuidedOptimization
            );
            Assert.Equal(
                "none",
                target.Build.MethodBodyFolding
            );
        }
    }
    [Fact]
    public void EveryPinnedTargetPinsTheCheckoutSdkAndItsRollForwardPolicy() {
        var (version, rollForward) = CheckoutSdk();

        foreach (var target in ReferenceScheduleManifest.Targets) {
            Assert.Equal(
                version,
                target.Build.Sdk
            );
            Assert.Equal(
                rollForward,
                target.Build.GlobalJsonRollForward
            );
        }
    }
    [Fact]
    public void TheCohortCarriesAtLeastTwoIsaFamiliesEachWithMoreThanOneTarget() {
        var families = ReferenceScheduleManifest.Targets
            .GroupBy(keySelector: target => target.Family, comparer: StringComparer.Ordinal)
            .ToList();

        Assert.True(condition: (families.Count >= 2));
        foreach (var family in families) {
            Assert.True(condition: (family.Count() >= 2));
            Assert.Equal(
                family.Count(),
                family.Select(selector: target => target.Analysis.CpuModel).Distinct(comparer: StringComparer.Ordinal).Count()
            );
        }
    }
    [Fact]
    public void EveryNormalizationUnitFollowsTheSerializedServiceRule() {
        foreach (var target in ReferenceScheduleManifest.Targets) {
            var service = target.ScalarIntegerAddService;

            Assert.True(condition: (service.ReciprocalThroughputDenominator > 0L));
            Assert.True(condition: (service.ReciprocalThroughputNumerator >= 0L));
            Assert.True(condition: (service.Latency >= 0L));
            Assert.Equal(
                Math.Max(
                    val1: 1L,
                    val2: Math.Max(
                        val1: service.Latency,
                        val2: ((service.ReciprocalThroughputNumerator + (service.ReciprocalThroughputDenominator - 1L)) / service.ReciprocalThroughputDenominator)
                    )
                ),
                service.Service
            );
        }
    }
    [Fact]
    public void TheManifestIdentifiesItselfByScheduleAndDigest() {
        Assert.Equal(
            "puck.cost.reference-schedule.v1",
            ReferenceScheduleManifest.Schema
        );
        Assert.False(condition: string.IsNullOrWhiteSpace(value: ReferenceScheduleManifest.ModelId));
        Assert.Matches(
            actualString: ReferenceScheduleManifest.Digest,
            expectedRegexPattern: "^[0-9a-f]{64}$"
        );
    }
}
