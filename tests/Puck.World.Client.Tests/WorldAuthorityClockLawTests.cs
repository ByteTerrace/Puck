using Puck.Maths;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// Laws for the seat clock's mapping onto a remote authority's ticks: however far the two processes' clocks drift apart
/// over a run (each drops the steps it cannot keep up with), the mapping stays within the link's least delay and one
/// window's drift of the authority's own tick, because every delivery anchors it afresh.
/// </summary>
public sealed class WorldAuthorityClockLawTests {
    // The client steps every tick; the authority drops one step in ten and, once, a burst of thirty.
    private const int Run = 9000;
    private const int BurstAt = 4000;
    private const int Burst = 30;
    private const int Delay = 2;
    private const uint Jitter = 15U;
    // The authority's drift over one window, rounded up, and the tick quantization of an arrival.
    private const int Slack = ((WorldAuthorityClock.Window / 10) + 2);

    [Fact]
    public void TickAt_AuthorityFallsBehindOverTheRun_TrailsItByTheLinkDelayAlone() {
        var authority = AuthorityTicks();
        var clock = new WorldAuthorityClock();

        for (var client = Delay; (client < Run); client++) {
            clock.Observe(clientTick: (ulong)client, deliveredTick: (ulong)authority[(client - Delay)]);
            if (Settling(client: client)) {
                continue;
            }

            Assert.InRange(actual: clock.TickAt(clientTick: (ulong)client), high: (authority[client] + Slack), low: (authority[client] - Delay - Slack));
        }

        // The clocks drifted apart by every step the authority dropped (most of a hundred over the run), which a fixed
        // offset would have carried as error.
        var dropped = ((authority[(Run - 1 - Delay)] - (Run - 1)) - (authority[0] - Delay));

        Assert.True(condition: (dropped < -(Run / 10)));
        Assert.InRange(actual: clock.Moved, high: (dropped + Slack), low: (dropped - Slack));
    }
    [Fact]
    public void TickAt_JitteredInOrderLink_StaysWithinTheLinksDelay() {
        var authority = AuthorityTicks();
        var arrival = new int[Run];
        var random = Pcg32XshRr.Create(state: 7UL, stream: 1UL);

        // Each tick is sent as it completes and arrives in order, held up by up to Jitter ticks more than the delay.
        for (var tick = 0; (tick < Run); tick++) {
            var at = (tick + Delay + (int)random.NextUInt32(maximum: Jitter, minimum: 0U));

            arrival[tick] = ((tick == 0) ? at : Math.Max(val1: arrival[(tick - 1)], val2: at));
        }

        var clock = new WorldAuthorityClock();
        var delivered = -1;

        for (var client = 0; (client < Run); client++) {
            while (((delivered + 1) < Run) && (arrival[(delivered + 1)] <= client)) {
                delivered++;
            }
            if (delivered < 0) {
                continue;
            }

            clock.Observe(clientTick: (ulong)client, deliveredTick: (ulong)authority[delivered]);
            if (Settling(client: client)) {
                continue;
            }

            Assert.InRange(actual: clock.TickAt(clientTick: (ulong)client), high: (authority[client] + Slack), low: (authority[client] - Delay - Jitter - Slack));
        }
    }

    // The authority tick at each client tick: one step in ten dropped, and a burst of steps dropped at once, while the
    // authority's tick stands still.
    private static long[] AuthorityTicks() {
        var ticks = new long[Run];

        for (var client = 1; (client < Run); client++) {
            var dropped = (((client % 10) == 0) || ((client >= BurstAt) && (client < (BurstAt + Burst))));

            ticks[client] = (ticks[(client - 1)] + (dropped ? 0L : 1L));
        }

        return ticks;
    }
    // Whether the mapping is still taking its first window, or stands in the burst or the window after it, when no
    // delivery says the authority stopped and then the least delayed delivery the window holds predates the stop.
    private const int Settle = (WorldAuthorityClock.Window + Delay + (int)Jitter + 1);

    private static bool Settling(int client) =>
        ((client < Settle) || ((client >= BurstAt) && (client < (BurstAt + Burst + Settle))));
}
