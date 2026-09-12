using System.Diagnostics;
using System.Numerics;
using Puck.Shaders;

namespace Puck.World.Client;

/// <summary>A non-destructive pointer sample in client pixels, supplied by the host.</summary>
public readonly record struct WorldPipelinePointerSample(Vector2 ClientPosition, bool HasPosition, bool Pressed);

/// <summary>Hosts named shader-pipeline instances. Compilation runs in the background; completed candidates
/// are installed by the frame presenter before producing a frame. Clocks and history are presentation state.</summary>
public sealed class WorldPipelineRuntime : IDisposable {
    /// <summary>One instance's presentation controls, pending compilation, and dependency watch.</summary>
    public sealed class Entry {
        private readonly Dictionary<string, (DateTime Time, long Length)> m_stamps = new(StringComparer.OrdinalIgnoreCase);
        private long m_changedAt;
        internal CancellationTokenSource? Cancellation { get; set; }
        internal Task<ShaderPipelineLoadResult>? Pending { get; set; }
        internal int PendingSteps { get; set; }
        internal float AuthoredTimeScale { get; set; } = 1f;
        /// <summary>The GPU executor, owned by the hosting render tree.</summary>
        public required ShaderPipelineRenderNode Node { get; init; }
        /// <summary>The most recently completed compilation, including any diagnostics.</summary>
        public ShaderPipelineLoadResult? LastCompile { get; internal set; }
        /// <summary>The currently requested document-relative source.</summary>
        public string Source { get; internal set; } = string.Empty;
        /// <summary>Whether time and feedback advancement are paused.</summary>
        public bool ClockPaused { get; set; }
        /// <summary>The presentation clock in seconds.</summary>
        public double ClockSeconds { get; set; }
        /// <summary>The non-negative presentation rate multiplier.</summary>
        public float ClockScale { get; set; } = 1f;
        /// <summary>The previous pointer state in Shadertoy pixel coordinates.</summary>
        public Vector4 Mouse { get; set; }
        /// <summary>Whether the pointer was pressed in the previous frame.</summary>
        public bool MouseWasPressed { get; set; }
        /// <summary>The root source being watched, or null when watching is disabled.</summary>
        public string? WatchPath { get; private set; }
        /// <summary>The number of dependency changes observed.</summary>
        public int SourceChangeCount { get; private set; }
        /// <summary>Whether a background compilation is still pending installation.</summary>
        public bool IsCompiling => Pending is not null;

        /// <summary>Watches the source and the last compilation's dependencies.</summary>
        public void Watch(string path) {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            Unwatch();
            WatchPath = path;
            RefreshDependencies();
        }
        /// <summary>Stops watching and discards any pending debounce event.</summary>
        public void Unwatch() {
            WatchPath = null;
            m_stamps.Clear();
            m_changedAt = 0;
        }
        internal void RefreshDependencies() {
            if (WatchPath is not { } root) { return; }
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            if (LastCompile is { } compiled) { retained.UnionWith(compiled.Dependencies); }
            foreach (var path in retained) { m_stamps.TryAdd(path, ReadStamp(path)); }
            foreach (var path in m_stamps.Keys.ToArray()) {
                if (!retained.Contains(path)) { m_stamps.Remove(path); }
            }
            // Preserve existing stamps and debounce state: an edit during compilation must schedule another load.
        }
        internal bool PollWatch(long debounceTicks) {
            if (WatchPath is null) { return false; }
            // Replacing values is safe while enumerating Dictionary on the supported runtime.
            foreach (var (path, before) in m_stamps) {
                var after = ReadStamp(path);
                if (before == after) { continue; }
                m_stamps[path] = after;
                SourceChangeCount++;
                m_changedAt = Stopwatch.GetTimestamp();
            }
            if (m_changedAt == 0 || Stopwatch.GetTimestamp() - m_changedAt < debounceTicks) { return false; }
            m_changedAt = 0;
            return true;
        }
        private static (DateTime, long) ReadStamp(string path) {
            try {
                var info = new FileInfo(path);
                return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1L);
            } catch (IOException) {
                return (DateTime.MinValue, -1L);
            } catch (UnauthorizedAccessException) {
                return (DateTime.MinValue, -1L);
            }
        }
        /// <summary>Pauses and schedules exactly one logical frame at the standard development rate.</summary>
        public void Step() {
            ClockPaused = true;
            PendingSteps = checked(PendingSteps + 1);
        }
        /// <summary>Resets time, pending steps, and GPU feedback to their initial values.</summary>
        public void Reset() {
            ClockSeconds = 0;
            PendingSteps = 0;
            Node.Reset();
        }
        /// <summary>Advances this instance once for a produced host frame and returns its shader time delta.</summary>
        public double AdvanceClock(double deltaSeconds) {
            Node.Paused = ClockPaused || ClockScale == 0;
            double delta;
            if (PendingSteps > 0) {
                PendingSteps--;
                delta = 1.0 / 60.0;
                Node.Step();
            } else {
                delta = Node.Paused ? 0 : deltaSeconds * ClockScale;
            }
            ClockSeconds += delta;
            return delta;
        }
    }

    /// <summary>The source watch's quiet period before requesting compilation.</summary>
    public const int WatchDebounceMilliseconds = 150;
    private readonly Dictionary<string, Entry> m_entries = new(StringComparer.Ordinal);
    private bool m_disposed;
    private IReadOnlyList<WorldViewPipeline>? m_lastRows;

    /// <summary>Creates a host registry using the shared pipeline loader and the world's document directory.</summary>
    public WorldPipelineRuntime(ShaderPipelineLoader loader, string documentDirectory) {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentDirectory);
        Loader = loader;
        DocumentDirectory = documentDirectory;
    }
    /// <summary>The source loader used by both boot and live authoring.</summary>
    public ShaderPipelineLoader Loader { get; }
    /// <summary>The directory against which authored pipeline source paths resolve.</summary>
    public string DocumentDirectory { get; }
    /// <summary>The factory for instances loaded after boot, using the same device as the host.</summary>
    public Func<string, ShaderPipelineRenderNode>? CreateNode { get; set; }
    /// <summary>Registers a newly created instance with the owning render tree.</summary>
    public Action<string, ShaderPipelineRenderNode>? RegisterNode { get; set; }
    /// <summary>Removes and retires an instance from the owning render tree.</summary>
    public Action<string>? RemoveNode { get; set; }
    /// <summary>A non-destructive pointer read, absent in an offscreen host.</summary>
    public Func<WorldPipelinePointerSample>? ReadPointer { get; set; }
    /// <summary>Completed compilation reports, delivered only from the presentation thread.</summary>
    public Action<string, string>? Report { get; set; }
    /// <summary>Every registered instance by its authored name.</summary>
    public IReadOnlyDictionary<string, Entry> Entries => m_entries;

    /// <summary>Registers a render-tree-owned instance exactly once.</summary>
    public void Register(string name, ShaderPipelineRenderNode node) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(node);
        m_entries.Add(name, new Entry { Node = node });
    }
    /// <summary>Reconciles accepted document rows before rendering. Refused mutations never create GPU instances.</summary>
    public void Reconcile(IReadOnlyList<WorldViewPipeline> rows) {
        if (ReferenceEquals(m_lastRows, rows)) { return; }
        var desiredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows) {
            desiredNames.Add(row.Name);
            if (!m_entries.TryGetValue(row.Name, out var entry)) {
                if (CreateNode is not { } create) { continue; }
                var node = create(row.Name);
                RegisterNode?.Invoke(row.Name, node);
                Register(row.Name, node);
                entry = m_entries[row.Name];
            }
            if (entry.AuthoredTimeScale != row.TimeScale) {
                entry.AuthoredTimeScale = row.TimeScale;
                entry.ClockScale = row.TimeScale;
            }
            if (!string.Equals(entry.Source, row.Source, StringComparison.Ordinal)) {
                QueueCompile(row.Name, row.Source);
            }
        }
        m_lastRows = rows;
        foreach (var name in m_entries.Keys.ToArray()) {
            if (desiredNames.Contains(name)) { continue; }
            var entry = m_entries[name];
            entry.Cancellation?.Cancel();
            entry.Cancellation?.Dispose();
            RemoveNode?.Invoke(name);
            m_entries.Remove(name);
        }
    }
    /// <summary>Schedules a complete candidate compilation. A newer request supersedes an older result.</summary>
    public void QueueCompile(string name, string source) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        var entry = m_entries[name];
        entry.Cancellation?.Cancel();
        entry.Cancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        entry.Cancellation = cancellation;
        entry.Source = source;
        var resolved = Path.GetFullPath(source, DocumentDirectory);
        var token = cancellation.Token;
        entry.Pending = Task.Run(() => {
            try {
                return Loader.Load(name, resolved, token);
            } catch (OperationCanceledException) {
                return new ShaderPipelineLoadResult(null, [resolved], "compilation superseded");
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or ShaderToolMissingException) {
                return new ShaderPipelineLoadResult(null, [resolved], exception.Message);
            }
        });
    }
    /// <summary>Installs complete candidates and polls dependency watches before this host frame renders.</summary>
    public void PumpWatches() {
        if (m_disposed) { return; }
        var debounce = Stopwatch.Frequency * WatchDebounceMilliseconds / 1000;
        foreach (var (name, entry) in m_entries) {
            if (entry.Pending is { IsCompleted: true } pending) {
                entry.Pending = null;
                ShaderPipelineLoadResult result;
                try { result = pending.GetAwaiter().GetResult(); }
                catch (Exception exception) { result = new ShaderPipelineLoadResult(null, [], exception.Message); }
                entry.LastCompile = result;
                if (result.Pipeline is { } pipeline) {
                    try { entry.Node.Swap(pipeline); }
                    catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException) {
                        result = result with { Pipeline = null, Message = exception.Message };
                        entry.LastCompile = result;
                    }
                }
                entry.RefreshDependencies();
                Report?.Invoke(name, result.Message);
            }
            if (entry.PollWatch(debounce)) { QueueCompile(name, entry.Source); }
        }
    }
    /// <summary>Looks up a registered instance without creating one.</summary>
    public bool TryGet(string name, out Entry entry) => m_entries.TryGetValue(name, out entry!);
    /// <summary>Cancels pending compilations; the render tree retains ownership of the GPU instances.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        foreach (var entry in m_entries.Values) {
            entry.Cancellation?.Cancel();
            entry.Cancellation?.Dispose();
        }
    }
}
