using Puck.Assets.Documents;
using Xunit;

namespace Puck.Assets.Tests;

// Every refusal of an unknown document member takes its suggestion from DocumentNearestNames, so what one calls near
// every one calls near.
public sealed class DocumentNearestNamesLawTests {
    private static readonly string[] Members = ["seconds", "renderScale", "seatCount", "slots"];

    [InlineData("secondz", "seconds")]
    [InlineData("RENDERSCALE", "renderScale")]
    [InlineData("renderscael", "renderScale")]
    [Theory]
    public void ANameWithinTheEditLimitIsSuggestedIgnoringCase(string written, string expected) {
        Assert.Equal(expected: expected, actual: Assert.Single(collection: DocumentNearestNames.Among(candidates: Members, name: written)));
    }
    [Fact]
    public void ANameFarFromEveryCandidateSuggestsNothing() {
        Assert.Empty(collection: DocumentNearestNames.Among(candidates: Members, name: "bogus"));
        Assert.Equal(expected: string.Empty, actual: DocumentNearestNames.Suggestion(candidates: Members, name: "bogus"));
    }
    [Fact]
    public void TheSuggestionListsAtMostThreeNamesClosestFirst() {
        var near = DocumentNearestNames.Among(candidates: ["slota", "slotc", "slotb", "slotd", "slot"], name: "slot");

        Assert.Equal(expected: ["slot", "slota", "slotb"], actual: near);
        Assert.Equal(expected: "; nearest: 'seconds'", actual: DocumentNearestNames.Suggestion(candidates: Members, name: "secondz"));
    }
}
