using System.Diagnostics;
using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.Hosting;
using Puck.Assets;
using Puck.World.Transpiler;

namespace Puck.World;

/// <summary>Watches the compiler inputs of every running world's source and requests the ordinary reload command after a
/// quiet period.</summary>
/// <remarks>Host presentation state only. The host supplies the authenticated text ingress; this watch never mutates a world.
/// The sources are those of every world running in the process (the boot world and each instance started beside it), so
/// a saved edit to any of them, or to anything their compiles read, requests one reload, which reloads every running
/// world from its own source (<see cref="WorldReload"/>).
/// A poll asks <see cref="CompileInputs.Current"/> for each input, which stats it (existence, length and last write) and
/// re-reads only an input whose stamp moved or was too recent to vouch for its last answer
/// (<see cref="CompileInputs.RacyStampWindow"/>), so a poll's cost over an unchanged tree is one stat per input and no
/// content read. The stats run on a pool thread (<see cref="DependencyWatch{TKey, TStamp}"/>), so the host pump that
/// calls <see cref="Poll"/> only adopts their answers.</remarks>
public sealed class WorldSourceWatch {
    private readonly Func<IReadOnlyList<string>> m_sourcePaths;
    private readonly string m_catalogFingerprint;
    private readonly IMachineValidationCatalog? m_catalog;
    private readonly DependencyWatch<CompileInput, CompileInput?> m_watch;
    // Each watched source's normalized path and the facts its compile last read, keyed by question.
    private readonly Dictionary<string, Dictionary<CompileInput, CompileInput?>> m_sources = new(comparer: PuckPaths.Comparer);

    private Action? m_reload;
    // The list of sources the watch last followed, by identity, and its first (the boot world's) source.
    private IReadOnlyList<string>? m_followed;
    private string? m_primary;
    private bool m_pending;
    private long m_stampReads;
    private long m_contentReads;

    /// <summary>Creates a watch over the sources a function names.</summary>
    /// <param name="sourcePaths">Returns the paths of the sources the running worlds were loaded from, the boot world's
    /// first. A host answers the same list instance while the set is unchanged, so a poll compares nothing.</param>
    /// <param name="catalogFingerprint">The machine catalog's fingerprint the composer checks documents against.</param>
    /// <param name="catalog">The machine validation catalog, or <see langword="null"/> for none.</param>
    public WorldSourceWatch(Func<IReadOnlyList<string>> sourcePaths, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        m_sourcePaths = sourcePaths;
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
    public long StampReads => Interlocked.Read(location: ref m_stampReads);
    /// <summary>Gets the number of file contents hashed and directories listed during this watch's lifetime, which a
    /// poll does only for an input whose stamp moved or is too recent to trust.</summary>
    public long ContentReads => Interlocked.Read(location: ref m_contentReads);
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
        _ = Interlocked.Increment(location: ref m_stampReads);
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
        _ = Interlocked.Increment(location: ref m_contentReads);
        WorldBootWork.Count(WorldBootWork.WatchContentReads);
    }
    // Records what one source's compile read as that source's facts, the source file itself always among them.
    private void Record(string path, CompileInputLog reads) {
        var facts = new Dictionary<CompileInput, CompileInput?>(comparer: InputComparer.Instance);

        foreach (var input in reads.Inputs) {
            var normalized = input with { Path = PuckPaths.Normalize(path: input.Path) };

            facts.TryAdd(key: normalized.Question, value: normalized);
        }
        var root = new CompileInput(ContentHash: string.Empty, Kind: CompileInputKind.Content, Path: path);

        if (!facts.ContainsKey(root)) { facts.Add(root, ReadCurrent(input: root)); }
        m_sources[path] = facts;
    }
    // Watches the union of every source's facts, keeping each fact's first recorded answer.
    private void Refresh() {
        var union = new Dictionary<CompileInput, CompileInput?>(comparer: InputComparer.Instance);

        foreach (var facts in m_sources.Values) {
            foreach (var (question, answer) in facts) { union.TryAdd(key: question, value: answer); }
        }
        m_watch.Refresh(union.Keys, firstRead: key => union[key]);
    }
    // Compiles and composes a source to learn what it reads.
    private void Track(string path) {
        var reads = new CompileInputLog();

        using (CompileInputs.Record(log: reads)) {
            try {
                var document = (path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".puck")
                    ? (WorldCompileSession.Shared.TryCompile(compiled: out var compiled, failure: out _, path: path) ? compiled!.Document : null)
                    : CompileInputs.ReadAllBytes(path: path));

                if (document is not null) {
                    _ = WorldCompileSession.Shared.TryComposeBytes(catalog: m_catalog, catalogFingerprint: m_catalogFingerprint, document: document, json: out _, path: path, reason: out _);
                }
            } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
                // Keep the failed read's absent fact. A later file recovery takes the ordinary reload path.
            }
        }
        Record(path: path, reads: reads);
    }
    // Follows the current source list: tracks each source newly running, drops each no longer running, and answers
    // whether the boot world's source moved (a world.load), whose new origin may have been saved since it was read.
    private bool Follow(IReadOnlyList<string> sources, CompileInputLog? bootReads) {
        m_followed = sources;
        var wanted = new HashSet<string>(comparer: PuckPaths.Comparer);
        var changed = false;

        for (var index = 0; (index < sources.Count); index++) {
            var path = PuckPaths.Normalize(path: sources[index]);

            if (!wanted.Add(item: path) || m_sources.ContainsKey(key: path)) { continue; }
            if ((index == 0) && (bootReads is not null)) { Record(path: path, reads: bootReads); } else { Track(path: path); }
            changed = true;
        }
        foreach (var path in m_sources.Keys.Where(predicate: path => !wanted.Contains(item: path)).ToArray()) {
            _ = m_sources.Remove(key: path);
            changed = true;
        }
        if (changed) { Refresh(); }

        var primary = ((sources.Count == 0) ? null : PuckPaths.Normalize(path: sources[0]));
        var moved = ((m_primary is not null) && !PuckPaths.Comparer.Equals(x: primary, y: m_primary));

        m_primary = primary;
        return moved;
    }

    /// <summary>Starts watching and records the current compiler dependency set.</summary>
    /// <param name="submitReload">Queues world.reload through the host-issued ingress of the enabling principal.</param>
    /// <param name="bootReads">The dependency answers the boot load read for the boot world's source, or null to read
    /// the current source.</param>
    public void Start(Action submitReload, CompileInputLog? bootReads = null) {
        ArgumentNullException.ThrowIfNull(submitReload);
        Stop();
        _ = Follow(bootReads: bootReads, sources: m_sourcePaths());
        m_reload = submitReload;
    }
    /// <summary>Stops watching and discards changes that have not yet submitted a reload.</summary>
    public void Stop() {
        m_reload = null;
        m_followed = null;
        m_primary = null;
        m_pending = false;
        m_sources.Clear();
        m_watch.Clear();
    }
    /// <summary>Opens the log an ordinary reload records its dependencies into, including a failed compile's missing
    /// imports. The reload records into it on whatever flow compiles (<see cref="CompileInputs.Record"/>) and hands it
    /// back through <see cref="AdoptReads"/> on the host pump.</summary>
    /// <param name="path">The source the reload is reading.</param>
    /// <returns>The log while watching this source, otherwise null.</returns>
    public Reads? RecordReads(string path) {
        var normalized = PuckPaths.Normalize(path: path);

        return ((Enabled && m_sources.ContainsKey(key: normalized)) ? new Reads(Path: normalized, Log: new CompileInputLog()) : null);
    }
    /// <summary>Watches the dependencies a reload recorded, when the watch still follows the source it read. Call on
    /// the host pump.</summary>
    /// <param name="reads">The reads <see cref="RecordReads"/> opened.</param>
    public void AdoptReads(Reads reads) {
        ArgumentNullException.ThrowIfNull(reads);
        if (!Enabled || !m_sources.ContainsKey(key: reads.Path)) { return; }
        Record(path: reads.Path, reads: reads.Log);
        Refresh();
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
        var sources = m_sourcePaths();

        // A save may have landed since world.load read the accepted candidate.
        if (!ReferenceEquals(objA: sources, objB: m_followed) && Follow(bootReads: null, sources: sources)) { m_watch.Retry(now: timestamp); }
        if (m_pending || !m_watch.Poll(debounceTicks: ((Stopwatch.Frequency * Client.WorldViewGraphHost.WatchDebounceMilliseconds) / 1000),
            now: timestamp,
            pollTicks: ((Stopwatch.Frequency * Client.WorldViewGraphHost.WatchPollMilliseconds) / 1000))) { return; }
        m_pending = true;
        ReloadCount++;
        reload();
    }
    /// <summary>Blocks until the stamp read a poll started finishes, so the next <see cref="Poll"/> adopts it.</summary>
    public void WaitForRead() => m_watch.WaitForRead();
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
