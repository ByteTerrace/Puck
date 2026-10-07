using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>body.engage</c> and <c>body.disengage</c> read their driving body the way every <c>body.*</c> verb does,
/// as an optional 0-based body index defaulting to body 0 (seat 1's body): a token <c>n</c> names <c>body:n</c>, a
/// token past the population is refused naming the 0-based range, and an absent token, an explicit <c>0</c> and a
/// trailing <c>capture:</c> option alone all resolve body 0. The verbs run in a boot composed as a real one is, and each
/// answer is the verb's own refusal, which names the body it resolved.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class EngageBodyIndexLawTests : IDisposable {
    // A world that authors a population past its one local seat, so its last body is a population entry.
    private const string World = "tests/Puck.World.Tests/Fixtures/minimal-snake-host.puck";

    private readonly TemporaryDirectory m_stateDirectory = new(prefix: "puck-engage-index-");

    public void Dispose() => m_stateDirectory.Dispose();
    [Fact]
    public void TheEngageVerbsReadOneZeroBasedBodyIndex() {
        var host = m_stateDirectory.Own(owner: WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        ).Build());
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var last = (host.Services.GetRequiredService<WorldPopulation>().Capacity - 1);

        // The last body index is a body, not a seat's player number: both verbs name body {last}, whatever it holds.
        var engaged = registry.Submit(line: $"body.engage body:0 {last}");
        var disengaged = registry.Submit(line: $"body.disengage {last}");

        Assert.True(condition: engaged.IsError);
        Assert.StartsWith(
            actualString: engaged.Output,
            expectedStartString: $"[body.engage: body {last} is not "
        );
        Assert.True(condition: disengaged.IsError);
        Assert.StartsWith(
            actualString: disengaged.Output,
            expectedStartString: $"[body.disengage: body {last} is not "
        );

        // One past the last body is refused naming the 0-based range, by both verbs alike.
        Assert.Equal(
            actual: registry.Submit(line: $"body.engage body:0 {(last + 1)}").Output,
            expected: $"[body.engage: body index must be an integer 0..{last}]"
        );
        Assert.Equal(
            actual: registry.Submit(line: $"body.disengage {(last + 1)}").Output,
            expected: $"[body.disengage: body index must be an integer 0..{last}]"
        );

        // The default is body 0: an absent index, an explicit 0 and a trailing capture option alone answer alike. The
        // target is the last body, so whatever body 0 may do, the answer never depends on the capture flag.
        var target = $"body:{last}";
        var absent = registry.Submit(line: $"body.engage {target}");

        Assert.Equal(
            actual: registry.Submit(line: $"body.engage {target} 0").Output,
            expected: absent.Output
        );
        Assert.Equal(
            actual: registry.Submit(line: $"body.engage {target} capture:off").Output,
            expected: absent.Output
        );
        Assert.Equal(
            actual: registry.Submit(line: "body.disengage").Output,
            expected: registry.Submit(line: "body.disengage 0").Output
        );
    }
}
