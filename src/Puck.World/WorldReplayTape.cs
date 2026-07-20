using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The tape's live state — what <see cref="WorldReplayTape"/> is doing with the running session.</summary>
internal enum WorldReplayMode {
    /// <summary>Neither recording; the loopback taps are detached and the session runs untouched.</summary>
    Idle,

    /// <summary>The live session's per-tick server-input stream is being captured into the in-flight recording.</summary>
    Recording,
}

/// <summary>
/// The record side of Puck.World's true deterministic replay. While armed it captures the live session's authoritative
/// SERVER-input stream — the intent submissions and authority commands that reach the <see cref="LoopbackTransport"/>
/// each tick — plus the record-start world definition and active seats, into a self-contained
/// <see cref="WorldReplaySnapshot"/>. A saved recording rehydrates a FRESH world from that starting state and re-drives
/// the captured stream through it (<see cref="WorldReplaySnapshot.Drive"/>), so the recorded and replayed per-tick pose
/// hashes are compared for a bit-identical match.
/// </summary>
/// <remarks>
/// <para>This REPLACES the earlier live input re-injection lever. There is no live-playback mode: a replay is an OFFLINE
/// recomputation over an isolated shadow world (<see cref="WorldReplaySnapshot.Drive"/>) that never touches the running
/// session, so live seat input is structurally excluded from a playback rather than merely advised against, and the
/// verdict is readable synchronously over the pipe the instant it completes (no per-tick drain to wait out).</para>
/// <para>HONEST SCOPE. The captured starting state is the SERVER simulation only — definition + active seats + the
/// per-tick intent/command stream. The population's body state at record-start is the deterministic boot image of the
/// captured definition, which the fresh world reconstructs exactly, so no per-body pose is serialized. Screen machines,
/// their pixels, cameras, overlays, and audio are PRESENTATION and are excluded (see <see cref="WorldReplaySnapshot"/>).</para>
/// <para>Single-threaded on the launcher's window-pump thread: the <c>replay.*</c> verbs are Immediate (they run inline
/// during the command pump's drain) and the taps + <see cref="NoteTick"/> run inside the fixed-step
/// <see cref="WorldSimulation.Step"/> — both on that one thread, so no locking is needed. The <c>replay.*</c> verbs are
/// NOT folded into the captured stream (they never reach the loopback), so a recording never records the recording
/// verbs themselves; physical device input and Simulation-routed world verbs DO reach the loopback and are captured.</para>
/// </remarks>
internal sealed class WorldReplayTape {
    private const string Extension = ".puckreplay";

    private readonly WorldServer m_liveServer;
    private readonly WorldProfiles m_profiles;
    private readonly LoopbackTransport m_transport;
    private WorldReplayMode m_mode;
    private string? m_recordName;
    private byte[]? m_definitionJson;
    private List<WorldReplaySeat>? m_seats;
    private List<WorldReplayTickInput>? m_ticks;
    // The current tick's accumulating input, rotated into m_ticks at each NoteTick.
    private List<WorldCommand> m_currentCommands = new();
    private List<IntentSubmission> m_currentIntents = new();

    /// <summary>Initializes the tape over the live server it snapshots the starting state from, the profile catalog a
    /// replay's seats re-resolve against, and the loopback whose per-tick submissions it taps.</summary>
    /// <param name="liveServer">The authoritative live server (read at record-start for the definition and active seats).</param>
    /// <param name="profiles">The profile catalog (handed to a replay's fresh world for seat re-resolution).</param>
    /// <param name="transport">The client→server loopback whose intent/command submissions the tape captures.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldReplayTape(WorldServer liveServer, WorldProfiles profiles, LoopbackTransport transport) {
        ArgumentNullException.ThrowIfNull(argument: liveServer);
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: transport);

        m_liveServer = liveServer;
        m_profiles = profiles;
        m_transport = transport;
    }

    /// <summary>The tape's current mode.</summary>
    public WorldReplayMode Mode => m_mode;

    /// <summary>The ticks captured so far in the active recording.</summary>
    public int TickCount => (m_ticks?.Count ?? 0);

    /// <summary>The name the active recording will persist under.</summary>
    public string? Name => m_recordName;

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

    /// <summary>Arms recording: snapshots the record-start starting state (the live definition and active seats) and
    /// attaches the loopback taps so the next ticks' server-input stream is captured.</summary>
    /// <param name="name">The name the recording will persist under at <see cref="StopRecording"/>.</param>
    public void BeginRecording(string name) {
        m_recordName = name;
        m_definitionJson = WorldDefinitionSerialization.Serialize(definition: m_liveServer.Definition);
        m_seats = CaptureActiveSeats();
        m_ticks = new List<WorldReplayTickInput>();
        m_currentCommands = new List<WorldCommand>();
        m_currentIntents = new List<IntentSubmission>();
        m_transport.IntentTap = submission => m_currentIntents.Add(item: submission);
        m_transport.CommandTap = command => m_currentCommands.Add(item: command);
        m_mode = WorldReplayMode.Recording;
    }

    /// <summary>Closes the current tick while recording: the submissions captured since the last call become one tick's
    /// input group, and the accumulators reset for the next tick. Called once per fixed tick from
    /// <see cref="WorldSimulation.Step"/> AFTER the server step, when the tick's whole stream has been submitted. A
    /// no-op while idle.</summary>
    public void NoteTick() {
        if ((m_mode != WorldReplayMode.Recording) || (m_ticks is not { } ticks)) {
            return;
        }

        ticks.Add(item: new WorldReplayTickInput(Commands: m_currentCommands, Intents: m_currentIntents));
        m_currentCommands = new List<WorldCommand>();
        m_currentIntents = new List<IntentSubmission>();
    }

    /// <summary>Finalizes the active recording: detaches the taps, computes the recorded tail hash by driving a fresh
    /// world through the captured stream (the record-side of the record-vs-replay comparison), and persists the
    /// self-contained recording.</summary>
    /// <returns>The path written, the tick count, and the recorded tail hash.</returns>
    /// <exception cref="InvalidOperationException">No recording is active.</exception>
    public (string Path, int Ticks, ulong Hash) StopRecording() {
        if ((m_mode != WorldReplayMode.Recording) || (m_definitionJson is not { } definitionJson) || (m_seats is not { } seats) || (m_ticks is not { } ticks) || (m_recordName is not { } name)) {
            throw new InvalidOperationException(message: "No recording is active.");
        }

        DetachTaps();

        // Compute the recorded tail hash from a FRESH world driven through the just-captured stream — the same Drive a
        // replay runs, so the persisted hash is a genuine fresh-world result, not a reading off the live session.
        var recording = new WorldReplaySnapshot {
            DefinitionJson = definitionJson,
            RecordedTailHash = 0UL,
            Seats = seats,
            Ticks = ticks,
        };
        var trace = recording.Drive(profiles: m_profiles);
        var hash = ((trace.Length > 0) ? trace[^1] : 0UL);
        var finalized = new WorldReplaySnapshot {
            DefinitionJson = definitionJson,
            RecordedTailHash = hash,
            Seats = seats,
            Ticks = ticks,
        };
        var path = PathFor(name: name);

        using (var stream = File.Create(path: path)) {
            WorldReplaySnapshot.Write(stream: stream, recording: finalized);
        }

        m_mode = WorldReplayMode.Idle;
        m_recordName = null;
        m_definitionJson = null;
        m_seats = null;
        m_ticks = null;

        return (Path: path, Ticks: ticks.Count, Hash: hash);
    }

    /// <summary>Aborts the active recording WITHOUT persisting it: detaches the taps and drops the captured stream.</summary>
    /// <returns>The dropped recording's name.</returns>
    /// <exception cref="InvalidOperationException">No recording is active.</exception>
    public string CancelRecording() {
        if ((m_mode != WorldReplayMode.Recording) || (m_recordName is not { } name)) {
            throw new InvalidOperationException(message: "No recording is active.");
        }

        DetachTaps();
        m_mode = WorldReplayMode.Idle;
        m_recordName = null;
        m_definitionJson = null;
        m_seats = null;
        m_ticks = null;

        return name;
    }

    /// <summary>Loads a saved recording, rehydrates a FRESH world from it, re-drives the recorded server-input stream,
    /// and compares the replayed tail hash against the recorded one — the offline verification, run synchronously so the
    /// verdict is readable the instant it returns. Never touches the live session.</summary>
    /// <param name="name">The saved recording's name.</param>
    /// <returns>The recorded and replayed tail hashes, the tick count, and whether they matched.</returns>
    /// <exception cref="FileNotFoundException">No recording of that name exists.</exception>
    /// <exception cref="InvalidDataException">The file is not a <c>.puckreplay</c> recording or is an unsupported version.</exception>
    public (ulong Recorded, ulong Replayed, int Ticks, bool Match) Verify(string name) {
        var path = PathFor(name: name);
        WorldReplaySnapshot recording;

        using (var stream = File.OpenRead(path: path)) {
            recording = WorldReplaySnapshot.Read(stream: stream);
        }

        var trace = recording.Drive(profiles: m_profiles);
        var replayed = ((trace.Length > 0) ? trace[^1] : 0UL);

        return (Recorded: recording.RecordedTailHash, Replayed: replayed, Ticks: recording.TickCount, Match: (replayed == recording.RecordedTailHash));
    }

    // Snapshot the seats active at record-start: their slot and the profile name (re-resolved by name in a replay's
    // fresh world). Only the four local seats can be active; a peer/inhabitant is boot-derived from the definition.
    private List<WorldReplaySeat> CaptureActiveSeats() {
        var seats = new List<WorldReplaySeat>();

        for (var slot = 0; (slot < WorldPopulation.LocalSeatCount); slot++) {
            if (m_liveServer.Population.IsActive(index: slot)) {
                seats.Add(item: new WorldReplaySeat(Slot: slot, ProfileName: m_liveServer.Body(index: slot)?.Profile?.Name));
            }
        }

        return seats;
    }

    private void DetachTaps() {
        m_transport.IntentTap = null;
        m_transport.CommandTap = null;
    }
}
