using System.Reflection;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldExplainPickLawTests {
    private sealed class UncapturedSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            throw new InvalidOperationException("The lifecycle witness never creates a GPU frame.");
    }
    private static SdfWorldResidency Residency() => new(pipelines: SdfTestPipelines.Cache(), frameSource: new UncapturedSource(),
        kernels: SdfTestPipelines.Kernels(), name: "explanation-pane", width: 32, height: 32, brickPoolVoxelCapacity: 0);
    private static SdfWorldPicker Picker(SdfWorldResidency residency) {
        var picker = new SdfWorldPicker();
        typeof(SdfWorldPicker).GetField("m_view", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(picker, new SdfWorldView(residency, 0));
        return picker;
    }

    [Fact]
    public void OneSurfacedRequestSettlesOnceAndRetainsOnlyItsFencedAnswer() {
        using var residency = Residency();
        var picker = Picker(residency);
        using var explanation = new WorldExplainPick();
        var evaluations = 0;
        var pending = explanation.Request(2, picker, .25f, .75f, answer => {
            evaluations++;
            Assert.Equal(picker.RequestIdentity, answer.Request);
            return new CommandResult("fenced answer");
        });
        var settlement = Assert.IsType<CommandSettlement>(pending.Settlement);
        Assert.True(explanation.Holds(picker));
        Assert.True(picker.Pending);
        Assert.Equal(1, picker.RequestIdentity);
        Assert.True(explanation.Request(2, picker, .5f, .5f, _ => CommandResult.None).IsError);
        explanation.Poll(2, picker);
        Assert.False(settlement.IsSettled);
        Assert.Equal(0, evaluations);
        var answer = new SdfPickResult(picker.RequestIdentity, 8, 24, 32, 32, 0, 0, 0,
            new SdfProgramBuilder().Build(), 0);
        typeof(SdfWorldPicker).GetProperty(nameof(SdfWorldPicker.Result))!.SetValue(picker, answer);
        explanation.Poll(2, picker);
        explanation.Poll(2, picker);
        Assert.Equal(1, evaluations);
        Assert.Equal("fenced answer", CommandResult.Settling(settlement).Output);
        Assert.False(explanation.Holds(picker));
        Assert.Equal(answer, explanation.Captured);
        Assert.Same(residency, explanation.CapturedResidency);
        Assert.Equal(2, explanation.CapturedSlot);
        picker.Clear();
        Assert.Equal(answer, explanation.Captured);
    }

    [Fact]
    public void OrdinaryHoverWaitsForTheExplanationAndResumesAfterItsRouteIsCancelled() {
        using var residency = Residency();
        var picker = Picker(residency);
        using var explanation = new WorldExplainPick();
        _ = explanation.Request(0, picker, .25f, .75f, _ => CommandResult.None);
        var request = picker.RequestIdentity;
        explanation.Demand(picker, .5f, .5f, surface: false);
        explanation.Demand(picker, .5f, .5f, surface: false);
        Assert.Equal(request, picker.RequestIdentity);
        Assert.True(explanation.Holds(picker));
        explanation.Poll(1, picker);
        Assert.False(explanation.Holds(picker));
        explanation.Demand(picker, .5f, .5f, surface: false);
        Assert.True(picker.RequestIdentity > request);
        Assert.True(picker.Pending);
    }

    [Theory]
    [InlineData("seat")]
    [InlineData("pane")]
    [InlineData("closed")]
    [InlineData("superseded")]
    public void AChangedOwnerRefusesPromptlyAndNeverClearsANewerConsumer(string change) {
        using var residency = Residency();
        var picker = Picker(residency);
        using var explanation = new WorldExplainPick();
        var pending = explanation.Request(0, picker, .25f, .75f, _ => throw new InvalidOperationException("No cancelled answer may be evaluated."));
        var settlement = Assert.IsType<CommandSettlement>(pending.Settlement);
        var newer = change == "superseded" ? picker.Request(.5f, .5f) : 0;
        if (change == "closed") { explanation.Dispose(); }
        else { explanation.Poll(change == "seat" ? 1 : 0, change == "pane" ? null : picker); }
        var verdict = CommandResult.Settling(settlement);
        Assert.True(settlement.IsSettled);
        Assert.True(verdict.IsError, verdict.Output);
        Assert.Null(explanation.Captured);
        Assert.False(explanation.Holds(picker));
        if (change == "superseded") {
            Assert.Equal(newer, picker.RequestIdentity);
            Assert.True(picker.Pending);
            Assert.Contains("superseded", verdict.Output);
        } else { Assert.False(picker.Pending); }
    }

    [Fact]
    public void AnAbsentOrOutsidePaneRefusesWithoutIssuingAPixel() {
        using var residency = Residency();
        var picker = Picker(residency);
        using var explanation = new WorldExplainPick();
        Assert.True(explanation.Request(0, null, .5f, .5f, _ => CommandResult.None).IsError);
        Assert.True(explanation.Request(0, picker, 1f, .5f, _ => CommandResult.None).IsError);
        Assert.True(explanation.Request(0, picker, float.NaN, .5f, _ => CommandResult.None).IsError);
        Assert.Equal(0, picker.RequestIdentity);
        Assert.False(picker.Pending);
    }
}
