namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The system event scheduler for the cycle-stepped engine. Time is a single monotonic master clock,
/// <see cref="Now"/>, advanced one cycle at a time by the bus's per-cycle stepping. Peripherals schedule events at
/// absolute times on that clock, and the bus fires each one at its <b>exact</b> cycle (ARES's model — no deferred
/// batching, so a peripheral register read mid-instruction sees state that has advanced to the precise cycle).
/// </summary>
public sealed class GbaScheduler {
    /// <summary>A scheduled callback. <see cref="When"/> is an absolute time on the <see cref="Now"/> clock.</summary>
    public sealed class Event {
        /// <summary>The callback, invoked with how many cycles late it fired (0 = exactly on time).</summary>
        public Action<int> Callback = static _ => { };
        /// <summary>The absolute fire time.</summary>
        public long When;
        /// <summary>Whether this event is currently in the queue.</summary>
        public bool Scheduled;
        internal Event? Next;
    }

    private Event? m_root;

    /// <summary>The master clock: the current absolute time, advanced one cycle at a time by the bus.</summary>
    public long Now { get; set; }

    /// <summary>The absolute time of the next scheduled event, or <see cref="long.MaxValue"/> if none is queued.</summary>
    public long NextWhen => m_root?.When ?? long.MaxValue;

    /// <summary>Schedules <paramref name="e"/> to fire <paramref name="cyclesFromNow"/> cycles from now.</summary>
    public void Schedule(Event e, int cyclesFromNow) => ScheduleAbsolute(e: e, when: Now + cyclesFromNow);

    /// <summary>Schedules <paramref name="e"/> to fire at the absolute time <paramref name="when"/>.</summary>
    public void ScheduleAbsolute(Event e, long when) {
        Deschedule(e: e);

        e.When = when;
        e.Scheduled = true;

        // Insert into the singly-linked list, kept sorted ascending by When.
        if ((m_root is null) || (when < m_root.When)) {
            e.Next = m_root;
            m_root = e;
        }
        else {
            var node = m_root;

            while ((node.Next is not null) && (node.Next.When <= when)) {
                node = node.Next;
            }

            e.Next = node.Next;
            node.Next = e;
        }
    }

    /// <summary>Removes <paramref name="e"/> from the queue if present.</summary>
    public void Deschedule(Event e) {
        if (!e.Scheduled) {
            return;
        }

        if (ReferenceEquals(m_root, e)) {
            m_root = e.Next;
        }
        else {
            var node = m_root;

            while ((node is not null) && !ReferenceEquals(node.Next, e)) {
                node = node.Next;
            }

            if (node is not null) {
                node.Next = e.Next;
            }
        }

        e.Scheduled = false;
        e.Next = null;
    }

    /// <summary>Advances the clock by <paramref name="cycles"/>, firing each scheduled event at its exact time along
    /// the way (so callbacks see the clock at their <see cref="Event.When"/>). The cycle-accurate way to drive the
    /// scheduler standalone — the bus normally interleaves this with per-cycle CPU/timer work via its own loop.</summary>
    public void Advance(long cycles) {
        var target = Now + cycles;

        while (NextWhen <= target) {
            Now = NextWhen;
            FireDue();
        }

        Now = target;
    }

    /// <summary>Fires every event whose time has arrived (<see cref="Event.When"/> &lt;= <see cref="Now"/>), each
    /// with how many cycles late it fired. Called from the bus's per-cycle loop the instant the clock reaches the
    /// event, so events take effect at their exact cycle.</summary>
    public void FireDue() {
        while ((m_root is not null) && (m_root.When <= Now)) {
            var e = m_root;

            m_root = e.Next;
            e.Scheduled = false;
            e.Next = null;

            e.Callback((int)(Now - e.When));
        }
    }
}
