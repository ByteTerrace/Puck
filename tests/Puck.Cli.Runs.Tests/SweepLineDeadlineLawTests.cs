using Puck.Cli.Sweep;
using Puck.Hosting;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a sweep line that passes its own call deadline is unmeasured with that reason, never a closed
/// attachment for the lines after it: <c>SweepWorld</c> opens a new session on the World's attachment and the next
/// line answers on the same World. The World here is a control server whose session never answers the line
/// <c>slow</c>, so the deadline is the only thing that ends it.
/// </summary>
public sealed class SweepLineDeadlineLawTests {
    private sealed class Session : IControlSession {
        public async Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
            if (request.Command == "slow") {
                await Task.Delay(cancellationToken: cancellationToken, millisecondsDelay: Timeout.Infinite);
            }

            return new ControlResponse(Id: request.Id, Output: $"answer {request.Command}", Status: "completed");
        }
        public void Dispose() { }
    }

    [Fact]
    public void ALineThatPassesItsDeadlineIsUnmeasuredAndTheNextLineAnswersOnTheSameWorld() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Skip(reason: "Windows capability ACLs are required.");

            return;
        }

        using var server = new LocalControlServer(createSession: static () => new Session());
        using var budget = new SweepBudget(caller: CancellationToken.None, total: TimeSpan.FromMinutes(value: 2));

        Assert.True(condition: SweepWorld.TryAttach(budget: budget, error: out var error, session: out var world, world: server.AttachmentPath), userMessage: error);
        using var attached = world!;

        var slow = attached.Run(script: "slow", timeoutSeconds: 1);

        Assert.False(condition: slow.Ok);
        Assert.True(condition: slow.Unmeasured);
        Assert.Contains(actualString: slow.Output, expectedSubstring: "1-second deadline");
        Assert.False(condition: attached.Gone);

        var next = attached.Run(script: "after");

        Assert.True(condition: next.Ok, userMessage: next.Output);
        Assert.Equal(expected: "answer after", actual: next.Output);
    }
}
