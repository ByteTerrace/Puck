using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The scheduler's closed presentation vocabulary names the ordinary offscreen registry's immediate,
/// seat-accessible render lever. Device activation remains sealed by the host composition fixture.</summary>
public sealed class WorldScheduledPresentationLawTests {
    [Fact]
    public void EveryAdmittedPresentationVerbIsRegisteredForImmediateSeatDispatch() {
        using var state = new TemporaryDirectory(prefix: "puck-scheduled-presentation-");
        using var host = WorldBootHarness.Compose(stateDirectory: state, presentation: WorldHostPresentation.Offscreen,
            world: "tests/Puck.Counters/counters.world.json").Build();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        Assert.Equal(expected: "world.temporal", actual: Assert.Single(collection: WorldScheduleCommands.Presentation));
        foreach (var verb in WorldScheduleCommands.Presentation) {
            Assert.True(condition: WorldScheduleCommands.IsAdmitted(verb: verb));
            Assert.False(condition: WorldScheduleCommands.IsAddressable(verb: verb));
            Assert.True(condition: registry.TryGetMetadata(name: verb, metadata: out var metadata));
            Assert.Equal(expected: CommandRouting.Immediate, actual: metadata.Routing);
            Assert.Equal(expected: CommandAudience.Anyone, actual: metadata.Audience);
        }
    }
}
