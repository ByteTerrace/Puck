using System.Diagnostics;
using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.Hosting;
using Puck.Transpiler.Modules;
using Puck.World.Transpiler.Composition;

namespace Puck.World;

/// <summary>Watches the current source's compiler inputs and requests the ordinary reload command after a quiet period.</summary>
/// <remarks>Host presentation state only. The host supplies the authenticated text ingress; this watch never mutates a world.</remarks>
public sealed class WorldSourceWatch(Func<string> sourcePath, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
    private readonly DependencyWatch<CompileInput, CompileInput?> m_watch = new(read: ReadCurrent, comparer: InputComparer.Instance);

    private Action? m_reload;
    private string? m_path;
    private bool m_pending;

    /// <summary>Gets whether source changes may request reloads.</summary>
    public bool Enabled => (m_reload is not null);
    /// <summary>Gets the number of reload requests submitted during this watch's lifetime.</summary>
    public int ReloadCount { get; private set; }
    /// <summary>Gets the most recent reload refusal, or null after a successful reload.</summary>
    public string? LastError { get; private set; }
    /// <summary>Gets or sets the host's inspector and toast report fan-out.</summary>
    public Action<string, bool>? Report { get; set; }

    private sealed class InputComparer : IEqualityComparer<CompileInput> {
        public static InputComparer Instance { get; } = new();

        public bool Equals(CompileInput first, CompileInput second) => ((first.Kind == second.Kind) && PuckPaths.Comparer.Equals(x: first.Path, y: second.Path));
        public int GetHashCode(CompileInput input) => HashCode.Combine(value1: input.Kind, value2: PuckPaths.Comparer.GetHashCode(obj: input.Path));
    }

    private static CompileInput? ReadCurrent(CompileInput input) {
        try { return input.ReadCurrent(); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { return null; }
    }
    private void Refresh(CompileInputLog reads) {
        if (m_path is not { } path) { return; }
        var facts = new Dictionary<CompileInput, CompileInput?>(comparer: InputComparer.Instance);

        foreach (var input in reads.Inputs) {
            var normalized = input with { Path = PuckPaths.Normalize(path: input.Path) };

            facts.TryAdd(key: normalized with { ContentHash = string.Empty }, value: normalized);
        }
        var root = new CompileInput(ContentHash: string.Empty, Kind: CompileInputKind.Content, Path: path);

        facts.TryAdd(key: root, value: ReadCurrent(input: root));
        m_watch.Refresh(facts.Keys, firstRead: key => facts[key]);
    }
    private void TrackSource() {
        m_watch.Clear();
        m_path = PuckPaths.Normalize(path: sourcePath());
        var reads = new CompileInputLog();

        using (CompileInputs.Record(log: reads)) {
            try {
                var document = (m_path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".puck")
                    ? (WorldCompileCache.Shared.TryCompile(compiled: out var compiled, failure: out _, path: m_path) ? compiled!.Document : null)
                    : CompileInputs.ReadAllBytes(path: m_path));

                if (document is not null) {
                    _ = PuckDocumentComposer.TryComposeWorldDocument(catalog: catalog, catalogFingerprint: catalogFingerprint, chainBytes: out _, composed: out _, reason: out _, rootBytes: document, rootResolvedPath: m_path);
                }
            } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
                // Keep the failed read's absent fact. A later file recovery takes the ordinary reload path.
            }
        }
        Refresh(reads: reads);
    }

    /// <summary>Starts watching and records the current compiler dependency set.</summary>
    /// <param name="submitReload">Queues world.reload through the host-issued ingress of the enabling principal.</param>
    public void Start(Action submitReload) {
        ArgumentNullException.ThrowIfNull(submitReload);
        Stop();
        TrackSource();
        m_reload = submitReload;
    }
    /// <summary>Stops watching and discards changes that have not yet submitted a reload.</summary>
    public void Stop() {
        m_reload = null;
        m_path = null;
        m_pending = false;
        m_watch.Clear();
    }
    /// <summary>Records the dependencies an ordinary reload reads, including a failed compile's missing imports.</summary>
    /// <param name="path">The source the reload is reading.</param>
    /// <returns>A recording scope while watching this source, otherwise null.</returns>
    public IDisposable? RecordReads(string path) => ((Enabled && PuckPaths.Comparer.Equals(x: PuckPaths.Normalize(path: path), y: m_path)) ? new ReadScope(owner: this) : null);

    private sealed class ReadScope : IDisposable {
        private readonly WorldSourceWatch m_owner;
        private readonly CompileInputLog m_reads = new();
        private readonly CompileInputs.Scope m_scope;

        public ReadScope(WorldSourceWatch owner) {
            m_owner = owner;
            m_scope = CompileInputs.Record(log: m_reads);
        }

        public void Dispose() {
            m_scope.Dispose();
            m_owner.Refresh(reads: m_reads);
        }
    }

    /// <summary>Polls on the host pump using its monotonic presentation timestamp.</summary>
    /// <param name="now">A Stopwatch timestamp; null reads the current timestamp.</param>
    public void Poll(long? now = null) {
        if (m_reload is not { } reload) { return; }
        var timestamp = (now ?? Stopwatch.GetTimestamp());

        if (!PuckPaths.Comparer.Equals(x: PuckPaths.Normalize(path: sourcePath()), y: m_path)) {
            TrackSource();
            // These bytes may have changed since world.load read its accepted candidate. Reload once through
            // the normal command door rather than treating this adoption-time read as the running world's proof.
            m_watch.Retry(now: timestamp);
        }
        if (m_pending || !m_watch.Poll(debounceTicks: ((Stopwatch.Frequency * Client.WorldViewGraphHost.WatchDebounceMilliseconds) / 1000),
            now: timestamp,
            pollTicks: ((Stopwatch.Frequency * Client.WorldViewGraphHost.WatchPollMilliseconds) / 1000))) { return; }
        m_pending = true;
        ReloadCount++;
        reload();
    }
    /// <summary>Releases the pending request after its ordinary command and server verdict have settled.</summary>
    /// <param name="result">The settled command result.</param>
    public void Complete(CommandResult result) {
        m_pending = false;
        NoteResult(result: result);
    }
    /// <summary>Publishes a manual or watched reload's diagnostic to the editor.</summary>
    /// <param name="result">The reload result; success clears the last refusal.</param>
    public void NoteResult(CommandResult result) {
        var error = (result.IsError ? result.Output : null);

        if (error == LastError) { return; }
        LastError = error;
        if (error is not null) { Report?.Invoke(error, true); }
    }
    /// <summary>Returns and publishes an ordinary reload refusal without changing the running world.</summary>
    /// <param name="diagnostic">The loader or validator's diagnostic, including any source location.</param>
    /// <returns>The refused command result.</returns>
    public CommandResult Refuse(string diagnostic) {
        var result = CommandResult.Error(output: $"[world.reload: {diagnostic}]");

        NoteResult(result: result);
        return result;
    }
}
