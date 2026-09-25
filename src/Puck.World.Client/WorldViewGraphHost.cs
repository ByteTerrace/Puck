using System.Diagnostics;
using System.Numerics;
using Puck.Abstractions;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.World.Client;

/// <summary>A non-destructive pointer sample in client pixels, supplied by the host.</summary>
public readonly record struct WorldPipelinePointerSample(Vector2 ClientPosition, bool HasPosition, bool Pressed);
/// <summary>Hosts named shader-pipeline instances. Compilation runs in the background; completed candidates
/// are installed by the frame presenter before producing a frame. Clocks and history are presentation state.</summary>
public sealed partial class WorldPipelineRuntime : IDisposable {
    /// <summary>The factory for instances loaded after boot, using the same device as the host.</summary>
    public Func<string, ShaderPipelineRenderNode>? CreateNode { get; set; }
    /// <summary>Gets the directory against which authored pipeline source paths resolve — the same directory the
    /// server's override gate resolves <c>views.pipelines</c> rows against. See <see cref="Rebase"/>.</summary>
    public string DocumentDirectory { get; private set; }
    /// <summary>Every registered instance by its authored name.</summary>
    public IReadOnlyDictionary<string, Entry> Entries => m_entries;
    /// <summary>The source loader used by both boot and live authoring: a row naming a package directory loads through
    /// the package, and any other row through the ordinary pipeline loader.</summary>
    public ShaderPackager Packager { get; }
    /// <summary>A non-destructive pointer read, absent in an offscreen host.</summary>
    public Func<WorldPipelinePointerSample>? ReadPointer { get; set; }
    /// <summary>Registers a newly created instance with the owning render tree.</summary>
    public Action<string, ShaderPipelineRenderNode>? RegisterNode { get; set; }
    /// <summary>Removes and retires an instance from the owning render tree.</summary>
    public Action<string>? RemoveNode { get; set; }
    /// <summary>Completed compilation reports, delivered only from the presentation thread.</summary>
    public Action<string, string>? Report { get; set; }

    /// <summary>One instance's presentation controls, pending compilation, and dependency watch.</summary>
    public sealed partial class Entry {
        private readonly Dictionary<string, (DateTime Time, long Length)> m_stamps = new(comparer: PuckPaths.Comparer);

        /// <summary>The currently requested document-relative source.</summary>
        public string Source { get; internal set; } = string.Empty;
        /// <summary>The non-negative presentation rate multiplier.</summary>
        public float ClockScale { get; set; } = 1f;

        private long m_changedAt;
        private long m_lastPolledAt;
        private long m_retryAt;

        internal BackgroundBuild<CompileOutcome> Compilation { get; } = new();

        internal int PendingSteps { get; set; }
        internal Exception? ReportedSwapError { get; set; }

        /// <summary>The latest console capture awaiting a completion report on the presentation pump.</summary>
        public FrameCaptureRequest? Capture { get; private set; }
        /// <summary>The number of console captures requested since this instance was registered.</summary>
        public int CapturesRequested { get; private set; }
        /// <summary>The failure of the most recently reported capture, or <see langword="null"/> when it was written.</summary>
        public string? LastCaptureError { get; private set; }
        /// <summary>Whether time and feedback advancement are paused.</summary>
        public bool ClockPaused { get; set; }
        /// <summary>The presentation clock in seconds.</summary>
        public double ClockSeconds { get; set; }
        /// <summary>Whether a background compilation is still pending installation.</summary>
        public bool IsCompiling => Compilation.IsPending;
        /// <summary>The most recently completed compilation, including any diagnostics.</summary>
        public ShaderPipelineLoadResult? LastCompile { get; internal set; }
        /// <summary>The pointer's position during its most recent press over the instance, in the instance's pixels with
        /// the origin at the top-left corner, or zero before the first press.</summary>
        public Vector2 Pointer { get; set; }
        /// <summary>Whether the pointer was pressed in the previous frame.</summary>
        public bool PointerWasDown { get; set; }
        /// <summary>How many presses the pointer has made over the instance.</summary>
        public uint PointerPresses { get; set; }
        /// <summary>The published mapping of the pane the instance is shown in, kept while its region and extent hold,
        /// or <see langword="null"/> before the instance is first shown.</summary>
        public Puck.Commands.SourceMapping? Pane { get; set; }
        /// <summary>The GPU executor, owned by the hosting render tree.</summary>
        public required ShaderPipelineRenderNode Node { get; init; }
        /// <summary>The number of dependency changes observed.</summary>
        public int SourceChangeCount { get; private set; }
        /// <summary>The root source being watched, or null when watching is disabled.</summary>
        public string? WatchPath { get; private set; }

        internal void CancelPending() =>
            Compilation.Cancel();
        internal bool PollWatch(long debounceTicks, long pollTicks) {
            var now = Stopwatch.GetTimestamp();

            if ((now - m_lastPolledAt) >= pollTicks) {
                m_lastPolledAt = now;
                // Bound filesystem metadata reads independently of the host's presentation frame rate.
                // Replacing values is safe while enumerating Dictionary on the supported runtime.
                foreach (var (path, before) in m_stamps) {
                    var after = ReadStamp(path: path);

                    if (before == after) { continue; }
                    m_stamps[path] = after;
                    SourceChangeCount++;
                    m_changedAt = now;
                }
            }
            var queuedAt = Math.Max(
                val1: m_changedAt,
                val2: m_retryAt
            );

            if (
                (queuedAt == 0) ||
                ((now - queuedAt) < debounceTicks)
            ) { return false; }
            m_retryAt = 0;
            m_changedAt = 0;
            return true;
        }
        internal void RefreshDependencies() {
            if (WatchPath is not { } root) { return; }
            var retained = new HashSet<string>(comparer: PuckPaths.Comparer) { root };

            if (LastCompile is { } compiled) { retained.UnionWith(other: compiled.Dependencies); }
            foreach (var path in retained) {
                m_stamps.TryAdd(
                key: path,
                value: ReadStamp(path: path)
            );
            }
            foreach (var path in m_stamps.Keys.ToArray()) {
                if (!retained.Contains(item: path)) { m_stamps.Remove(key: path); }
            }
            // Preserve existing stamps and debounce state: an edit during compilation must schedule another load.
        }
        internal void ScheduleRetry() => m_retryAt = Stopwatch.GetTimestamp();

        private static (DateTime, long) ReadStamp(string path) {
            try {
                var info = new FileInfo(fileName: path);

                return (info.Exists
                    ? (info.LastWriteTimeUtc, info.Length)
                    : (DateTime.MinValue, -1L)
                );
            } catch (IOException) {
                return (DateTime.MinValue, -1L);
            } catch (UnauthorizedAccessException) {
                return (DateTime.MinValue, -1L);
            }
        }

        /// <summary>Advances this instance once for a produced host frame and returns its shader time delta.</summary>
        public double AdvanceClock(double deltaSeconds) {
            Node.Paused = (ClockPaused || (ClockScale == 0));
            if (!Node.IsReady) { return 0; }
            double delta;

            if (PendingSteps > 0) {
                PendingSteps--;
                delta = (1.0 / 60.0);
                Node.Step();
            } else {
                delta = (Node.Paused
                    ? 0
                    : (deltaSeconds * ClockScale)
                );
            }
            ClockSeconds += delta;
            return delta;
        }
        /// <summary>Resets time, pending steps, and GPU feedback to their initial values.</summary>
        public void Reset() {
            ClockSeconds = 0;
            PendingSteps = 0;
            Node.Reset();
        }
        /// <summary>Pauses and schedules exactly one logical frame at the standard development rate.</summary>
        public void Step() {
            ClockPaused = true;
            PendingSteps = checked((PendingSteps + 1));
        }
        /// <summary>Stops watching and discards any pending debounce event.</summary>
        public void Unwatch() {
            WatchPath = null;
            m_stamps.Clear();
            m_lastPolledAt = 0;
            m_changedAt = 0;
        }
        /// <summary>Watches the source and the last compilation's dependencies.</summary>
        public void Watch(string path) {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            Unwatch();
            WatchPath = path;
            RefreshDependencies();
        }
    }

    // One background compilation: the loader's result and, for a compiled candidate, the read of the source it was
    // compiled from, taken before and after the load so an edit during compilation retries instead of mislabeling.
    internal sealed record CompileOutcome(ShaderPipelineLoadResult Result, ShaderPipelineSource? Source);

    /// <summary>The source watch's quiet period before requesting compilation.</summary>
    public const int WatchDebounceMilliseconds = 150;
    /// <summary>The minimum interval between dependency metadata polls, independent of presentation cadence.</summary>
    public const int WatchPollMilliseconds = 50;

    private readonly Dictionary<string, Entry> m_entries = new(comparer: StringComparer.Ordinal);

    private bool m_disposed;
    private IReadOnlyList<WorldViewPipeline>? m_lastRows;

    /// <summary>Creates a host registry using the shared source loader and the world's document directory.</summary>
    public WorldPipelineRuntime(ShaderPackager packager, string documentDirectory) {
        ArgumentNullException.ThrowIfNull(packager);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentDirectory);
        Packager = packager;
        DocumentDirectory = Path.GetFullPath(path: documentDirectory);
    }

    /// <summary>Rebases every future relative source resolution onto a newly loaded document's own directory — a
    /// <c>world.load</c>/<c>world.reload</c> that installs a document from another directory moves what a row's
    /// relative <c>source</c> resolves against, the same directory the server's override gate begins reading rows
    /// from at that same moment. Does not retroactively re-resolve an already-compiled instance; a live
    /// <see cref="QueueCompile"/> after this call is what reads the new directory.</summary>
    /// <param name="documentDirectory">The newly loaded document's directory.</param>
    public void Rebase(string documentDirectory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentDirectory);
        DocumentDirectory = Path.GetFullPath(path: documentDirectory);
    }
    /// <summary>Cancels pending compilations; the render tree retains ownership of the GPU instances.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        foreach (var entry in m_entries.Values) {
            entry.CancelPending();
        }
    }
    /// <summary>Installs complete candidates and polls dependency watches before this host frame renders.</summary>
    public void PumpWatches() {
        if (m_disposed) { return; }
        var debounce = ((Stopwatch.Frequency * WatchDebounceMilliseconds) / 1000);
        var poll = ((Stopwatch.Frequency * WatchPollMilliseconds) / 1000);

        foreach (var (name, entry) in m_entries) {
            if (entry.Capture is { Completion.IsCompleted: true } capture) {
                var result = capture.Completion.GetAwaiter().GetResult();

                entry.CompleteCapture(error: (result.Succeeded
                    ? null
                    : result.Error!.Message));
                Report?.Invoke(
                    name,
                    (result.Succeeded
                    ? $"captured {result.Path}"
                    : $"capture failed: {result.Error!.Message}")
                );
            }
            if (!ReferenceEquals(
                objA: entry.ReportedSwapError,
                objB: entry.Node.LastSwapError
            )) {
                entry.ReportedSwapError = entry.Node.LastSwapError;
                if (entry.ReportedSwapError is { } error) {
                    Report?.Invoke(
                    name,
                    $"GPU candidate refused: {error.Message}"
                );
                }
            }
            if (entry.Compilation.TryTake(
                error: out var compileError,
                result: out var compiled
            )) {
                var result = (compiled?.Result ?? new ShaderPipelineLoadResult(
                    Dependencies: [],
                    Message: compileError!.Message,
                    Pipeline: null,
                    Status: ShaderPipelineLoadStatus.Failed
                ));

                entry.LastCompile = result;
                if (result.Pipeline is { } pipeline) {
                    // A compiled candidate not yet installed — queued for the next frame, or still building — is replaced
                    // by a newer one.
                    var held = entry.Node.HasPendingCandidate;

                    try {
                        entry.Node.Swap(pipeline: pipeline);
                        entry.Candidate = ((compiled?.Source is { } source)
                            ? (pipeline.Plan, source)
                            : null);
                        if (held) {
                            Report?.Invoke(
                                name,
                                "superseded: compiled candidate"
                            );
                        }
                    } catch (Exception exception) when ((exception is InvalidOperationException or ArgumentException or NotSupportedException or InvalidDataException)) {
                        result = result with { Message = exception.Message, Pipeline = null, Status = ShaderPipelineLoadStatus.Failed };
                        entry.LastCompile = result;
                    }
                }
                entry.RefreshDependencies();
                if (result.Status == ShaderPipelineLoadStatus.Retry) { entry.ScheduleRetry(); }
                Report?.Invoke(
                    name,
                    ((result.Status == ShaderPipelineLoadStatus.Unsupported)
                    ? $"unsupported: {result.Message}"
                    : result.Message)
                );
            }
            entry.Synchronize();
            if (entry.PollWatch(
                debounceTicks: debounce,
                pollTicks: poll
            )) {
                QueueCompile(
                name: name,
                source: entry.Source
            );
            }
        }
    }
    /// <summary>Schedules a complete candidate compilation. A newer request supersedes an older result: a compilation
    /// still pending is canceled and never installed, and <see cref="Report"/> says so as <c>superseded: compilation</c>.
    /// A compiled candidate not yet installed, queued for the next frame or with its pipelines still building, is
    /// likewise replaced when the newer one compiles (<c>superseded: compiled candidate</c>).</summary>
    public void QueueCompile(string name, string source) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        var entry = m_entries[name];
        var superseded = entry.IsCompiling;

        entry.CancelPending();
        if (superseded) {
            Report?.Invoke(
                name,
                "superseded: compilation"
            );
        }
        entry.Source = source;
        if (!WorldDocumentPaths.TryResolve(
            documentDirectory: DocumentDirectory,
            path: source,
            reason: out var unresolved,
            resolved: out var resolved
        )) {
            entry.LastCompile = new ShaderPipelineLoadResult(
                Dependencies: [],
                Message: unresolved,
                Pipeline: null,
                Status: ShaderPipelineLoadStatus.Failed
            );
            Report?.Invoke(
                name,
                unresolved
            );
            return;
        }
        if (
            (entry.WatchPath is { } watched) &&
            !string.Equals(
            a: watched,
            b: resolved,
            comparisonType: PuckPaths.Comparison
        )
        ) { entry.Watch(path: resolved); }
        entry.Compilation.Start(build: token => {
            try {
                _ = ShaderPipelineSource.TryRead(
                    name: name,
                    path: resolved,
                    reason: out _,
                    source: out var before
                );

                var loaded = Packager.LoadSource(
                    cancellationToken: token,
                    name: name,
                    path: resolved
                );

                if (loaded.Status != ShaderPipelineLoadStatus.Compiled) {
                    return new CompileOutcome(
                        Result: loaded,
                        Source: null
                    );
                }
                // The identity a commit carries is the source this candidate was compiled from; a source that moved
                // while the loader ran retries the whole pipeline, as the loader's own source check does.
                if (
                    !ShaderPipelineSource.TryRead(
                    name: name,
                    path: resolved,
                    reason: out _,
                    source: out var after
                ) ||
                    !string.Equals(
                    a: before?.SourceIdentity,
                    b: after.SourceIdentity,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    return new CompileOutcome(
                        Result: new ShaderPipelineLoadResult(
                            Dependencies: loaded.Dependencies,
                            Message: "Source changed during compilation; retrying the complete pipeline.",
                            Pipeline: null,
                            Status: ShaderPipelineLoadStatus.Retry
                        ),
                        Source: null
                    );
                }

                return new CompileOutcome(
                    Result: loaded,
                    Source: after
                );
            } catch (OperationCanceledException) {
                return Failed(
                    message: "compilation superseded",
                    status: ShaderPipelineLoadStatus.Failed
                );
            } catch (ShaderToolMissingException exception) {
                return Failed(
                    message: exception.Message,
                    status: ShaderPipelineLoadStatus.Unsupported
                );
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)) {
                return Failed(
                    message: exception.Message,
                    status: ShaderPipelineLoadStatus.Failed
                );
            }

            CompileOutcome Failed(string message, ShaderPipelineLoadStatus status) => new(
                Result: new ShaderPipelineLoadResult(
                    Dependencies: [resolved],
                    Message: message,
                    Pipeline: null,
                    Status: status
                ),
                Source: null
            );
        });
    }
    /// <summary>Reconciles accepted document rows before rendering. Refused mutations never create GPU instances.</summary>
    public void Reconcile(IReadOnlyList<WorldViewPipeline> rows) {
        if (ReferenceEquals(
            objA: m_lastRows,
            objB: rows
        )) { return; }
        var desiredNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var row in rows) {
            desiredNames.Add(item: row.Name);
            if (!m_entries.TryGetValue(
                key: row.Name,
                value: out var entry
            )) {
                if (CreateNode is not { } create) { continue; }
                var node = create(row.Name);

                RegisterNode?.Invoke(
                    row.Name,
                    node
                );
                Register(
                    name: row.Name,
                    node: node
                );
                entry = m_entries[row.Name];
            }
            entry.Adopt(row: row);
            if (!string.Equals(
                a: entry.Source,
                b: row.Source,
                comparisonType: StringComparison.Ordinal
            )) {
                QueueCompile(
                    name: row.Name,
                    source: row.Source
                );
            }
        }
        m_lastRows = rows;
        foreach (var name in m_entries.Keys.ToArray()) {
            if (desiredNames.Contains(item: name)) { continue; }
            var entry = m_entries[name];

            entry.CancelPending();
            RemoveNode?.Invoke(name);
            m_entries.Remove(key: name);
        }
    }
    /// <summary>Registers a render-tree-owned instance exactly once.</summary>
    public void Register(string name, ShaderPipelineRenderNode node) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(node);
        m_entries.Add(
            key: name,
            value: new Entry { Name = name, Node = node, Owner = this }
        );
    }
    /// <summary>Looks up a registered instance without creating one.</summary>
    public bool TryGet(string name, out Entry entry) => m_entries.TryGetValue(
        key: name,
        value: out entry!
    );
}
