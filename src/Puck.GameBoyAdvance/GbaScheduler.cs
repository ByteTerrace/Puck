namespace Puck.GameBoyAdvance;

/// <summary>
/// The system event scheduler, modelled on mGBA's <c>mTiming</c>. Time is split into a committed
/// <see cref="MasterCycles"/> clock and the CPU's running <see cref="RelativeCycles"/> offset (which may briefly
/// go negative — e.g. when the game-pak prefetcher credits back the cost of code fetches it absorbed). The true
/// time at any instant is <see cref="Now"/> = MasterCycles + RelativeCycles. Peripherals schedule events at
/// absolute times and compute their state lazily from <see cref="Now"/> on read, so the machine is cycle-exact
/// without stepping every peripheral on every access.
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

    /// <summary>The committed master clock — advanced only forward, as events are processed.</summary>
    public long MasterCycles { get; private set; }

    /// <summary>The CPU's running cycle offset since the last commit; may be negative momentarily.</summary>
    public int RelativeCycles { get; set; }

    /// <summary>The cycle threshold (in <see cref="RelativeCycles"/> terms) at which the next event is due.</summary>
    public int NextEvent { get; private set; } = int.MaxValue;

    /// <summary>The true current time: the committed clock plus the CPU's running offset.</summary>
    public long Now => MasterCycles + RelativeCycles;

    /// <summary>Schedules <paramref name="e"/> to fire <paramref name="cyclesFromNow"/> cycles from now.</summary>
    public void Schedule(Event e, int cyclesFromNow) {
        ScheduleAbsolute(e: e, when: Now + cyclesFromNow);
    }

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

        UpdateNextEvent();
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
        UpdateNextEvent();
    }

    /// <summary>Commits <paramref name="cycles"/> of CPU progress to the master clock and fires every event now
    /// due, each with its lateness. Returns the cycle count (relative to the new clock) of the next event.</summary>
    public int Tick(int cycles) {
        MasterCycles += cycles;

        while (m_root is not null) {
            var late = MasterCycles - m_root.When;

            if (late < 0) {
                break;
            }

            var e = m_root;

            m_root = e.Next;
            e.Scheduled = false;
            e.Next = null;

            e.Callback((int)late);
        }

        UpdateNextEvent();

        return NextEvent;
    }

    private void UpdateNextEvent() {
        NextEvent = (m_root is null)
            ? int.MaxValue
            : (int)Math.Min(int.MaxValue, m_root.When - MasterCycles);
    }
}
