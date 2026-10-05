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
            throw new InvalidOperationException(message: "The lifecycle witness never creates a GPU frame.");
    }

    private static SdfWorldResidency Residency() => new(pipelines: SdfTestPipelines.Cache(), frameSource: new UncapturedSource(),
        kernels: SdfTestPipelines.Kernels(), name: "explanation-pane", width: 32, height: 32, brickPoolVoxelCapacity: 0);
    private static SdfWorldPicker Picker(SdfWorldResidency residency) {
        var picker = new SdfWorldPicker();

        typeof(SdfWorldPicker).GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_view")!
            .SetValue(obj: picker, value: new SdfWorldView(Residency: residency, View: 0));
        return picker;
    }

    [Fact]
    public void OneSurfacedRequestSettlesOnceAndRetainsOnlyItsFencedAnswer() {
        using var residency = Residency();
        var picker = Picker(residency: residency);
        using var explanation = new WorldExplainPick();
        var evaluations = 0;
        var pending = explanation.Request(2, picker, .25f, .75f, answer => {
            evaluations++;
            Assert.Equal(picker.RequestIdentity, answer.Request);
            return new CommandResult("fenced answer");
        });
        var settlement = Assert.IsType<CommandSettlement>(@object: pending.Settlement);

        Assert.True(condition: explanation.Holds(picker: picker));
        Assert.True(condition: picker.Pending);
        Assert.Equal(1, picker.RequestIdentity);
        Assert.True(condition: explanation.Request(2, picker, .5f, .5f, _ => CommandResult.None).IsError);
        explanation.Poll(picker: picker, slot: 2);
        Assert.False(condition: settlement.IsSettled);
        Assert.Equal(actual: evaluations, expected: 0);
        var answer = new SdfPickResult(picker.RequestIdentity, 8, 24, 32, 32, 0, 0, 0,
            new SdfProgramBuilder().Build(), 0);

        typeof(SdfWorldPicker).GetProperty(name: nameof(SdfWorldPicker.Result))!.SetValue(obj: picker, value: answer);
        explanation.Poll(picker: picker, slot: 2);
        explanation.Poll(picker: picker, slot: 2);
        Assert.Equal(actual: evaluations, expected: 1);
        Assert.Equal("fenced answer", CommandResult.Settling(settlement).Output);
        Assert.False(condition: explanation.Holds(picker: picker));
        Assert.Equal(answer, explanation.Captured);
        Assert.Same(residency, explanation.CapturedResidency);
        Assert.Equal(2, explanation.CapturedSlot);
        picker.Clear();
        Assert.Equal(answer, explanation.Captured);
    }
    [Fact]
    public void OrdinaryHoverWaitsForTheExplanationAndResumesAfterItsRouteIsCancelled() {
        using var residency = Residency();
        var picker = Picker(residency: residency);
        using var explanation = new WorldExplainPick();

        _ = explanation.Request(0, picker, .25f, .75f, _ => CommandResult.None);
        var request = picker.RequestIdentity;

        explanation.Demand(picker, .5f, .5f, surface: false);
        explanation.Demand(picker, .5f, .5f, surface: false);
        Assert.Equal(request, picker.RequestIdentity);
        Assert.True(condition: explanation.Holds(picker: picker));
        explanation.Poll(picker: picker, slot: 1);
        Assert.False(condition: explanation.Holds(picker: picker));
        explanation.Demand(picker, .5f, .5f, surface: false);
        Assert.True(condition: (picker.RequestIdentity > request));
        Assert.True(condition: picker.Pending);
    }
    [InlineData("seat")]
    [InlineData("pane")]
    [InlineData("closed")]
    [InlineData("superseded")]
    [Theory]
    public void AChangedOwnerRefusesPromptlyAndNeverClearsANewerConsumer(string change) {
        using var residency = Residency();
        var picker = Picker(residency: residency);
        using var explanation = new WorldExplainPick();
        var pending = explanation.Request(0, picker, .25f, .75f, _ => throw new InvalidOperationException(message: "No cancelled answer may be evaluated."));
        var settlement = Assert.IsType<CommandSettlement>(@object: pending.Settlement);
        var newer = ((change == "superseded") ? picker.Request(.5f, .5f) : 0);

        if (change == "closed") { explanation.Dispose(); } else { explanation.Poll(picker: ((change == "pane") ? null : picker), slot: ((change == "seat") ? 1 : 0)); }
        var verdict = CommandResult.Settling(settlement);

        Assert.True(condition: settlement.IsSettled);
        Assert.True(condition: verdict.IsError, userMessage: verdict.Output);
        Assert.Null(value: explanation.Captured);
        Assert.False(condition: explanation.Holds(picker: picker));
        if (change == "superseded") {
            Assert.Equal(newer, picker.RequestIdentity);
            Assert.True(condition: picker.Pending);
            Assert.Contains("superseded", verdict.Output);
        } else { Assert.False(condition: picker.Pending); }
    }
    [Fact]
    public void AnAbsentOrOutsidePaneRefusesWithoutIssuingAPixel() {
        using var residency = Residency();
        var picker = Picker(residency: residency);
        using var explanation = new WorldExplainPick();

        Assert.True(condition: explanation.Request(0, null, .5f, .5f, _ => CommandResult.None).IsError);
        Assert.True(condition: explanation.Request(0, picker, 1f, .5f, _ => CommandResult.None).IsError);
        Assert.True(condition: explanation.Request(0, picker, float.NaN, .5f, _ => CommandResult.None).IsError);
        Assert.Equal(0, picker.RequestIdentity);
        Assert.False(condition: picker.Pending);
    }
}
