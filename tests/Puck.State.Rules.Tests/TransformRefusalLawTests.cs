using System.Reflection;

using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="TransformRefusal"/> is the one place the eleven scalar transforms'
/// refusal codes are enumerated. Every member is catalogued under the <c>state.transform</c> door, every member
/// belongs to a transform the union declares, and every case of the union either has codes named after it or is one
/// of the vector transforms, whose catalogued codes travel out of
/// <see cref="ArenaVectorTransforms"/> unchanged.</summary>
public sealed class TransformRefusalLawTests {
    private static readonly string[] Vectors = ["Copy", "Mean", "Mix", "Nearest", "Remember"];

    private static string[] Cases() => [.. typeof(ArenaTransform)
        .GetNestedTypes(bindingAttr: BindingFlags.Public)
        .Where(predicate: static type => type.IsSubclassOf(c: typeof(ArenaTransform)))
        .Select(selector: static type => type.Name)
        .OrderBy(keySelector: static name => name, comparer: StringComparer.Ordinal)];

    [Fact]
    public void EveryRefusalIsCataloguedUnderTheTransformDoor() {
        var members = Enum.GetValues<TransformRefusal>();

        Assert.NotEmpty(collection: members);
        foreach (var member in members) {
            var refusal = typeof(TransformRefusal)
                .GetField(name: member.ToString())!
                .GetCustomAttribute<RefusalAttribute>();

            Assert.NotNull(@object: refusal);
            Assert.Equal(
                actual: refusal.Door,
                expected: "state.transform"
            );
            Assert.False(condition: string.IsNullOrWhiteSpace(value: refusal.Condition));
        }
    }
    [Fact]
    public void EveryTransformCaseIsCoveredByCodesNamedAfterItOrByTheVectorKernels() {
        var cases = Cases();
        var names = Enum.GetNames<TransformRefusal>();

        Assert.NotEmpty(collection: cases);
        foreach (var name in cases) {
            Assert.True(
                condition: (Vectors.Contains(
                    comparer: StringComparer.Ordinal,
                    value: name
                ) || names.Any(predicate: code => code.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: name
                ))),
                userMessage: $"transform case '{name}' has no refusal code named after it"
            );
        }
    }
    [Fact]
    public void EveryRefusalNamesATransformTheUnionDeclaresOrIsTheSharedAddressingCode() {
        var cases = Cases();

        foreach (var name in Enum.GetNames<TransformRefusal>()) {
            Assert.True(
                condition: ((name == nameof(TransformRefusal.RowUnaddressable)) || cases.Any(predicate: transform => name.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: transform
                ))),
                userMessage: $"refusal '{name}' names no transform the union declares"
            );
        }
    }
}
