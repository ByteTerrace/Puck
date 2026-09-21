using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

public sealed class ReferenceScheduleVocabularyLawTests {
    private static IReadOnlyList<CostCoefficient> Of(string vocabulary) => [.. ReferenceScheduleManifest.Coefficients
        .Where(predicate: coefficient => string.Equals(
            a: coefficient.Vocabulary,
            b: vocabulary,
            comparisonType: StringComparison.Ordinal
        ))];

    [Fact]
    public void EveryRegisteredExpressionOperationCarriesExactlyOneCoefficient() {
        var registered = Of(vocabulary: "expression");

        Assert.Equal(
            Enum.GetValues<ExpressionOp>().Select(selector: operation => operation.ToString()).Order(),
            registered.Select(selector: coefficient => coefficient.Operation).Order()
        );
        Assert.Equal(
            registered.Count,
            registered.Select(selector: coefficient => coefficient.Operation).Distinct(comparer: StringComparer.Ordinal).Count()
        );
    }
    [Fact]
    public void EveryRegisteredEffectArmCarriesExactlyOneCoefficient() {
        var registered = Of(vocabulary: "effect");
        var arms = typeof(ActionEffect)
            .GetCustomAttributes(
                attributeType: typeof(JsonDerivedTypeAttribute),
                inherit: false
            )
            .Cast<JsonDerivedTypeAttribute>()
            .Select(selector: attribute => ((string)attribute.TypeDiscriminator!));

        Assert.Equal(
            arms.Order(),
            registered.Select(selector: coefficient => coefficient.Operation).Order()
        );
    }
    [Fact]
    public void EveryCoefficientIsEitherPricedByAKernelOrExplicitlyUnmodeled() {
        var kernels = ReferenceScheduleManifest.Kernels.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: kernel => kernel,
            keySelector: kernel => kernel.Id
        );

        foreach (var coefficient in ReferenceScheduleManifest.Coefficients) {
            Assert.False(condition: string.IsNullOrWhiteSpace(value: coefficient.Operation));
            Assert.NotEmpty(collection: coefficient.NumericKinds);
            if (coefficient.Kernel is not { } id) {
                Assert.True(condition: coefficient.Bound.IsUnmodeled);
                Assert.False(condition: string.IsNullOrWhiteSpace(value: coefficient.Bound.Reason));
                continue;
            }
            var kernel = kernels[id];

            // The coefficient's own reason names the kernel that left it unresolved, which the kernel's reason
            // cannot; only the verdict and the price have to agree.
            Assert.Equal(
                kernel.RepresentativeService.Kind,
                coefficient.Bound.Kind
            );
            Assert.Equal(
                kernel.RepresentativeService.Cycles,
                coefficient.Bound.Cycles
            );
            Assert.False(condition: string.IsNullOrWhiteSpace(value: kernel.IncludedOverhead));
            Assert.False(condition: string.IsNullOrWhiteSpace(value: kernel.InputDomainBound));
            if (coefficient.Bound.IsKnown) {
                Assert.True(condition: (coefficient.Bound.Cycles > 0L));
                continue;
            }
            Assert.False(condition: string.IsNullOrWhiteSpace(value: coefficient.Bound.Reason));
        }
    }
    [Fact]
    public void EveryExpressionCoefficientAgreesWithTheOperatorTableItPrices() {
        foreach (var coefficient in Of(vocabulary: "expression")) {
            var operation = Enum.Parse<ExpressionOp>(value: coefficient.Operation);
            var descriptor = ExpressionOperators.Find(operation: operation);

            Assert.NotNull(@object: descriptor);
            Assert.Equal(
                new[] { CellKind.Int, CellKind.Fixed }
                    .Where(predicate: kind => descriptor!.Admits(kind: kind))
                    .Select(selector: kind => kind.ToString()),
                coefficient.NumericKinds
            );
            switch (coefficient.Kernel) {
                case "state.expression.function":
                    Assert.True(
                        condition: descriptor!.Function,
                        userMessage: coefficient.Operation
                    );
                    break;
                case "state.expression.unary":
                    Assert.False(condition: descriptor!.Function);
                    Assert.Equal(
                        1,
                        descriptor.Arity
                    );
                    break;
                case "state.expression.binary":
                    Assert.False(condition: descriptor!.Function);
                    Assert.Equal(
                        2,
                        descriptor.Arity
                    );
                    break;
                default:
                    Assert.NotNull(@object: coefficient.Kernel);
                    break;
            }
        }
    }
    [Fact]
    public void TheScheduleAnswersEveryOperationTheManifestRegisters() {
        foreach (var coefficient in Of(vocabulary: "expression")) {
            var operation = Enum.Parse<ExpressionOp>(value: coefficient.Operation);

            foreach (var kind in new[] { CellKind.Int, CellKind.Fixed }) {
                var expected = (coefficient.NumericKinds.Contains(
                    value: kind.ToString(),
                    comparer: StringComparer.Ordinal
                )
                    ? coefficient.Bound
                    : CostBound.Unmodeled(reason: $"ExpressionOp.{operation} ({kind}) is not a registered operation of the reference schedule."));

                Assert.Equal(
                    expected,
                    ReferenceSchedule.OperationCostBound(
                        kind: kind,
                        operation: operation
                    )
                );
            }
        }
        Assert.Equal(
            ReferenceScheduleManifest.Digest,
            CostModel.Default.EvidenceDigest
        );
    }
    [Fact]
    public void AChangeToAReferenceKernelSourceInvalidatesItsEvidence() {
        foreach (var kernel in ReferenceScheduleManifest.Kernels) {
            foreach (var source in kernel.Sources) {
                var path = RepositoryPaths.Resolve(relativePath: source.Path);

                Assert.True(
                    condition: File.Exists(path: path),
                    userMessage: source.Path
                );
                Assert.Equal(
                    source.Sha256,
                    Convert.ToHexStringLower(inArray: SHA256.HashData(source: File.ReadAllBytes(path: path)))
                );
            }
        }
    }
}
