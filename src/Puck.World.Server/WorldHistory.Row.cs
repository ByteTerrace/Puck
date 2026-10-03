namespace Puck.World;

/// <summary>The window a history's scrubber row spans and the tick its cursor marks.</summary>
/// <param name="Oldest">The oldest tick a seek can reach.</param>
/// <param name="Head">The newest recorded tick.</param>
/// <param name="Cursor">The tick the live world sits at.</param>
public readonly record struct WorldHistoryRowWindow(ulong Oldest, ulong Head, ulong Cursor);
/// <summary>One kept branch as the scrubber row reads it.</summary>
/// <param name="Fork">The tick the branch leaves the timeline after.</param>
/// <param name="Head">The tick its recorded future ends at.</param>
public readonly record struct WorldHistoryRowFork(ulong Fork, ulong Head);
public sealed partial class WorldHistory {
    /// <summary>The most keyframe ticks the scrubber row draws, and its read-back echoes; a longer window is sampled
    /// evenly, first and last included.</summary>
    public const int RowKeyframes = 32;
    /// <summary>The most kept branches the scrubber row draws, and its read-back echoes.</summary>
    public const int RowForks = 8;

    /// <summary>Gets or sets where each seat's pointer lies along the scrubber row a presented host draws, or
    /// <see langword="null"/> on a host that draws none; <c>world.history.drag</c> reads it.</summary>
    public IWorldHistoryPointer? Pointer { get; set; }

    // The row as the step thread last published it, read by presentation under the same gate: every read copies out,
    // so a frame never walks the window while a tick reshapes it. Publishing reuses these buffers, so a steady tick
    // allocates nothing here.
    private readonly Lock m_rowGate = new();
    private WorldHistoryRowFork[] m_rowForks = new WorldHistoryRowFork[4];

    private int m_rowForkCount;

    private ulong[] m_rowKeyframes = new ulong[16];

    private int m_rowKeyframeCount;
    private WorldHistoryRowWindow? m_rowWindow;

    // Publishes the window, the cursor, the keyframes and the kept branches for the scrubber row. Called on the step
    // thread whenever any of them may have moved: every closed tick, a seek, a switch, and a cleared window.
    private void PublishRow() {
        lock (m_rowGate) {
            if (m_segments.Count == 0) {
                m_rowWindow = null;
                m_rowKeyframeCount = 0;
                m_rowForkCount = 0;

                return;
            }

            m_rowWindow = new WorldHistoryRowWindow(
                Cursor: m_cursor,
                Head: Head,
                Oldest: Oldest
            );

            if (m_rowKeyframes.Length < m_segments.Count) {
                m_rowKeyframes = new ulong[Math.Max(val1: m_segments.Count, val2: (m_rowKeyframes.Length * 2))];
            }

            for (var index = 0; (index < m_segments.Count); index++) {
                m_rowKeyframes[index] = m_segments[index].KeyframeTick;
            }

            m_rowKeyframeCount = m_segments.Count;

            if (m_rowForks.Length < m_branches.Count) {
                m_rowForks = new WorldHistoryRowFork[Math.Max(val1: m_branches.Count, val2: (m_rowForks.Length * 2))];
            }

            for (var index = 0; (index < m_branches.Count); index++) {
                m_rowForks[index] = new WorldHistoryRowFork(
                    Fork: m_branches[index].ForkTick,
                    Head: m_branches[index].HeadTick
                );
            }

            m_rowForkCount = m_branches.Count;
        }
    }

    /// <summary>Reads the scrubber row the history last published: its window and cursor, the keyframe ticks — sampled
    /// evenly, first and last included, when there are more than <paramref name="keyframes"/> holds — and the kept
    /// branches, oldest first, up to what <paramref name="forks"/> holds. Safe from a presentation thread; the
    /// <c>world.history row</c> read-back echoes exactly what this returns.</summary>
    /// <param name="window">The window and cursor, on success.</param>
    /// <param name="keyframes">Receives the keyframe ticks.</param>
    /// <param name="keyframeCount">The keyframe ticks written.</param>
    /// <param name="forks">Receives the kept branches.</param>
    /// <param name="forkCount">The branches written.</param>
    /// <returns><see langword="true"/> when the history holds a window.</returns>
    public bool TryReadRow(out WorldHistoryRowWindow window, Span<ulong> keyframes, out int keyframeCount, Span<WorldHistoryRowFork> forks, out int forkCount) {
        lock (m_rowGate) {
            if (m_rowWindow is not { } published) {
                window = default;
                keyframeCount = 0;
                forkCount = 0;

                return false;
            }

            window = published;

            if (m_rowKeyframeCount <= keyframes.Length) {
                m_rowKeyframes.AsSpan(length: m_rowKeyframeCount, start: 0).CopyTo(destination: keyframes);
                keyframeCount = m_rowKeyframeCount;
            } else if (keyframes.Length == 1) {
                keyframes[0] = m_rowKeyframes[0];
                keyframeCount = 1;
            } else {
                for (var index = 0; (index < keyframes.Length); index++) {
                    keyframes[index] = m_rowKeyframes[((int)((((long)index) * (m_rowKeyframeCount - 1)) / (keyframes.Length - 1)))];
                }

                keyframeCount = keyframes.Length;
            }

            forkCount = Math.Min(val1: m_rowForkCount, val2: forks.Length);
            m_rowForks.AsSpan(length: forkCount, start: 0).CopyTo(destination: forks);

            return true;
        }
    }
    /// <summary>Returns the tick a fraction of the window names: 0 the oldest tick, 1 the head, rounded to the nearest
    /// tick — the target of the scrubber row's drag and of <c>world.history scrub</c>.</summary>
    /// <param name="fraction">The fraction along the window, clamped to 0..1.</param>
    /// <param name="tick">The tick, when the history holds a window.</param>
    /// <returns><see langword="true"/> when the history holds a window.</returns>
    public bool TryTickAt(double fraction, out ulong tick) {
        lock (m_rowGate) {
            if (m_rowWindow is not { } window) {
                tick = 0UL;

                return false;
            }

            var span = (window.Head - window.Oldest);
            var clamped = Math.Clamp(value: (double.IsFinite(d: fraction) ? fraction : 0d), min: 0d, max: 1d);

            tick = (window.Oldest + ((ulong)Math.Round(mode: MidpointRounding.AwayFromZero, value: (clamped * span))));

            return true;
        }
    }
}
