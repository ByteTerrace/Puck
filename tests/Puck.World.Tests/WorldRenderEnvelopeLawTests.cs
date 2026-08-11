using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the render-capacity registrations shared by the world continuum and session-screen views.</summary>
public sealed class WorldRenderEnvelopeLawTests {
    /// <summary>Every active renderer constrains admission independently, and disposing one renderer removes only
    /// its own constraint. This pins both halves of the lease contract: no last-writer-wins overwrite and no stale
    /// capacity after the consumer goes away.</summary>
    [Fact]
    public void RegistrationsComposeAndDisposeIndependently() {
        var envelope = new WorldRenderEnvelope();
        var definition = Fixtures.BuildDocument();
        var accepting = envelope.Configure(programWordCapacity: 10, instanceCapacity: 10, measure: static _ => (Words: 10, Instances: 10));
        var refusing = envelope.Configure(programWordCapacity: 10, instanceCapacity: 10, measure: static _ => (Words: 11, Instances: 10));

        Assert.False(condition: envelope.TryFit(candidate: definition, reason: out var refusal));
        Assert.Contains(expectedSubstring: "program words 11 exceed", actualString: refusal);

        refusing.Dispose();

        Assert.True(condition: envelope.TryFit(candidate: definition, reason: out var acceptedReason), userMessage: acceptedReason);

        accepting.Dispose();

        Assert.True(condition: envelope.TryFit(candidate: definition, reason: out var unconfiguredReason), userMessage: unconfiguredReason);

        // Idempotent teardown is required because view release and composition-root disposal can converge.
        accepting.Dispose();
        refusing.Dispose();
    }
}
