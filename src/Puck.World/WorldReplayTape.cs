using Puck.Commands;
using Puck.Maths;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The tape's live state — what <see cref="WorldReplayTape"/> is doing with the running session's per-tick
/// <see cref="CommandSnapshot"/> stream.</summary>
internal enum WorldReplayMode {
    /// <summary>Neither recording nor replaying; the live snapshot passes through untouched.</summary>
    Idle,

    /// <summary>Every live tick's snapshot is appended to the in-flight recording.</summary>
    Recording,

    /// <summary>The saved recording's snapshots re-drive the session, one per tick, until the tape runs out.</summary>
    Replaying,
}

/// <summary>
/// The live record/replay tape wired into Puck.World — the engine snapshot recorder
/// (<see cref="InputRecorder"/>/<see cref="SnapshotRecording"/>/<see cref="ReplaySnapshotSource"/>) connected to the
/// world's real per-tick <see cref="CommandSnapshot"/> stream. Unlike the demo's scripted-only capture, World's shared
/// launcher loop produces one genuine snapshot per fixed tick and hands it to <see cref="WorldSimulation"/>, so
/// <see cref="Intercept"/> records the actual interactive session and, on replay, feeds the saved snapshots back through
/// the same command-apply path. A per-tick state hash over the population's fixed-point poses lets a scripted run compare
/// a recorded run's tail hash against its replay's — "a saved moment re-happens" over the pipe.
/// </summary>
/// <remarks>Single-threaded on the launcher's window-pump thread: the <c>replay.*</c> verbs are Immediate (they run
/// inline during the command pump's drain) and <see cref="Intercept"/> runs inside the fixed-step
/// <see cref="WorldSimulation.Step"/> — both on that one thread, so no locking is needed. Immediate stdin verbs are NOT
/// folded into the snapshot, so the <c>replay.*</c> verbs never record or replay themselves; physical device input and
/// Simulation-routed world verbs are, so a replay reproduces the operator's driving and any world edits they made.</remarks>
internal sealed class WorldReplayTape {
    private const string Extension = ".puckreplay";
    // World is unseeded (its determinism pins the mapping, not a seed), so recordings carry a fixed seed field — it is
    // informational only; ReplaySnapshotSource never derives state from it here.
    private const uint TapeSeed = 0u;

    private readonly Func<CommandRegistry> m_registry;
    private InputRecorder? m_recorder;
    private ReplaySnapshotSource? m_replay;
    private WorldReplayMode m_mode;
    private string? m_recordName;
    private int m_localTick;
    private ulong m_lastHash;

    /// <summary>Initializes the tape over the registry whose interned ids a saved recording remaps against.</summary>
    /// <param name="registry">A lazy accessor for the live command registry (used for the recording's id↔name table on
    /// write/read). It is resolved LAZILY because the registry aggregates every <see cref="ICommandModule"/> — including
    /// the replay verbs that depend on this tape — so a direct dependency would cycle the container.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
    public WorldReplayTape(Func<CommandRegistry> registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        m_registry = registry;
    }

    /// <summary>The tape's current mode.</summary>
    public WorldReplayMode Mode => m_mode;

    /// <summary>The ticks recorded or replayed so far in the active run.</summary>
    public int TickCount => m_localTick;

    /// <summary>The name the active recording will persist under (or the replay currently playing).</summary>
    public string? Name => m_recordName;

    /// <summary>The most recent per-tick state hash the simulation reported.</summary>
    public ulong LastHash => m_lastHash;

    /// <summary>The <c>Replays/</c> directory (created on first use), beside World's other local data.</summary>
    public static string Directory() {
        var directory = Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData), path2: "Puck", path3: "World", path4: "Replays");

        _ = System.IO.Directory.CreateDirectory(path: directory);

        return directory;
    }

    /// <summary>Validates a replay name: non-empty and free of path-navigation characters — a console verb argument is
    /// untrusted, so this keeps every resolved path under <see cref="Directory"/>.</summary>
    /// <param name="name">The candidate name.</param>
    /// <returns><see langword="true"/> when the name is safe to use as a filename stem.</returns>
    public static bool IsValidName(string name) {
        return (!string.IsNullOrWhiteSpace(value: name) &&
            (name.IndexOfAny(anyOf: Path.GetInvalidFileNameChars()) < 0) &&
            !name.Contains(value: '.') &&
            !name.Contains(value: '/') &&
            !name.Contains(value: '\\'));
    }

    /// <summary>The on-disk path a valid <paramref name="name"/> resolves to.</summary>
    /// <param name="name">The replay's name.</param>
    /// <returns>The path.</returns>
    public static string PathFor(string name) {
        return Path.Combine(path1: Directory(), path2: (name + Extension));
    }

    /// <summary>The names of every persisted replay.</summary>
    /// <returns>The saved names, sorted; empty when none exist.</returns>
    public static IReadOnlyList<string> List() {
        var directory = Directory();
        var names = new List<string>();

        foreach (var path in System.IO.Directory.EnumerateFiles(path: directory, searchPattern: ("*" + Extension))) {
            names.Add(item: Path.GetFileNameWithoutExtension(path: path));
        }

        names.Sort(comparer: StringComparer.OrdinalIgnoreCase);

        return names;
    }

    /// <summary>Arms recording: the next live tick begins appending snapshots.</summary>
    /// <param name="name">The name the recording will persist under at <see cref="StopRecording"/>.</param>
    public void BeginRecording(string name) {
        m_recorder = new InputRecorder(seed: TapeSeed);
        m_replay = null;
        m_recordName = name;
        m_localTick = 0;
        m_mode = WorldReplayMode.Recording;
    }

    /// <summary>Finalizes and persists the active recording.</summary>
    /// <returns>The path written, the tick count, and the final state hash.</returns>
    /// <exception cref="InvalidOperationException">No recording is active.</exception>
    public (string Path, int Ticks, ulong Hash) StopRecording() {
        if ((m_mode != WorldReplayMode.Recording) || (m_recorder is not { } recorder) || (m_recordName is not { } name)) {
            throw new InvalidOperationException(message: "No recording is active.");
        }

        var recording = recorder.ToRecording();
        var path = PathFor(name: name);

        using (var stream = File.Create(path: path)) {
            SnapshotRecording.Write(stream: stream, recording: recording, registry: m_registry());
        }

        var ticks = m_localTick;
        var hash = m_lastHash;

        m_recorder = null;
        m_mode = WorldReplayMode.Idle;

        return (Path: path, Ticks: ticks, Hash: hash);
    }

    /// <summary>Loads a saved replay and arms playback: the next ticks re-drive the session from the tape.</summary>
    /// <param name="name">The saved replay's name.</param>
    /// <returns>The number of ticks the loaded tape will replay.</returns>
    /// <exception cref="FileNotFoundException">No replay of that name exists.</exception>
    /// <exception cref="InvalidDataException">The file is not a snapshot recording or is an unsupported version.</exception>
    public int BeginReplay(string name) {
        var path = PathFor(name: name);

        using var stream = File.OpenRead(path: path);
        var recording = SnapshotRecording.Read(stream: stream, registry: m_registry());
        var replay = new ReplaySnapshotSource(recording: recording);

        m_recorder = null;
        m_replay = replay;
        m_recordName = name;
        m_localTick = 0;
        m_mode = WorldReplayMode.Replaying;

        return replay.TickCount;
    }

    /// <summary>Interposes the tape between the launcher-produced live snapshot and the simulation: records it while
    /// recording, or substitutes the saved snapshot for this tick while replaying.</summary>
    /// <param name="live">The launcher's live snapshot for this tick.</param>
    /// <param name="replaying">Set to <see langword="true"/> when the returned snapshot is a REPLAYED one the caller must
    /// re-apply through the registry to drive the seats (the launcher already applied <paramref name="live"/>).</param>
    /// <returns>The snapshot the simulation should act on this tick.</returns>
    public CommandSnapshot Intercept(in CommandSnapshot live, out bool replaying) {
        switch (m_mode) {
            case WorldReplayMode.Recording when (m_recorder is { } recorder): {
                recorder.Record(snapshot: in live);
                m_localTick++;
                replaying = false;

                return live;
            }
            case WorldReplayMode.Replaying when (m_replay is { } replay): {
                if (m_localTick >= replay.TickCount) {
                    // The tape ran out — auto-stop and let the live session resume.
                    m_replay = null;
                    m_mode = WorldReplayMode.Idle;

                    replaying = false;

                    return live;
                }

                var snapshot = replay.SnapshotForTick(tick: (ulong)m_localTick, windowEndTick: ulong.MaxValue);

                m_localTick++;
                replaying = true;

                return snapshot;
            }
            default: {
                replaying = false;

                return live;
            }
        }
    }

    /// <summary>Records the simulation's post-step state hash for this tick (the recording's tail hash, and the replay's
    /// comparison value).</summary>
    /// <param name="hash">The tick's state hash.</param>
    public void NoteState(ulong hash) {
        if (m_mode != WorldReplayMode.Idle) {
            m_lastHash = hash;
        }
    }

    /// <summary>The deterministic per-tick state hash: every active body's fixed-point pose folded in index order, so two
    /// runs with identical input produce identical traces regardless of wall-clock or backend.</summary>
    /// <param name="population">The entity table to hash.</param>
    /// <returns>The state hash.</returns>
    public static ulong HashState(WorldPopulation population) {
        ArgumentNullException.ThrowIfNull(argument: population);

        var hash = Fnv1aHash.Create();

        for (var index = 0; (index < WorldPopulation.MaxPopulation); index++) {
            if (!population.IsActive(index: index) || (population.EntryBody(index: index) is not { } body)) {
                continue;
            }

            var position = body.FixedPosition;

            hash.Add(value: (uint)index);
            hash.Add(value: position.X.Value);
            hash.Add(value: position.Y.Value);
            hash.Add(value: position.Z.Value);
            hash.Add(value: body.FixedYaw.Value);
        }

        return hash.Value;
    }
}
