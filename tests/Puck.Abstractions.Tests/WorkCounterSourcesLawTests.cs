using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="WorkCounterSources"/>: a single-kind read answers the declared kind by reference and nothing
/// else, not even a kind of the same name; and a source name is admitted only in the dotted form a
/// <see cref="WorkKind"/> name takes.
/// </summary>
public sealed class WorkCounterSourcesLawTests {
    private static readonly WorkKind Declared = new(name: "test.single.count", unit: "count", workClass: WorkClass.Deterministic);

    [Fact]
    public void ASingleKindReadAnswersOnlyTheDeclaredKind() {
        var count = default(WorkCount);

        count.Add(amount: 3L);

        Assert.True(condition: WorkCounterSources.TryReadSingle(count: in count, declared: Declared, kind: Declared, value: out var value));
        Assert.Equal(actual: value, expected: 3L);

        var namesake = new WorkKind(name: "test.single.count", unit: "count", workClass: WorkClass.Deterministic);

        Assert.False(condition: WorkCounterSources.TryReadSingle(count: in count, declared: Declared, kind: namesake, value: out var other));
        Assert.Equal(actual: other, expected: 0L);
    }
    [Fact]
    public void ASourceNameMustBeDotted() {
        Assert.Equal(actual: WorkKind.RequireSourceName(name: "test.single", paramName: "name"), expected: "test.single");
        Assert.Throws<ArgumentException>(testCode: () => WorkKind.RequireSourceName(name: "single", paramName: "name"));
        Assert.Throws<ArgumentNullException>(testCode: () => WorkKind.RequireSourceName(name: null!, paramName: "name"));
    }
}
