using System.Diagnostics;
using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.Hosting;
using Puck.Assets;
using Puck.World.Transpiler;

namespace Puck.World;

/// <summary>Watches the current source's compiler inputs and requests the ordinary reload command after a quiet period.</summary>
/// <remarks>Host presentation state only. The host supplies the authenticated text ingress; this watch never mutates a world.
/// A poll asks <see cref="CompileInputs.Current"/> for each input, which stats it (existence, length and last write) and
/// re-reads only an input whose stamp moved or was too recent to vouch for its last answer
/// (<see cref="CompileInputs.RacyStampWindow"/>), so the polling thread's cost over an unchanged tree is one stat per
/// input and no content read.</remarks>
public sealed class WorldSourceWatch {
    private readonly Func<string> m_sourcePath;
    private readonly string m_catalogFingerprint;
    private readonly IMachineValidationCatalog? m_catalog;
    private readonly DependencyWatch<CompileInput, CompileInput?> m_watch;

    private Action? m_reload;
    private string? m_source;
    private string? m_path;
    private bool m_pending;

    /// <summary>Creates a watch over the source a function names.</summary>
    /// <param name="sourcePath">Returns the path of the source the running world was loaded from.</param>
    /// <param name="catalogFingerprint">The machine catalog's fingerprint the composer checks documents against.</param>
    /// <param name="catalog">The machine validation catalog, or <see langword="null"/> for none.</param>
    public WorldSourceWatch(Func<string> sourcePath, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
        ArgumentNullException.ThrowIfNull(sourcePath);
        m_sourcePath = sourcePath;
        m_catalogFingerprint = catalogFingerprint;
        m_catalog = catalog;
        m_watch = new DependencyWatch<CompileInput, CompileInput?>(read: Read, comparer: InputComparer.Instance);
    }

    /// <summary>Gets whether source changes may request reloads.</summary>
    public bool Enabled => (m_reload is not null);
    /// <summary>Gets the number of reload requests submitted during this watch's lifetime.</summary>
    public int ReloadCount { get; private set; }
    /// <summary>Gets the number of input stamps (existence, length, last write) polls have read during this watch's
    /// lifetime: one per watched input per poll.</summary>
    public long StampReads { get; private set; }
    /// <summary>Gets the number of file contents hashed and directories listed during this watch's lifetime, which a
    /// poll does only for an input whose stamp moved or is too recent to trust.</summary>
    public long ContentReads { get; private set; }
    /// <summary>Gets the most recent reload refusal, or null after a successful reload.</summary>
    public string? LastError { get; private set; }
    /// <summary>Gets or sets the host's inspector and toast report fan-out.</summary>
    public Action<string, bool>? Report { get; set; }

    private sealed class InputComparer : IEqualityComparer<CompileInput> {
        public static InputComparer Instance { get; } = new();

        public bool Equals(CompileInput first, CompileInput second) => ((first.Kind == second.Kind) && PuckPaths.Comparer.Equals(x: first.Path, y: second.Path));
        public int GetHashCode(CompileInput input) => HashCode.Combine(value1: input.Kind, value2: PuckPaths.Comparer.GetHashCode(obj: input.Path));
    }

    // A read that fails proves nothing, so it answers null and is tried again on the next poll.
    private static CompileInput? ReadCurrent(CompileInput input) {
        try { return input.ReadCurrent(); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) { return null; }
    }
    private CompileInput? Read(CompileInput input) {
        StampReads++;
        WorldBootWork.Count(WorldBootWork.WatchStampReads);
        try {
            var fact = CompileInputs.Current(query: input, read: out var read);

            if (read) { CountContentRead(); }
            return fact;
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            CountContentRead();
            return null;
        }
    }
    private void CountContentRead() {
        ContentReads++;
        WorldBootWork.Count(WorldBootWork.WatchContentReads);
    }
    private void Refresh(CompileInputLog reads) {
        if (m_path is not { } path) { return; }
        var facts = new Dictionary<CompileInput, CompileInput?>(comparer: InputComparer.Instance);

        foreach (var input in reads.Inputs) {
            var normalized = input with { Path = PuckPaths.Normalize(path: input.Path) };

            facts.TryAdd(key: normalized.Question, value: normalized);
        }
        var root = new CompileInput(ContentHash: string.Empty, Kind: CompileInputKind.Content, Path: path);

        if (!facts.ContainsKey(root)) { facts.Add(root, ReadCurrent(input: root)); }
        m_watch.Refresh(facts.Keys, firstRead: key => facts[key]);
    }
    private void TrackSource(string source) {
        m_watch.Clear();
        m_source = source;
        m_path = PuckPaths.Normalize(path: source);
        var reads = new CompileInputLog();

        using (CompileInputs.Record(log: reads)) {
            try {
                var document = (m_path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".puck")
                    ? (WorldCompileSession.Shared.TryCompile(compiled: out var compiled, failure: out _, path: m_path) ? compiled!.Document : null)
                    : CompileInputs.ReadAllBytes(path: m_path));

                if (document is not null) {
                    _ = WorldCompileSession.Shared.TryComposeBytes(catalog: m_catalog, catalogFingerprint: m_catalogFingerprint, document: document, json: out _, path: m_path, reason: out _);
                }
            } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
                // Keep the failed read's absent fact. A later file recovery takes the ordinary reload path.
            }
        }
        Refresh(reads: reads);
    }

    /// <summary>Starts watching and records the current compiler dependency set.</summary>
    /// <param name="submitReload">Queues world.reload through the host-issued ingress of the enabling principal.</param>
    /// <param name="bootReads">The dependency answers used by the boot load, or null to read the current source.</param>
    public void Start(Action submitReload, CompileInputLog? bootReads = null) {
        ArgumentNullException.ThrowIfNull(submitReload);
        Stop();
        var source = m_sourcePath();
        if (bootReads is null) {
            TrackSource(source);
        } else {
            m_source = source;
            m_path = PuckPaths.Normalize(path: source);
            Refresh(bootReads);
        }
        m_reload = submitReload;
    }
    /// <summary>Stops watching and discards changes that have not yet submitted a reload.</summary>
    public void Stop() {
        m_reload = null;
        m_source = null;
        m_path = null;
        m_pending = false;
        m_watch.Clear();
    }
    /// <summary>Opens the log an ordinary reload records its dependencies into, including a failed compile's missing
    /// imports. The reload records into it on whatever flow compiles (<see cref="CompileInputs.Record"/>) and hands it
    /// back through <see cref="AdoptReads"/> on the host pump.</summary>
    /// <param name="path">The source the reload is reading.</param>
    /// <returns>The log while watching this source, otherwise null.</returns>
    public Reads? RecordReads(string path) {
        var normalized = PuckPaths.Normalize(path: path);

        return ((Enabled && PuckPaths.Comparer.Equals(x: normalized, y: m_path)) ? new Reads(Path: normalized, Log: new CompileInputLog()) : null);
    }
    /// <summary>Watches the dependencies a reload recorded, when the watch still follows the source it read. Call on
    /// the host pump.</summary>
    /// <param name="reads">The reads <see cref="RecordReads"/> opened.</param>
    public void AdoptReads(Reads reads) {
        ArgumentNullException.ThrowIfNull(reads);
        if (Enabled && PuckPaths.Comparer.Equals(x: reads.Path, y: m_path)) { Refresh(reads: reads.Log); }
    }

    /// <summary>The dependencies one reload of a watched source recorded.</summary>
    /// <param name="Path">The normalized source path the reload read.</param>
    /// <param name="Log">The log its compile recorded into.</param>
    public sealed record Reads(string Path, CompileInputLog Log);

    /// <summary>Polls on the host pump using its monotonic presentation timestamp.</summary>
    /// <param name="now">A Stopwatch timestamp; null reads the current timestamp.</param>
    public void Poll(long? now = null) {
        if (m_reload is not { } reload) { return; }
        WorldBootWork.Count(WorldBootWork.WatchPumps);
        var timestamp = (now ?? Stopwatch.GetTimestamp());
        var source = m_sourcePath();

        if (!string.Equals(source, m_source, StringComparison.Ordinal)) {
            if (PuckPaths.Comparer.Equals(PuckPaths.Normalize(source), m_path)) {
                m_source = source;
            } else {
                TrackSource(source);
                // A save may have landed since world.load read the accepted candidate.
                m_watch.Retry(now: timestamp);
            }
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
}
