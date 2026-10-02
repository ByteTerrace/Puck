using Puck.Commands;
using System.Text;
using Puck.Abstractions.Machines;
using Puck.Maths;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The profile a seat was seated on at record-start: its catalog name, plus the locomotion rates the recorded
/// run actually integrated with. The rates are pinned because they are simulation input — <c>WorldBody.Advance</c> reads
/// them off the seated handle every frame — and they are pinned as the simulation's own <see cref="FixedQ4816"/> values,
/// so a re-drive consumes the recorded number rather than one re-derived from a float. Nothing about the profile that
/// only presentation reads is here: not the color or portable seat-look preference, which the client applies before intent
/// production, upstream of the link, so a recorded intent already carries it.</summary>
/// <param name="Name">The profile the seat was seated on.</param>
/// <param name="MoveSpeed">The pinned locomotion rate (<see cref="WorldIdentity.FixedMoveSpeed"/> as recorded —
/// <see langword="null"/> pins an identity that claimed no rate, so the re-drive falls back to the kit's rate the
/// same way the live run did).</param>
/// <param name="TurnSpeed">The pinned angular rate (<see cref="WorldIdentity.FixedTurnSpeed"/> as recorded).</param>
public readonly record struct WorldReplayProfilePin(string Name, FixedQ4816? MoveSpeed, FixedQ4816? TurnSpeed);
/// <summary>One local seat active at record-start — the seat slice of the captured starting state, re-joined into the
/// replay's fresh world so its body exists to receive the recorded intent stream.</summary>
/// <param name="Slot">The 0-based seat slot.</param>
/// <param name="Profile">The seat's pinned profile, or <see langword="null"/> for a profileless seat. One nullable
/// carries both the name and the rates deliberately: they are present or absent together, so there is no shape where a
/// seat has a name but no pinned rates for a reader to have to rule on.</param>
public readonly record struct WorldReplaySeat(int Slot, WorldReplayProfilePin? Profile);
/// <summary>One captured authority input — the closed, discriminated set of synchronous writes that cross
/// <see cref="IServerLink"/> inside a tick's command-apply window. One ordered stream rather than a list per kind,
/// because the live order between a driving command and a grant change is stdin FIFO and position-within-tick is the
/// coordinate every verdict is pinned against: a grant that lands before a command in the live session
/// must land before it in the replay, and parallel per-kind lists have no relative order to preserve.</summary>
public abstract record WorldReplayEntry {
    /// <summary>An authority command applied to one body (the <c>player.*</c> drive verbs).</summary>
    /// <param name="Value">The command, carrying its own acting principal and target entity.</param>
    internal sealed record Command(WorldCommand Value) : WorldReplayEntry;
    /// <summary>A grant acquisition (<c>world.grant</c>) — the authority a later command or a guest's act is checked
    /// against, so a replay that skipped it would re-drive a differently-authorized world.</summary>
    /// <param name="Value">The grant row acquired.</param>
    /// <param name="Actor">The principal that asked for it — distinct from the grant's own receiving principal, and the
    /// identity the administration check runs against.</param>
    internal sealed record Grant(WorldGrant Value, Principal Actor) : WorldReplayEntry;
    /// <summary>A revocation (<c>world.revoke</c>). <see cref="WorldGrant.Exclusive"/> is ignored by the revoke path but
    /// is carried verbatim, because the tape records what was submitted, never a normalization of it.</summary>
    /// <param name="Value">The grant row (capability + subject) revoked.</param>
    /// <param name="Actor">The principal that asked for the revocation.</param>
    internal sealed record Revoke(WorldGrant Value, Principal Actor) : WorldReplayEntry;
    /// <summary>A session request re-executed through the authoritative session door.</summary>
    /// <param name="Value">The canonical request submitted by the live client.</param>
    internal sealed record Session(SessionRequest Value) : WorldReplayEntry;
    /// <summary>A target-register designation and its acting principal.</summary>
    internal sealed record Designation(WorldDesignation Value, Principal Actor) : WorldReplayEntry;
    /// <summary>A submitted document mutation and its acting principal — buffered to the tick boundary on re-drive
    /// through the same <c>Server.WorldServer.EnqueueMutation</c> door a live submission uses, so the whole apply
    /// pipeline (admission, compose, whole-document validate, capacity, install, addon prepare, journal)
    /// re-executes rather than a recorded effect being replayed. A mutation the pipeline refuses reproduces as the
    /// identical refusal — proven, not merely hoped for, by <see cref="Outcome"/>.</summary>
    /// <param name="Value">The submitted mutation.</param>
    /// <param name="Actor">The principal that submitted it.</param>
    /// <param name="Outcome">Whether this exact mutation was ACCEPTED live, recorded from
    /// <c>Server.WorldServer.MutationOutcomeTap</c> the same tick it was recorded on. The re-drive's own outcome —
    /// captured through the identical tap wired onto the shadow server — must agree, or the whole re-drive is a
    /// FATAL replay refusal: once acceptance can depend on module bytes on disk (addon preparation), a live-
    /// accepted-but-now-refused or live-refused-but-now-accepted disagreement is a real determinism finding, never
    /// something a later-tick pose comparison alone could ever surface.</param>
    internal sealed record Mutation(WorldMutation Value, Principal Actor, bool Outcome) : WorldReplayEntry;
    /// <summary>A journal undo (<c>world.undo</c>) — buffered to the tick boundary on re-drive exactly as a live
    /// submission is, so the recorded journal tail is replayed back through the same all-or-nothing gates.</summary>
    /// <param name="Count">The number of journal entries to undo.</param>
    /// <param name="Actor">The principal that submitted it.</param>
    internal sealed record Undo(int Count, Principal Actor) : WorldReplayEntry;
    /// <summary>A window-composition override (<c>view.override</c>) and its acting principal — applied
    /// synchronously on re-drive, exactly as it is live.</summary>
    /// <param name="Value">The composition submission.</param>
    /// <param name="Actor">The principal that submitted it.</param>
    internal sealed record Composition(WorldComposition Value, Principal Actor) : WorldReplayEntry;
    /// <summary>A read-back query and the identity the envelope stamped. Re-executed on re-drive at the same
    /// position it held live, so any read-back state its composition touches is reproduced; the answer itself is
    /// discarded, since a query moves no simulation state and therefore cannot alter either replay trace.</summary>
    /// <param name="Value">The query.</param>
    /// <param name="Actor">The identity the envelope stamped.</param>
    internal sealed record Query(WorldQuery Value, Principal Actor) : WorldReplayEntry;
    /// <summary>A server-authored peer admission, emitted at the point of effect.</summary>
    /// <param name="Value">The ordered admission event.</param>
    internal sealed record PeerAdmitted(WorldServerEvent.PeerAdmitted Value) : WorldReplayEntry;
    /// <summary>A server-authored peer disconnect, emitted at the point of effect.</summary>
    /// <param name="Value">The ordered disconnect event.</param>
    internal sealed record PeerDisconnected(WorldServerEvent.PeerDisconnected Value) : WorldReplayEntry;
    /// <summary>A server-authored session lifecycle event (admitted, embodied or ended), emitted at the point of effect.</summary>
    /// <param name="Value">The <see cref="WorldServerEvent.SessionAdmitted"/>, <see cref="WorldServerEvent.SessionEmbodied"/> or <see cref="WorldServerEvent.SessionEnded"/> event.</param>
    internal sealed record SessionEvent(WorldServerEvent Value) : WorldReplayEntry;
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
    /// <param name="ContentHash">The CAS pin a re-drive refuses by name against, on mismatch.</param>
    /// <param name="Actor">The principal that submitted the rebuild.</param>
    internal sealed record Rebuild(WorldRebuildKind Kind, string? PathHint, bool Force, string ContentHash, Principal Actor) : WorldReplayEntry;
    /// <summary>A live screen-machine lifecycle change (<c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/
    /// <c>.options</c>/<c>.link</c>/<c>.unlink</c>) — screen ops join the ordered domain and the tape as their own
    /// authority entry kind, applying synchronously on re-drive exactly as they do live (see
    /// <see cref="Server.WorldServer.ApplyScreenOp"/>).</summary>
    /// <param name="Value">The screen op.</param>
    /// <param name="ContentHash">The CAS pin (a real <c>sha256-64</c> hash, or the concrete host's
    /// content-absent sentinel) a recorded <see cref="WorldScreenOp.Insert"/> or
    /// machine-booting <see cref="WorldScreenOp.Select"/> entry carries (Select shares Insert's own CAS pin — a
    /// magazine entry's document-declared path is not immune to on-disk drift either) — <see langword="null"/> for
    /// every other op kind, and this rides the tape regardless of whether the op succeeded (a failed insert/select
    /// still pins whatever it read, or the absence sentinel when it could not read anything at all, including an
    /// engine-resolution failure, since content is signed before engine resolution is even attempted and is never
    /// left null on that path).</param>
    /// <param name="Actor">The principal that submitted the op.</param>
    internal sealed record ScreenOp(WorldScreenOp Value, string? ContentHash, Principal Actor) : WorldReplayEntry;
    /// <summary>A pause or resume of the boot instance's own live schedule lever (<c>world.rate pause</c>/
    /// <c>resume</c>) — recorded so a saved tape carries a legible history of when a pause/resume happened, alongside
    /// the header's own <see cref="WorldReplaySnapshot.SimulationRate"/> (the initial authored rate). Purely
    /// informational at re-drive: <see cref="WorldReplaySnapshot.Drive"/> takes no action on it, because the tape's
    /// own tick-count invariant already reproduces the stepping effect — a paused span records zero ticks live
    /// (<c>Puck.World.WorldServerStepShell.Step</c> is skipped outright while paused, so its <c>NoteTick</c> call
    /// never fires for that span), so re-driving exactly <c>Ticks.Count</c> steps already reproduces the identical
    /// cadence with no separate pause-state tracking needed on the replay side. Only the boot instance's own lever is
    /// taped — a named instance's own pause/resume is not (the tape's own scope, see
    /// <see cref="Puck.World.WorldReplayTape"/>'s class remarks).</summary>
    /// <param name="Paused"><see langword="true"/> for a pause, <see langword="false"/> for a resume.</param>
    internal sealed record RateLever(bool Paused) : WorldReplayEntry;
    /// <summary>A crossing this authority decided as its source, recorded by
    /// <see cref="Puck.World.WorldReplayTape.NoteTransfer"/> when <c>Puck.World.WorldInstanceHost</c> settles the
    /// transfer. A re-drive acts on its departure: every slot in <see cref="DepartedSlots"/> leaves the shadow
    /// population exactly as it left live, so an inactive index stops contributing to the hash on the same tick. The
    /// arrival is the destination's own fact and rides the destination's tape as a <see cref="Arrival"/>; a set of
    /// tapes pairs the two by <see cref="Target"/> and <see cref="TransferId"/>.
    /// <see cref="Outcome"/>, <see cref="DestinationName"/>, <see cref="ScopeKey"/> and <see cref="GenerationId"/> are
    /// narration. The entry sits partly outside the hash's coverage, so
    /// <see cref="WorldReplaySnapshot.ReadTransferEntry"/> recomputes a content signature over every decoded field and
    /// refuses a disagreement by name (<see cref="ReplayRefusal.TransferEventTampered"/>).</summary>
    /// <param name="TransferId">The transfer id this authority minted for the crossing.</param>
    /// <param name="Target">The destination's authority identity, or empty when the transfer never resolved one.</param>
    /// <param name="TargetRemote">Whether the destination is an authority reached over federation rather than a row
    /// of this process.</param>
    /// <param name="DestinationName">The resolved destinations row name, or empty for a console transfer.</param>
    /// <param name="ScopeKey">The resolved scope key, or empty for a console transfer.</param>
    /// <param name="GenerationId">The resolver-issued generation id, or 0 for a console transfer.</param>
    /// <param name="Outcome">A short canonical outcome summary.</param>
    /// <param name="DepartedSlots">The 0-based body indices this crossing removed from this authority's population;
    /// empty for a refused or aborted transfer.</param>
    internal sealed record Transfer(ulong TransferId, string Target, bool TargetRemote, string DestinationName, string ScopeKey, ulong GenerationId, string Outcome, IReadOnlyList<int> DepartedSlots) : WorldReplayEntry;

    /// <summary>An arrival a commit decided in this authority as a crossing's destination: the reservation, the
    /// assigned body indices and the commit, encoded with the same leaf the authority's crossing log writes
    /// (<see cref="Server.WorldAuthorityCheckpointCodec.EncodeCrossingArrival"/>), and its outcome
    /// (<see cref="WorldServer.ArrivalTap"/>). A traveler's admission, a transferred peer's <c>PeerAdmitted</c>
    /// included, is the landing's own consequence and is not taped beside it. A re-drive lands the arrival again
    /// through the shadow's own escrow (<see cref="Server.WorldTransferEscrow.TryReland"/>) at the commit's position
    /// among the tick's authority entries: each traveler at its recorded body index and generation, and a recorded
    /// rollback at the same traveler, because a landing advances its index's generation, which outlives the rollback.
    /// It refuses by name (<see cref="ReplayRefusal.ArrivalRefused"/>) when the shadow cannot. The identity it carries
    /// hydrates against the recorded definition's player defaults, so it decodes at re-drive; read decodes it against
    /// the engine defaults to refuse a malformed arrival at intake.</summary>
    /// <param name="SourceAuthority">The source's authority identity — the half of the handoff token a set of tapes
    /// pairs against the source's <see cref="Transfer"/>.</param>
    /// <param name="TransferId">The source-scoped transfer id.</param>
    /// <param name="Encoded">The encoded arrival.</param>
    /// <param name="Outcome">What the commit decided: each landed traveler's generation, and whether it rolled back.</param>
    public sealed record Arrival(string SourceAuthority, ulong TransferId, byte[] Encoded, WorldArrivalOutcome Outcome) : WorldReplayEntry;

    /// <summary>The federated device images a recorded step held — the input forwarded and federated travelers drive
    /// this authority's bodies with, which crosses no loopback. Taped at every step that holds any and once as the
    /// set empties; a re-drive replaces the shadow's held images with exactly this set at the same position, and the
    /// shadow's own step applies them where the live step did.</summary>
    /// <param name="Held">Each held image with the body index it drives.</param>
    internal sealed record FederatedIntents(IReadOnlyList<(int Index, IntentSubmission Submission)> Held) : WorldReplayEntry;
    /// <summary>One authored <c>adjacencies</c> row's delivered neighbour refresh, observed on this tick. The one
    /// piece of federation ingress the tape carries: whether a neighbour delivered is decided by the transport, not
    /// by the document or the population, so <c>Server.WorldEventFeed</c>'s link family and the
    /// <c>$link:&lt;name&gt;</c> rule channel could not otherwise be re-derived. Re-drive replays it through the SAME
    /// <c>WorldEventFeed.ObserveLinkDelivery</c> entry point the live poll uses, at the same pre-step position, so
    /// the staleness counts and both link edges reproduce exactly.
    /// <para>Deliberately carries the row name only: the delivered CONTENT (the neighbour's poses, its definition
    /// revision, its own overlap geometry) is not on the tape, so a replay reproduces WHEN a seam went dark and
    /// never what the neighbour was showing — cross-authority contact against delivered remote poses stays outside
    /// what a MATCH proves.</para></summary>
    /// <param name="Adjacency">The authored <c>adjacencies</c> row name that refreshed.</param>
    internal sealed record LinkDelivery(string Adjacency) : WorldReplayEntry;
}
/// <summary>One recorded tick's server-facing input — the exact <see cref="IServerLink"/> traffic the live session
/// applied that tick, captured at the loopback: the synchronous <see cref="Authority"/> stream (commands, grants, and
/// revokes, applied before the step exactly as the live command-apply window does) and the buffered per-entity
/// <see cref="Intents"/> (drained at the step). Re-applying these to a fresh world in the same order reproduces the tick.
/// <para>The tape carries the human/authority stream only. A mounted addon's driving never crosses
/// <see cref="IServerLink"/> — it applies inside <c>WorldServer.Step</c> — so guest-driven motion is not recorded here
/// and is instead re-derived by re-running the same pinned guests in <see cref="WorldReplaySnapshot.Drive"/>, which is
/// what makes it reproducible rather than merely replayed. That re-run is exactly why the grant changes belong in this
/// stream: a re-run guest is checked against the replayed world's own grant table, so a tape that recorded the commands
/// but not the grants would re-drive a guest that holds nothing and never moves.</para></summary>
/// <param name="Authority">The synchronous authority inputs applied this tick — commands, grants, revokes, and sessions
/// interleaved in submission order.</param>
/// <param name="Intents">The per-entity intent submissions buffered this tick (seat driving), in submission order.</param>
public readonly record struct WorldReplayTickInput(IReadOnlyList<WorldReplayEntry> Authority, IReadOnlyList<IntentSubmission> Intents);
/// <summary>Where a forked tape came from — narration only, carried in the header so an operator reading a child
/// tape can tell it was cut from a parent rather than recorded from boot. The child is STANDALONE: it carries the
/// parent's whole boot image and the parent's leading <see cref="Tick"/> tick groups copied verbatim, so verify,
/// inspect, and a further fork all work on it with no parent lookup; this record is never consulted by
/// <see cref="WorldReplaySnapshot.Drive"/>.</summary>
/// <param name="ParentName">The parent tape's name, as it was saved under (see <c>WorldReplayTape.PathFor</c>).</param>
/// <param name="Tick">How many leading tick groups (ticks <c>0..Tick-1</c>) were copied from the parent — the child's
/// tick <c>Tick</c> is its first live tick. Never greater than the child's own <see cref="WorldReplaySnapshot.TickCount"/>,
/// which <see cref="WorldReplaySnapshot.Read"/> refuses by name.</param>
public readonly record struct WorldReplayForkProvenance(string ParentName, int Tick);
/// <summary>The two replay traces computed in one re-drive: diagnostic poses and the verification boundary.</summary>
/// <param name="Pose">The historical pose-only trace used by trajectory inspection.</param>
/// <param name="Authoritative">The authoritative state-system trace used for replay verdicts.</param>
public readonly record struct WorldReplayHashTraces(ulong[] Pose, ulong[] Authoritative);
/// <summary>
/// A deterministic world-state recording: the server starting state captured at record-start plus the per-tick
/// server-input stream that drove the recorded span, so the recording replays through a fresh world. The starting state
/// is the record-start <see cref="WorldDefinition"/> (embedded as its canonical JSON) and the active seats; the fresh
/// world's starting body state is that definition's deterministic boot image (a fresh <see cref="WorldServer"/>
/// reconstructs it exactly), not a per-body pose snapshot. The recording carries both the live session's per-tick
/// pose trace (<see cref="RecordedHashes"/>) for inspection and the broader authoritative trace
/// (<see cref="RecordedAuthoritativeHashes"/>) used for replay verdicts, sampled against the actual running session
/// tick by tick rather than only at the tail.
/// </summary>
/// <remarks>
/// <para>The seat's profile rates are pinned, not re-resolved. A seated profile's MoveSpeed/TurnSpeed are read live off
/// the handle by <c>WorldBody.Advance</c> every frame, which makes them simulation input — and they reach the catalog
/// through <c>SetPlayerSection</c>, which never crosses the <see cref="WorldCommand"/>/grant/revoke union the tick
/// stream records, so an edit to them is structurally invisible to that stream. Each <see cref="WorldReplaySeat"/>
/// therefore carries the rates its profile actually ran at (<see cref="WorldReplayProfilePin"/>, in raw fixed-point),
/// and <see cref="Drive"/> seats its bodies on those rather than on whatever the live catalog now holds. That makes a
/// re-drive hermetic with respect to the catalog: an <c>identity.motion</c> between record and verify no longer moves the
/// replayed trajectory. When the live values have moved, <see cref="Drive"/> says so on stderr — naming the profile,
/// the field, and both values — because a pin that silently disagreed with the running world would trade one
/// unattributable verdict for another.</para>
/// <para>Honest scope. The captured state is the authoritative server simulation only — the world definition, the active
/// seats, and the per-tick stream of human/authority inputs (commands, grants, revokes) and intents. A mounted addon's
/// driving is deliberately absent from that stream (it never crosses <see cref="IServerLink"/>) and is re-derived by
/// re-running the document's own pinned guests during <see cref="Drive"/>. Grant changes made before record-start are
/// likewise absent — they were never submitted during the capture — which is the same mid-session-capture boundary the
/// boot-image start already has, and it reports honestly as a mismatch rather than as a false match. Screen machines
/// and their pixels, camera rigs, overlays, and audio are
/// presentation and are excluded: they are re-derived from the definition by the live client each frame and never feed
/// back into simulation, so a replay reproduces the hashed authoritative server state but does not
/// re-run the emulated cabinets or redraw the HUD. Because the fresh world starts from the definition boot image, a
/// replayed tail matches the live tail precisely when the live session was still at that boot image at record-start (a
/// boot-anchored capture); a mid-session capture — the session already moved from boot — faithfully re-drives its stream
/// but from the boot image, so the verify honestly reports mismatch. Full per-body record-start rehydration (so a
/// mid-session capture also matches) is the identified next lever.</para>
/// <para>Determinism. The hashed state is fixed-point or an exact integer tick — no wall-clock, no float in the hashed
/// pose. The recorded intent currency is likewise fixed-point: a
/// <see cref="PlayerIntent"/> crosses as sixteen raw <see cref="FixedQ4816"/> lanes and an optional pointer ray of six more, so the replay currency is the
/// simulation's own numeric type rather than a conversion of it. (The serialized command stream carries the authored
/// float fields of the recorded <see cref="WorldCommand"/>s verbatim; those are authored values — the numbers an
/// operator typed — which round-trip bit-exactly through the shared command leaf and quantize deterministically at
/// one apply site each. They never break the guarantee, but they are not absent from the on-disk form.) A
/// fresh world built from this recording and driven by the recorded stream produces bit-identical per-tick pose and authoritative hashes
/// on every run, machine, and backend at a fixed code version. <see cref="Drive"/> is the offline re-drive the
/// replay/verify side runs; the record side samples the live population instead, so a match proves the fresh re-drive
/// reproduces the running session, not merely another re-drive of itself.</para>
/// <para>Wire form. The tape is one <c>Puck.Networking</c> <c>WireWriter</c> leaf, read back by one bounded
/// <c>WireReader</c>: the same stack, string encoding, and leaves (<see cref="WorldWireCodec"/>) the submission wire,
/// the authority checkpoint, and the federation frames use. Every enum crosses as its <see cref="WorldWireTags"/>
/// byte, never by an ordinal cast — including the <see cref="WorldSection"/> byte inside a section
/// <see cref="GrantSubject"/>. The channel vector (<see cref="PlayerIntent"/>) and a channel press's ordinal cross as
/// plain integers. A member no table covers is refused by name at write; a byte no table names, a non-finite command
/// vector, a truncated field, or trailing bytes are refused at read. The header also carries
/// the mounted addon set as recorded-at-mount receipts (<see cref="MountedAddons"/>): because the re-drive re-runs the
/// document's guests rather than replaying their output, the identity of what mounts is part of what the tape pins, and
/// <see cref="Drive"/> refuses a disagreement before the first tick. The header also carries the recording's own
/// <see cref="SimulationRate"/> — simulation input, not metadata, since it is what <see cref="Drive"/> derives the
/// step size from — and <see cref="Drive"/> refuses a disagreement with the embedded definition's own
/// <see cref="WorldDefinition.SimulationRateHz"/>, right after deserializing it, for the identical reason the mount
/// pin does: a wrong step size re-drives a different trajectory that would otherwise report as an ordinary mismatch.
/// There is exactly one supported tape shape: the leading magic identifies a World tape and ShapeToken identifies
/// the current binary and authoritative-hash contract. Both are checked strictly; older development shapes have no
/// compatibility reader.</para>
/// <para><b>Public, not <c>internal</c> behind an <c>InternalsVisibleTo</c> grant</b> (widen the member, not the
/// assembly) — every instance member here was already <c>public</c>; only the class declaration
/// (and <see cref="WorldReplaySeat"/>/<see cref="WorldReplayProfilePin"/>/<see cref="WorldReplayTickInput"/>/the
/// <see cref="WorldReplayEntry"/> base it composes with) had not caught up. Widened so
/// <c>tests/Puck.World.Tests</c> — which reads this surface directly per its own documented no-IVT/no-reflection
/// convention — can exercise <see cref="ResolveStepWidth"/> without a grant.</para>
/// </remarks>
public sealed partial class WorldReplaySnapshot {
    private const uint Magic = 0x5052_4C57u; // "WLRP" in little-endian wire order.
    // A shape-identity token, not a compatibility sequence: this build writes and reads exactly one tape contract.
    // Shape 8 carries the recorded authority and its document paths, the companion tapes of a set, departures by
    // target authority, every arrival a commit decided with its outcome, and federated input. Refuse earlier tapes at
    // intake instead of reporting their old shape as a simulation divergence.
    private const uint ShapeToken = 8u;

    /// <summary>Gets the recorded authority's identity — the namespace its crossings are keyed under, so a set of
    /// tapes pairs one authority's departure with another's arrival.</summary>
    public required string Authority { get; init; }
    /// <summary>Gets the tapes of the other authorities this process recorded alongside this one, each a standalone
    /// tape of one row. Verification re-drives every one and pairs each crossing's departure with its arrival; a
    /// crossing whose other half is on no tape in the set is reported as not verified.</summary>
    public IReadOnlyList<WorldReplaySnapshot> Companions { get; init; } = [];
    /// <summary>Gets the record-start world definition as its canonical UTF-8 JSON — the rehydrated starting state.</summary>
    public required byte[] DefinitionJson { get; init; }
    /// <summary>Gets the directory the recorded world's document was read from
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a directory-less document. The
    /// re-drive's definition resolves every relative path it authors — machine content, asset rows — where the live
    /// one did.</summary>
    public string? DocumentDirectory { get; init; }
    /// <summary>Gets the recorded row's own document file, which the re-drive's cabinets read their content beside, or
    /// <see langword="null"/> for the booted row, whose re-drive reads beside the booted document.</summary>
    public string? DocumentPath { get; init; }
    /// <summary>Gets the recorded row's instance identity — the instance rung of its draw seed ladder, which the
    /// re-drive's shadow server is built under.</summary>
    public required string Instance { get; init; }
    /// <summary>Gets the fork provenance — the parent tape and the count of leading tick groups copied from it —
    /// or <see langword="null"/> for a tape recorded from boot. Header narration only; <see cref="Drive"/> never
    /// reads it, because the copied prefix already sits in <see cref="Ticks"/> like any other recorded tick.</summary>
    public WorldReplayForkProvenance? ForkedFrom { get; init; }
    /// <summary>Gets the guests mounted at record-start, in mount order — the recorded-at-mount receipts (name, module
    /// content hash, fuel, lane) the re-drive re-establishes before it runs a tick. Empty when the recorded session
    /// mounted nothing, which is itself pinned: a re-drive that mounts a guest against an empty set is refused.</summary>
    public required IReadOnlyList<WorldAddonReceipt> MountedAddons { get; init; }
    /// <summary>Gets the directory the recording server's pipeline source reader resolved <c>views.graphs</c> rows
    /// against (<see cref="WorldPipelineSources.DocumentDirectory"/>), or <see langword="null"/> when it attached none.
    /// <see cref="Drive"/> attaches a reader over the same directory to the shadow server, so a recorded
    /// <c>CommitViewGraph</c>, or a row upsert naming overrides, binds against the sources it bound against live.
    /// It is the recorded document's own directory, so the re-drive's definition resolves every relative path it
    /// authors (<see cref="WorldDefinition.DocumentDirectory"/>) where the live one did.</summary>
    public string? PipelineSourceDirectory { get; init; }
    /// <summary>Gets the live session's per-tick authoritative state-system hashes: world state, rule and interaction
    /// latches, body action state, live fields, and poses. Replay verdicts compare this trace;
    /// <see cref="RecordedHashes"/> remains the population-only diagnostic trace.</summary>
    public required ulong[] RecordedAuthoritativeHashes { get; init; }
    /// <summary>Gets the live session's per-tick population hash trace — one entry per recorded tick, sampled off the live
    /// population after each tick's server step, so the last entry is the state the running world actually reached. A
    /// replay recomputes this diagnostic trace by re-driving the recording through a fresh world
    /// (<see cref="Drive"/>); <see cref="RecordedAuthoritativeHashes"/> drives the verdict. Its length always equals
    /// <see cref="TickCount"/>; keeping every entry rather than only the tail lets inspection name the first tick
    /// body pose, rigid residue, or carry relationships diverged.</summary>
    public required ulong[] RecordedHashes { get; init; }
    /// <summary>Gets the live session's tail population-state hash, or <c>0</c> when nothing was recorded.</summary>
    public ulong RecordedPoseTailHash => ((RecordedHashes.Length > 0)
        ? RecordedHashes[^1]
        : 0UL
    );
    /// <summary>Gets the live session's tail authoritative hash, or <c>0</c> when nothing was recorded.</summary>
    public ulong RecordedTailHash => ((RecordedAuthoritativeHashes.Length > 0)
        ? RecordedAuthoritativeHashes[^1]
        : 0UL
    );
    /// <summary>Gets the seats active at record-start, re-joined into the fresh world before the stream replays — each
    /// carrying its profile's pinned locomotion rates, which <see cref="Drive"/> seats the body on in place of the live
    /// catalog's current ones.</summary>
    public required IReadOnlyList<WorldReplaySeat> Seats { get; init; }
    /// <summary>Gets the simulation rate (Hz) this recording was captured at — simulation input, not metadata:
    /// <see cref="Drive"/> derives <c>stepTicks</c> from this value, never from the rate this build happens to run
    /// some other world at, so a re-drive always runs at the granularity the tape actually recorded. The rate is now
    /// authored per-world (<see cref="WorldSimulationDefaults"/>), so there is no longer one build-wide rate to check
    /// this against — <see cref="Drive"/> instead refuses by name (<see cref="ReplayRefusal.RateMismatch"/>), right
    /// after deserializing the embedded definition, when this disagrees with that definition's own
    /// <see cref="WorldDefinition.SimulationRateHz"/> — an internal-consistency check in the same family as the mount
    /// pin below: the header and the embedded document describe the same record-start world, so they must agree, and
    /// a re-drive at the wrong step size would otherwise produce a genuinely different trajectory that reports as an
    /// ordinary mismatch, sending the reader to hunt a determinism regression that is really a decode-time
    /// inconsistency.</summary>
    public required uint SimulationRate { get; init; }
    /// <summary>Gets the number of recorded ticks.</summary>
    public int TickCount => Ticks.Count;
    /// <summary>Gets the per-tick server-input stream, in tick order from the recording's first tick.</summary>
    public required IReadOnlyList<WorldReplayTickInput> Ticks { get; init; }

    /// <summary>Feeds one recorded tick's input into <paramref name="server"/> through the same doors a live
    /// submission uses, at the pre-step position the live command-apply window holds: the authority entries in
    /// recorded order (a grant that preceded a command live must precede it here, or the command is checked against
    /// a table the live one never had), then the tick's intents into the buffer the next <see cref="WorldServer.Step"/>
    /// drains. Shared by the offline <see cref="Drive"/> and the live drive (<c>WorldReplayTape</c>), so the two can
    /// never apply a tape differently.</summary>
    /// <param name="server">The server the tick applies to — the offline shadow, or the running session's own.</param>
    /// <param name="population">That server's population (a departure entry detaches seats from it directly).</param>
    /// <param name="input">The recorded tick.</param>
    /// <param name="expectedMutationOutcomes">Receives each recorded <see cref="WorldReplayEntry.Mutation"/>'s
    /// pinned outcome, in entry order.</param>
    /// <param name="replayedMutationOutcomes">Receives each re-enqueued mutation's actual outcome once the next
    /// <see cref="WorldServer.Step"/> drains it, in the same order.</param>
    /// <param name="rebuildContentPin">Resolves the CAS pin a recorded rebuild is enqueued under: the entry's own
    /// hash for the offline drive (a disagreement then refuses by name from inside the step), or
    /// <see langword="null"/> for the live drive, which must never let a refusal throw out of the running session's
    /// step and narrates the disagreement itself instead.</param>
    /// <exception cref="WorldReplayCodecException">An authority-entry kind this apply does not handle.</exception>
    internal static void ApplyRecordedTick(WorldServer server, WorldPopulation population, WorldReplayTickInput input, List<bool> expectedMutationOutcomes, Queue<bool> replayedMutationOutcomes, Func<WorldReplayEntry.Rebuild, string?> rebuildContentPin) {
        foreach (var entry in input.Authority) {
            switch (entry) {
                case WorldReplayEntry.Command command:
                    server.ApplyCommand(command: command.Value);

                    break;
                case WorldReplayEntry.Grant grant:
                    server.Grant(
                        grant: grant.Value,
                        actor: grant.Actor
                    );

                    break;
                case WorldReplayEntry.Revoke revoke:
                    server.Revoke(
                        grant: revoke.Value,
                        actor: revoke.Actor
                    );

                    break;
                case WorldReplayEntry.Session session:
                    _ = server.ApplySession(request: session.Value);

                    break;
                case WorldReplayEntry.Designation designation:
                    server.ApplyDesignation(
                        designation: designation.Value,
                        principal: designation.Actor
                    );

                    break;
                case WorldReplayEntry.PeerAdmitted admitted:
                    server.ApplyServerEvent(serverEvent: admitted.Value);

                    break;
                case WorldReplayEntry.PeerDisconnected disconnected:
                    server.ApplyServerEvent(serverEvent: disconnected.Value);

                    break;
                case WorldReplayEntry.SessionEvent session:
                    server.ApplyServerEvent(serverEvent: session.Value);

                    break;
                case WorldReplayEntry.Rebuild rebuild:
                    // Deliberately NO Definition: Load/Reload re-read rebuild.PathHint fresh inside
                    // WorldServer.ApplyRebuild (called from DrainPendingOps below), which is the content-address
                    // proof — a stored copy would let a moved file pass unnoticed. expectedContentHash is what
                    // makes this a REPLAY drive rather than a live one: ApplyRebuild refuses BY NAME, before
                    // installing anything, when the resolved candidate's hash disagrees with what was recorded.
                    server.EnqueueRebuild(
                        request: new WorldRebuildRequest(
                            Kind: rebuild.Kind,
                            Definition: null,
                            PathHint: rebuild.PathHint,
                            Force: rebuild.Force
                        ),
                        principal: rebuild.Actor,
                        expectedContentHash: rebuildContentPin(rebuild)
                    );

                    break;
                case WorldReplayEntry.ScreenOp screenOp:
                    // Synchronous, like Command/Grant/Revoke above — never buffered — mirroring the live apply
                    // exactly. expectedContentHash is null for every kind but a recorded Insert or a
                    // machine-booting Select (Select shares Insert's own CAS pin); WorldMachineHost.TryBootMachine
                    // refuses BY NAME, before booting anything, when a fresh re-read of the content path disagrees
                    // with it (including an engine-resolution failure, since content is signed before engine
                    // resolution and is never left unpinned on that path) — the negative control an edited/moved
                    // ROM exercises.
                    server.ApplyScreenOp(
                        op: screenOp.Value,
                        principal: screenOp.Actor,
                        expectedContentHash: screenOp.ContentHash
                    );

                    break;
                case WorldReplayEntry.Mutation mutation:
                    // Buffered to the tick boundary through the ordinary door, drained by THIS tick's
                    // server.Step call below (DrainPendingOps) — a live mutation never applies synchronously
                    // either, so the whole apply pipeline (including addon preparation) re-executes at the same
                    // point it did live. The completion queues this mutation's REPLAYED outcome; the recorded
                    // one is queued alongside it and both are compared right after server.Step below.
                    server.EnqueueMutation(
                        mutation: mutation.Value,
                        outcomeObserved: applied => replayedMutationOutcomes.Enqueue(item: applied)
                    );
                    expectedMutationOutcomes.Add(item: mutation.Outcome);

                    break;
                case WorldReplayEntry.Undo undo:
                    server.EnqueueUndo(
                        count: undo.Count,
                        principal: undo.Actor
                    );

                    break;
                case WorldReplayEntry.Composition composition:
                    server.ApplyComposition(
                        composition: composition.Value,
                        principal: composition.Actor
                    );

                    break;
                case WorldReplayEntry.Query query:
                    // Re-executed so any read-back state the composition touches is reproduced at the same
                    // position it was live; the answer itself is discarded because a query moves no simulation
                    // state and therefore cannot alter either replay trace.
                    _ = server.Answer(query: query.Value);

                    break;
                case WorldReplayEntry.LinkDelivery linkDelivery:
                    // The same entry point the live poll calls, at the same pre-step position, so this tick's
                    // Collect sees the identical pending-refresh set. This shadow world holds no adjacency
                    // source, so the live poll contributes nothing here and cannot double-count.
                    server.Events.ObserveLinkDelivery(adjacencyName: linkDelivery.Adjacency);

                    break;
                case WorldReplayEntry.RateLever:
                    // Deliberately a no-op: a paused span recorded zero ticks live, so re-driving exactly
                    // Ticks.Count steps already reproduces the identical stepping cadence with no lever to apply.
                    break;
                case WorldReplayEntry.FederatedIntents federated:
                    server.ReplaceFederatedIntents(held: federated.Held);

                    break;
                case WorldReplayEntry.Arrival arrivalEvent:
                    RelandArrival(
                        arrival: arrivalEvent,
                        server: server
                    );

                    break;
                case WorldReplayEntry.Transfer transferEvent:
                    // The departure half: a departed body stops contributing to HashState here exactly as it did
                    // live. A slot already inactive is a no-op by TryDetachSeatForTransfer's own contract.
                    foreach (var departedSlot in transferEvent.DepartedSlots) {
                        _ = population.TryDetachSeatForTransfer(
                            profile: out _,
                            slot: departedSlot
                        );
                    }

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
    }
    // The mount pin compares index-by-index: mount order is document order, and the recording pins the whole receipt
    // sequence — name, hash, and fuel, at each position — never merely the set of names. Position is load-bearing
    // simulation state (the order guests are pumped, disclosed, and fold their contributions), not a cosmetic
    // ordering, even though a guest's mounted INDEX no longer addresses it for completion routing (see
    // Addons.WorldAddonRuntime.MountedAddon.InstanceId). Duplicates are refused first and separately, so a
    // collision reports as itself rather than a confusing index mismatch.
    internal static void VerifyMountedAddons(IReadOnlyList<WorldAddonReceipt> recorded, IReadOnlyList<WorldAddonReceipt> fresh) {
        // Read refuses a duplicate name in a TAPE'S OWN mounted set (a name identifies exactly one mounted guest), but
        // Drive can also run over an IN-PROCESS recording that never passed through Read at all —
        // WorldReplayTape.StopRecording's post-persist verify hands this method its recording straight from the live
        // server's own receipts — and "fresh" is the just-built offline runtime's OWN receipts, never validated
        // either. A duplicate in EITHER set is refused here before the positional pins below ever compare a name
        // that might not be unique.
        EnsureNoDuplicateAddonNames(
            receipts: recorded,
            side: "recorded"
        );
        EnsureNoDuplicateAddonNames(
            receipts: fresh,
            side: "fresh"
        );

        if (recorded.Count != fresh.Count) {
            throw ReplayRefusal.PinnedAddonNotMounted.Raise(message: $"This .puckreplay recording pins {recorded.Count} addon(s), but the replay's fresh world mounts {fresh.Count} — the mounted SEQUENCE (not merely the set of names) is part of what a recording pins, because document order decides the order guests are pumped, disclosed, and fold their contributions.");
        }

        for (var index = 0; (index < recorded.Count); index++) {
            var pin = recorded[index];
            var mounted = fresh[index];

            if (!string.Equals(
                a: mounted.Name,
                b: pin.Name,
                comparisonType: StringComparison.Ordinal
            )) {
                throw ReplayRefusal.PinnedAddonNotMounted.Raise(message: $"This .puckreplay recording pins '{pin.Name}' at mount index {index}, but the replay's fresh world mounts '{mounted.Name}' there instead — a reordered, added, or removed addon changes the sequence even when the same names appear somewhere on both sides.");
            }

            if (!string.Equals(
                a: mounted.Hash,
                b: pin.Hash,
                comparisonType: StringComparison.Ordinal
            )) {
                throw ReplayRefusal.AddonModuleMismatch.Raise(message: $"Addon '{pin.Name}' module mismatch: this .puckreplay recording was made against {pin.Hash}, the replay would re-run {mounted.Hash} — the tape re-runs its guests, so the module identity is part of what it pins; re-record against the current module.");
            }

            if (mounted.Fuel != pin.Fuel) {
                throw ReplayRefusal.AddonFuelMismatch.Raise(message: $"Addon '{pin.Name}' fuel mismatch: this .puckreplay recording was made at {pin.Fuel} fuel/tick, the replay would re-run at {mounted.Fuel} — a different budget is a different guest execution.");
            }
        }
    }
    /// <summary>Compares one tick's recorded mutation outcomes against what the apply pipeline actually produced,
    /// right after the step that drained them — the mutation-outcome pin. Any disagreement, in either direction or in
    /// count, refuses by name (<see cref="ReplayRefusal.MutationOutcomeMismatch"/>); <paramref name="replayed"/> is
    /// drained by the comparison.</summary>
    /// <param name="tick">The recorded tick index, for the refusal text.</param>
    /// <param name="expected">The recorded outcomes, in entry order.</param>
    /// <param name="replayed">The outcomes the drained step produced, in the same order.</param>
    /// <exception cref="InvalidDataException">The two disagree.</exception>
    internal static void VerifyRecordedMutationOutcomes(int tick, List<bool> expected, Queue<bool> replayed) {
        for (var index = 0; (index < expected.Count); index++) {
            if (!replayed.TryDequeue(result: out var replayedOutcome)) {
                throw ReplayRefusal.MutationOutcomeMismatch.Raise(message: $"tick {tick}: {expected.Count} mutation(s) were recorded this tick, but the replay produced only {index} outcome(s) — the mutation stream itself diverged, not merely a later tick's pose.");
            }

            var recordedOutcome = expected[index];

            if (replayedOutcome != recordedOutcome) {
                throw ReplayRefusal.MutationOutcomeMismatch.Raise(message: $"tick {tick}: mutation #{index} was {(recordedOutcome
                    ? "ACCEPTED"
                    : "REFUSED")} live but is {(replayedOutcome
                    ? "ACCEPTED"
                    : "REFUSED")} on replay — once acceptance can depend on module bytes on disk, this disagreement is a real determinism finding, never an ordinary later-tick pose drift.");
            }
        }

        if (replayed.Count > 0) {
            throw ReplayRefusal.MutationOutcomeMismatch.Raise(message: $"tick {tick}: the replay produced {replayed.Count} more mutation outcome(s) than were recorded — the mutation stream itself diverged, not merely a later tick's pose.");
        }
    }

    private static void AddLengthPrefixedUtf8(ref Fnv1aHash hash, string value) {
        var bytes = Encoding.UTF8.GetBytes(s: value);

        hash.Add(value: ((ulong)bytes.Length));
        hash.Add(values: bytes);
    }
    private static ulong ComputeTransferSignature(WorldReplayEntry.Transfer transfer) {
        var hash = Fnv1aHash.Create();

        hash.Add(value: transfer.TransferId);
        AddLengthPrefixedUtf8(
            hash: ref hash,
            value: transfer.Target
        );
        hash.Add(value: (transfer.TargetRemote
            ? 1U
            : 0U));
        AddLengthPrefixedUtf8(
            hash: ref hash,
            value: transfer.DestinationName
        );
        AddLengthPrefixedUtf8(
            hash: ref hash,
            value: transfer.ScopeKey
        );
        hash.Add(value: transfer.GenerationId);
        AddLengthPrefixedUtf8(
            hash: ref hash,
            value: transfer.Outcome
        );
        hash.Add(value: ((uint)transfer.DepartedSlots.Count));

        foreach (var slot in transfer.DepartedSlots) {
            hash.Add(value: ((uint)slot));
        }

        return hash.Value;
    }
    // The rate as a readable decimal beside its exact raw lane. The decimal is for the operator; the raw is the number
    // the comparison actually ran on, printed so a drift the decimal rounds away is still legible in the report.
    private static string Describe(FixedQ4816? rate) => WorldReplayInspector.DescribeRate(
        absent: "kit (no claimed rate)",
        rate: rate
    );
    // Shared by VerifyMountedAddons for BOTH sides it compares (recorded and fresh) — see its call site's remarks for
    // why an in-process Drive needs this even though Read already guards its own copy of "recorded". Quadratic in the
    // mounted count deliberately, matching VerifyMountedAddons' own posture: a handful of rows, checked once per drive.
    private static void EnsureNoDuplicateAddonNames(IReadOnlyList<WorldAddonReceipt> receipts, string side) {
        for (var index = 0; (index < receipts.Count); index++) {
            for (var other = (index + 1); (other < receipts.Count); other++) {
                if (string.Equals(
                    a: receipts[index].Name,
                    b: receipts[other].Name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    throw new InvalidDataException(message: $"This .puckreplay drive's {side} addon set pins '{receipts[index].Name}' twice — a name identifies exactly one mounted guest, the same ambiguity Read refuses on its own copy of the tape.");
                }
            }
        }
    }
    private static WorldAddonReceipt? Find(IReadOnlyList<WorldAddonReceipt> receipts, string name) {
        for (var index = 0; (index < receipts.Count); index++) {
            if (string.Equals(
                a: receipts[index].Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
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
    private static WorldCommand ReadCommandLeaf(ref WireReader reader) => ReadLeaf<WorldCommand>(
        reader: ref reader,
        tryDecode: WorldSubmissionCodec.TryDecodeCommand,
        what: "command"
    );
    private static WorldComposition ReadCompositionLeaf(ref WireReader reader) => ReadLeaf<WorldComposition>(
        reader: ref reader,
        tryDecode: WorldSubmissionCodec.TryDecodeComposition,
        what: "composition"
    );
    private static WorldDesignation ReadDesignationLeaf(ref WireReader reader) => ReadLeaf<WorldDesignation>(
        reader: ref reader,
        tryDecode: WorldSubmissionCodec.TryDecodeDesignation,
        what: "designation"
    );
    private static WorldReplayEntry ReadEntry(ref WireReader reader) {
        var kind = reader.ReadByte();

        switch (kind) {
            case 0:
                return new WorldReplayEntry.Command(Value: ReadCommandLeaf(reader: ref reader));
            case 1: {
                    var grant = ReadGrantLeaf(
                        reader: ref reader,
                        revoke: false
                    );

                    return new WorldReplayEntry.Grant(
                        Actor: WorldWireCodec.ReadPrincipal(reader: ref reader),
                        Value: grant
                    );
                }
            case 2: {
                    var grant = ReadGrantLeaf(
                        reader: ref reader,
                        revoke: true
                    );

                    return new WorldReplayEntry.Revoke(
                        Actor: WorldWireCodec.ReadPrincipal(reader: ref reader),
                        Value: grant
                    );
                }
            case 3: {
                    var (entries, grants) = ReadPeerEvent(
                        reader: ref reader,
                        revoked: false
                    );

                    return new WorldReplayEntry.PeerAdmitted(Value: new WorldServerEvent.PeerAdmitted(
                        Entries: entries,
                        MintedGrants: grants
                    ));
                }
            case 4: {
                    var (entries, grants) = ReadPeerEvent(
                        reader: ref reader,
                        revoked: true
                    );

                    return new WorldReplayEntry.PeerDisconnected(Value: new WorldServerEvent.PeerDisconnected(
                        Entries: entries,
                        RevokedGrants: grants
                    ));
                }
            case 5:
                return ReadRebuildEntry(reader: ref reader);
            case 6:
                return ReadScreenOpEntry(reader: ref reader);
            case 7:
                return new WorldReplayEntry.Session(Value: ReadSessionLeaf(reader: ref reader));
            case 8: {
                    var designation = ReadDesignationLeaf(reader: ref reader);

                    return new WorldReplayEntry.Designation(
                        Actor: WorldWireCodec.ReadPrincipal(reader: ref reader),
                        Value: designation
                    );
                }
            case 9:
                return new WorldReplayEntry.RateLever(Paused: reader.ReadBoolean());
            case 10:
                return ReadTransferEntry(reader: ref reader);
            case 11: {
                    var mutation = ReadMutationLeaf(reader: ref reader);
                    var actor = WorldWireCodec.ReadPrincipal(reader: ref reader);

                    return new WorldReplayEntry.Mutation(
                        Actor: actor,
                        Outcome: reader.ReadBoolean(),
                        Value: mutation
                    );
                }
            case 12: {
                    var count = reader.ReadInt32();

                    return new WorldReplayEntry.Undo(
                        Actor: WorldWireCodec.ReadPrincipal(reader: ref reader),
                        Count: count
                    );
                }
            case 13: {
                    var composition = ReadCompositionLeaf(reader: ref reader);

                    return new WorldReplayEntry.Composition(
                        Actor: WorldWireCodec.ReadPrincipal(reader: ref reader),
                        Value: composition
                    );
                }
            case 14: {
                    var query = ReadQueryLeaf(reader: ref reader);

                    return new WorldReplayEntry.Query(
                        Actor: WorldWireCodec.ReadPrincipal(reader: ref reader),
                        Value: query
                    );
                }
            case 15:
                return new WorldReplayEntry.LinkDelivery(Adjacency: reader.ReadString(field: "link delivery adjacency"));
            case 19:
                return ReadArrivalEntry(reader: ref reader);
            case 20:
                return new WorldReplayEntry.FederatedIntents(Held: reader.ReadArray(
                    field: "federated intents",
                    maximum: WorldBodiesLimits.CapacityCeiling,
                    readItem: static (ref WireReader r) => {
                        var index = r.ReadInt32();
                        var submission = WorldWireCodec.ReadIntentSubmission(reader: ref r);

                        return (index, submission);
                    }
                ));
            case 16 or 17 or 18:
                return ReadSessionEntry(
                    kind: kind,
                    reader: ref reader
                );
            default:
                if (!reader.Failed) {
                    throw new InvalidDataException(message: $"unknown .puckreplay authority entry discriminant {kind}.");
                }

                return new WorldReplayEntry.RateLever(Paused: false);
        }
    }
    private static WorldGrant ReadGrantLeaf(ref WireReader reader, bool revoke) => ReadLeaf<WorldGrant>(
        reader: ref reader,
        tryDecode: (revoke
        ? WorldSubmissionCodec.TryDecodeRevoke
        : WorldSubmissionCodec.TryDecodeGrant),
        what: (revoke
        ? "revoke"
        : "grant")
    );
    private static T ReadLeaf<T>(ref WireReader reader, string what, TryDecodeLeaf<T> tryDecode) {
        var bytes = reader.ReadBlock(
            field: $"{what} leaf",
            maxBytes: WireLimits.MaxDocumentBytes
        );

        if (reader.Failed) {
            return default!;
        }

        if (
            !tryDecode(
            bytes,
            out var value,
            out var failure
        ) ||
            (value is null)
        ) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay {what} leaf: {failure}");
        }

        return value;
    }
    private static WorldMutation ReadMutationLeaf(ref WireReader reader) => ReadLeaf<WorldMutation>(
        reader: ref reader,
        tryDecode: WorldSubmissionCodec.TryDecodeMutation,
        what: "mutation"
    );
    private static (IReadOnlyList<WorldPeerEventEntry> Entries, IReadOnlyList<WorldGrant> Grants) ReadPeerEvent(ref WireReader reader, bool revoked) {
        var entries = ReadTapeArray(
            minimumBytesEach: 16,
            readItem: static (ref WireReader r) => WorldWireLeaves.ReadPeerEventEntry(reader: ref r),
            reader: ref reader,
            what: "peer event entry"
        );
        var grants = ReadTapeArray(
            minimumBytesEach: 5,
            readItem: (ref WireReader r) => ReadGrantLeaf(
                reader: ref r,
                revoke: revoked
            ),
            reader: ref reader,
            what: "peer event grant"
        );

        return (entries, grants);
    }
    private static WorldQuery ReadQueryLeaf(ref WireReader reader) => ReadLeaf<WorldQuery>(
        reader: ref reader,
        tryDecode: WorldSubmissionCodec.TryDecodeQuery,
        what: "query"
    );
    private static WorldReplayEntry ReadRebuildEntry(ref WireReader reader) {
        var kind = WorldWireCodec.ReadRebuildKind(reader: ref reader);
        var force = reader.ReadBoolean();
        var pathHint = reader.ReadNullableString(field: "rebuild path hint");
        var contentHash = reader.ReadString(field: "rebuild content hash");
        var actor = WorldWireCodec.ReadPrincipal(reader: ref reader);

        if (
            !reader.Failed &&
            (((kind == WorldRebuildKind.Reset) && (pathHint is not null)) ||
            ((kind != WorldRebuildKind.Reset) && (pathHint is null)))
        ) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay rebuild entry: kind '{kind}' does not carry the path-hint shape its kind requires (none for Reset, one for Load/Reload).");
        }

        return new WorldReplayEntry.Rebuild(
            Actor: actor,
            ContentHash: contentHash,
            Force: force,
            Kind: kind,
            PathHint: pathHint
        );
    }
    private static WorldReplayEntry ReadScreenOpEntry(ref WireReader reader) {
        var screenOp = ReadLeaf<WorldScreenOp>(
            reader: ref reader,
            tryDecode: WorldSubmissionCodec.TryDecodeScreenOp,
            what: "screen-op"
        );
        var contentHash = reader.ReadNullableString(field: "screen-op content hash");
        var actor = WorldWireCodec.ReadPrincipal(reader: ref reader);

        return new WorldReplayEntry.ScreenOp(
            Actor: actor,
            ContentHash: contentHash,
            Value: screenOp
        );
    }
    private static SessionRequest ReadSessionLeaf(ref WireReader reader) => ReadLeaf<SessionRequest>(
        reader: ref reader,
        tryDecode: WorldSubmissionCodec.TryDecodeSession,
        what: "session"
    );
    // Every count in a tape is UNTRUSTED — a doctored or truncated file reaches this reader through
    // `replay.verify <name>` — so a count is bounded by the bytes actually left before it sizes an allocation: a
    // count whose smallest possible elements could not fit in what remains is refused as out of range.
    private static T[] ReadTapeArray<T>(ref WireReader reader, string what, int minimumBytesEach, WireReadItem<T> readItem) {
        var maximum = (Math.Max(
            val1: 0,
            val2: (reader.Remaining - sizeof(int))
        ) / minimumBytesEach);

        return reader.ReadArray(
            field: $"{what} count",
            maximum: maximum,
            readItem: readItem
        );
    }
    // The arrival's carried identity hydrates against the recorded world's own player defaults, which only the
    // re-drive holds; read validates the leaf against the engine defaults and keeps the bytes. An outcome must name a
    // landing for every traveler of a commit that stood, and at least one and at most every traveler of one that
    // rolled back, each at a generation an admission can mint.
    private static WorldReplayEntry ReadArrivalEntry(ref WireReader reader) {
        var encoded = reader.ReadBlock(
            field: "arrival",
            maxBytes: WireLimits.MaxDocumentBytes
        );
        var generations = reader.ReadArray(
            field: "arrival generations",
            readItem: static (ref WireReader r) => r.ReadInt32(),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        var outcome = new WorldArrivalOutcome(
            Generations: generations,
            RolledBack: reader.ReadBoolean()
        );
        var refused = new WorldReplayEntry.Arrival(
            Encoded: encoded,
            Outcome: outcome,
            SourceAuthority: string.Empty,
            TransferId: 0
        );

        if (reader.Failed) {
            return refused;
        }
        if (!WorldAuthorityCheckpointCodec.TryDecodeCrossingArrival(
            arrival: out var arrival,
            bytes: encoded,
            defaults: WorldPlayerDefaults.Default,
            reason: out var reason
        )) {
            reader.Fail(
                detail: reason,
                refusal: WireRefusal.PayloadMalformed
            );
            return refused;
        }

        var travelers = arrival!.Members.Count;

        if (
            (generations.Length == 0) ||
            (generations.Length > travelers) ||
            (!outcome.RolledBack && (generations.Length != travelers)) ||
            generations.Any(predicate: static generation => (generation <= 0))
        ) {
            reader.Fail(
                detail: $"arrival #{arrival.Request.TransferId} records {generations.Length} landing(s) for {travelers} traveler(s){(outcome.RolledBack ? " before its rollback" : string.Empty)}, or a generation no admission mints",
                refusal: WireRefusal.PayloadMalformed
            );
            return refused;
        }

        return new WorldReplayEntry.Arrival(
            Encoded: encoded,
            Outcome: outcome,
            SourceAuthority: arrival.Request.SourceAuthority,
            TransferId: arrival.Request.TransferId
        );
    }
    private static void RelandArrival(WorldServer server, WorldReplayEntry.Arrival arrival) {
        if (!WorldAuthorityCheckpointCodec.TryDecodeCrossingArrival(
            arrival: out var decoded,
            bytes: arrival.Encoded,
            defaults: server.Definition.PlayerDefaults,
            reason: out var decodeReason
        )) {
            throw ReplayRefusal.ArrivalRefused.Raise(message: $"transfer {arrival.TransferId} from '{arrival.SourceAuthority}' does not decode against the recorded world — {decodeReason}");
        }

        var reason = string.Empty;
        var reproduced = server.ExecuteAuthorityOperation(operation: () => server.TransferEscrow.TryReland(
            arrival: decoded!,
            reason: out reason,
            recorded: arrival.Outcome
        ));

        if (!reproduced) {
            throw ReplayRefusal.ArrivalRefused.Raise(message: $"transfer {arrival.TransferId} from '{arrival.SourceAuthority}' {(arrival.Outcome.RolledBack ? "rolled back" : "landed")} live but the re-drive's own escrow did not reproduce it — {reason}");
        }
    }
    private static WorldReplayEntry ReadTransferEntry(ref WireReader reader) {
        var transferId = reader.ReadUInt64();
        var target = reader.ReadString(field: "transfer target authority");
        var targetRemote = reader.ReadBoolean();
        var destinationName = reader.ReadString(field: "transfer destination");
        var scopeKey = reader.ReadString(field: "transfer scope key");
        var generationId = reader.ReadUInt64();
        var outcome = reader.ReadString(field: "transfer outcome");
        var departedSlots = reader.ReadArray(
            field: "transfer departed-slot count",
            maximum: WorldBodiesLimits.CapacityCeiling,
            readItem: static (ref WireReader r) => {
                var slot = r.ReadInt32();

                if (((uint)slot) >= WorldBodiesLimits.CapacityCeiling) {
                    r.Fail(
                        detail: $"transfer departed-slot {slot} is out of range (expected 0..{(WorldBodiesLimits.CapacityCeiling - 1)})",
                        refusal: WireRefusal.PayloadMalformed
                    );
                }

                return slot;
            }
        );
        var storedSignature = reader.ReadUInt64();
        var entry = new WorldReplayEntry.Transfer(
            DepartedSlots: departedSlots,
            DestinationName: destinationName,
            GenerationId: generationId,
            Outcome: outcome,
            ScopeKey: scopeKey,
            Target: target,
            TargetRemote: targetRemote,
            TransferId: transferId
        );

        if (reader.Failed) {
            return entry;
        }

        var recomputed = ComputeTransferSignature(transfer: entry);

        if (recomputed != storedSignature) {
            throw ReplayRefusal.TransferEventTampered.Raise(message: $"transfer {transferId} -> '{target}' '{destinationName}' (scope '{scopeKey}', generation {generationId}): stored content signature 0x{storedSignature:x16} disagrees with the recomputed 0x{recomputed:x16} — the tape's transfer event bytes were corrupted or edited after recording (this entry sits outside the population hash's own coverage, so nothing else on the tape would catch it)");
        }

        return entry;
    }
    // Reports a live/pinned rate drift without refusing: a drifted profile is a perfectly replayable recording, so
    // nothing here throws. Without this, an operator who edited a profile between record and verify would see a
    // MATCH with no way to tell a profile edit from a genuine determinism regression.
    private static void ReportProfileDrift(WorldOwnedWorlds profiles, WorldReplayProfilePin pin) {
        if (profiles.Find(name: pin.Name) is not { } live) {
            // Drift all the way to absent. The re-drive is unaffected — the pinned handle needs no catalog entry — but
            // an operator reading a MATCH for a profile that no longer exists deserves to be told why it still ran.
            if (profiles.NarrationHub is { HasNarrationSink: true }) {
                profiles.NarrationHub?.Narrate(
                    channel: "replay.profile",
                    text: $"[replay.profile: '{pin.Name}' is pinned by this recording but is no longer in the live catalog; the replay used the pinned rates (move {Describe(rate: pin.MoveSpeed)}, turn {Describe(rate: pin.TurnSpeed)})]"
                );
            }

            return;
        }

        ReportRateDrift(
            narrationHub: profiles.NarrationHub,
            name: pin.Name,
            field: "move-speed",
            pinned: pin.MoveSpeed,
            live: live.FixedMoveSpeed
        );
        ReportRateDrift(
            narrationHub: profiles.NarrationHub,
            name: pin.Name,
            field: "turn-speed",
            pinned: pin.TurnSpeed,
            live: live.FixedTurnSpeed
        );
    }
    // Compared on the RAW fixed lane, never on the rendered decimal: a drift too small to show in four places is still
    // a different trajectory, and a comparison that reads the display string would miss exactly those.
    private static void ReportRateDrift(WorldOutputHub? narrationHub, string name, string field, FixedQ4816? pinned, FixedQ4816? live) {
        if (pinned?.Value == live?.Value) {
            return;
        }

        if (narrationHub is { HasNarrationSink: true }) {
            narrationHub?.Narrate(
                channel: "replay.profile",
                text: $"[replay.profile: '{name}' {field} drifted since record-start — pinned {Describe(rate: pinned)}, live {Describe(rate: live)}; the replay used the PINNED value, so this verdict reports the recording, not the edit]"
            );
        }
    }
    private static void WriteCommandLeaf(WireWriter writer, WorldCommand command) => WriteLeaf(
        tryEncode: WorldSubmissionCodec.TryEncodeCommand,
        value: command,
        what: "command",
        writer: writer
    );
    private static void WriteCompositionLeaf(WireWriter writer, WorldComposition composition) => WriteLeaf(
        tryEncode: WorldSubmissionCodec.TryEncodeComposition,
        value: composition,
        what: "composition",
        writer: writer
    );
    private static void WriteDesignationLeaf(WireWriter writer, WorldDesignation designation) => WriteLeaf(
        tryEncode: WorldSubmissionCodec.TryEncodeDesignation,
        value: designation,
        what: "designation",
        writer: writer
    );
    // The authority-INPUT tagged union: one discriminant byte, then the entry's own payload. Kept distinct from the
    // command tagged union — that one discriminates WorldCommand's sealed subtypes, this one discriminates what KIND
    // of authority write crossed the link at all.
    private static void WriteEntry(WireWriter writer, WorldReplayEntry entry) {
        switch (entry) {
            case WorldReplayEntry.Command command:
                writer.WriteByte(value: 0);
                WriteCommandLeaf(
                    writer: writer,
                    command: command.Value
                );

                break;
            case WorldReplayEntry.Grant grant:
                writer.WriteByte(value: 1);
                WriteGrantLeaf(
                    writer: writer,
                    grant: grant.Value,
                    revoke: false
                );
                WritePrincipal(
                    writer: writer,
                    principal: grant.Actor
                );

                break;
            case WorldReplayEntry.Revoke revoke:
                writer.WriteByte(value: 2);
                WriteGrantLeaf(
                    writer: writer,
                    grant: revoke.Value,
                    revoke: true
                );
                WritePrincipal(
                    writer: writer,
                    principal: revoke.Actor
                );

                break;
            case WorldReplayEntry.PeerAdmitted admitted:
                writer.WriteByte(value: 3);
                WritePeerEvent(
                    writer: writer,
                    entries: admitted.Value.Entries,
                    grants: admitted.Value.MintedGrants,
                    revoked: false
                );

                break;
            case WorldReplayEntry.PeerDisconnected disconnected:
                writer.WriteByte(value: 4);
                WritePeerEvent(
                    writer: writer,
                    entries: disconnected.Value.Entries,
                    grants: disconnected.Value.RevokedGrants,
                    revoked: true
                );

                break;
            case WorldReplayEntry.Rebuild rebuild:
                writer.WriteByte(value: 5);
                WriteRebuildLeaf(
                    rebuild: rebuild,
                    writer: writer
                );
                WritePrincipal(
                    writer: writer,
                    principal: rebuild.Actor
                );

                break;
            case WorldReplayEntry.ScreenOp screenOp:
                writer.WriteByte(value: 6);
                WriteLeaf(
                    tryEncode: WorldSubmissionCodec.TryEncodeScreenOp,
                    value: screenOp.Value,
                    what: "screen-op",
                    writer: writer
                );
                writer.WriteNullableString(value: screenOp.ContentHash);
                WritePrincipal(
                    writer: writer,
                    principal: screenOp.Actor
                );

                break;
            case WorldReplayEntry.Session session:
                writer.WriteByte(value: 7);
                WriteLeaf(
                    tryEncode: WorldSubmissionCodec.TryEncodeSession,
                    value: session.Value,
                    what: "session",
                    writer: writer
                );

                break;
            case WorldReplayEntry.Designation designation:
                writer.WriteByte(value: 8);
                WriteDesignationLeaf(
                    writer: writer,
                    designation: designation.Value
                );
                WritePrincipal(
                    writer: writer,
                    principal: designation.Actor
                );

                break;
            case WorldReplayEntry.RateLever rateLever:
                writer.WriteByte(value: 9);
                writer.WriteBoolean(value: rateLever.Paused);

                break;
            case WorldReplayEntry.Transfer transfer:
                writer.WriteByte(value: 10);
                WriteTransferLeaf(
                    transfer: transfer,
                    writer: writer
                );

                break;
            case WorldReplayEntry.Mutation mutation:
                writer.WriteByte(value: 11);
                WriteLeaf(
                    tryEncode: WorldSubmissionCodec.TryEncodeMutation,
                    value: mutation.Value,
                    what: "mutation",
                    writer: writer
                );
                WritePrincipal(
                    writer: writer,
                    principal: mutation.Actor
                );
                // The recorded accept/refuse outcome — see WorldReplayEntry.Mutation's own remarks. Every entry this
                // writer ever sees was resolved synchronously within the tick it was recorded on (the same tick
                // MutationOutcomeTap fired), so Outcome is never speculative by the time it reaches here.
                writer.WriteBoolean(value: mutation.Outcome);

                break;
            case WorldReplayEntry.Undo undo:
                writer.WriteByte(value: 12);
                writer.WriteInt32(value: undo.Count);
                WritePrincipal(
                    writer: writer,
                    principal: undo.Actor
                );

                break;
            case WorldReplayEntry.Composition composition:
                writer.WriteByte(value: 13);
                WriteCompositionLeaf(
                    writer: writer,
                    composition: composition.Value
                );
                WritePrincipal(
                    writer: writer,
                    principal: composition.Actor
                );

                break;
            case WorldReplayEntry.Query query:
                writer.WriteByte(value: 14);
                WriteLeaf(
                    tryEncode: WorldSubmissionCodec.TryEncodeQuery,
                    value: query.Value,
                    what: "query",
                    writer: writer
                );
                WritePrincipal(
                    writer: writer,
                    principal: query.Actor
                );

                break;
            case WorldReplayEntry.LinkDelivery linkDelivery:
                writer.WriteByte(value: 15);
                writer.WriteString(value: linkDelivery.Adjacency);
                break;
            case WorldReplayEntry.Arrival arrival:
                writer.WriteByte(value: 19);
                writer.WriteBlock(value: arrival.Encoded);
                writer.WriteArray(
                    items: arrival.Outcome.Generations,
                    writeItem: static (w, generation) => w.WriteInt32(value: generation)
                );
                writer.WriteBoolean(value: arrival.Outcome.RolledBack);

                break;
            case WorldReplayEntry.FederatedIntents federated:
                writer.WriteByte(value: 20);
                writer.WriteArray(
                    items: federated.Held,
                    writeItem: static (w, held) => {
                        w.WriteInt32(value: held.Index);
                        if (!WorldWireCodec.TryWriteIntentSubmission(
                            submission: in held.Submission,
                            writer: w
                        )) {
                            throw new WorldReplayCodecException(message: $"no .puckreplay wire value for {nameof(PrincipalKind)}.{held.Submission.Principal.Kind} — a federated image is driven by a peer principal.");
                        }
                    }
                );

                break;
            case WorldReplayEntry.SessionEvent session:
                WriteSessionEntry(
                    serverEvent: session.Value,
                    writer: writer
                );

                break;
            default:
                throw new WorldReplayCodecException(message: $"no .puckreplay encoding for authority entry kind '{entry.GetType().Name}'.");
        }
    }
    private static void WriteGrantLeaf(WireWriter writer, WorldGrant grant, bool revoke) => WriteLeaf(
        tryEncode: (revoke
        ? WorldSubmissionCodec.TryEncodeRevoke
        : WorldSubmissionCodec.TryEncodeGrant),
        value: grant,
        what: (revoke
        ? "revoke"
        : "grant"),
        writer: writer
    );
    private static void WriteLeaf<T>(WireWriter writer, T value, string what, TryEncodeLeaf<T> tryEncode) {
        if (!tryEncode(
            value,
            out var bytes,
            out var failure
        )) {
            throw new WorldReplayCodecException(message: $"the canonical {what} leaf refused while writing .puckreplay: {failure}");
        }

        writer.WriteBlock(value: bytes);
    }
    private static void WritePeerEvent(WireWriter writer, IReadOnlyList<WorldPeerEventEntry> entries, IReadOnlyList<WorldGrant> grants, bool revoked) {
        writer.WriteArray(
            items: entries,
            writeItem: static (w, entry) => {
                if (!WorldWireLeaves.TryWritePeerEventEntry(
                    peer: entry,
                    writer: w
                )) {
                    throw new WorldReplayCodecException(message: $"no .puckreplay wire value for peer event entry {entry.Identity.Describe()} (source '{entry.Source}').");
                }
            }
        );
        writer.WriteArray(
            items: grants,
            writeItem: (w, grant) => WriteGrantLeaf(
                grant: grant,
                revoke: revoked,
                writer: w
            )
        );
    }
    private static void WritePrincipal(WireWriter writer, Principal principal) {
        if (!WorldWireCodec.TryWritePrincipal(
            principal: principal,
            writer: writer
        )) {
            throw new WorldReplayCodecException(message: $"no .puckreplay wire value for {nameof(PrincipalKind)}.{principal.Kind} — the world's own program never rides the tape as an actor.");
        }
    }
    // Deliberately its OWN small leaf, never WorldSubmissionCodec's TryEncodeRebuild/TryDecodeRebuild: that leaf's
    // shape REQUIRES an embedded document for Load/Reload (the ordinary submission needs it to cross the loopback),
    // while the tape must NEVER carry one — Drive re-reads PathHint fresh, which is the content-address proof. The
    // kind byte is the one WorldWireTags table both leaves share.
    private static void WriteRebuildLeaf(WireWriter writer, WorldReplayEntry.Rebuild rebuild) {
        if (!WorldWireTags.TryToWire(
            value: rebuild.Kind,
            wire: out var kind
        )) {
            throw new WorldReplayCodecException(message: $"no .puckreplay wire value for {nameof(WorldRebuildKind)}.{rebuild.Kind}.");
        }

        writer.WriteByte(value: kind);
        writer.WriteBoolean(value: rebuild.Force);
        writer.WriteNullableString(value: rebuild.PathHint);
        writer.WriteString(value: rebuild.ContentHash);
    }
    // The transfer leaf: its semantic fields (see WorldReplayEntry.Transfer's own remarks) followed by an
    // FNV-1a content signature folded over those SAME fields, length-prefixing every string so two distinct
    // field sequences can never fold to the same signature regardless of what any one field contains (the identical
    // netstring argument WorldSessionResolver.ScopedSegment already documents for the same reason). This entry sits
    // OUTSIDE the population hash's own coverage — a crossing's destination/scope/generation/outcome text is not
    // simulation state — so this signature is the ONLY thing on the tape that would ever catch a tampered byte here;
    // ReadTransferEntry recomputes it from the DECODED fields and refuses BY NAME on a disagreement.
    private static void WriteTransferLeaf(WireWriter writer, WorldReplayEntry.Transfer transfer) {
        writer.WriteUInt64(value: transfer.TransferId);
        writer.WriteString(value: transfer.Target);
        writer.WriteBoolean(value: transfer.TargetRemote);
        writer.WriteString(value: transfer.DestinationName);
        writer.WriteString(value: transfer.ScopeKey);
        writer.WriteUInt64(value: transfer.GenerationId);
        writer.WriteString(value: transfer.Outcome);
        writer.WriteArray(
            items: transfer.DepartedSlots,
            writeItem: static (w, slot) => w.WriteInt32(value: slot)
        );
        writer.WriteUInt64(value: ComputeTransferSignature(transfer: transfer));
    }

    /// <summary>Rehydrates a fresh authoritative world from this recording and re-drives the recorded server-input stream
    /// through it, returning the per-tick pose-hash trace — the offline re-drive the replay/verify side runs (the record
    /// side samples the live population instead). A fresh <see cref="WorldServer"/>/<see cref="WorldPopulation"/> is built
    /// from the embedded definition (its boot image is the starting body state), the recorded seats re-join and are
    /// re-seated on their pinned locomotion rates (so the catalog's current values cannot steer the re-drive), then each
    /// tick's authority entries apply in recorded order — commands, grants, revokes, and sessions interleaved exactly
    /// as they were submitted, before the step, as the live command-apply window does — and its intents buffer and
    /// drain at the step.
    /// Exactly the live per-tick order, and re-applying the grant changes is what gives the re-driven world the same
    /// authority table the live one had.
    /// <para><b>The embedded definition's addons re-mount and re-run here.</b> Guest driving never crossed the loopback
    /// and so was never recorded; re-running the same pinned modules (the same content-hash enforcement, from the same
    /// embedded document) is what reproduces it. That is the stronger property, not a weaker one: a replay that replayed
    /// recorded guest output would prove only that the tape was read back, while re-running proves the guests are
    /// themselves deterministic. Mount and disclosure lines print again during a drive; that is the honest cost.</para>
    /// <para><b>The mount pin is checked before the first tick.</b> The fresh runtime's own receipts are compared,
    /// index by index, against <see cref="MountedAddons"/> — the sequence recorded at record-start — and any
    /// disagreement (an addon this tape pins that did not mount, one that mounted and was never pinned, or a
    /// module-hash or fuel difference) refuses the drive outright, naming the addon and both sides. Without that gate a moved module or a
    /// faulted mount would surface as an ordinary trajectory mismatch at some arbitrary tick, sending the reader into
    /// the simulation for a defect that is in the tree.</para></summary>
    /// <param name="profiles">The profile catalog seats re-resolve their name against, and the drift report reads the
    /// current rates from (read-only here — the re-drive seats bodies on detached pinned handles instead of mutating
    /// this catalog's shared ones).</param>
    /// <param name="engines">The registered screen-machine engines (the same DI-collected set the live session ran
    /// under) — handed to <paramref name="machineHostFactory"/> so the shadow world's own machine host boots and
    /// steps machines off this exactly like the live one did, so a tape spanning a CAS-pinned <c>screen.insert</c>
    /// re-proves the pinned content still matches. Disposed at the end of this drive — the shadow
    /// machines exist only for the duration of the re-drive.</param>
    /// <param name="machineHostFactory">Builds a fresh <see cref="IWorldMachineHost"/> over the re-deserialized
    /// definition's screens and <paramref name="engines"/> — <c>Puck.World.Server</c> carries no reference to the
    /// concrete host, so it cannot build one itself (the same "the server calls out, the composition root supplies
    /// the capability" shape as <paramref name="addonHostFactory"/>).</param>
    /// <param name="addonHostFactory">Builds a fresh <see cref="IWorldAddonHost"/> over the re-deserialized
    /// definition and the shadow server — a fresh guest set must mount per drive, so this is a factory rather than a
    /// shared instance. Disposed at the end of this drive. The factory must return a host already attached to the
    /// <see cref="WorldServer"/> it is handed (or rely on <see cref="Drive"/> attaching it) — a host that never
    /// reaches <see cref="WorldServer.AttachAddons"/> re-drives with no guests and produces a MATCH that proves
    /// nothing.</param>
    /// <param name="documents">The source a recorded <c>world.load</c>/<c>world.reload</c> re-reads its origin through
    /// (<see cref="WorldServer.RebuildDocuments"/>); <see langword="null"/> reads JSON files directly.</param>
    /// <returns>The per-tick population-hash trace, one entry per recorded tick. <see cref="DriveTraces"/> exposes both
    /// traces and is what verdicts use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profiles"/>, <paramref name="engines"/>,
    /// <paramref name="machineHostFactory"/>, or <paramref name="addonHostFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The guests this recording pins are not the guests the fresh world would
    /// re-run.</exception>
    /// <exception cref="WorldReplayCodecException">A host-side codec bug: an authority-entry kind the re-drive switch
    /// below does not handle, which would silently drop a recorded input from the re-drive.</exception>
    public ulong[] Drive(WorldOwnedWorlds profiles, IEnumerable<IMachineEngine> engines, Func<IReadOnlyList<WorldScreen>, IEnumerable<IMachineEngine>, string?, WorldOutputHub?, IWorldMachineHost> machineHostFactory, Func<WorldDefinition, WorldServer, IWorldAddonHost> addonHostFactory, IWorldDocumentSource? documents = null) => DriveTraces(
        addonHostFactory: addonHostFactory,
        documents: documents,
        engines: engines,
        machineHostFactory: machineHostFactory,
        profiles: profiles
    ).Pose;
    /// <summary>Re-drives once and returns both the pose inspection trace and authoritative state-system trace.</summary>
    public WorldReplayHashTraces DriveTraces(WorldOwnedWorlds profiles, IEnumerable<IMachineEngine> engines, Func<IReadOnlyList<WorldScreen>, IEnumerable<IMachineEngine>, string?, WorldOutputHub?, IWorldMachineHost> machineHostFactory, Func<WorldDefinition, WorldServer, IWorldAddonHost> addonHostFactory, IWorldDocumentSource? documents = null) {
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: engines);
        ArgumentNullException.ThrowIfNull(argument: machineHostFactory);
        ArgumentNullException.ThrowIfNull(argument: addonHostFactory);

        var definition = WorldDefinitionSerialization.Deserialize(documentDirectory: DocumentDirectory, utf8Json: DefinitionJson);

        // The header's SimulationRate must agree with the embedded definition's own SimulationRateHz — the same
        // internal-consistency family as the mount pin below. A disagreement re-driven anyway would produce a
        // different trajectory reporting as an ordinary MISMATCH rather than naming the real cause.
        if (SimulationRate != ((uint)definition.SimulationRateHz)) {
            throw ReplayRefusal.RateMismatch.Raise(message: $"This .puckreplay recording's header pins {SimulationRate} Hz, but its own embedded world definition authors {definition.SimulationRateHz} Hz — re-driving at the wrong step size would produce a different trajectory that reports as an ordinary MISMATCH rather than naming the real cause. This tape is internally inconsistent; re-record it.");
        }

        var population = new WorldPopulation(definition: definition);
        using var machines = machineHostFactory(
            definition.Screens,
            engines,
            DocumentPath,
            null
        );
        // A fresh, unconfigured render envelope reads as "fits" — the replay applies no render-growing edits, and the
        // authoritative simulation never consults GPU capacity, so no probe is needed offline.
        var server = new WorldServer(
            definition: definition,
            population: population,
            profiles: profiles,
            envelope: new WorldRenderEnvelope(),
            machines: machines,
            instanceIdentity: Instance
        );

        server.RebuildDocuments = documents;
        server.PipelineSources = ((PipelineSourceDirectory is { } pipelineSources)
            ? new WorldPipelineSources(documentDirectory: pipelineSources)
            : null);
        server.Extensions.EnterReplay();

        // Replay verification is side-effect-free: a rule's 'save' effect re-derives deterministically like any
        // other rule effect, but writing the world's own file is engine I/O. The tap narrates why no file write
        // happened; the population hash this drive compares never depends on whether the write occurred. This
        // shadow server binds no sink of its own — Server cannot construct a console-writing one — so the line
        // reaches a caller only if it attaches one first.
        server.SaveEffectTap = tick => server.Output.Narrate(
            channel: "replay",
            text: $"[replay: save effect suppressed (tick {tick}) — replay verification is side-effect-free]"
        );

        SeatRecordedSeats(
            definition: definition,
            population: population,
            profiles: profiles,
            server: server
        );

        // Mounted AFTER the seats re-join and after the server's constructor applied the embedded document's grants —
        // the same order the live composition mounts in, so the mount-time disclosure reads the same settled table.
        // Owns a Wasmtime engine, hence the using.
        using var addons = addonHostFactory(
            definition,
            server
        );

        // The factory's product is useless to this drive unless the shadow server pumps it — attach structurally
        // here rather than relying on the factory to have self-attached, because a conforming factory that returns
        // an unattached host would otherwise re-drive silently addon-less. Safe to call even when the factory did
        // self-attach: AttachAddons only reassigns m_addons and resizes the three per-tick contention arrays.
        server.AttachAddons(runtime: addons);

        // Checked before the first tick: a guest that failed to mount, mounted from moved module bytes, or mounted
        // under a different budget would otherwise re-drive a different world and surface as an ordinary trajectory
        // mismatch at some arbitrary tick instead of naming the real cause. This is ALSO the initial-document-
        // mounting half of the outcome pin below: comparing prepare receipts before tick zero refuses the drive on
        // a boot-time mismatch the same way a per-mutation outcome disagreement refuses it later.
        VerifyMountedAddons(
            recorded: MountedAddons,
            fresh: addons.Receipts
        );

        // The mutation outcome pin: this drive calls WorldServer.EnqueueMutation directly (never through
        // ApplyEnvelope, which is what threads MutationOutcomeTap on the LIVE path — see its own remarks), so each
        // Mutation case below passes its own completion straight to EnqueueMutation's outcomeObserved parameter,
        // queued in enqueue order and drained in the SAME order immediately after each tick's server.Step call, so
        // the Nth queued outcome always answers the Nth recorded Mutation entry that tick.
        var replayedMutationOutcomes = new Queue<bool>();

        var stepTicks = ResolveStepWidth(
            simulationRate: SimulationRate,
            recordedTickCount: Ticks.Count
        );
        var poseHashes = new ulong[Ticks.Count];
        var authoritativeHashes = new ulong[Ticks.Count];

        for (var tick = 0; (tick < Ticks.Count); tick++) {
            var expectedMutationOutcomes = new List<bool>();

            ApplyRecordedTick(
                expectedMutationOutcomes: expectedMutationOutcomes,
                input: Ticks[tick],
                population: population,
                rebuildContentPin: static rebuild => rebuild.ContentHash,
                replayedMutationOutcomes: replayedMutationOutcomes,
                server: server
            );

            server.Advance(stepTicks: stepTicks);
            VerifyRecordedMutationOutcomes(
                expected: expectedMutationOutcomes,
                replayed: replayedMutationOutcomes,
                tick: tick
            );
            poseHashes[tick] = HashState(population: population);
            authoritativeHashes[tick] = WorldStateHashComposition.HashAuthoritative(
                server: server,
                tick: (server.NextInputTick - 1UL)
            );
        }

        return new WorldReplayHashTraces(
            Authoritative: authoritativeHashes,
            Pose: poseHashes
        );
    }
    /// <summary>Returns the deterministic per-tick population hash: every active body's fixed-point pose, rigid
    /// velocity/contact/rest residue, and carry relationship folded in index order, so two runs with identical input
    /// produce identical traces regardless of wall-clock or backend. This remains the diagnostic population trace;
    /// replay verdicts compare the wider <see cref="WorldStateHashComposition.HashAuthoritative"/> scope.</summary>
    /// <param name="population">The entity table to hash.</param>
    /// <returns>The state hash.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="population"/> is <see langword="null"/>.</exception>
    public static ulong HashState(WorldPopulation population) {
        ArgumentNullException.ThrowIfNull(argument: population);

        var hash = Fnv1aHash.Create();

        for (var index = 0; (index < population.Capacity); index++) {
            if (
                !population.IsActive(index: index) ||
                (population.EntryBody(index: index) is not { } body)
            ) {
                continue;
            }

            var position = body.FixedPosition;
            // The whole attitude, as its raw quaternion lanes — never a float, never a re-derived Euler angle, so the
            // hash stays bit-exact and machine-independent. Hashing only an extracted yaw would leave pitch and roll
            // uncovered: two free-motion trajectories differing in only those axes would hash identically.
            var orientation = body.FixedOrientation;

            hash.Add(value: ((uint)index));
            hash.Add(value: position.X.Value);
            hash.Add(value: position.Y.Value);
            hash.Add(value: position.Z.Value);
            hash.Add(value: orientation.X.Value);
            hash.Add(value: orientation.Y.Value);
            hash.Add(value: orientation.Z.Value);
            hash.Add(value: orientation.W.Value);
            // The heading scalar rides beside the quaternion rather than being replaced by it: under the grounded
            // program m_yaw is authoritative and the quaternion is built from it, so an edit too small to survive that
            // construction would otherwise go unhashed. Under the free program this lane is redundant with the
            // quaternion's own extracted yaw, but deterministically so, and costs only one fold.
            hash.Add(value: body.FixedYaw.Value);
            // A rigid body's velocity, resting latch/hold-tick counter, both restitution edge latches, and each
            // latch's own consecutive-miss streak are simulation state a hashed pose alone does not cover: two
            // identical poses with different velocities diverge on the very next tick, and two identical
            // poses/velocities with different hold-tick counts, edge latches, or miss streaks diverge the first tick
            // either one's own threshold decides differently (the resting latch closing early or late, a wall hit
            // reading as a continuation of an earlier contact instead of a fresh impact, or a latch dropping on a
            // miss one run tolerates and the other does not). Zero/false for a locomotion body, costing one fold each.
            hash.Add(value: body.RigidVelocity.X.Value);
            hash.Add(value: body.RigidVelocity.Y.Value);
            hash.Add(value: body.RigidVelocity.Z.Value);
            hash.Add(value: body.RigidAngularVelocity.X.Value);
            hash.Add(value: body.RigidAngularVelocity.Y.Value);
            hash.Add(value: body.RigidAngularVelocity.Z.Value);
            hash.Add(value: (body.Resting
                ? 1UL
                : 0UL));
            hash.Add(value: body.RigidRestingHoldTicks);
            hash.Add(value: (body.RigidGroundContacting
                ? 1UL
                : 0UL));
            hash.Add(value: (body.RigidObstructionContacting
                ? 1UL
                : 0UL));
            hash.Add(value: unchecked((uint)body.RigidGroundMissStreak));
            hash.Add(value: unchecked((uint)body.RigidObstructionMissStreak));
            // Two identical poses/velocities that differ only in WHO carries WHOM diverge the instant either side's
            // next tick reads it — a carried body's own advance is a no-op FollowCarrier is about to overwrite, so
            // the relationship itself is state a hashed pose does not otherwise cover. -1 means no relationship.
            hash.Add(value: unchecked((uint)(body.Carrying ?? -1)));
            hash.Add(value: unchecked((uint)(body.CarriedBy ?? -1)));
            // Scale changes every scaled quantity a later tick reads (colliders, speed, mass) before pose diverges.
            hash.Add(value: unchecked((ulong)body.Scale.Value));
        }

        return hash.Value;
    }
}
