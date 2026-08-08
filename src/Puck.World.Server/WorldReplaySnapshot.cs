using System.Globalization;
using System.Text;
using Puck.Abstractions.Machines;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The profile a seat was seated on at record-start: its catalog NAME, plus the locomotion rates the recorded
/// run actually integrated with. The rates are pinned because they are simulation INPUT — <c>WorldBody.Advance</c> reads
/// them off the seated handle every frame — and they are pinned as the simulation's own <see cref="FixedQ4816"/> values,
/// so a re-drive consumes the recorded number rather than one re-derived from a float. Nothing about the profile that
/// only PRESENTATION reads is here: not the color, and not <c>InvertLookX</c>, which the CLIENT applies at intent
/// production, upstream of the link, so a recorded intent already carries it.</summary>
/// <param name="Name">The profile the seat was seated on.</param>
/// <param name="MoveSpeed">The pinned locomotion rate (<see cref="WorldIdentity.FixedMoveSpeed"/> as recorded).</param>
/// <param name="TurnSpeed">The pinned angular rate (<see cref="WorldIdentity.FixedTurnSpeed"/> as recorded).</param>
internal readonly record struct WorldReplayProfilePin(string Name, FixedQ4816 MoveSpeed, FixedQ4816 TurnSpeed);

/// <summary>One local seat active at record-start — the seat slice of the captured starting state, re-joined into the
/// replay's fresh world so its body exists to receive the recorded intent stream.</summary>
/// <param name="Slot">The 0-based seat slot.</param>
/// <param name="Profile">The seat's pinned profile, or <see langword="null"/> for a profileless seat. ONE nullable
/// carries both the name and the rates deliberately: they are present or absent together, so there is no shape where a
/// seat has a name but no pinned rates for a reader to have to rule on.</param>
internal readonly record struct WorldReplaySeat(int Slot, WorldReplayProfilePin? Profile);

/// <summary>One captured authority input — the closed, DISCRIMINATED set of synchronous writes that cross
/// <see cref="IServerLink"/> inside a tick's command-apply window. One ordered stream rather than a list per kind,
/// because the live order between a driving command and a grant change is stdin FIFO and position-within-tick is the
/// coordinate every verdict in this campaign is pinned against: a grant that lands before a command in the live session
/// must land before it in the replay, and parallel per-kind lists have no relative order to preserve.</summary>
internal abstract record WorldReplayEntry {
    /// <summary>An authority command applied to one body (the <c>player.*</c> drive verbs).</summary>
    /// <param name="Value">The command, carrying its own acting principal and target entity.</param>
    internal sealed record Command(WorldCommand Value) : WorldReplayEntry;

    /// <summary>A grant acquisition (<c>world.grant</c>) — the authority a later command or a guest's act is checked
    /// against, so a replay that skipped it would re-drive a differently-authorized world.</summary>
    /// <param name="Value">The grant row acquired.</param>
    /// <param name="Actor">The principal that ASKED for it — distinct from the grant's own receiving principal, and the
    /// identity the administration check runs against.</param>
    internal sealed record Grant(WorldGrant Value, WorldPrincipal Actor) : WorldReplayEntry;

    /// <summary>A revocation (<c>world.revoke</c>). <see cref="WorldGrant.Exclusive"/> is ignored by the revoke path but
    /// is carried verbatim, because the tape records what was submitted, never a normalization of it.</summary>
    /// <param name="Value">The grant row (capability + subject) revoked.</param>
    /// <param name="Actor">The principal that asked for the revocation.</param>
    internal sealed record Revoke(WorldGrant Value, WorldPrincipal Actor) : WorldReplayEntry;

    /// <summary>A session request re-executed through the authoritative session door.</summary>
    /// <param name="Value">The canonical request submitted by the live client.</param>
    internal sealed record Session(SessionRequest Value) : WorldReplayEntry;

    /// <summary>A target-register designation and its acting principal.</summary>
    internal sealed record Designation(WorldDesignation Value, WorldPrincipal Actor) : WorldReplayEntry;

    /// <summary>A server-authored peer admission, emitted at the point of effect.</summary>
    /// <param name="Value">The ordered admission event.</param>
    internal sealed record PeerAdmitted(WorldServerEvent.PeerAdmitted Value) : WorldReplayEntry;

    /// <summary>A server-authored peer disconnect, emitted at the point of effect.</summary>
    /// <param name="Value">The ordered disconnect event.</param>
    internal sealed record PeerDisconnected(WorldServerEvent.PeerDisconnected Value) : WorldReplayEntry;

    /// <summary>A live addon-runtime lifecycle change (<c>world.addon.mount</c>/<c>world.addon.unmount</c>) — P5:
    /// lifecycle joins the ordered domain, so a replay re-executes a recorded mount/unmount through the SAME
    /// <c>Server.WorldServer.EnqueueAddonLifecycle</c> door the live session used, rather than the tape carrying no
    /// record of it at all (the taint-bitset posture this replaces).</summary>
    /// <param name="Value">The mount/unmount action.</param>
    /// <param name="Actor">The principal that submitted it.</param>
    internal sealed record AddonLifecycle(WorldAddonLifecycle Value, WorldPrincipal Actor) : WorldReplayEntry;

    /// <summary>A whole-document rebuild-and-swap (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>) —
    /// CAS-pinned: <see cref="ContentHash"/> is the canonical <c>sha256-64/{hex}</c> pin of the exact bytes the live
    /// session consumed (Load/Reload, off disk) or of the base's canonical bytes at the moment the rebuild applied
    /// (Reset). Deliberately carries NO document: <see cref="WorldReplaySnapshot.Drive"/> re-reads
    /// <see cref="PathHint"/> fresh for Load/Reload and re-reads its own live base for Reset, so a re-drive proves the
    /// pinned content still matches rather than trusting a stored copy — the content-address proof the negative
    /// control (editing a byte of the file on disk) exercises.</summary>
    /// <param name="Kind">Which of the three document sources this rebuild came from.</param>
    /// <param name="PathHint">The origin path for Load/Reload; <see langword="null"/> for Reset.</param>
    /// <param name="Force">Load's dirty-journal override, carried verbatim — the tape records what was submitted,
    /// never a normalization of it (the same convention <see cref="Revoke"/> follows for <c>Exclusive</c>).</param>
    /// <param name="ContentHash">The CAS pin a re-drive refuses BY NAME against, on mismatch.</param>
    /// <param name="Actor">The principal that submitted the rebuild.</param>
    internal sealed record Rebuild(WorldRebuildKind Kind, string? PathHint, bool Force, string ContentHash, WorldPrincipal Actor) : WorldReplayEntry;

    /// <summary>A live screen-machine lifecycle change (<c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/
    /// <c>.options</c>/<c>.link</c>/<c>.unlink</c>) — the authoritative-machines campaign (2026-08-03): screen ops
    /// join the ordered domain and the tape as their own authority entry kind, applying SYNCHRONOUSLY on re-drive
    /// exactly as they do live (see <see cref="Server.WorldServer.ApplyScreenOp"/>).</summary>
    /// <param name="Value">The screen op.</param>
    /// <param name="ContentHash">The CAS pin (a real <c>sha256-64</c> hash, or
    /// <see cref="Server.WorldMachineHost.ContentAbsentSignature"/>) a recorded <see cref="WorldScreenOp.Insert"/> or
    /// machine-booting <see cref="WorldScreenOp.Select"/> entry carries (Select shares Insert's own CAS pin — a
    /// magazine entry's document-declared path is not immune to on-disk drift either) — <see langword="null"/> for
    /// every other op kind, and this rides the tape REGARDLESS of whether the op succeeded (a failed insert/select
    /// still pins whatever it read, or the absence sentinel when it could not read anything at all, INCLUDING an
    /// engine-resolution failure, since content is signed before engine resolution is even attempted and is never
    /// left null on that path).</param>
    /// <param name="Actor">The principal that submitted the op.</param>
    internal sealed record ScreenOp(WorldScreenOp Value, string? ContentHash, WorldPrincipal Actor) : WorldReplayEntry;
}

/// <summary>One recorded tick's server-facing input — the exact <see cref="IServerLink"/> traffic the live session
/// applied that tick, captured at the loopback: the synchronous <see cref="Authority"/> stream (commands, grants, and
/// revokes, applied before the step exactly as the live command-apply window does) and the buffered per-entity
/// <see cref="Intents"/> (drained at the step). Re-applying these to a fresh world in the same order reproduces the tick.
/// <para>The tape carries the HUMAN/AUTHORITY stream only. A mounted addon's driving never crosses
/// <see cref="IServerLink"/> — it applies inside <c>WorldServer.Step</c> — so guest-driven motion is not recorded here
/// and is instead RE-DERIVED by re-running the same pinned guests in <see cref="WorldReplaySnapshot.Drive"/>, which is
/// what makes it reproducible rather than merely replayed. That re-run is exactly why the grant changes belong in this
/// stream: a re-run guest is checked against the replayed world's OWN grant table, so a tape that recorded the commands
/// but not the grants would re-drive a guest that holds nothing and never moves.</para></summary>
/// <param name="Authority">The synchronous authority inputs applied this tick — commands, grants, revokes, and sessions
/// interleaved in submission order.</param>
/// <param name="Intents">The per-entity intent submissions buffered this tick (seat driving), in submission order.</param>
internal readonly record struct WorldReplayTickInput(IReadOnlyList<WorldReplayEntry> Authority, IReadOnlyList<IntentSubmission> Intents);

/// <summary>
/// A deterministic world-state recording: the SERVER starting state captured at record-start plus the per-tick
/// server-input stream that drove the recorded span, so the recording replays through a fresh world. The starting state
/// is the record-start <see cref="WorldDefinition"/> (embedded as its canonical JSON) and the active seats; the fresh
/// world's starting body state is that definition's deterministic BOOT IMAGE (a fresh <see cref="WorldServer"/>
/// reconstructs it exactly), not a per-body pose snapshot. The recording also carries the LIVE session's per-tick pose
/// hash trace (<see cref="RecordedHashes"/>) so a replay's fresh re-drive is verified against the actual running
/// session, tick by tick rather than only at the tail.
/// </summary>
/// <remarks>
/// <para>THE SEAT'S PROFILE RATES ARE PINNED, NOT RE-RESOLVED. A seated profile's MoveSpeed/TurnSpeed are read live off
/// the handle by <c>WorldBody.Advance</c> every frame, which makes them simulation INPUT — and they reach the catalog
/// through <c>SetPlayerSection</c>, which never crosses the <see cref="WorldCommand"/>/grant/revoke union the tick
/// stream records, so an edit to them is structurally invisible to that stream. Each <see cref="WorldReplaySeat"/>
/// therefore carries the rates its profile actually ran at (<see cref="WorldReplayProfilePin"/>, in raw fixed-point),
/// and <see cref="Drive"/> seats its bodies on those rather than on whatever the live catalog now holds. That makes a
/// re-drive hermetic with respect to the catalog: an <c>identity.motion</c> between record and verify no longer moves the
/// replayed trajectory. When the live values HAVE moved, <see cref="Drive"/> says so on stderr — naming the profile,
/// the field, and both values — because a pin that silently disagreed with the running world would trade one
/// unattributable verdict for another.</para>
/// <para>HONEST SCOPE. The captured state is the authoritative SERVER simulation only — the world definition, the active
/// seats, and the per-tick stream of HUMAN/AUTHORITY inputs (commands, grants, revokes) and intents. A mounted addon's
/// driving is deliberately absent from that stream (it never crosses <see cref="IServerLink"/>) and is RE-DERIVED by
/// re-running the document's own pinned guests during <see cref="Drive"/>. Grant changes made BEFORE record-start are
/// likewise absent — they were never submitted during the capture — which is the same mid-session-capture boundary the
/// boot-image start already has, and it reports honestly as a MISMATCH rather than as a false MATCH. Screen machines
/// and their pixels, camera rigs, overlays, and audio are
/// PRESENTATION and are excluded: they are re-derived from the definition by the live client each frame and never feed
/// back into simulation, so a replay reproduces the authoritative population trajectory (the hashed poses) but does not
/// re-run the emulated cabinets or redraw the HUD. Because the fresh world starts from the definition boot image, a
/// replayed tail MATCHES the live tail precisely when the live session was still at that boot image at record-start (a
/// boot-anchored capture); a mid-session capture — the session already moved from boot — faithfully re-drives its stream
/// but from the boot image, so the verify honestly reports MISMATCH. Full per-body record-start rehydration (so a
/// mid-session capture also MATCHes) is the identified next lever.</para>
/// <para>DETERMINISM. The hashed state is fixed-point or an exact integer tick — no wall-clock, no float in the hashed
/// pose. The recorded INTENT currency is likewise fixed-point: a
/// <see cref="PlayerIntent"/> crosses as six raw <see cref="FixedQ4816"/> lanes, so the replay currency is the
/// simulation's own numeric type rather than a conversion of it. (The serialized command stream carries the authored
/// float fields of the recorded <see cref="WorldCommand"/>s verbatim; those are AUTHORED VALUES — the numbers an
/// operator typed — which round-trip bit-exactly through the shared command leaf and quantize deterministically at
/// one apply site each. They never break the guarantee, but they are NOT absent from the on-disk form.) A
/// fresh world built from this recording and driven by the recorded stream produces a bit-identical per-tick pose hash
/// on every run, machine, and backend at a fixed code version. <see cref="Drive"/> is the offline re-drive the
/// replay/verify side runs; the record side samples the LIVE population instead, so a match proves the fresh re-drive
/// reproduces the running session, not merely another re-drive of itself.</para>
/// <para>WIRE FORM. Every enum that reaches this codec crosses as an explicitly declared wire value, mapped by an
/// exhaustive switch in both directions and never by an ordinal cast — including the <see cref="WorldSection"/> ordinal
/// nested inside a section <see cref="GrantSubject"/>'s value lane. The channel vector (<see cref="PlayerIntent"/>) and
/// a channel press's ordinal cross as plain integers instead of a pinned bit set now that <c>ActionLanes</c> has
/// dissolved. A member the set does not
/// cover is refused BY NAME at write; a byte the set does not name is refused loudly at read. The header also carries
/// the MOUNTED ADDON SET as recorded-at-mount receipts (<see cref="MountedAddons"/>): because the re-drive re-runs the
/// document's guests rather than replaying their output, the identity of what mounts is part of what the tape pins, and
/// <see cref="Drive"/> refuses a disagreement before the first tick. There is exactly ONE tape shape: the leading
/// MAGIC is the opaque shape-identity value — re-keyed whenever the shape changes, never incremented — and the
/// ShapeToken that follows it stays pinned at 1 permanently; a file carrying either the wrong magic or the wrong
/// token is refused rather than read tolerantly.</para>
/// </remarks>
internal sealed class WorldReplaySnapshot {
    // THE FIELD THAT CARRIES SHAPE IDENTITY. An OPAQUE VALUE, not a version sequence: re-keyed to a new opaque value
    // whenever the tape's BYTE LAYOUT changes, never incremented as a counter. ShapeToken (below) is pinned at 1
    // permanently under the everything-stays-v1 owner ruling and so cannot, by itself, distinguish an older-shape file
    // from a current one — which is exactly the collision each prior value hit: fourteen pre-unit-5 tapes on disk
    // carry "PKRP" with ShapeToken == 1, in an OLDER byte layout, the SAME token this build also writes. Re-keying
    // MAGIC — never ShapeToken — is what refuses those loudly instead of misparsing them (garbage counts, a wrong
    // refusal wording, or a false MISMATCH). RETIREMENT CHAIN (each value opaque, never a sequence — read as "the
    // Nth shape", not "newer than the last"; PKRM sorting BEFORE PKRW is the chain demonstrating its own point):
    // PKRP (pre-unit-5) → PKRT (unit 5, the grants-recording fix) → PKRW (the grant row's Budget field) → PKRM (the
    // seat row's pinned profile rates) → PKRC (the channel-model dissolution: PlayerIntent widened from six named
    // fields to the full channel vector, and PressLane's lane byte became a channel ordinal) → PKRV (the pose hash
    // widening to the FULL orientation: HashState folds all four raw FixedQuaternion lanes beside the yaw scalar, so
    // every RecordedHashes entry a PKRC tape carries means something this build no longer computes) → PKRJ (the
    // grant row's co-driving payload: WriteGrant/ReadGrant dropped Reach/Consent and Ceiling entirely, so a
    // re-drive reconstructed Drive authority carrying no channel reach and no pool — the addon's contribution was
    // dropped, the trajectories diverged, and replay.verify reported a MISMATCH the simulation never earned) → PKRE
    // (this change, the engagement dissolution: WriteCommand/ReadCommand gained the Engage/Disengage discriminants
    // 8/9 — a PKRJ tape's command union cannot represent either kind, so a session that engaged or disengaged a
    // screen would silently re-drive with the engagement command simply missing from the stream, reproducing a
    // DIFFERENT trajectory than the one recorded) → PKRL (P4-lean: command/grant/revoke bodies moved to the shared
    // leaf codecs, grants gained the verb mask (KindMask today), Peer principals gained Generation, and peer lifecycle events joined the
    // ordered tape stream) → PKRM (P5: addon lifecycle — world.addon.mount/.unmount — joined the ordered submission
    // domain as its own leaf codec and authority-entry kind (WorldReplayEntry.AddonLifecycle, discriminant 5); a
    // PKRL tape's authority union cannot represent it, so a session spanning a live mount/unmount would silently
    // re-drive with the lifecycle change missing) → PKRP (context routes, merged with P5 in the same landing wave:
    // WorldCommand.Engage's ScreenIndex became a GrantSubject Target union — screen OR body — plus a Capture bool;
    // a PKRM tape's Engage leaf cannot represent either field, so a session that engaged/possessed anything would
    // silently re-drive with a garbled or truncated command. The lanes re-keyed independently to PKRM and PKRN; the
    // merged format holds BOTH changes, so it takes a value distinct from every intermediate) → PKRQ (CAS-REPLAY: the
    // rebuild trio — world.reset/world.load/world.reload — joins the ordered domain and the tape as its own authority
    // entry kind (WorldReplayEntry.Rebuild, discriminant 6), CAS-pinned by a sha256-64 content hash rather than
    // carrying a document; a PKRP tape's authority union cannot represent it, so a session spanning a live rebuild
    // would silently re-drive with the rebuild missing — and the trio's prior REFUSE-while-armed posture is gone with
    // it, since a rebuild is now ordered, hash-pinned tape data like any other authority entry) → PKRG (the
    // blank-slate campaign's senses lane: GrantSubjectKind gained Region/Seat (wire values 6/7, the world-events
    // families' subjects) and WorldGrant gained EventBudget, a new optional field WriteGrant/ReadGrant serialize
    // after the verb mask — a PKRQ tape's grant leaf cannot represent either, so a session that granted/revoked an
    // event-bearing subject would silently re-drive with the row missing or truncated) → PKRX (the authoritative-
    // machines campaign, 2026-08-03: screen ops — screen.insert/.eject/.select/.options/.link/.unlink — join the
    // ordered domain and the tape as their own authority entry kind, WorldReplayEntry.ScreenOp, discriminant 7,
    // CAS-pinned by an optional sha256-64 content hash exactly like Rebuild; a PKRG tape's authority union cannot
    // represent it, so a session spanning a live screen op would silently re-drive with the machine-lifecycle
    // change missing — and machine stepping itself moved from presentation-side WorldScreenBinder.AdvanceMachines
    // into WorldServer.Step, so a re-drive now boots and steps real machines through the SAME WorldMachineHost the
    // live session ran, off the SAME per-tick WorldEngagement.BuildPadSnapshot() fold) → PKRJ (the SnapPose command
    // collapse changed the shared command leaf, reusing the earlier retired PKRJ value) → PKRY (session requests
    // joined the authority union as discriminant 8, and the record-start player document joined the header so offline
    // profile edits use a detached catalog) → PKRZ (IntentSubmission gained its measured input-hold tick count) →
    // PKRU (designation joined the authority union as discriminant 9).
    // Each magic was checked
    // against committed history; each is opaque, never the previous value incremented.
    // A SEMANTIC break re-keys exactly as a layout break does, and for the same reason: at PKRV the byte OFFSETS were
    // untouched, so a PKRC tape would decode cleanly and then mismatch at tick 0 — a verdict blaming the simulation
    // for a hash-definition change. "PKRS" is also spent — it belonged to the retired SnapshotRecording codec, named
    // in PKRP's own comment — so the picked value clears eight, not six. The next shape break re-keys this constant
    // again to another opaque value, picked against the COMMITTED history of every prior value, never merely against
    // the last one seen, for the identical reason, never by incrementing this one. (Picking against COMMITTED history
    // is also what keeps two in-flight re-keys in sibling lanes from colliding on the same value.)
    //
    // THIS IS ONE OF TWO INDEPENDENT RE-KEY BOUNDARIES, never one key covering both. The other is the guest ABI's
    // artifact pins (Puck.Scripting.AddonAbi: regenerate the module, move the moduleHash pins). The coupling runs one
    // way: MountedAddons below records what actually mounted, so an ABI break invalidates every existing tape through
    // receipt mismatch without touching a byte offset here — but a tape shape change does NOT re-key the ABI. Move
    // only one of the two and a stale artifact passes its own door and fails at the other's. One key for both would
    // also make an unrelated guest rebuild silently invalidate every tape on disk.

    private const uint Magic = 0x504B_5242u; // "PKRB" — puck replay tape; the grant leaf carries two typed masks.
    // A SHAPE-IDENTITY TOKEN, not a version sequence, and it stays 1 permanently — see Magic's remarks above for why a
    // same-token collision across an incompatible layout is possible, and how it is actually caught (by re-keying
    // Magic, never this token). This build writes and reads exactly ONE tape shape; there is no older shape to be
    // newer than and no consumer to negotiate with, so counting up would record a history nobody can act on. A token
    // that is anything but this refuses the file loudly, naming found and expected, which is the whole job: a foreign
    // or garbage file fails as unreadable instead of decoding as nonsense.
    //
    // A format change under this posture changes the SHAPE and re-keys Magic (never this token) to signal it. Stale
    // local tapes are then deleted and re-recorded — they are owed nothing (greenfield, zero consumers) — though a
    // session that does not own a given tape must not delete it out from under another session; the loud refusal is
    // what tells that session to re-record its own. What catches a same-MAGIC shape drift, once Magic itself agrees,
    // is the rest of the surface: the untrusted-length guards every count passes through, the pinned wire sets below
    // (a byte no set names is refused by name), the mount pin, and replay.verify's own hash comparison, which is the
    // honest end of it — a tape that decodes but means something else does not match.
    private const uint ShapeToken = 1u;

    /// <summary>Gets the record-start world definition as its canonical UTF-8 JSON — the rehydrated starting state.</summary>
    public required byte[] DefinitionJson { get; init; }

    /// <summary>Gets the guests MOUNTED at record-start, in mount order — the recorded-at-mount receipts (name, module
    /// content hash, fuel, lane) the re-drive re-establishes before it runs a tick. Empty when the recorded session
    /// mounted nothing, which is itself pinned: a re-drive that mounts a guest against an empty set is refused.</summary>
    public required IReadOnlyList<WorldAddonReceipt> MountedAddons { get; init; }

    /// <summary>Gets the seats active at record-start, re-joined into the fresh world before the stream replays — each
    /// carrying its profile's pinned locomotion rates, which <see cref="Drive"/> seats the body on in place of the live
    /// catalog's current ones.</summary>
    public required IReadOnlyList<WorldReplaySeat> Seats { get; init; }

    /// <summary>Gets the per-tick server-input stream, in tick order from the recording's first tick.</summary>
    public required IReadOnlyList<WorldReplayTickInput> Ticks { get; init; }

    /// <summary>Gets the LIVE session's per-tick pose hash trace — one entry per recorded tick, sampled off the live
    /// population after each tick's server step, so the last entry is the state the running world actually reached. A
    /// replay recomputes the whole trace by re-driving this recording through a fresh world (<see cref="Drive"/>) and
    /// compares against this one, so a match is a genuine live-vs-replay fidelity proof rather than a fresh re-drive
    /// compared against another fresh re-drive of the same stream. Its length always equals <see cref="TickCount"/>;
    /// keeping every entry rather than only the tail is what lets a mismatch name the tick it began at.</summary>
    public required ulong[] RecordedHashes { get; init; }

    /// <summary>Gets the LIVE session's tail pose hash — the last entry of <see cref="RecordedHashes"/>, or <c>0</c> when
    /// nothing was recorded.</summary>
    public ulong RecordedTailHash => ((RecordedHashes.Length > 0) ? RecordedHashes[^1] : 0UL);

    /// <summary>Gets the number of recorded ticks.</summary>
    public int TickCount => Ticks.Count;

    /// <summary>Returns the deterministic per-tick state hash: every active body's fixed-point pose — position AND the full 6DOF
    /// attitude — folded in index order, so two runs with identical input produce identical traces regardless of
    /// wall-clock or backend. The hashed scope is the authoritative population pose — the honest boundary of what a
    /// replay reproduces.</summary>
    /// <param name="population">The entity table to hash.</param>
    /// <returns>The state hash.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="population"/> is <see langword="null"/>.</exception>
    public static ulong HashState(WorldPopulation population) {
        ArgumentNullException.ThrowIfNull(argument: population);

        var hash = Fnv1aHash.Create();

        for (var index = 0; (index < population.Capacity); index++) {
            if (!population.IsActive(index: index) || (population.EntryBody(index: index) is not { } body)) {
                continue;
            }

            var position = body.FixedPosition;
            // THE WHOLE ATTITUDE, AS ITS RAW LANES. Folding only an extracted YAW hashed a body's heading and threw its
            // PITCH AND ROLL away, so two free-motion trajectories differing in nothing but those two axes collided —
            // replay.verify printed MATCH for genuinely different 6DOF motion, a verification that could not fail on the
            // axes the free model exists to move. The quaternion is the canonical orientation both models carry
            // (WorldBody.FixedOrientation — a pure yaw rotation while grounded, an arbitrary body attitude while free),
            // and its four FixedQ4816 lanes are folded RAW: never a float, never a re-derived Euler angle, so the hash
            // stays bit-exact and machine-independent, which a decompose-then-hash would not be.
            var orientation = body.FixedOrientation;

            hash.Add(value: (uint)index);
            hash.Add(value: position.X.Value);
            hash.Add(value: position.Y.Value);
            hash.Add(value: position.Z.Value);
            hash.Add(value: orientation.X.Value);
            hash.Add(value: orientation.Y.Value);
            hash.Add(value: orientation.Z.Value);
            hash.Add(value: orientation.W.Value);
            // The heading scalar rides BESIDE the quaternion rather than being replaced by it, because under the
            // GROUNDED model m_yaw is the authoritative number and the quaternion is built FROM it — an edit too small
            // to survive that construction would otherwise go unhashed. Under the free model this lane is that same
            // quaternion's extracted yaw: redundant, and deterministically so (fixed-point throughout), which costs one
            // fold and removes a case split from the honest answer to "what does this hash cover".
            hash.Add(value: body.FixedYaw.Value);
        }

        return hash.Value;
    }

    /// <summary>Rehydrates a FRESH authoritative world from this recording and re-drives the recorded server-input stream
    /// through it, returning the per-tick pose-hash trace — the offline re-drive the replay/verify side runs (the record
    /// side samples the live population instead). A fresh <see cref="WorldServer"/>/<see cref="WorldPopulation"/> is built
    /// from the embedded definition (its boot image is the starting body state), the recorded seats re-join AND are
    /// re-seated on their pinned locomotion rates (so the catalog's current values cannot steer the re-drive), then each
    /// tick's AUTHORITY entries apply in recorded order — commands, grants, revokes, and sessions interleaved exactly
    /// as they were submitted, before the step, as the live command-apply window does — and its intents buffer and
    /// drain at the step.
    /// Exactly the live per-tick order, and re-applying the grant changes is what gives the re-driven world the same
    /// authority table the live one had.
    /// <para><b>The embedded definition's addons re-mount and RE-RUN here.</b> Guest driving never crossed the loopback
    /// and so was never recorded; re-running the same pinned modules (the same content-hash enforcement, from the same
    /// embedded document) is what reproduces it. That is the stronger property, not a weaker one: a replay that replayed
    /// recorded guest output would prove only that the tape was read back, while re-running proves the guests are
    /// themselves deterministic. Mount and disclosure lines print again during a drive; that is the honest cost.</para>
    /// <para><b>The mount pin is checked BEFORE the first tick.</b> The fresh runtime's own receipts are compared,
    /// index by index, against <see cref="MountedAddons"/> — the sequence recorded at record-start — and any
    /// disagreement (an addon this tape pins that did not mount, one that mounted and was never pinned, or a
    /// module-hash or fuel difference) refuses the drive outright, naming the addon and both sides. Without that gate a moved module or a
    /// faulted mount would surface as an ordinary trajectory mismatch at some arbitrary tick, sending the reader into
    /// the simulation for a defect that is in the tree.</para></summary>
    /// <param name="profiles">The profile catalog seats re-resolve their name against, and the drift report reads the
    /// current rates from (read-only here — the re-drive seats bodies on detached pinned handles instead of mutating
    /// this catalog's shared ones).</param>
    /// <param name="engines">The registered screen-machine engines (the SAME DI-collected set the live session ran
    /// under) — the shadow world's own <see cref="WorldMachineHost"/> boots and steps machines off this exactly like
    /// the live one did, so a tape spanning a CAS-pinned <c>screen.insert</c> re-proves the pinned content still
    /// matches (authoritative-machines campaign, 2026-08-03). Disposed at the end of this drive — the shadow
    /// machines exist only for the duration of the re-drive.</param>
    /// <returns>The per-tick state-hash trace, one entry per recorded tick.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profiles"/> or <paramref name="engines"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The guests this recording pins are not the guests the fresh world would
    /// re-run.</exception>
    /// <exception cref="WorldReplayCodecException">A host-side codec bug: an authority-entry kind the re-drive switch
    /// below does not handle, which would silently DROP a recorded input from the re-drive.</exception>
    public ulong[] Drive(WorldOwnedWorlds profiles, IEnumerable<IScreenMachineEngine> engines) {
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: engines);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: DefinitionJson);
        var population = new WorldPopulation(definition: definition);
        using var machines = new WorldMachineHost(screens: definition.Screens, engines: engines);
        // A fresh, unconfigured render envelope reads as "fits" — the replay applies no render-growing edits, and the
        // authoritative simulation never consults GPU capacity, so no probe is needed offline.
        var server = new WorldServer(definition: definition, population: population, profiles: profiles, envelope: new WorldRenderEnvelope(), machines: machines);

        // REPLAY VERIFICATION IS SIDE-EFFECT-FREE (owner ruling, 2026-08-06). A rule's 'save' effect re-derives
        // deterministically like any other rule effect (see WorldServer.FireWorldRuleEffect's Save arm), but its tap
        // is engine I/O — WorldPostBuildWiring's live closure writes the world's own loaded file. Left unwired here,
        // that write is already skipped (WorldServer.SaveEffectTap defaults to null and a null tap is a silent
        // no-op), but leaving the omission implicit means a future shared-wiring helper could accidentally attach
        // the live closure to this shadow server too. Wire an EXPLICIT narration-only tap instead, so the
        // side-effect-free contract is a decision this construction site states, not a side effect of what nobody
        // got around to wiring — and so an operator watching a verify run sees why a save effect produced no file
        // write rather than reading silence as "the rule never fired". No sim state depends on this: the tick after
        // a fired save is bit-identical to a tick without one (see ActionEffect.Save's own remarks), so suppressing
        // the write can never move the pose hash this drive compares.
        server.SaveEffectTap = tick => Console.Error.WriteLine(value: $"[replay: save effect suppressed (tick {tick}) — replay verification is side-effect-free]");

        foreach (var seat in Seats) {
            // Seat(slot) is right here: this rehydrates an ISOLATED, fresh WorldServer offline — there is no
            // PlayerRoster (and so no claim) in this rehydration at all to ask PrincipalOf of.
            _ = server.ApplySession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: seat.Slot), Slot: seat.Slot, IdentityName: seat.Profile?.Name, WireProtocolKey: WorldProtocol.WireProtocolKey));

            if (seat.Profile is not { } pin) {
                continue;
            }

            // THE JOIN ABOVE RESOLVED A LIVE HANDLE, AND ITS RATES ARE FREE TO HAVE MOVED. WorldBody.Advance reads
            // MoveSpeed/TurnSpeed off the seated profile every frame, so they are simulation INPUT — and an
            // identity.motion or a hand edit of player.json between record and verify would otherwise re-drive a
            // different world under
            // the identical recorded stream, reported as a bare MISMATCH that nothing could attribute to the edit.
            // Re-seat on a DETACHED handle carrying the recorded rates instead: a replay reproduces what was recorded,
            // never what the machine currently prefers. The live catalog is only READ here (the drift report below) and
            // is never mutated — every seat sharing a live handle would otherwise be retuned by an offline re-drive.
            ReportProfileDrift(profiles: profiles, pin: pin);
            population.SetSeatProfile(slot: seat.Slot, profile: WorldIdentity.Pinned(name: pin.Name, moveSpeed: pin.MoveSpeed, turnSpeed: pin.TurnSpeed, defaults: definition.PlayerDefaults));
        }

        // Mounted AFTER the seats re-join and after the server's constructor applied the embedded document's grants —
        // the same order the live composition mounts in, so the mount-time disclosure reads the same settled table.
        // Owns a Wasmtime engine, hence the using.
        using var addons = WorldAddonRuntime.Create(definition: definition, server: server);

        // BEFORE the first tick, always. A guest that failed to mount, mounted from moved module bytes, or mounted
        // under a different budget would otherwise re-drive a DIFFERENT world and report as an ordinary
        // trajectory mismatch at some tick — a verdict that sends the reader to the simulation for a defect that is in
        // the tree. The pin is what makes "re-run the guests" a stronger property than "replay their recorded output".
        VerifyMountedAddons(recorded: MountedAddons, fresh: addons.Receipts);

        var stepTicks = EngineTicks.PerRate(ratePerSecond: SimulationRate);
        var hashes = new ulong[Ticks.Count];

        for (var tick = 0; (tick < Ticks.Count); tick++) {
            var input = Ticks[tick];

            // In recorded order, at the same pre-Step position the live command-apply window applied them: a grant that
            // preceded a command live must precede it here, or the command is checked against a table the live one
            // never had.
            foreach (var entry in input.Authority) {
                switch (entry) {
                    case WorldReplayEntry.Command command:
                        server.ApplyCommand(command: command.Value);

                        break;
                    case WorldReplayEntry.Grant grant:
                        server.Grant(grant: grant.Value, actor: grant.Actor);

                        break;
                    case WorldReplayEntry.Revoke revoke:
                        server.Revoke(grant: revoke.Value, actor: revoke.Actor);

                        break;
                    case WorldReplayEntry.Session session:
                        _ = server.ApplySession(request: session.Value);

                        break;
                    case WorldReplayEntry.Designation designation:
                        server.ApplyDesignation(designation: designation.Value, principal: designation.Actor);

                        break;
                    case WorldReplayEntry.PeerAdmitted admitted:
                        server.ApplyServerEvent(serverEvent: admitted.Value);

                        break;
                    case WorldReplayEntry.PeerDisconnected disconnected:
                        server.ApplyServerEvent(serverEvent: disconnected.Value);

                        break;
                    case WorldReplayEntry.AddonLifecycle lifecycle:
                        // Same shape as a recorded mutation: buffer through the ordinary door, drained by THIS
                        // tick's server.Step call below (DrainPendingOps), never applied directly here — a live
                        // mount/unmount never applies synchronously either (see WorldServer.EnqueueAddonLifecycle).
                        server.EnqueueAddonLifecycle(lifecycle: lifecycle.Value, principal: lifecycle.Actor);

                        break;
                    case WorldReplayEntry.Rebuild rebuild:
                        // Deliberately NO Definition: Load/Reload re-read rebuild.PathHint fresh inside
                        // WorldServer.ApplyRebuild (called from DrainPendingOps below), which is the content-address
                        // proof — a stored copy would let a moved file pass unnoticed. expectedContentHash is what
                        // makes this a REPLAY drive rather than a live one: ApplyRebuild refuses BY NAME, before
                        // installing anything, when the resolved candidate's hash disagrees with what was recorded.
                        server.EnqueueRebuild(request: new WorldRebuildRequest(Kind: rebuild.Kind, Definition: null, PathHint: rebuild.PathHint, Force: rebuild.Force), principal: rebuild.Actor, expectedContentHash: rebuild.ContentHash);

                        break;
                    case WorldReplayEntry.ScreenOp screenOp:
                        // Synchronous, like Command/Grant/Revoke above — never buffered — mirroring the live apply
                        // exactly. expectedContentHash is null for every kind but a recorded Insert or a
                        // machine-booting Select (Select shares Insert's own CAS pin); WorldMachineHost.TryBootMachine
                        // refuses BY NAME, before booting anything, when a fresh re-read of the content path disagrees
                        // with it (including an engine-resolution failure, since content is signed before engine
                        // resolution and is never left unpinned on that path) — the negative control an edited/moved
                        // ROM exercises.
                        server.ApplyScreenOp(op: screenOp.Value, principal: screenOp.Actor, expectedContentHash: screenOp.ContentHash);

                        break;
                    default:
                        // A new entry kind that reaches here unhandled would be silently DROPPED from the re-drive,
                        // which is a determinism hole wearing a robustness costume.
                        throw new WorldReplayCodecException(message: $"no .puckreplay re-drive for authority entry kind '{entry.GetType().Name}'.");
                }
            }

            foreach (var intent in input.Intents) {
                var submission = intent;

                server.EnqueueIntent(submission: in submission);
            }

            var context = new FixedStepContext(Tick: (ulong)tick, ElapsedTicks: ((ulong)(tick + 1) * stepTicks), StepTicks: stepTicks);

            server.Step(context: in context);
            hashes[tick] = HashState(population: population);
        }

        return hashes;
    }

    // THE OTHER HALF OF THE PIN. Using the recorded rates makes the re-drive hermetic; saying so out loud is what turns
    // a mystery verdict into a diagnosis. Without this line an operator who edited a profile between record and verify
    // sees a verdict with no way to tell a profile edit from a genuine determinism regression — and now that the pin
    // holds the trajectory steady, the surprising verdict is a MATCH that disagrees with the world they are looking at.
    // A REPORT, NEVER A REFUSAL: a drifted profile is a perfectly replayable recording, so nothing here throws.
    private static void ReportProfileDrift(WorldOwnedWorlds profiles, WorldReplayProfilePin pin) {
        if (profiles.Find(name: pin.Name) is not { } live) {
            // Drift all the way to absent. The re-drive is unaffected — the pinned handle needs no catalog entry — but
            // an operator reading a MATCH for a profile that no longer exists deserves to be told why it still ran.
            Console.Error.WriteLine(value: $"[replay.profile: '{pin.Name}' is pinned by this recording but is no longer in the live catalog; the replay used the pinned rates (move {Describe(rate: pin.MoveSpeed)}, turn {Describe(rate: pin.TurnSpeed)})]");

            return;
        }

        ReportRateDrift(name: pin.Name, field: "move-speed", pinned: pin.MoveSpeed, live: live.FixedMoveSpeed);
        ReportRateDrift(name: pin.Name, field: "turn-speed", pinned: pin.TurnSpeed, live: live.FixedTurnSpeed);
    }

    // Compared on the RAW fixed lane, never on the rendered decimal: a drift too small to show in four places is still
    // a different trajectory, and a comparison that reads the display string would miss exactly those.
    private static void ReportRateDrift(string name, string field, FixedQ4816 pinned, FixedQ4816 live) {
        if (pinned.Value == live.Value) {
            return;
        }

        Console.Error.WriteLine(value: $"[replay.profile: '{name}' {field} drifted since record-start — pinned {Describe(rate: pinned)}, live {Describe(rate: live)}; the replay used the PINNED value, so this verdict reports the recording, not the edit]");
    }

    // The rate as a readable decimal beside its exact raw lane. The decimal is for the operator; the raw is the number
    // the comparison actually ran on, printed so a drift the decimal rounds away is still legible in the report.
    private static string Describe(FixedQ4816 rate) {
        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{(double)rate:0.####} (raw {rate.Value})");
    }

    // THE MOUNT PIN, INDEX-BY-INDEX (Phase-3 plan L6, superseding the prior BY-NAME/reorder-tolerant comparison):
    // mount order is document order (WorldAddonRuntime's own constructor doc), and the boot-anchored replay
    // contract pins the WHOLE receipt sequence a session actually ran under — name, hash, AND fuel, at each
    // POSITION — never merely "the same set of names showed up somewhere". A reorder is a different mount sequence
    // even when every name/hash/fuel triple individually matches something on the other side (an addon-index-keyed
    // completion field, PendingOp.Mutate's sourceAddonIndex among them, means POSITION is load-bearing state now,
    // not a cosmetic ordering the tape could shrug off). Duplicates are refused first and separately, so a
    // collision reports as itself rather than surfacing as a confusing index mismatch two lines later.
    private static void VerifyMountedAddons(IReadOnlyList<WorldAddonReceipt> recorded, IReadOnlyList<WorldAddonReceipt> fresh) {
        // Read refuses a duplicate name in a TAPE'S OWN mounted set (a name identifies exactly one mounted guest), but
        // Drive can also run over an IN-PROCESS recording that never passed through Read at all —
        // WorldReplayTape.StopRecording's post-persist verify hands this method its recording straight from the live
        // server's own receipts — and "fresh" is the just-built offline runtime's OWN receipts, never validated
        // either. A duplicate in EITHER set is refused here before the positional pins below ever compare a name
        // that might not be unique.
        EnsureNoDuplicateAddonNames(receipts: recorded, side: "recorded");
        EnsureNoDuplicateAddonNames(receipts: fresh, side: "fresh");

        if (recorded.Count != fresh.Count) {
            throw ReplayRefusal.PinnedAddonNotMounted.Raise(message: $"This .puckreplay recording pins {recorded.Count} addon(s), but the replay's fresh world mounts {fresh.Count} — the mounted SEQUENCE (not merely the set of names) is part of what a recording pins, because an addon's mount INDEX is load-bearing wire state (the mutation seam's completion fields address a guest by index).");
        }

        for (var index = 0; (index < recorded.Count); index++) {
            var pin = recorded[index];
            var mounted = fresh[index];

            if (!string.Equals(a: mounted.Name, b: pin.Name, comparisonType: StringComparison.Ordinal)) {
                throw ReplayRefusal.PinnedAddonNotMounted.Raise(message: $"This .puckreplay recording pins '{pin.Name}' at mount index {index}, but the replay's fresh world mounts '{mounted.Name}' there instead — a reordered, added, or removed addon changes the sequence even when the same names appear somewhere on both sides.");
            }

            if (!string.Equals(a: mounted.Hash, b: pin.Hash, comparisonType: StringComparison.Ordinal)) {
                throw ReplayRefusal.AddonModuleMismatch.Raise(message: $"Addon '{pin.Name}' module mismatch: this .puckreplay recording was made against {pin.Hash}, the replay would re-run {mounted.Hash} — the tape re-runs its guests, so the module identity is part of what it pins; re-record against the current module.");
            }

            if (mounted.Fuel != pin.Fuel) {
                throw ReplayRefusal.AddonFuelMismatch.Raise(message: $"Addon '{pin.Name}' fuel mismatch: this .puckreplay recording was made at {pin.Fuel} fuel/tick, the replay would re-run at {mounted.Fuel} — a different budget is a different guest execution.");
            }
        }
    }

    private static WorldAddonReceipt? Find(IReadOnlyList<WorldAddonReceipt> receipts, string name) {
        for (var index = 0; (index < receipts.Count); index++) {
            if (string.Equals(a: receipts[index].Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return receipts[index];
            }
        }

        return null;
    }

    private static WorldReplaySeat? FindSeat(IReadOnlyList<WorldReplaySeat> seats, int slot) {
        for (var index = 0; (index < seats.Count); index++) {
            if (seats[index].Slot == slot) {
                return seats[index];
            }
        }

        return null;
    }

    // Shared by VerifyMountedAddons for BOTH sides it compares (recorded and fresh) — see its call site's remarks for
    // why an in-process Drive needs this even though Read already guards its own copy of "recorded". Quadratic in the
    // mounted count deliberately, matching VerifyMountedAddons' own posture: a handful of rows, checked once per drive.
    private static void EnsureNoDuplicateAddonNames(IReadOnlyList<WorldAddonReceipt> receipts, string side) {
        for (var index = 0; (index < receipts.Count); index++) {
            for (var other = (index + 1); (other < receipts.Count); other++) {
                if (string.Equals(a: receipts[index].Name, b: receipts[other].Name, comparisonType: StringComparison.Ordinal)) {
                    throw new InvalidDataException(message: $"This .puckreplay drive's {side} addon set pins '{receipts[index].Name}' twice — a name identifies exactly one mounted guest, the same ambiguity Read refuses on its own copy of the tape.");
                }
            }
        }
    }

    /// <summary>The fixed simulation rate (Hz) the recording assumes — the launcher's own <c>TargetUpdateRate</c>, a
    /// divisor of <see cref="EngineTicks.PerSecond"/>. Both the record and replay drives use it, so the step duration is
    /// identical on each side.</summary>
    public const uint SimulationRate = 240U;

    /// <summary>Serializes a recording to <paramref name="path"/> in ONE write: the whole tape is encoded to an
    /// in-memory buffer first (where every write-side throw in <see cref="Write"/> can still fire — an unmapped enum
    /// member, a duplicate mounted-addon name, any host-side codec bug — see their remarks), and only a COMPLETE
    /// buffer ever reaches the destination file, via one <see cref="File.WriteAllBytes(string, byte[])"/> call. A throw
    /// during encoding therefore never truncates or creates a partial file on disk — the destination is untouched
    /// until the whole tape is ready to go, which is the property that matters (this is not a defense against the disk
    /// itself failing mid-write, only against a codec throw racing an already-opened, already-truncated file handle).</summary>
    /// <param name="path">The destination file path.</param>
    /// <param name="recording">The recording to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recording"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="WorldReplayCodecException">A host-side codec bug: the recording carries a value no pinned
    /// wire set or discriminated encoding covers, or pins one mounted-addon name twice (see <see cref="Write"/>'s
    /// per-site remarks).</exception>
    public static void WriteFile(string path, WorldReplaySnapshot recording) {
        ArgumentNullException.ThrowIfNull(argument: recording);
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        using var buffer = new MemoryStream();

        Write(stream: buffer, recording: recording);

        File.WriteAllBytes(path: path, bytes: buffer.ToArray());
    }

    /// <summary>Serializes a recording to a stream in the <c>.puckreplay</c> binary form.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="recording">The recording to write.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="WorldReplayCodecException">A host-side codec bug: the recording carries a value no pinned
    /// wire set or discriminated encoding covers, or pins one mounted-addon name twice.</exception>
    public static void Write(Stream stream, WorldReplaySnapshot recording) {
        ArgumentNullException.ThrowIfNull(argument: recording);
        ArgumentNullException.ThrowIfNull(argument: stream);

        using var writer = new BinaryWriter(output: stream, encoding: Encoding.UTF8, leaveOpen: true);

        writer.Write(value: Magic);
        writer.Write(value: ShapeToken);
        writer.Write(value: recording.RecordedHashes.Length);

        foreach (var hash in recording.RecordedHashes) {
            writer.Write(value: hash);
        }

        writer.Write(value: recording.DefinitionJson.Length);
        writer.Write(buffer: recording.DefinitionJson);

        // Immediately after the definition and before the seats: the definition says which addons a world DECLARES, the
        // receipt set says which ones actually mounted and from which bytes. The second is the one a re-drive is pinned
        // against, and it reads next to the document it qualifies.
        writer.Write(value: recording.MountedAddons.Count);

        for (var index = 0; (index < recording.MountedAddons.Count); index++) {
            var receipt = recording.MountedAddons[index];

            // Read refuses a duplicate mounted-addon NAME (a name identifies exactly one mounted guest — see Read's
            // matching guard); the same ambiguity is reachable HERE too, straight from the live server's OWN receipts
            // (WorldReplayTape.StopRecording never validated them). This is the host's OWN runtime having mounted two
            // instances under one name — a host bug, not untrusted tape bytes — hence WorldReplayCodecException,
            // matching every other "the codec cannot honestly encode this" throw in this method.
            for (var other = (index + 1); (other < recording.MountedAddons.Count); other++) {
                if (string.Equals(a: receipt.Name, b: recording.MountedAddons[other].Name, comparisonType: StringComparison.Ordinal)) {
                    throw new WorldReplayCodecException(message: $"a .puckreplay recording's mounted-addon set pins '{receipt.Name}' twice — the live runtime mounted two instances under the same name, a host bug, not tape data.");
                }
            }

            writer.Write(value: receipt.Name);
            writer.Write(value: receipt.Hash);
            writer.Write(value: receipt.Fuel);
            WriteAddonLanePlaceholder(writer: writer);
        }

        writer.Write(value: recording.Seats.Count);

        foreach (var seat in recording.Seats) {
            writer.Write(value: seat.Slot);
            WriteProfilePin(writer: writer, pin: seat.Profile);
        }

        writer.Write(value: recording.Ticks.Count);

        foreach (var input in recording.Ticks) {
            writer.Write(value: input.Authority.Count);

            foreach (var entry in input.Authority) {
                WriteEntry(writer: writer, entry: entry);
            }

            writer.Write(value: input.Intents.Count);

            foreach (var intent in input.Intents) {
                WriteIntent(writer: writer, submission: in intent);
            }
        }
    }

    /// <summary>Reads a recording from a stream.</summary>
    /// <param name="stream">The source stream.</param>
    /// <returns>The deserialized recording.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The stream is not a <c>.puckreplay</c> tape, or is an older shape this
    /// build does not read (refused outright — greenfield keeps no read-side tolerance for a foreign shape); is
    /// truncated/corrupt (including a truncated or malformed length-prefixed string, normalized here from the BCL's own
    /// <see cref="EndOfStreamException"/>/<see cref="FormatException"/> so every corruption this reader detects throws
    /// the SAME exception type); carries a value no pinned wire set names; pins one addon name twice; or pins a seat
    /// slot out of range or twice.</exception>
    public static WorldReplaySnapshot Read(Stream stream) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        using var reader = new BinaryReader(input: stream, encoding: Encoding.UTF8, leaveOpen: true);

        try {
            var magic = reader.ReadUInt32();
            var shapeToken = reader.ReadUInt32();

            if ((magic != Magic) || (shapeToken != ShapeToken)) {
                throw ReplayRefusal.ShapeMismatch.Raise(message: $"Not a Puck replay tape, or an older shape this build does not read — re-record it. (found magic 0x{magic:x8}, shape token {shapeToken}; this build reads magic 0x{Magic:x8}, shape token {ShapeToken} only)");
            }

            var hashCount = ReadCount(reader: reader, minimumBytesEach: 8, what: "hash");
            var recordedHashes = new ulong[hashCount];

            for (var index = 0; (index < hashCount); index++) {
                recordedHashes[index] = reader.ReadUInt64();
            }

            var definitionLength = ReadCount(reader: reader, minimumBytesEach: 1, what: "definition");
            var definitionJson = reader.ReadBytes(count: definitionLength);

            if (definitionJson.Length != definitionLength) {
                throw new InvalidDataException(message: "Truncated .puckreplay recording (definition).");
            }

            // 11 = the smallest possible receipt: two 1-byte string length prefixes, the u64 fuel, and the former-lane placeholder byte.
            var addonCount = ReadCount(reader: reader, minimumBytesEach: 11, what: "mounted addon");
            var mountedAddons = new List<WorldAddonReceipt>(capacity: addonCount);

            for (var index = 0; (index < addonCount); index++) {
                var name = reader.ReadString();
                var hash = reader.ReadString();
                var fuel = reader.ReadUInt64();

                ReadAddonLanePlaceholder(reader: reader);

                // The set is compared BY NAME at re-drive, so two receipts under one name make the pin ambiguous: whichever
                // the comparison happened to reach first would decide, and the other would be silently unenforced.
                if (Find(receipts: mountedAddons, name: name) is not null) {
                    throw new InvalidDataException(message: $"Corrupt .puckreplay recording: addon '{name}' is pinned twice in the mounted set — a name identifies exactly one mounted guest.");
                }

                mountedAddons.Add(item: new WorldAddonReceipt(Name: name, Hash: hash, Fuel: fuel));
            }

            var seatCount = ReadCount(reader: reader, minimumBytesEach: 5, what: "seat");
            var seats = new List<WorldReplaySeat>(capacity: seatCount);

            for (var index = 0; (index < seatCount); index++) {
                var slot = reader.ReadInt32();
                var profile = ReadProfilePin(reader: reader);

                // An out-of-range slot indexes straight into WorldPopulation's local-seat array during Drive
                // (Join's own range check only refuses the session reply; SetSeatProfile does not check again),
                // so it is refused here, before that reach, rather than crashing the host with an index exception.
                if ((uint)slot >= WorldPopulation.LocalSeatCount) {
                    throw new InvalidDataException(message: $"Corrupt .puckreplay recording: seat slot {slot} is out of range (expected 0..{WorldPopulation.LocalSeatCount - 1}).");
                }

                // The set is compared BY SLOT at re-drive (Drive re-joins each recorded slot once), so two seats
                // pinning the same slot make the pin ambiguous — the same ambiguity the mounted-addon duplicate-name
                // guard above refuses for names.
                if (FindSeat(seats: seats, slot: slot) is not null) {
                    throw new InvalidDataException(message: $"Corrupt .puckreplay recording: seat slot {slot} is pinned twice in the seat set — a slot identifies exactly one seat.");
                }

                seats.Add(item: new WorldReplaySeat(Slot: slot, Profile: profile));
            }

            var tickCount = ReadCount(reader: reader, minimumBytesEach: 8, what: "tick");
            var ticks = new List<WorldReplayTickInput>(capacity: tickCount);

            for (var index = 0; (index < tickCount); index++) {
                // 12 = the smallest possible entry: the discriminant byte, a Command's minimal principal (kind + index +
                // the absent-name flag), its entity index, and its own kind byte. A Grant/Revoke entry is strictly larger.
                var entryCount = ReadCount(reader: reader, minimumBytesEach: 12, what: "authority entry");
                var authority = new List<WorldReplayEntry>(capacity: entryCount);

                for (var entry = 0; (entry < entryCount); entry++) {
                    authority.Add(item: ReadEntry(reader: reader));
                }

                var intentCount = ReadCount(reader: reader, minimumBytesEach: 60, what: "intent");
                var intents = new List<IntentSubmission>(capacity: intentCount);

                for (var intent = 0; (intent < intentCount); intent++) {
                    intents.Add(item: ReadIntent(reader: reader));
                }

                ticks.Add(item: new WorldReplayTickInput(Authority: authority, Intents: intents));
            }

            // The two lengths are equal BY CONSTRUCTION on the record side (one hash sampled per tick appended), so a file
            // where they disagree is doctored or truncated between the two sections. Reject it here rather than letting the
            // shorter one silently bound the comparison — a trace cut short would otherwise read as "matched everywhere it
            // was checked", which is exactly the shape of a verification that cannot fail.
            if (recordedHashes.Length != ticks.Count) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: {recordedHashes.Length} recorded hashes for {ticks.Count} ticks.");
            }

            return new WorldReplaySnapshot {
                DefinitionJson = definitionJson,
                MountedAddons = mountedAddons,
                RecordedHashes = recordedHashes,
                Seats = seats,
                Ticks = ticks,
            };
        } catch (Exception exception) when (exception is EndOfStreamException or FormatException) {
            // BinaryReader.ReadString throws THESE directly on a truncated or malformed length-prefixed string — never
            // InvalidDataException — so without this normalization they would escape every caller's catch list (the
            // codec's own claim, stated at the class level, is that a corrupt or truncated tape is refused, never
            // crashes the host). Every OTHER corruption path in this reader already throws InvalidDataException by
            // hand (ReadCount, the pinned wire sets, the duplicate-name guards); this is the one BCL-thrown exception
            // shape this codec does not otherwise control, normalized here so the read side's catch list stays short
            // and its no-host-kill claim stays true.
            throw new InvalidDataException(message: $"Corrupt .puckreplay recording (truncated or malformed while reading a length-prefixed field): {exception.Message}", innerException: exception);
        }
    }

    // Every length prefix in a tape is UNTRUSTED — a doctored or truncated file reaches this reader through
    // `replay.verify <name>`, so a count is validated against the bytes actually left in the stream BEFORE it sizes an
    // allocation. Without it a negative count throws ArgumentOutOfRangeException and an absurd one throws
    // OutOfMemoryException, neither of which the verb's catch list covers: the tape kills the host instead of being
    // named and refused.
    private static int ReadCount(BinaryReader reader, int minimumBytesEach, string what) {
        var count = reader.ReadInt32();

        if (count < 0) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay recording ({what} count {count} is negative).");
        }

        var stream = reader.BaseStream;

        if (stream.CanSeek && (((long)count * minimumBytesEach) > (stream.Length - stream.Position))) {
            throw new InvalidDataException(message: $"Truncated .puckreplay recording ({what} count {count} exceeds the bytes remaining).");
        }

        return count;
    }

    private static void WriteIntent(BinaryWriter writer, in IntentSubmission submission) {
        writer.Write(value: submission.Tick);
        writer.Write(value: submission.EntityIndex);
        WriteIntentValue(writer: writer, intent: submission.Intent);
        WritePrincipal(writer: writer, principal: submission.Principal);
        WriteIntentValue(writer: writer, intent: submission.HeldChannels);
        writer.Write(value: submission.MeasuredHoldTicks);
    }

    private static IntentSubmission ReadIntent(BinaryReader reader) {
        var tick = reader.ReadUInt64();
        var entityIndex = reader.ReadInt32();
        var intent = ReadIntentValue(reader: reader);
        var principal = ReadPrincipal(reader: reader);
        var heldChannels = ReadIntentValue(reader: reader);
        var measuredHoldTicks = reader.ReadInt32();

        return new IntentSubmission(Tick: tick, EntityIndex: entityIndex, Intent: intent, Principal: principal, HeldChannels: heldChannels, MeasuredHoldTicks: measuredHoldTicks);
    }

    // The whole channel vector, unconditionally — ChannelLimits.MaxChannels raw Int64 values, one per ordinal. A
    // world declaring fewer channels simply leaves the unused ordinals zero; this codec needs no per-document channel
    // count to decode, because the vector's CAPACITY (not a document's declared count) is what is wire-shaped. The
    // codec never needs the world's channel table at decode time.
    private static void WriteIntentValue(BinaryWriter writer, PlayerIntent intent) {
        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            writer.Write(value: intent[ordinal].Value);
        }
    }

    private static PlayerIntent ReadIntentValue(BinaryReader reader) {
        var channels = default(ChannelValues);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            channels[ordinal] = new FixedQ4816(Value: reader.ReadInt64());
        }

        return new PlayerIntent(Channels: channels);
    }

    private static void WritePrincipal(BinaryWriter writer, WorldPrincipal principal) {
        WritePrincipalKind(writer: writer, kind: principal.Kind);
        writer.Write(value: principal.Index);
        writer.Write(value: principal.Generation);
        WriteNullableString(writer: writer, value: principal.Name);
    }

    private static WorldPrincipal ReadPrincipal(BinaryReader reader) {
        var kind = ReadPrincipalKind(reader: reader);
        var index = reader.ReadInt32();
        var generation = reader.ReadInt32();
        var name = ReadNullableString(reader: reader);

        return new WorldPrincipal(Kind: kind, Index: index, Name: name, Generation: generation);
    }

    // The authority-INPUT tagged union: one discriminant byte, then the entry's own payload. Kept distinct from the
    // command tagged union below — that one discriminates WorldCommand's sealed subtypes, this one discriminates what
    // KIND of authority write crossed the link at all.
    private static void WriteEntry(BinaryWriter writer, WorldReplayEntry entry) {
        switch (entry) {
            case WorldReplayEntry.Command command:
                writer.Write(value: (byte)0);
                WriteCommandLeaf(writer: writer, command: command.Value);

                break;
            case WorldReplayEntry.Grant grant:
                writer.Write(value: (byte)1);
                WriteGrantLeaf(writer: writer, grant: grant.Value, revoke: false);
                WritePrincipal(writer: writer, principal: grant.Actor);

                break;
            case WorldReplayEntry.Revoke revoke:
                writer.Write(value: (byte)2);
                WriteGrantLeaf(writer: writer, grant: revoke.Value, revoke: true);
                WritePrincipal(writer: writer, principal: revoke.Actor);

                break;
            case WorldReplayEntry.Session session:
                writer.Write(value: (byte)8);
                WriteSessionLeaf(writer: writer, request: session.Value);

                break;
            case WorldReplayEntry.Designation designation:
                writer.Write(value: (byte)9);
                WriteDesignationLeaf(writer: writer, designation: designation.Value);
                WritePrincipal(writer: writer, principal: designation.Actor);

                break;
            case WorldReplayEntry.PeerAdmitted admitted:
                writer.Write(value: (byte)3);
                WritePeerEvent(writer: writer, entries: admitted.Value.Entries, grants: admitted.Value.MintedGrants, revoked: false);

                break;
            case WorldReplayEntry.PeerDisconnected disconnected:
                writer.Write(value: (byte)4);
                WritePeerEvent(writer: writer, entries: disconnected.Value.Entries, grants: disconnected.Value.RevokedGrants, revoked: true);

                break;
            case WorldReplayEntry.AddonLifecycle lifecycle:
                writer.Write(value: (byte)5);
                WriteAddonLifecycleLeaf(writer: writer, lifecycle: lifecycle.Value);
                WritePrincipal(writer: writer, principal: lifecycle.Actor);

                break;
            case WorldReplayEntry.Rebuild rebuild:
                writer.Write(value: (byte)6);
                WriteRebuildLeaf(writer: writer, rebuild: rebuild);
                WritePrincipal(writer: writer, principal: rebuild.Actor);

                break;
            case WorldReplayEntry.ScreenOp screenOp:
                writer.Write(value: (byte)7);
                WriteScreenOpLeaf(writer: writer, screenOp: screenOp);
                WritePrincipal(writer: writer, principal: screenOp.Actor);

                break;
            default:
                throw new WorldReplayCodecException(message: $"no .puckreplay encoding for authority entry kind '{entry.GetType().Name}'.");
        }
    }

    private static WorldReplayEntry ReadEntry(BinaryReader reader) {
        var kind = reader.ReadByte();

        return kind switch {
            0 => new WorldReplayEntry.Command(Value: ReadCommandLeaf(reader: reader)),
            1 => new WorldReplayEntry.Grant(Value: ReadGrantLeaf(reader: reader, revoke: false), Actor: ReadPrincipal(reader: reader)),
            2 => new WorldReplayEntry.Revoke(Value: ReadGrantLeaf(reader: reader, revoke: true), Actor: ReadPrincipal(reader: reader)),
            3 => new WorldReplayEntry.PeerAdmitted(Value: ReadPeerAdmitted(reader: reader)),
            4 => new WorldReplayEntry.PeerDisconnected(Value: ReadPeerDisconnected(reader: reader)),
            5 => new WorldReplayEntry.AddonLifecycle(Value: ReadAddonLifecycleLeaf(reader: reader), Actor: ReadPrincipal(reader: reader)),
            6 => ReadRebuildEntry(reader: reader),
            7 => ReadScreenOpEntry(reader: reader),
            8 => new WorldReplayEntry.Session(Value: ReadSessionLeaf(reader: reader)),
            9 => new WorldReplayEntry.Designation(Value: ReadDesignationLeaf(reader: reader), Actor: ReadPrincipal(reader: reader)),
            _ => throw new InvalidDataException(message: $"unknown .puckreplay authority entry discriminant {kind}."),
        };
    }

    private static void WriteSessionLeaf(BinaryWriter writer, SessionRequest request) {
        if (!WorldSubmissionCodec.TryEncodeSession(request: request, bytes: out var bytes, failure: out var failure)) {
            throw new WorldReplayCodecException(message: $"the canonical session leaf refused while writing .puckreplay: {failure}");
        }

        WriteLeafBytes(writer: writer, bytes: bytes);
    }

    private static SessionRequest ReadSessionLeaf(BinaryReader reader) {
        var bytes = ReadLeafBytes(reader: reader, what: "session leaf");

        if (!WorldSubmissionCodec.TryDecodeSession(bytes: bytes, request: out var request, failure: out var failure) || (request is null)) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay session leaf: {failure}");
        }

        return request;
    }

    private static void WriteDesignationLeaf(BinaryWriter writer, WorldDesignation designation) {
        if (!WorldSubmissionCodec.TryEncodeDesignation(designation: designation, bytes: out var bytes, failure: out var failure)) {
            throw new WorldReplayCodecException(message: $"the canonical designation leaf refused while writing .puckreplay: {failure}");
        }

        WriteLeafBytes(writer: writer, bytes: bytes);
    }

    private static WorldDesignation ReadDesignationLeaf(BinaryReader reader) {
        var bytes = ReadLeafBytes(reader: reader, what: "designation leaf");

        if (!WorldSubmissionCodec.TryDecodeDesignation(bytes: bytes, designation: out var designation, failure: out var failure)) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay designation leaf: {failure}");
        }

        return designation;
    }

    // Reuses WorldSubmissionCodec's canonical screen-op leaf directly (the AddonLifecycle leaf's own precedent —
    // unlike Rebuild, a screen op carries no embedded document either way, so there is no shape asymmetry forcing a
    // fork here). The nullable ContentHash and the actor are tape-only metadata riding beside the shared leaf.
    private static void WriteScreenOpLeaf(BinaryWriter writer, WorldReplayEntry.ScreenOp screenOp) {
        if (!WorldSubmissionCodec.TryEncodeScreenOp(screenOp: screenOp.Value, bytes: out var bytes, failure: out var failure)) {
            throw new WorldReplayCodecException(message: $"the canonical screen-op leaf refused while writing .puckreplay: {failure}");
        }

        WriteLeafBytes(writer: writer, bytes: bytes);
        WriteNullableString(writer: writer, value: screenOp.ContentHash);
    }

    private static WorldReplayEntry ReadScreenOpEntry(BinaryReader reader) {
        var bytes = ReadLeafBytes(reader: reader, what: "screen-op leaf");

        if (!WorldSubmissionCodec.TryDecodeScreenOp(bytes: bytes, screenOp: out var screenOp, failure: out var failure) || (screenOp is null)) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay screen-op leaf: {failure}");
        }

        var contentHash = ReadNullableString(reader: reader);
        var actor = ReadPrincipal(reader: reader);

        return new WorldReplayEntry.ScreenOp(Value: screenOp, ContentHash: contentHash, Actor: actor);
    }

    // Deliberately its OWN small leaf, never WorldSubmissionCodec's TryEncodeRebuild/TryDecodeRebuild: that leaf's
    // shape REQUIRES an embedded document for Load/Reload (the ordinary submission needs it to cross the loopback),
    // while the tape must NEVER carry one — Drive re-reads PathHint fresh, which is the content-address proof. This
    // codec's own discriminant set for WorldRebuildKind (below, in Wire) is independent of WorldSubmissionCodec's,
    // matching this file's own doctrine on why two frozen surfaces are never welded together by reuse.
    private static void WriteRebuildLeaf(BinaryWriter writer, WorldReplayEntry.Rebuild rebuild) {
        writer.Write(value: RebuildKindToWire(kind: rebuild.Kind));
        writer.Write(value: rebuild.Force);
        WriteNullableString(writer: writer, value: rebuild.PathHint);
        writer.Write(value: rebuild.ContentHash);
    }

    private static WorldReplayEntry ReadRebuildEntry(BinaryReader reader) {
        var kind = RebuildKindFromWire(reader: reader);
        var force = reader.ReadBoolean();
        var pathHint = ReadNullableString(reader: reader);
        var contentHash = reader.ReadString();
        var actor = ReadPrincipal(reader: reader);

        if (((kind == WorldRebuildKind.Reset) && (pathHint is not null)) || ((kind != WorldRebuildKind.Reset) && (pathHint is null))) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay rebuild entry: kind '{kind}' does not carry the path-hint shape its kind requires (none for Reset, one for Load/Reload).");
        }

        return new WorldReplayEntry.Rebuild(Kind: kind, PathHint: pathHint, Force: force, ContentHash: contentHash, Actor: actor);
    }

    private static byte RebuildKindToWire(WorldRebuildKind kind) => kind switch {
        WorldRebuildKind.Reset => Wire.RebuildKindReset,
        WorldRebuildKind.Load => Wire.RebuildKindLoad,
        WorldRebuildKind.Reload => Wire.RebuildKindReload,
        _ => throw new WorldReplayCodecException(message: $"no .puckreplay wire value for {nameof(WorldRebuildKind)}.{kind} — give the new member one in the pinned wire set."),
    };

    private static WorldRebuildKind RebuildKindFromWire(BinaryReader reader) {
        var wire = reader.ReadByte();

        return wire switch {
            Wire.RebuildKindReset => WorldRebuildKind.Reset,
            Wire.RebuildKindLoad => WorldRebuildKind.Load,
            Wire.RebuildKindReload => WorldRebuildKind.Reload,
            _ => throw new InvalidDataException(message: $"unknown .puckreplay {nameof(WorldRebuildKind)} wire value {wire}."),
        };
    }

    private static void WriteAddonLifecycleLeaf(BinaryWriter writer, WorldAddonLifecycle lifecycle) {
        if (!WorldSubmissionCodec.TryEncodeAddonLifecycle(lifecycle: lifecycle, bytes: out var bytes, failure: out var failure)) {
            throw new WorldReplayCodecException(message: $"the canonical addon-lifecycle leaf refused while writing .puckreplay: {failure}");
        }

        WriteLeafBytes(writer: writer, bytes: bytes);
    }

    private static WorldAddonLifecycle ReadAddonLifecycleLeaf(BinaryReader reader) {
        var bytes = ReadLeafBytes(reader: reader, what: "addon-lifecycle leaf");

        if (!WorldSubmissionCodec.TryDecodeAddonLifecycle(bytes: bytes, lifecycle: out var lifecycle, failure: out var failure) || (lifecycle is null)) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay addon-lifecycle leaf: {failure}");
        }

        return lifecycle;
    }

    private static void WriteCommandLeaf(BinaryWriter writer, WorldCommand command) {
        if (!WorldSubmissionCodec.TryEncodeCommand(command: command, bytes: out var bytes, failure: out var failure)) {
            throw new WorldReplayCodecException(message: $"the canonical command leaf refused while writing .puckreplay: {failure}");
        }

        WriteLeafBytes(writer: writer, bytes: bytes);
    }

    private static WorldCommand ReadCommandLeaf(BinaryReader reader) {
        var bytes = ReadLeafBytes(reader: reader, what: "command leaf");

        if (!WorldSubmissionCodec.TryDecodeCommand(bytes: bytes, command: out var command, failure: out var failure) || (command is null)) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay command leaf: {failure}");
        }

        return command;
    }

    private static void WriteGrantLeaf(BinaryWriter writer, WorldGrant grant, bool revoke) {
        var accepted = (revoke
            ? WorldSubmissionCodec.TryEncodeRevoke(revoke: grant, bytes: out var bytes, failure: out var failure)
            : WorldSubmissionCodec.TryEncodeGrant(grant: grant, bytes: out bytes, failure: out failure));

        if (!accepted) {
            throw new WorldReplayCodecException(message: $"the canonical {(revoke ? "revoke" : "grant")} leaf refused while writing .puckreplay: {failure}");
        }

        WriteLeafBytes(writer: writer, bytes: bytes);
    }

    private static WorldGrant ReadGrantLeaf(BinaryReader reader, bool revoke) {
        var bytes = ReadLeafBytes(reader: reader, what: (revoke ? "revoke leaf" : "grant leaf"));
        var accepted = (revoke
            ? WorldSubmissionCodec.TryDecodeRevoke(bytes: bytes, revoke: out var grant, failure: out var failure)
            : WorldSubmissionCodec.TryDecodeGrant(bytes: bytes, grant: out grant, failure: out failure));

        if (!accepted) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay {(revoke ? "revoke" : "grant")} leaf: {failure}");
        }

        return grant;
    }

    private static void WriteLeafBytes(BinaryWriter writer, byte[] bytes) {
        writer.Write(value: bytes.Length);
        writer.Write(buffer: bytes);
    }

    private static byte[] ReadLeafBytes(BinaryReader reader, string what) {
        var length = ReadCount(reader: reader, minimumBytesEach: 1, what: what);
        var bytes = reader.ReadBytes(count: length);

        if (bytes.Length != length) {
            throw new InvalidDataException(message: $"Truncated .puckreplay recording ({what}).");
        }

        return bytes;
    }

    private static void WritePeerEvent(BinaryWriter writer, IReadOnlyList<WorldPeerEventEntry> entries, IReadOnlyList<WorldGrant> grants, bool revoked) {
        writer.Write(value: entries.Count);

        foreach (var entry in entries) {
            writer.Write(value: entry.BodyIndex);
            writer.Write(value: entry.Generation);
            WriteIntentSource(writer: writer, source: entry.Source);
            WritePrincipal(writer: writer, principal: entry.Identity);
        }

        writer.Write(value: grants.Count);

        foreach (var grant in grants) {
            WriteGrantLeaf(writer: writer, grant: grant, revoke: revoked);
        }
    }

    private static (IReadOnlyList<WorldPeerEventEntry> Entries, IReadOnlyList<WorldGrant> Grants) ReadPeerEvent(BinaryReader reader, bool revoked) {
        var entryCount = ReadCount(reader: reader, minimumBytesEach: 14, what: "peer event entry");
        var entries = new List<WorldPeerEventEntry>(capacity: entryCount);

        for (var index = 0; index < entryCount; index++) {
            var bodyIndex = reader.ReadInt32();
            var generation = reader.ReadInt32();
            var source = ReadIntentSource(reader: reader);
            var identity = ReadPrincipal(reader: reader);

            if ((identity.Kind != PrincipalKind.Peer) || (identity.Index != bodyIndex) || (identity.Generation != generation)) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay peer event entry: identity {identity.Describe()} does not match body {bodyIndex}, generation {generation}.");
            }

            entries.Add(item: new WorldPeerEventEntry(BodyIndex: bodyIndex, Generation: generation, Source: source, Identity: identity));
        }

        var grantCount = ReadCount(reader: reader, minimumBytesEach: 5, what: "peer event grant");
        var grants = new List<WorldGrant>(capacity: grantCount);

        for (var index = 0; index < grantCount; index++) {
            grants.Add(item: ReadGrantLeaf(reader: reader, revoke: revoked));
        }

        return (entries, grants);
    }

    private static WorldServerEvent.PeerAdmitted ReadPeerAdmitted(BinaryReader reader) {
        var value = ReadPeerEvent(reader: reader, revoked: false);

        return new WorldServerEvent.PeerAdmitted(Entries: value.Entries, MintedGrants: value.Grants);
    }

    private static WorldServerEvent.PeerDisconnected ReadPeerDisconnected(BinaryReader reader) {
        var value = ReadPeerEvent(reader: reader, revoked: true);

        return new WorldServerEvent.PeerDisconnected(Entries: value.Entries, RevokedGrants: value.Grants);
    }


    // ---------------------------------------------------------------------------------------------------------------
    // THE PINNED WIRE SETS. Every enum that reaches this codec crosses as a value declared HERE, mapped by an
    // exhaustive switch in both directions — never by a cast. A cast pins whatever ordinals the enum happened to have
    // when the mapping was written: reorder a member, insert one, or delete one, and every saved tape's bytes change
    // MEANING with no line here changing. The precedent is Puck.Scripting.AddonVerdict, which owns its own
    // discriminant for exactly this reason; the sets below are this codec's, never that one's, because two independently
    // frozen surfaces must not be welded together by reuse.
    //
    // Every value is numerically identical to the enum's ordinal TODAY, deliberately — the first mapping is the
    // identity, so this change moves no byte. The point is not the numbers, it is that from here on a divergence
    // between an enum and its wire form is a visible edit in this file rather than a silent reinterpretation.
    //
    // FROZEN WIRE VALUES: changing one invalidates every saved tape. The write side throws by NAME on a member the set
    // does not cover (a new enum member must be given a wire value here, not silently dropped); the read side throws
    // InvalidDataException naming the value it found (a doctored or drifted tape is refused, never decoded as garbage).
    private static class Wire {
        // PrincipalKind
        public const byte PrincipalSeat = 0;
        public const byte PrincipalConsole = 1;
        public const byte PrincipalAddon = 2;
        public const byte PrincipalPeer = 3;

        // IntentSource
        public const byte SourceLive = 0;
        public const byte SourceIdle = 1;
        public const byte SourceProducer = 2;

        // The former AddonLane receipt byte. The lane axis is deleted (owner ruling, 2026-08-02), but the tape SHAPE
        // does not move here — that is the ONE re-key L7 owns — so the receipt writer keeps emitting a constant byte
        // in this slot and the reader validates it as that constant. AddonLaneReceiptConstant reuses the retired
        // Simulation wire value (1): every receipt this build ever wrote carries it already, so old tapes keep
        // verifying unchanged.
        public const byte AddonLaneReceiptConstant = 1;

        // WorldRebuildKind — this codec's OWN discriminant set, independent of WorldSubmissionCodec's identically-
        // numbered one (see WriteRebuildLeaf's remarks on why the two are never welded together).
        public const byte RebuildKindReset = 0;
        public const byte RebuildKindLoad = 1;
        public const byte RebuildKindReload = 2;
    }

    private static void WritePrincipalKind(BinaryWriter writer, PrincipalKind kind) {
        writer.Write(value: kind switch {
            PrincipalKind.Seat => Wire.PrincipalSeat,
            PrincipalKind.Console => Wire.PrincipalConsole,
            PrincipalKind.Addon => Wire.PrincipalAddon,
            PrincipalKind.Peer => Wire.PrincipalPeer,
            _ => throw new WorldReplayCodecException(message: $"no .puckreplay wire value for {nameof(PrincipalKind)}.{kind} — give the new member one in the pinned wire set."),
        });
    }

    private static PrincipalKind ReadPrincipalKind(BinaryReader reader) {
        var wire = reader.ReadByte();

        return wire switch {
            Wire.PrincipalSeat => PrincipalKind.Seat,
            Wire.PrincipalConsole => PrincipalKind.Console,
            Wire.PrincipalAddon => PrincipalKind.Addon,
            Wire.PrincipalPeer => PrincipalKind.Peer,
            _ => throw new InvalidDataException(message: $"unknown .puckreplay {nameof(PrincipalKind)} wire value {wire}."),
        };
    }


    private static void WriteIntentSource(BinaryWriter writer, IntentSource source) {
        if (source.IsLive) {
            writer.Write(value: Wire.SourceLive);
        } else if (source.IsIdle) {
            writer.Write(value: Wire.SourceIdle);
        } else if (source.ProducerName is { } name) {
            writer.Write(value: Wire.SourceProducer);
            writer.Write(value: name);
        } else {
            throw new WorldReplayCodecException(message: $"no .puckreplay wire value for {nameof(IntentSource)} '{source}'.");
        }
    }

    private static IntentSource ReadIntentSource(BinaryReader reader) {
        var wire = reader.ReadByte();

        return wire switch {
            Wire.SourceLive => IntentSource.Live,
            Wire.SourceIdle => IntentSource.Idle,
            Wire.SourceProducer => IntentSource.Producer(name: reader.ReadString()),
            _ => throw new InvalidDataException(message: $"unknown .puckreplay {nameof(IntentSource)} wire value {wire}."),
        };
    }


    // The receipt's former lane byte. TAPE SHAPE MUST NOT MOVE (the one re-key belongs to L7), so this slot keeps
    // emitting and validating a byte even though WorldAddonReceipt no longer carries a Lane to encode.
    private static void WriteAddonLanePlaceholder(BinaryWriter writer) {
        writer.Write(value: Wire.AddonLaneReceiptConstant);
    }

    private static void ReadAddonLanePlaceholder(BinaryReader reader) {
        var wire = reader.ReadByte();

        if (wire != Wire.AddonLaneReceiptConstant) {
            throw new InvalidDataException(message: $"unknown .puckreplay mounted-addon lane-slot wire value {wire} — the lane axis is deleted and this slot is now a pinned constant ({Wire.AddonLaneReceiptConstant}), carried only so the tape shape does not move ahead of its own re-key.");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------

    // The PressLane hold is written as (bool present, float value) so the float slot is always consumed; the value is
    // meaningful only when the present flag is set, else the command carried no explicit hold.
    private static float? ReadNullableSingle(BinaryReader reader) {
        var present = reader.ReadBoolean();
        var value = reader.ReadSingle();

        return (present ? value : null);
    }

    // The seat's profile pin rides the same present-flag convention as WriteNullableString: a profileless seat writes
    // the flag and nothing else, and its body falls back to the seat kit's own tuning on the re-drive exactly as it did
    // live. The two rates cross as their RAW fixed-point lanes — the simulation's own currency, never a float — so a
    // recorded rate re-enters WorldBody.Advance bit-identical.
    private static void WriteProfilePin(BinaryWriter writer, WorldReplayProfilePin? pin) {
        writer.Write(value: (pin is not null));

        if (pin is { } value) {
            writer.Write(value: value.Name);
            writer.Write(value: value.MoveSpeed.Value);
            writer.Write(value: value.TurnSpeed.Value);
        }
    }

    private static WorldReplayProfilePin? ReadProfilePin(BinaryReader reader) {
        if (!reader.ReadBoolean()) {
            return null;
        }

        var name = reader.ReadString();
        var moveSpeed = new FixedQ4816(Value: reader.ReadInt64());
        var turnSpeed = new FixedQ4816(Value: reader.ReadInt64());

        return new WorldReplayProfilePin(Name: name, MoveSpeed: moveSpeed, TurnSpeed: turnSpeed);
    }

    private static void WriteNullableString(BinaryWriter writer, string? value) {
        writer.Write(value: (value is not null));

        if (value is not null) {
            writer.Write(value: value);
        }
    }

    private static string? ReadNullableString(BinaryReader reader) {
        return (reader.ReadBoolean() ? reader.ReadString() : null);
    }
}
