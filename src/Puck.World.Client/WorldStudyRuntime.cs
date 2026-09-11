using System.Diagnostics;
using System.Numerics;
using Puck.Shaders.Study;

namespace Puck.World.Client;

/// <summary>One read of the process's pointer, as a study's <c>iMouse</c> source — the composition root (which owns
/// the pointer store) supplies it through <see cref="WorldStudyRuntime.ReadPointer"/>; <see cref="WorldFramePresenter"/>
/// maps it into each study slot's own pixel space every produced frame.</summary>
/// <param name="ClientPosition">The pointer position in CLIENT pixels, as the platform reported it.</param>
/// <param name="HasPosition">Whether a position has been reported at all (before the first motion there is none).</param>
/// <param name="Pressed">Whether the primary button is held.</param>
public readonly record struct WorldStudyPointerSample(Vector2 ClientPosition, bool HasPosition, bool Pressed);
/// <summary>
/// The live shader-study runtime this session's console verb layer and frame presenter share — the registered
/// <see cref="StudyPassNode"/> per <c>views.studies</c> row name (the set a <c>SdfWorldRenderSpec.Children</c> build
/// starts from; a study loaded later registers here AND on the engine node through
/// <c>SdfEngineNode.RegisterChild</c>, via <see cref="CreateNode"/>), each row's last compile outcome, and each
/// row's presentation-only clock. <c>Puck.World.WorldStudyCommandModule</c> (owned outside this project) writes
/// clock overrides and drives recompiles through <see cref="Compiler"/>; <see cref="WorldFramePresenter"/>
/// accumulates each unpaused clock every produced frame, polls every watched source's write stamp and runs the due
/// reloads (<see cref="PumpWatches"/> — the pump thread is the only one that may compile, swap a node's pipeline, or
/// write the console; no file-system watcher thread is involved), and fills <see cref="StudyPassNode.Input"/>.
/// </summary>
public sealed class WorldStudyRuntime {
    /// <summary>One registered study's live state.</summary>
    public sealed class Entry {
        /// <summary>Gets the study's render node — resized and fed per frame by the presenter, and the target of
        /// every <c>study.reload</c>/<c>study.watch</c>-triggered <see cref="StudyPassNode.Swap(StudyProgram)"/>.</summary>
        public required StudyPassNode Node { get; init; }
        /// <summary>Gets or sets the most recent compile result — <see langword="null"/> before this row's first
        /// compile attempt this session.</summary>
        public StudyProgram? LastCompile { get; set; }
        /// <summary>Gets or sets whether the clock is held — the presenter skips accumulation while
        /// <see langword="true"/>.</summary>
        public bool ClockPaused { get; set; }
        /// <summary>Gets or sets the study clock, in seconds — <see cref="Shaders.Study.StudyFrameInput.Seconds"/>.</summary>
        public double ClockSeconds { get; set; }
        /// <summary>Gets or sets the clock's rate multiplier — 0 freezes accumulation the same way
        /// <see cref="ClockPaused"/> does, read back separately from it.</summary>
        public float ClockScale { get; set; } = 1f;
        /// <summary>Gets or sets the last <c>iMouse</c> value the presenter derived for this study — Shadertoy's
        /// convention (<c>xy</c> the current position while pressed, retained after release; <c>z</c> the press x,
        /// negated once released; <c>w</c> the press y, positive on the press frame only), in the slot's own pixel
        /// space with y up. Carried across frames because the convention is stateful.</summary>
        public Vector4 Mouse { get; set; }
        /// <summary>Gets or sets whether the primary button was held on the previous produced frame — the edge the
        /// press frame is detected from.</summary>
        public bool MouseWasPressed { get; set; }

        // The last write stamp PumpWatches saw for the watched source, and the Stopwatch timestamp at which that
        // stamp last moved (0 = no reload pending).
        private (DateTime LastWriteUtc, long Length) m_watchStamp;
        private long m_watchChangedAt;

        /// <summary>Gets the resolved source path <see cref="WorldStudyRuntime.PumpWatches"/> polls, or
        /// <see langword="null"/> when this study is not watched.</summary>
        public string? WatchPath { get; private set; }
        /// <summary>Gets the number of write-stamp changes the watch has seen — the <c>study.status</c> read-back.</summary>
        public int SourceChangeCount { get; private set; }

        /// <summary>Starts watching <paramref name="path"/>: its current write stamp is the baseline, so only a later
        /// write reloads.</summary>
        /// <param name="path">The resolved source path.</param>
        public void Watch(string path) {
            ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

            WatchPath = path;
            m_watchStamp = ReadStamp(path: path);
            m_watchChangedAt = 0L;
        }
        /// <summary>Stops watching; a pending reload is dropped.</summary>
        public void Unwatch() {
            WatchPath = null;
            m_watchChangedAt = 0L;
        }
        /// <summary>Polls the watched source's write stamp and reports whether a reload is due — the stamp moved and
        /// has held still for <paramref name="debounceTicks"/>. Pump thread.</summary>
        /// <param name="debounceTicks">The quiet period, in <see cref="Stopwatch"/> ticks.</param>
        /// <returns>Whether a reload is due (and the pending change is now consumed).</returns>
        public bool PollWatch(long debounceTicks) {
            if (WatchPath is not { } path) {
                return false;
            }

            var stamp = ReadStamp(path: path);

            if (stamp != m_watchStamp) {
                m_watchStamp = stamp;
                m_watchChangedAt = Stopwatch.GetTimestamp();
                SourceChangeCount++;

                return false;
            }

            if (
                (m_watchChangedAt == 0L) ||
                ((Stopwatch.GetTimestamp() - m_watchChangedAt) < debounceTicks)
            ) {
                return false;
            }

            m_watchChangedAt = 0L;

            return true;
        }

        private static (DateTime LastWriteUtc, long Length) ReadStamp(string path) {
            var info = new FileInfo(fileName: path);

            return (info.Exists
                ? (info.LastWriteTimeUtc, info.Length)
                : (DateTime.MinValue, -1L)
            );
        }
    }

    /// <summary>The quiet period a watched source's write stamp must hold before its reload runs — an editor's
    /// atomic-write save moves the stamp more than once.</summary>
    public const int WatchDebounceMilliseconds = 150;

    private readonly Dictionary<string, Entry> m_entries = new(comparer: StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="WorldStudyRuntime"/> class.</summary>
    /// <param name="compiler">The shared compiler every registered row's recompile runs through.</param>
    /// <param name="documentDirectory">The boot document's own directory — every row's <c>source</c> resolves
    /// relative to this, never <see cref="AppContext.BaseDirectory"/>.</param>
    public WorldStudyRuntime(StudyShaderCompiler compiler, string documentDirectory) {
        ArgumentNullException.ThrowIfNull(argument: compiler);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: documentDirectory);

        Compiler = compiler;
        DocumentDirectory = documentDirectory;
    }

    /// <summary>Gets the shared compiler.</summary>
    public StudyShaderCompiler Compiler { get; }
    /// <summary>Gets the boot document's own directory.</summary>
    public string DocumentDirectory { get; }
    /// <summary>Gets or sets the factory a study loaded AFTER boot (<c>study.load</c> naming a new row) gets its
    /// <see cref="StudyPassNode"/> from — the same GPU services, vertex stage, backend, and initial size the boot-time
    /// rows were built with — or <see langword="null"/> for a host with no render tree. The composition root sets it.</summary>
    public Func<string, StudyPassNode>? CreateNode { get; set; }
    /// <summary>Gets or sets the reload a due watch change runs, by study name — pump thread, from
    /// <see cref="PumpWatches"/>; the console verb layer that owns compiling sets it.</summary>
    public Action<string>? ReloadFromWatch { get; set; }
    /// <summary>Gets or sets the pointer read every study's <c>iMouse</c> derives from, or <see langword="null"/>
    /// for a host with no pointer (an offscreen boot) — every study then sees <c>iMouse</c> zero. The composition
    /// root sets it, since the pointer store lives there rather than in this project.</summary>
    public Func<WorldStudyPointerSample>? ReadPointer { get; set; }
    /// <summary>Gets every registered row's entry, by name.</summary>
    public IReadOnlyDictionary<string, Entry> Entries => m_entries;

    /// <summary>Registers a study's node under <paramref name="name"/> — once per <c>views.studies</c> row while
    /// building the render tree, and again for each study a console verb loads later (whose node the caller also hands
    /// to <c>SdfEngineNode.RegisterChild</c>).</summary>
    /// <param name="name">The row's name.</param>
    /// <param name="node">The node to register.</param>
    /// <param name="compile">The boot compile's result, when one was attempted (<see langword="null"/> only when a
    /// toolchain tool itself could not be found — the node still registers, showing the flat-grey placeholder).</param>
    public void Register(string name, StudyPassNode node, StudyProgram? compile = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentNullException.ThrowIfNull(argument: node);

        m_entries[name] = new Entry { Node = node, LastCompile = compile };
    }
    /// <summary>Polls every watched entry's source write stamp (one <c>stat</c> per watched study) and runs
    /// <see cref="ReloadFromWatch"/> for each whose stamp has moved and then held still for
    /// <see cref="WatchDebounceMilliseconds"/> — pump thread, once per produced frame.</summary>
    public void PumpWatches() {
        if (ReloadFromWatch is not { } reload) {
            return;
        }

        var debounceTicks = ((Stopwatch.Frequency * WatchDebounceMilliseconds) / 1000L);

        foreach (var (name, entry) in m_entries) {
            if (entry.PollWatch(debounceTicks: debounceTicks)) {
                reload(name);
            }
        }
    }
    /// <summary>Looks up a registered entry by name.</summary>
    /// <param name="name">The row's name.</param>
    /// <param name="entry">The entry, when registered.</param>
    /// <returns>Whether <paramref name="name"/> is registered.</returns>
    public bool TryGet(string name, out Entry entry) =>
        m_entries.TryGetValue(key: name, value: out entry!);
}
