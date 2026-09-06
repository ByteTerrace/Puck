using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The replay verb surface holds no dependency <c>Puck.World.Console</c> cannot reach (the tape, the inspector, and
/// <c>WorldInstanceHost</c> are all public Server types), so it lives beside every other module moved out of
/// <c>Puck.World</c> rather than in the composition root.
/// </summary>
public sealed class ReplayCommandModuleLocationLawTests {
    [Fact]
    public void ReplayCommandModule_LivesInConsole() {
        Assert.Equal(expected: "Puck.World.Console", actual: typeof(WorldReplayCommandModule).Assembly.GetName().Name);
    }
}
