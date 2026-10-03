using Puck.Commands;
using Puck.Hosting;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldReplaySnapshot {
    /// <summary>Reads a recording from a stream, consuming the stream to its end.</summary>
    /// <param name="stream">The source stream.</param>
    /// <returns>The deserialized recording.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The stream is not a <c>.puckreplay</c> tape, or is an older shape this
    /// build does not read (refused outright — greenfield keeps no read-side tolerance for a foreign shape); is
    /// truncated, corrupt, or carries bytes after the tape; carries a value no wire table names; pins one addon name
    /// twice; pins a seat slot out of range or twice; or carries an arrival no commit could have decided.</exception>
    public static WorldReplaySnapshot Read(Stream stream) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        byte[] bytes;

        using (var buffer = new MemoryStream()) {
            stream.CopyTo(destination: buffer);
            bytes = buffer.ToArray();
        }

        var reader = new WireReader(bytes: bytes);
        var magic = reader.ReadUInt32();
        var shapeToken = reader.ReadUInt32();

        if (reader.Failed) {
            throw Corrupt(failure: reader.Failure);
        }

        if (
            (magic != Magic) ||
            (shapeToken != ShapeToken)
        ) {
            throw ReplayRefusal.ShapeMismatch.Raise(message: $"Not a Puck replay tape, or an older shape this build does not read — re-record it. (found magic 0x{magic:x8}, shape token {shapeToken}; this build reads magic 0x{Magic:x8}, shape token {ShapeToken} only)");
        }

        var simulationRate = reader.ReadUInt32();
        var forkedFrom = reader.ReadOptional(readValue: static (ref WireReader r) => {
            var parentName = r.ReadString(field: "fork provenance parent");
            var forkTick = r.ReadInt32();

            return new WorldReplayForkProvenance(
                ParentName: parentName,
                Tick: forkTick
            );
        });
        var pipelineSourceDirectory = reader.ReadNullableString(field: "pipeline source directory");
        var instance = reader.ReadString(field: "tape instance");
        var authority = reader.ReadString(field: "tape authority");
        var documentDirectory = reader.ReadNullableString(field: "tape document directory");
        var documentPath = reader.ReadNullableString(field: "tape document path");

        if (
            !reader.Failed &&
            (forkedFrom is { } fork)
        ) {
            if (string.IsNullOrWhiteSpace(value: fork.ParentName)) {
                throw new InvalidDataException(message: "Corrupt .puckreplay recording: the fork provenance names an empty parent tape.");
            }

            if (fork.Tick < 0) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: the fork provenance tick {fork.Tick} is negative.");
            }
        }

        var recordedHashes = ReadTapeArray(
            minimumBytesEach: sizeof(ulong),
            readItem: static (ref WireReader r) => r.ReadUInt64(),
            reader: ref reader,
            what: "hash"
        );
        var recordedAuthoritativeHashes = ReadTapeArray(
            minimumBytesEach: sizeof(ulong),
            readItem: static (ref WireReader r) => r.ReadUInt64(),
            reader: ref reader,
            what: "authoritative hash"
        );
        var definitionJson = reader.ReadBlock(
            field: "definition",
            maxBytes: WireLimits.MaxDocumentBytes
        );
        // 12 = the smallest possible receipt: two empty strings' 16-bit length prefixes and the u64 fuel.
        var mountedAddons = ReadTapeArray(
            minimumBytesEach: 12,
            readItem: static (ref WireReader r) => {
                var name = r.ReadString(field: "mounted addon name");
                var hash = r.ReadString(field: "mounted addon hash");
                var fuel = r.ReadUInt64();

                return new WorldAddonReceipt(
                    Fuel: fuel,
                    Hash: hash,
                    Name: name
                );
            },
            reader: ref reader,
            what: "mounted addon"
        );
        var seats = ReadTapeArray(
            minimumBytesEach: 5,
            readItem: static (ref WireReader r) => {
                var slot = r.ReadInt32();
                var profile = ReadProfilePin(reader: ref r);

                return new WorldReplaySeat(
                    Profile: profile,
                    Slot: slot
                );
            },
            reader: ref reader,
            what: "seat"
        );
        var ticks = ReadTapeArray(
            minimumBytesEach: 8,
            readItem: static (ref WireReader r) => {
                // 2 = the smallest possible entry: RateLever's discriminant byte plus its one bool. Every other kind
                // (Command's minimal principal, a Grant/Revoke leaf, ...) is strictly larger.
                var authority = ReadTapeArray(
                    minimumBytesEach: 2,
                    readItem: static (ref WireReader entry) => ReadEntry(reader: ref entry),
                    reader: ref r,
                    what: "authority entry"
                );
                var intents = ReadTapeArray(
                    minimumBytesEach: 60,
                    readItem: static (ref WireReader intent) => WorldWireCodec.ReadIntentSubmission(reader: ref intent),
                    reader: ref r,
                    what: "intent"
                );

                return new WorldReplayTickInput(
                    Authority: authority,
                    Intents: intents
                );
            },
            reader: ref reader,
            what: "tick"
        );
        // 8 = the smallest possible companion: two empty strings' length prefixes and an empty tape block's.
        var companions = ReadTapeArray(
            minimumBytesEach: 8,
            readItem: static (ref WireReader r) => r.ReadBlock(
                field: "companion tape",
                maxBytes: int.MaxValue
            ),
            reader: ref reader,
            what: "companion"
        );

        if (!reader.TryFinish(failure: out var failure)) {
            throw Corrupt(failure: failure);
        }

        // The set is compared BY NAME at re-drive, so two receipts under one name make the pin ambiguous: whichever the
        // comparison happened to reach first would decide, and the other would be silently unenforced.
        for (var index = 0; (index < mountedAddons.Length); index++) {
            if (Find(
                name: mountedAddons[index].Name,
                receipts: new ArraySegment<WorldAddonReceipt>(
                    array: mountedAddons,
                    count: index,
                    offset: 0
                )
            ) is not null) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: addon '{mountedAddons[index].Name}' is pinned twice in the mounted set — a name identifies exactly one mounted guest.");
            }
        }

        for (var index = 0; (index < seats.Length); index++) {
            var slot = seats[index].Slot;

            // An out-of-range slot indexes straight into WorldPopulation's local-seat array during Drive (Join's own
            // range check only refuses the session reply; SetSeatProfile does not check again), so it is refused
            // here, before that reach, rather than crashing the host with an index exception.
            if (((uint)slot) >= WorldBodiesLimits.LocalSeatCount) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: seat slot {slot} is out of range (expected 0..{(WorldBodiesLimits.LocalSeatCount - 1)}).");
            }

            // The set is compared BY SLOT at re-drive (Drive re-joins each recorded slot once), so two seats pinning
            // the same slot make the pin ambiguous — the same ambiguity the mounted-addon duplicate-name guard above
            // refuses for names.
            if (FindSeat(
                seats: new ArraySegment<WorldReplaySeat>(
                    array: seats,
                    count: index,
                    offset: 0
                ),
                slot: slot
            ) is not null) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: seat slot {slot} is pinned twice in the seat set — a slot identifies exactly one seat.");
            }
        }

        // A commit that stood answers every later commit of its handoff token as already committed and lands nothing,
        // so no recording holds a landed arrival's token again.
        var landed = new HashSet<(string Source, ulong TransferId)>();

        foreach (var arrival in ticks.SelectMany(selector: static tick => tick.Authority).OfType<WorldReplayEntry.Arrival>()) {
            if (landed.Contains(item: (arrival.SourceAuthority, arrival.TransferId))) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: transfer {arrival.TransferId} from '{arrival.SourceAuthority}' arrives again after its commit stood.");
            }
            if (!arrival.Outcome.RolledBack) {
                _ = landed.Add(item: (arrival.SourceAuthority, arrival.TransferId));
            }
        }

        // The two lengths are equal BY CONSTRUCTION on the record side (one hash sampled per tick appended), so a file
        // where they disagree is doctored or truncated between the two sections. Reject it here rather than letting the
        // shorter one silently bound the comparison — a trace cut short would otherwise read as "matched everywhere it
        // was checked", which is exactly the shape of a verification that cannot fail.
        if (recordedHashes.Length != ticks.Length) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay recording: {recordedHashes.Length} recorded hashes for {ticks.Length} ticks.");
        }
        if (recordedAuthoritativeHashes.Length != ticks.Length) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay recording: {recordedAuthoritativeHashes.Length} recorded authoritative hashes for {ticks.Length} ticks.");
        }

        // A child carries its copied prefix in its own Ticks, so a provenance claiming more copied ticks than the
        // tape holds is doctored or truncated after the header.
        if (
            (forkedFrom is { } provenance) &&
            (provenance.Tick > ticks.Length)
        ) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay recording: the fork provenance claims {provenance.Tick} tick(s) copied from '{provenance.ParentName}', but the tape carries only {ticks.Length}.");
        }

        if (
            string.IsNullOrEmpty(value: instance) ||
            string.IsNullOrEmpty(value: authority)
        ) {
            throw new InvalidDataException(message: "Corrupt .puckreplay recording: the tape names no instance or no authority.");
        }

        var companionTapes = new WorldReplaySnapshot[companions.Length];

        for (var index = 0; (index < companions.Length); index++) {
            using var companionStream = new MemoryStream(buffer: companions[index], writable: false);

            companionTapes[index] = Read(stream: companionStream);
            if (companionTapes[index].Companions.Count > 0) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: companion '{companionTapes[index].Instance}' carries companions of its own — a set has exactly one level.");
            }
            for (var other = 0; (other < index); other++) {
                if (string.Equals(
                    a: companionTapes[other].Authority,
                    b: companionTapes[index].Authority,
                    comparisonType: StringComparison.Ordinal
                )) {
                    throw new InvalidDataException(message: $"Corrupt .puckreplay recording: authority '{companionTapes[index].Authority}' is taped twice in one set.");
                }
            }
            if (string.Equals(
                a: companionTapes[index].Authority,
                b: authority,
                comparisonType: StringComparison.Ordinal
            )) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: companion authority '{authority}' repeats the tape's own.");
            }
        }

        return new WorldReplaySnapshot {
            Authority = authority,
            Companions = companionTapes,
            DefinitionJson = definitionJson,
            DocumentDirectory = documentDirectory,
            DocumentPath = documentPath,
            ForkedFrom = forkedFrom,
            Instance = instance,
            MountedAddons = mountedAddons,
            PipelineSourceDirectory = pipelineSourceDirectory,
            RecordedAuthoritativeHashes = recordedAuthoritativeHashes,
            RecordedHashes = recordedHashes,
            Seats = seats,
            SimulationRate = simulationRate,
            Ticks = ticks,
        };
    }
    /// <summary>Derives the engine-tick step width <see cref="Drive"/> re-runs each recorded tick at — the one place
    /// <see cref="SimulationRate"/>'s "0 means a static world that never steps" contract and
    /// <c>Puck.Hosting.EngineTicks.PerRate</c>'s "0 has no representable step width" contract meet. Extracted as its
    /// own testable primitive because exercising it through a real <see cref="Drive"/> call requires an embedded
    /// <see cref="WorldDefinition"/> that itself authors <c>simulation.rateHz</c> 0 — not buildable end to end through
    /// the ordinary document pipeline until a separate <c>WorldDefinitionValidator</c> change admits that
    /// value as legitimate authored input — while this logic needs
    /// nothing but the two raw numbers a hand-built tape can supply directly.</summary>
    /// <param name="simulationRate">The tape's own <see cref="SimulationRate"/> header.</param>
    /// <param name="recordedTickCount">The recording's own <see cref="Ticks"/>.Count.</param>
    /// <returns>The step width in engine ticks — <c>0</c> for a legitimate rate-0/zero-tick tape, since a rate-0
    /// recording's own invariant is that its step-loop never runs and the value is therefore never consumed.</returns>
    /// <exception cref="InvalidDataException">Rate 0 with a nonzero recorded tick count — the one shape that is
    /// genuinely inconsistent (see <see cref="ReplayRefusal.RateZeroCarriesTicks"/>): a rate-0 tape's own invariant is
    /// zero recorded ticks, because <c>NoteTick</c> never fires while the boot world never steps.</exception>
    public static ulong ResolveStepWidth(uint simulationRate, int recordedTickCount) {
        // Rate 0 is legitimate tape metadata: a durable stop that never steps. NoteTick never fires while boot never
        // steps, so a rate-0 recording carries zero ticks and the step-loop never runs — deriving a step width
        // unconditionally would turn an honest rate-0 recording into an unnamed exception instead of the named
        // refusal below for the one shape that actually is inconsistent.
        if (
            (simulationRate == 0U) &&
            (recordedTickCount > 0)
        ) {
            throw ReplayRefusal.RateZeroCarriesTicks.Raise(message: $"This .puckreplay recording pins rateHz 0 (a static world with no step width) but carries {recordedTickCount} recorded tick(s) — a rate-0 tape's own invariant is zero recorded ticks; this tape is internally inconsistent, re-record it.");
        }

        return ((simulationRate == 0U)
            ? 0UL
            : EngineTicks.PerRate(ratePerSecond: simulationRate)
        );
    }
    /// <summary>Encodes a recording in the <c>.puckreplay</c> binary form.</summary>
    /// <param name="recording">The recording to encode.</param>
    /// <returns>The complete tape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recording"/> is <see langword="null"/>.</exception>
    /// <exception cref="WorldReplayCodecException">A host-side codec bug: the recording carries a value no wire table
    /// or discriminated encoding covers, or pins one mounted-addon name twice.</exception>
    public static byte[] Encode(WorldReplaySnapshot recording) {
        ArgumentNullException.ThrowIfNull(argument: recording);

        var writer = new WireWriter(capacity: (recording.DefinitionJson.Length + 4096));

        writer.WriteUInt32(value: Magic);
        writer.WriteUInt32(value: ShapeToken);
        // Right after the shape header, before anything else: the rate is simulation INPUT the same way the
        // definition and seats are, and Drive needs it before it can honestly derive a step size.
        writer.WriteUInt32(value: recording.SimulationRate);

        if (
            (recording.ForkedFrom is { } forkedFrom) &&
            (string.IsNullOrWhiteSpace(value: forkedFrom.ParentName) ||
            (forkedFrom.Tick < 0) ||
            (forkedFrom.Tick > recording.Ticks.Count))
        ) {
            throw new WorldReplayCodecException(message: $"a .puckreplay recording's fork provenance is inconsistent (parent '{forkedFrom.ParentName}', {forkedFrom.Tick} copied tick(s) of {recording.Ticks.Count}) — a host bug, not tape data.");
        }

        // (present, parent, int32 tick) — the fork provenance slot, right behind the rate it shares a header with;
        // absent for a tape recorded from boot.
        writer.WriteOptional(
            value: recording.ForkedFrom,
            writeValue: static (w, fork) => {
                w.WriteString(value: fork.ParentName);
                w.WriteInt32(value: fork.Tick);
            }
        );
        writer.WriteNullableString(value: recording.PipelineSourceDirectory);
        writer.WriteString(value: recording.Instance);
        writer.WriteString(value: recording.Authority);
        writer.WriteNullableString(value: recording.DocumentDirectory);
        writer.WriteNullableString(value: recording.DocumentPath);
        writer.WriteArray(
            items: recording.RecordedHashes,
            writeItem: static (w, hash) => w.WriteUInt64(value: hash)
        );
        writer.WriteArray(
            items: recording.RecordedAuthoritativeHashes,
            writeItem: static (w, hash) => w.WriteUInt64(value: hash)
        );
        writer.WriteBlock(value: recording.DefinitionJson);

        // Read refuses a duplicate mounted-addon NAME (a name identifies exactly one mounted guest); the same
        // ambiguity is reachable HERE too, straight from the live server's OWN receipts (WorldReplayTape.StopRecording
        // never validated them). That is the host's own runtime having mounted two instances under one name — a host
        // bug, not untrusted tape bytes — hence WorldReplayCodecException.
        for (var index = 0; (index < recording.MountedAddons.Count); index++) {
            for (var other = (index + 1); (other < recording.MountedAddons.Count); other++) {
                if (string.Equals(
                    a: recording.MountedAddons[index].Name,
                    b: recording.MountedAddons[other].Name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    throw new WorldReplayCodecException(message: $"a .puckreplay recording's mounted-addon set pins '{recording.MountedAddons[index].Name}' twice — the live runtime mounted two instances under the same name, a host bug, not tape data.");
                }
            }
        }

        // Immediately after the definition and before the seats: the definition says which addons a world DECLARES, the
        // receipt set says which ones actually mounted and from which bytes. The second is the one a re-drive is pinned
        // against, and it reads next to the document it qualifies.
        writer.WriteArray(
            items: recording.MountedAddons,
            writeItem: static (w, receipt) => {
                w.WriteString(value: receipt.Name);
                w.WriteString(value: receipt.Hash);
                w.WriteUInt64(value: receipt.Fuel);
            }
        );
        // The seat's profile pin rides a presence bit: a profileless seat writes the bit and nothing else, and its
        // body falls back to the seat kit's own tuning on the re-drive exactly as it did live. The two rates cross as
        // their RAW fixed-point lanes — the simulation's own currency, never a float — so a recorded rate re-enters
        // WorldBody.Advance bit-identical.
        writer.WriteArray(
            items: recording.Seats,
            writeItem: static (w, seat) => {
                w.WriteInt32(value: seat.Slot);
                WorldIdentityProjectionWire.WriteOptional(
                    projection: seat.Profile,
                    writer: w
                );
            }
        );
        writer.WriteArray(
            items: recording.Ticks,
            writeItem: static (w, input) => {
                w.WriteArray(
                    items: input.Authority,
                    writeItem: WriteEntry
                );
                w.WriteArray(
                    items: input.Intents,
                    writeItem: static (intentWriter, intent) => {
                        if (!WorldWireCodec.TryWriteIntentSubmission(
                            submission: in intent,
                            writer: intentWriter
                        )) {
                            throw new WorldReplayCodecException(message: $"no .puckreplay wire value for {nameof(PrincipalKind)}.{intent.Principal.Kind} — the world's own program never rides the tape as an actor.");
                        }
                    }
                );
            }
        );
        writer.WriteArray(
            items: recording.Companions,
            writeItem: static (w, companion) => {
                if (companion.Companions.Count > 0) {
                    throw new WorldReplayCodecException(message: $"companion tape '{companion.Instance}' carries companions of its own — a set has exactly one level, a host bug, not tape data.");
                }
                w.WriteBlock(value: Encode(recording: companion));
            }
        );

        return writer.ToArray();
    }
    /// <summary>Serializes a recording to a stream in the <c>.puckreplay</c> binary form: the whole tape is encoded
    /// first (<see cref="Encode"/>), then written in one call.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="recording">The recording to write.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="WorldReplayCodecException">A host-side codec bug (see <see cref="Encode"/>).</exception>
    public static void Write(Stream stream, WorldReplaySnapshot recording) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        stream.Write(buffer: Encode(recording: recording));
    }
    /// <summary>Serializes a recording to <paramref name="path"/> in one write: the whole tape is encoded to memory
    /// first (<see cref="Encode"/>, where every write-side throw can still fire), and only a complete buffer ever
    /// reaches the destination file, via one <see cref="File.WriteAllBytes(string, byte[])"/> call. A throw during
    /// encoding therefore never truncates or creates a partial file on disk — the destination is untouched until the
    /// whole tape is ready (a guard against a codec throw, not against the disk failing mid-write).</summary>
    /// <param name="path">The destination file path.</param>
    /// <param name="recording">The recording to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recording"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="WorldReplayCodecException">A host-side codec bug (see <see cref="Encode"/>).</exception>
    public static void WriteFile(string path, WorldReplaySnapshot recording) {
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        File.WriteAllBytes(
            bytes: Encode(recording: recording),
            path: path
        );
    }

    private static InvalidDataException Corrupt(WireFailure failure) => new(message: $"Corrupt .puckreplay recording: {failure}.");

    // The one shape every fixed leaf codec's TryDecodeX follows: a span of bytes decodes to a T or names a
    // WorldCodecFailure. `value is null` is reachable only for the reference-typed leaves (WorldCommand,
    // WorldComposition, WorldMutation, WorldQuery, SessionRequest, WorldScreenOp) — a defensive check against a codec
    // that reports success with no value, always false for the struct-typed leaves (WorldDesignation, WorldGrant).
    private delegate bool TryDecodeLeaf<T>(ReadOnlySpan<byte> bytes, out T? value, out WorldCodecFailure failure);
    // The write-side twin of ReadLeaf: every fixed leaf codec's TryEncodeX turns a T into bytes or names a
    // WorldCodecFailure.
    private delegate bool TryEncodeLeaf<T>(T value, out byte[] bytes, out WorldCodecFailure failure);
}
