using System.Text;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for a count's class: a kind carries the class its owner declared and never the allocation class, the engine's
/// own GPU and presentation kinds carry the classes a collector relies on, and a class or a pass state is spelled on
/// the wire exactly as the strict enum converter writes it, by the one attribute on each member.
/// </summary>
public sealed class WorkClassLawTests {
    private static string Converted<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonSerializer.Deserialize<string>(json: JsonSerializer.Serialize(
            options: new JsonSerializerOptions { Converters = { new StrictEnumConverter<TEnum>() } },
            value: value
        ))!;

    [InlineData(WorkClass.Deterministic)]
    [InlineData(WorkClass.PerBackendDeterministic)]
    [InlineData(WorkClass.Pacing)]
    [Theory]
    public void AKindCarriesTheClassItsOwnerDeclared(WorkClass workClass) =>
        Assert.Equal(
            actual: new WorkKind(name: "test.kind", unit: "count", workClass: workClass).Class,
            expected: workClass
        );
    [Fact]
    public void NoKindIsOfTheAllocationClass() =>
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new WorkKind(name: "test.kind", unit: "count", workClass: WorkClass.AllocationZeroNonzero));
    [Fact]
    public void GpuSubmissionCountsAreDeterministicAndCreatedObjectsPerBackend() {
        Assert.All(collection: GpuWork.SubmissionKinds.ToArray(), action: static kind => Assert.Equal(actual: kind.Class, expected: WorkClass.Deterministic));
        Assert.All(collection: GpuWork.LifetimeKinds.ToArray(), action: static kind => Assert.Equal(actual: kind.Class, expected: WorkClass.PerBackendDeterministic));
        Assert.Equal(actual: PresentationWork.Skipped.Class, expected: WorkClass.Pacing);
    }
    [Fact]
    public void AClassIsSpelledAsTheStrictConverterWritesIt() {
        Assert.Equal(
            actual: Enum.GetValues<WorkClass>().Select(selector: static value => EnumWireName<WorkClass>.Of(value: value)),
            expected: ["deterministic", "per-backend-deterministic", "pacing", "allocation-zero-nonzero"]
        );

        foreach (var value in Enum.GetValues<WorkClass>()) {
            Assert.Equal(actual: EnumWireName<WorkClass>.Of(value: value), expected: Converted(value: value));
            Assert.True(condition: EnumWireName<WorkClass>.TryParse(name: Converted(value: value), value: out var parsed));
            Assert.Equal(actual: parsed, expected: value);
        }

        Assert.False(condition: EnumWireName<WorkClass>.TryParse(name: "Deterministic", value: out _));
    }
    [Fact]
    public void APassStateIsSpelledAsTheStrictConverterWritesIt() {
        foreach (var value in Enum.GetValues<GpuPassState>()) {
            Assert.Equal(actual: EnumWireName<GpuPassState>.Of(value: value), expected: Converted(value: value));
        }

        Assert.Equal(actual: EnumWireName<GpuPassState>.Of(value: GpuPassState.NotReached), expected: "not-reached");
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => EnumWireName<GpuPassState>.Of(value: ((GpuPassState)9)));
    }
    [Fact]
    public void TheLegendNamesAKindsUnitAndClass() {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(utf8Json: buffer)) {
            writer.WriteStartObject();
            WorkCounterReport.WriteKind(
                kind: GpuWork.PipelinesCreated,
                writer: writer
            );
            writer.WriteEndObject();
        }

        Assert.Equal(
            actual: Encoding.UTF8.GetString(bytes: buffer.ToArray()),
            expected: """{"gpu.created.pipelines":{"unit":"count","class":"per-backend-deterministic"}}"""
        );
    }
}
