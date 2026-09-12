using Xunit;

namespace Puck.World.Schema.Tests;

internal static class MembershipLawAssertions {
    public static void RefusalWithControl(string lawId, Func<bool> deniedOutcome, Func<bool> controlOutcome) {
        Assert.False(condition: deniedOutcome(), userMessage: $"{lawId}: denied case succeeded");
        Assert.True(condition: controlOutcome(), userMessage: $"{lawId}: control case refused");
    }
}
