using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldReplaySnapshot {
    /// <summary>Gets the authority checkpoint the recording starts from, or <see langword="null"/> for a tape that starts
    /// from its definition's boot image with its seats joined. A recording armed after the world's first step, and a
    /// history branch saved as a tape, start from the live state itself: the re-drive restores this checkpoint into
    /// its shadow server instead of booting the definition, so its first tick continues exactly where the recording
    /// began. The checkpoint never carries the owned-world catalog (<see cref="ForTape"/>): seats hold their identities
    /// as projections, the way <see cref="Seats"/> pins them, and the re-drive keeps them detached.</summary>
    public WorldAuthorityCheckpoint? StartCheckpoint { get; init; }
    /// <summary>Gets the tick the recording's start checkpoint was taken after, or <see langword="null"/> for a tape that
    /// starts from its boot image.</summary>
    public ulong? StartTick => StartCheckpoint?.Server.LastCompletedTick;

    /// <summary>Returns the part of a live checkpoint a tape may carry: everything but the owned-world catalog, whose
    /// documents a tape never holds (the identities its seats carry ride the population as projections), and with no
    /// host-row residue, which a tape's single authority does not own.</summary>
    /// <param name="checkpoint">A checkpoint captured from the live server.</param>
    /// <returns>The checkpoint to start a tape from.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    public static WorldAuthorityCheckpoint ForTape(WorldAuthorityCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        return (checkpoint with {
            HostRow = WorldAuthorityHostRowCheckpoint.Empty,
            OwnedWorlds = new WorldOwnedWorldsCheckpoint(
                Documents: [],
                Revision: 0L
            ),
        });
    }

    /// <summary>Returns the local seats a checkpoint holds, each with the identity projection its body carries, in slot
    /// order — the seat set a tape started from that checkpoint pins.</summary>
    /// <param name="checkpoint">The start checkpoint.</param>
    /// <returns>The seats.</returns>
    internal static List<WorldReplaySeat> SeatsOf(WorldAuthorityCheckpoint checkpoint) {
        var seats = new List<WorldReplaySeat>();

        foreach (var entry in checkpoint.Population.Entries) {
            if (
                (entry.Index < WorldBodiesLimits.LocalSeatCount) &&
                !entry.IsRemoteHuman
            ) {
                seats.Add(item: new WorldReplaySeat(
                    Profile: entry.Profile,
                    Slot: entry.Index
                ));
            }
        }

        seats.Sort(comparison: static (left, right) => left.Slot.CompareTo(value: right.Slot));

        return seats;
    }
    // This recording with another pose trace: a saved history branch records only authoritative hashes, so its pose
    // trace is the re-drive's own.
    internal WorldReplaySnapshot WithPoseTrace(ulong[] pose) => new() {
        Authority = Authority,
        Companions = Companions,
        DefinitionJson = DefinitionJson,
        DocumentDirectory = DocumentDirectory,
        DocumentPath = DocumentPath,
        ForkedFrom = ForkedFrom,
        Instance = Instance,
        MountedAddons = MountedAddons,
        PipelineSourceDirectory = PipelineSourceDirectory,
        RecordedAuthoritativeHashes = RecordedAuthoritativeHashes,
        RecordedHashes = pose,
        Seats = Seats,
        SimulationRate = SimulationRate,
        StartCheckpoint = StartCheckpoint,
        Ticks = Ticks,
    };

    // The write side's guard: a start checkpoint describes the embedded definition and carries no owned document. A
    // recording that broke either would be the host's own defect, never tape data.
    private static byte[] EncodeStartCheckpoint(WorldAuthorityCheckpoint checkpoint, byte[] definitionJson) {
        if (checkpoint.OwnedWorlds.Documents.Count != 0) {
            throw new WorldReplayCodecException(message: $"a .puckreplay recording's start checkpoint carries {checkpoint.OwnedWorlds.Documents.Count} owned document(s) — a tape never holds an owned identity's document, a host bug, not tape data.");
        }

        if (!checkpoint.Server.DefinitionJson.AsSpan().SequenceEqual(other: definitionJson)) {
            throw new WorldReplayCodecException(message: "a .puckreplay recording's start checkpoint describes another document than the one the tape embeds — a host bug, not tape data.");
        }

        return WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
    }
    // The read side's twin: the bytes decode to a checkpoint of the embedded document, holding no owned document.
    private static WorldAuthorityCheckpoint? ReadStartCheckpoint(byte[]? bytes, byte[] definitionJson, string? documentDirectory) {
        if (bytes is null) {
            return null;
        }

        if (!WorldAuthorityCheckpointCodec.TryDecode(
            bytes: bytes,
            checkpoint: out var checkpoint,
            documentDirectory: documentDirectory,
            reason: out var reason
        )) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay recording: its start checkpoint does not decode ({reason}).");
        }

        if (checkpoint!.OwnedWorlds.Documents.Count != 0) {
            throw new InvalidDataException(message: "Corrupt .puckreplay recording: its start checkpoint carries owned documents, which a tape never holds.");
        }

        if (!checkpoint.Server.DefinitionJson.AsSpan().SequenceEqual(other: definitionJson)) {
            throw new InvalidDataException(message: "Corrupt .puckreplay recording: its start checkpoint describes another document than the one the tape embeds.");
        }

        return checkpoint;
    }
    // The shadow server a re-drive steps: restored from the start checkpoint when the tape carries one, else booted from
    // the embedded definition with the recorded seats joined. Either way it reads the replay's own detached catalog,
    // resolves rebuilds and pipeline sources where the recording did, and performs no save effect.
    private (WorldServer Server, WorldPopulation Population) CreateShadow(WorldDefinition definition, IWorldMachineHost machines, WorldOwnedWorlds profiles, IWorldDocumentSource? documents) {
        var replayProfiles = profiles.CreateReplayCopy();
        WorldServer server;
        WorldPopulation population;

        if (StartCheckpoint is { } start) {
            (server, population) = WorldServer.FromCheckpoint(
                checkpoint: start,
                documentDirectory: DocumentDirectory,
                instanceIdentity: Instance,
                machines: machines,
                profiles: replayProfiles,
                restoreOwnedIdentities: false
            );
        } else {
            population = new WorldPopulation(definition: definition);
            // A fresh, unconfigured render envelope reads as "fits" — the replay applies no render-growing edits, and
            // the authoritative simulation never consults GPU capacity, so no probe is needed offline.
            server = new WorldServer(
                definition: definition,
                envelope: new WorldRenderEnvelope(),
                instanceIdentity: Instance,
                machines: machines,
                population: population,
                profiles: replayProfiles
            );
        }

        server.RebuildDocuments = documents;
        server.PipelineSources = ((PipelineSourceDirectory is { } pipelineSources)
            ? new WorldPipelineSources(documentDirectory: pipelineSources)
            : null);
        server.Extensions.EnterReplay();
        // Replay verification is side-effect-free: a rule's 'save' effect re-derives deterministically like any other
        // rule effect, but writing the world's own file is engine I/O. The tap narrates why no file write happened; the
        // hash this drive compares never depends on whether the write occurred. This shadow binds no sink of its own,
        // so the line reaches a caller only if it attaches one first.
        server.SaveEffectTap = tick => server.Output.Narrate(
            channel: "replay",
            text: $"[replay: save effect suppressed (tick {tick}) — replay verification is side-effect-free]"
        );

        if (StartCheckpoint is null) {
            SeatRecordedSeats(
                definition: definition,
                population: population,
                profiles: profiles,
                server: server
            );
        }

        return (server, population);
    }
}
