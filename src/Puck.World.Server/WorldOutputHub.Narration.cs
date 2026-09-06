namespace Puck.World.Server;

public sealed partial class WorldOutputHub {
    // One attached narration sink slot, the same lease shape the typed-lane Subscription above uses. A class (not a
    // struct) so the lease AttachNarrationSink returns IS this object — no separate handle to invalidate.
    private sealed class NarrationSubscription(WorldOutputHub hub, IWorldNarrationSink sink) : IDisposable {
        public readonly IWorldNarrationSink Sink = sink;
        public bool Active = true;

        public void Dispose() {
            lock (hub.m_narrationLock) {
                if (!Active) {
                    return;
                }

                Active = false;
                hub.m_activeNarrationCount--;
            }
        }
    }

    // Guards every field below. Unlike the typed lane above (exclusive with the tick thread by the caller's own
    // discipline — see the class remarks), a narration line can originate off the tick thread entirely (a peer
    // host's own connection-handling tasks narrate a listen/admit/disconnect line directly), so this lane keeps its
    // own lock rather than leaning on that same discipline.
    private readonly Lock m_narrationLock = new();
    private readonly List<NarrationSubscription> m_narration = new();
    private int m_activeNarrationCount;
    // Nonzero on the thread currently running a Narrate fan-out, so a sink that itself calls AttachNarrationSink
    // from inside its own Narrate refuses rather than mutating the list Narrate is still enumerating.
    private int m_narrationDepth;

    /// <summary>Gets a value indicating whether at least one narration sink is attached — lets a caller skip
    /// formatting a line nobody would receive. Reflects only active subscribers.</summary>
    public bool HasNarrationSink {
        get {
            lock (m_narrationLock) {
                return (m_activeNarrationCount > 0);
            }
        }
    }

    /// <summary>Adds a narration sink, delivered every subsequent <see cref="Narrate"/> call until either the
    /// process ends or the returned lease is disposed.</summary>
    /// <param name="sink">The sink to add.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Called from the thread currently running a <see cref="Narrate"/>
    /// fan-out.</exception>
    public IDisposable AttachNarrationSink(IWorldNarrationSink sink) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        lock (m_narrationLock) {
            if (m_narrationDepth != 0) {
                throw new InvalidOperationException(message: "WorldOutputHub.AttachNarrationSink was called from the thread running a Narrate fan-out; attach before or after a call, never during.");
            }

            var subscription = new NarrationSubscription(
                hub: this,
                sink: sink
            );

            m_narration.Add(item: subscription);
            m_activeNarrationCount++;

            return subscription;
        }
    }

    /// <summary>Formats and delivers one narration line to every attached sink, doing neither when
    /// <see cref="HasNarrationSink"/> is <see langword="false"/> — the deterministic core's replacement for an
    /// unconditional <c>Console.Error.WriteLine($"...")</c>. A faulting sink is detached without itself being
    /// narrated about (narrating a sink's own fault back through the sink that just faulted risks the same
    /// exception again); it simply stops receiving further lines. Safe to call from any thread — a peer host's own
    /// connection-handling tasks narrate without hopping to the tick thread first.</summary>
    /// <param name="channel">The narration's channel tag.</param>
    /// <param name="format">Produces the line's text. Invoked at most once, and only while a sink is attached.</param>
    public void Narrate(string channel, Func<string> format) {
        if (!HasNarrationSink) {
            return;
        }

        var narration = new WorldNarration(
            Channel: channel,
            Text: format()
        );

        lock (m_narrationLock) {
            m_narrationDepth++;

            try {
                var writeIndex = 0;

                for (var readIndex = 0; (readIndex < m_narration.Count); readIndex++) {
                    var subscription = m_narration[readIndex];

                    if (!subscription.Active) {
                        continue;
                    }

                    try {
                        subscription.Sink.Narrate(narration: in narration);
                    } catch {
                        subscription.Active = false;
                        m_activeNarrationCount--;

                        continue;
                    }

                    if (subscription.Active) {
                        m_narration[writeIndex++] = subscription;
                    }
                }

                if (writeIndex < m_narration.Count) {
                    m_narration.RemoveRange(
                        index: writeIndex,
                        count: (m_narration.Count - writeIndex)
                    );
                }
            } finally {
                m_narrationDepth--;
            }
        }
    }
}
