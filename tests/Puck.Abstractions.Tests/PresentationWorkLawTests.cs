using Puck.Abstractions.Counting;
using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="PresentationWork"/>: a presenter's skipped presents read as <c>presentation.skipped</c> under
/// the source name it was given, only go up, and no other kind is available from it.
/// </summary>
public sealed class PresentationWorkLawTests {
    [Fact]
    public void SkipsCountUnderTheSourcesName() {
        var work = new PresentationWork(name: "presentation.vulkan");
        IWorkCounterSource source = work;

        Assert.Equal(expected: "presentation.vulkan", actual: source.Name);
        Assert.Equal(expected: ["presentation.skipped"], actual: source.WorkKinds.ToArray().Select(selector: static kind => kind.Name));
        Assert.True(condition: source.TryRead(kind: PresentationWork.Skipped, value: out var before));
        Assert.Equal(actual: before, expected: 0L);

        work.RecordSkip();
        work.RecordSkip();

        Assert.True(condition: source.TryRead(kind: PresentationWork.Skipped, value: out var after));
        Assert.Equal(actual: after, expected: 2L);
        Assert.False(condition: source.TryRead(kind: new WorkKind(name: "presentation.skipped", unit: "count", workClass: WorkClass.Deterministic), value: out var other));
        Assert.Equal(actual: other, expected: 0L);
    }
    [Fact]
    public void AMalformedNameIsRefused() =>
        Assert.Throws<ArgumentException>(testCode: () => new PresentationWork(name: "vulkan"));
}
