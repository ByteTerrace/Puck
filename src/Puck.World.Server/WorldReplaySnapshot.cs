using System.Globalization;
using System.Text;
using Puck.Abstractions.Machines;
using Puck.Hosting;
using Puck.Maths;
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
    internal sealed record Mutation(WorldMutation Value, WorldPrincipal Actor, bool Outcome) : WorldReplayEntry;
    /// <summary>A journal undo (<c>world.undo</c>) — buffered to the tick boundary on re-drive exactly as a live
    /// submission is, so the recorded journal tail is replayed back through the same all-or-nothing gates.</summary>
    /// <param name="Count">The number of journal entries to undo.</param>
    /// <param name="Actor">The principal that submitted it.</param>
    internal sealed record Undo(int Count, WorldPrincipal Actor) : WorldReplayEntry;
    /// <summary>A window-composition override (<c>view.override</c>) and its acting principal — applied
    /// synchronously on re-drive, exactly as it is live.</summary>
    /// <param name="Value">The composition submission.</param>
    /// <param name="Actor">The principal that submitted it.</param>
    internal sealed record Composition(WorldComposition Value, WorldPrincipal Actor) : WorldReplayEntry;
    /// <summary>A read-back query and the identity the envelope stamped. Re-executed on re-drive at the same
    /// position it held live, so any read-back state its composition touches is reproduced; the answer itself is
    /// discarded, since a query moves no simulation state and therefore cannot alter either replay trace.</summary>
    /// <param name="Value">The query.</param>
    /// <param name="Actor">The identity the envelope stamped.</param>
    internal sealed record Query(WorldQuery Value, WorldPrincipal Actor) : WorldReplayEntry;
    /// <summary>A server-authored peer admission, emitted at the point of effect.</summary>
    /// <param name="Value">The ordered admission event.</param>
    internal sealed record PeerAdmitted(WorldServerEvent.PeerAdmitted Value) : WorldReplayEntry;
    /// <summary>A server-authored peer disconnect, emitted at the point of effect.</summary>
    /// <param name="Value">The ordered disconnect event.</param>
    internal sealed record PeerDisconnected(WorldServerEvent.PeerDisconnected Value) : WorldReplayEntry;
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
    internal sealed record Rebuild(WorldRebuildKind Kind, string? PathHint, bool Force, string ContentHash, WorldPrincipal Actor) : WorldReplayEntry;
    /// <summary>A live screen-machine lifecycle change (<c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/
    /// <c>.options</c>/<c>.link</c>/<c>.unlink</c>) — screen ops join the ordered domain and the tape as their own
    /// authority entry kind, applying synchronously on re-drive exactly as they do live (see
    /// <see cref="Server.WorldServer.ApplyScreenOp"/>).</summary>
    /// <param name="Value">The screen op.</param>
    /// <param name="ContentHash">The CAS pin (a real <c>sha256-64</c> hash, or
    /// <see cref="Server.WorldMachineHost.ContentAbsentSignature"/>) a recorded <see cref="WorldScreenOp.Insert"/> or
    /// machine-booting <see cref="WorldScreenOp.Select"/> entry carries (Select shares Insert's own CAS pin — a
    /// magazine entry's document-declared path is not immune to on-disk drift either) — <see langword="null"/> for
    /// every other op kind, and this rides the tape regardless of whether the op succeeded (a failed insert/select
    /// still pins whatever it read, or the absence sentinel when it could not read anything at all, including an
    /// engine-resolution failure, since content is signed before engine resolution is even attempted and is never
    /// left null on that path).</param>
    /// <param name="Actor">The principal that submitted the op.</param>
    internal sealed record ScreenOp(WorldScreenOp Value, string? ContentHash, WorldPrincipal Actor) : WorldReplayEntry;
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
    /// <summary>A same-process crossing's decided outcome — the local multi-authority tape contract — recorded by
    /// <see cref="Puck.World.WorldReplayTape.NoteTransfer"/> the moment <c>Puck.World.WorldInstanceHost.ApplyTransfer</c>
    /// commits or aborts a transfer touching the boot instance. Acts on the departure half only, at re-drive:
    /// <see cref="WorldReplaySnapshot.Drive"/> constructs one shadow <see cref="Server.WorldServer"/> for the boot
    /// instance alone, so a member arriving from elsewhere is structurally unreachable (there is no source instance's
    /// population to arrive from), but a member leaving boot's own population is a fact this shadow world can and
    /// must reproduce — <see cref="DepartedBootSlots"/> is what makes that honest: without it, the live trace's pose
    /// hash stops covering a departed body (an inactive index contributes nothing to
    /// <see cref="WorldReplaySnapshot.HashState"/>) while the replay's shadow body would keep integrating right
    /// through the crossing. <see cref="DestinationName"/>/<see cref="ScopeKey"/>/<see cref="GenerationId"/>/
    /// <see cref="Outcome"/> remain narration only — proving the outcome reproducible by name, never re-deriving the
    /// destination's own simulation. The entry's byte-level integrity (including <see cref="DepartedBootSlots"/>
    /// itself) is enforced separately: this sits partly outside the pose hash's own coverage (the destination/scope/
    /// generation/outcome text is never simulation state), so <see cref="WorldReplaySnapshot.ReadTransferEntry"/>
    /// recomputes a content signature from every decoded field and refuses by name
    /// (<see cref="ReplayRefusal.TransferEventTampered"/>) on a disagreement, never a plausible-looking ordinary
    /// trajectory mismatch.</summary>
    /// <param name="TransferId">The transfer id minted for this crossing.</param>
    /// <param name="DestinationName">The resolved destinations row name.</param>
    /// <param name="ScopeKey">The resolved scope key.</param>
    /// <param name="GenerationId">The resolver-issued generation id the cohort resolved against.</param>
    /// <param name="Outcome">A short canonical outcome summary — narration only.</param>
    /// <param name="DepartedBootSlots">The 0-based boot local-seat slots this crossing actually removed from boot's
    /// own population (empty for a refused or aborted transfer, or one whose source is not boot) — replayed against
    /// the shadow world's own population at re-drive, see this entry's own remarks.</param>
    internal sealed record Transfer(ulong TransferId, string DestinationName, string ScopeKey, ulong GenerationId, string Outcome, IReadOnlyList<int> DepartedBootSlots) : WorldReplayEntry;
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
/// <see cref="PlayerIntent"/> crosses as six raw <see cref="FixedQ4816"/> lanes, so the replay currency is the
/// simulation's own numeric type rather than a conversion of it. (The serialized command stream carries the authored
/// float fields of the recorded <see cref="WorldCommand"/>s verbatim; those are authored values — the numbers an
/// operator typed — which round-trip bit-exactly through the shared command leaf and quantize deterministically at
/// one apply site each. They never break the guarantee, but they are not absent from the on-disk form.) A
/// fresh world built from this recording and driven by the recorded stream produces bit-identical per-tick pose and authoritative hashes
/// on every run, machine, and backend at a fixed code version. <see cref="Drive"/> is the offline re-drive the
/// replay/verify side runs; the record side samples the live population instead, so a match proves the fresh re-drive
/// reproduces the running session, not merely another re-drive of itself.</para>
/// <para>Wire form. Every enum that reaches this codec crosses as an explicitly declared wire value, mapped by an
/// exhaustive switch in both directions and never by an ordinal cast — including the <see cref="WorldSection"/> ordinal
/// nested inside a section <see cref="GrantSubject"/>'s value lane. The channel vector (<see cref="PlayerIntent"/>) and
/// a channel press's ordinal cross as plain integers instead of a pinned bit set now that <c>ActionLanes</c> has
/// dissolved. A member the set does not
/// cover is refused by name at write; a byte the set does not name is refused loudly at read. The header also carries
/// the mounted addon set as recorded-at-mount receipts (<see cref="MountedAddons"/>): because the re-drive re-runs the
/// document's guests rather than replaying their output, the identity of what mounts is part of what the tape pins, and
/// <see cref="Drive"/> refuses a disagreement before the first tick. The header also carries the recording's own
/// <see cref="SimulationRate"/> — simulation input, not metadata, since it is what <see cref="Drive"/> derives the
/// step size from — and <see cref="Drive"/> refuses a disagreement with the embedded definition's own
/// <see cref="WorldDefinition.SimulationRateHz"/>, right after deserializing it, for the identical reason the mount
/// pin does: a wrong step size re-drives a different trajectory that would otherwise report as an ordinary mismatch.
/// There is exactly one tape shape: the leading
/// magic is the opaque shape-identity value — re-keyed whenever the shape changes, never incremented — and the
/// ShapeToken that follows it stays pinned at 1 permanently; a file carrying either the wrong magic or the wrong
/// token is refused rather than read tolerantly.</para>
/// <para><b>Public, not <c>internal</c> behind an <c>InternalsVisibleTo</c> grant</b> (widen the member, not the
/// assembly) — every instance member here was already <c>public</c>; only the class declaration
/// (and <see cref="WorldReplaySeat"/>/<see cref="WorldReplayProfilePin"/>/<see cref="WorldReplayTickInput"/>/the
/// <see cref="WorldReplayEntry"/> base it composes with) had not caught up. Widened so
/// <c>tests/Puck.World.Tests</c> — which reads this surface directly per its own documented no-IVT/no-reflection
/// convention — can exercise <see cref="ResolveStepWidth"/> without a grant.</para>
/// </remarks>
public sealed class WorldReplaySnapshot {
    // Opaque shape-identity value: re-keyed to a new opaque value whenever the tape's byte layout or hashed
    // semantics change, never incremented as a counter. ShapeToken (below) is pinned at 1 permanently, so it cannot
    // by itself distinguish an incompatible shape — Magic alone carries that distinction, and a file carrying either
    // the wrong Magic or the wrong ShapeToken is refused rather than read tolerantly.
    //
    // This is one of two independent re-key boundaries, never one key covering both. The other is the guest ABI's
    // artifact pins (Puck.Scripting.AddonAbi). A tape-shape change does not re-key the ABI, and an ABI break does
    // not re-key this constant — MountedAddons below records what actually mounted, so an ABI break invalidates an
    // existing tape through receipt mismatch without a byte-offset change here.
    private const uint Magic = 0x504B_4155u; // "PKAU" — puck replay tape; re-keyed for the authoritative hash trace. Retired: 0x504B_464B ("PKFK"), 0x504B_4146 ("PKAF"), 0x504B_4C4B ("PKLK"), 0x504B_4341 ("PKCA"), 0x504B_5754 ("PKWT").
    // A shape-identity token, not a version sequence, pinned at 1 permanently: this build writes and reads exactly
    // one tape shape, so there is no older shape to be newer than. A token that disagrees refuses the file by name
    // (found vs. expected) instead of decoding it as nonsense.
    private const uint ShapeToken = 1u;

    /// <summary>Gets the record-start world definition as its canonical UTF-8 JSON — the rehydrated starting state.</summary>
    public required byte[] DefinitionJson { get; init; }
    /// <summary>Gets the fork provenance — the parent tape and the count of leading tick groups copied from it —
    /// or <see langword="null"/> for a tape recorded from boot. Header narration only; <see cref="Drive"/> never
    /// reads it, because the copied prefix already sits in <see cref="Ticks"/> like any other recorded tick.</summary>
    public WorldReplayForkProvenance? ForkedFrom { get; init; }
    /// <summary>Gets the guests mounted at record-start, in mount order — the recorded-at-mount receipts (name, module
    /// content hash, fuel, lane) the re-drive re-establishes before it runs a tick. Empty when the recorded session
    /// mounted nothing, which is itself pinned: a re-drive that mounts a guest against an empty set is refused.</summary>
    public required IReadOnlyList<WorldAddonReceipt> MountedAddons { get; init; }
    /// <summary>Gets the live session's per-tick pose hash trace — one entry per recorded tick, sampled off the live
    /// population after each tick's server step, so the last entry is the state the running world actually reached. A
    /// replay recomputes this diagnostic trace by re-driving the recording through a fresh world
    /// (<see cref="Drive"/>); <see cref="RecordedAuthoritativeHashes"/> drives the verdict. Its length always equals
    /// <see cref="TickCount"/>; keeping every entry rather than only the tail lets pose inspection name the tick
    /// visible motion first diverged.</summary>
    public required ulong[] RecordedHashes { get; init; }
    /// <summary>Gets the live session's tail pose-only hash, or <c>0</c> when nothing was recorded.</summary>
    public ulong RecordedPoseTailHash => ((RecordedHashes.Length > 0) ? RecordedHashes[^1] : 0UL);
    /// <summary>Gets the live session's per-tick authoritative state-system hashes: world state, rule and interaction
    /// latches, body action state, live fields, and poses. Replay verdicts compare this trace;
    /// <see cref="RecordedHashes"/> remains the pose-only diagnostic trace.</summary>
    public required ulong[] RecordedAuthoritativeHashes { get; init; }
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
        hash.Add(value: ((uint)transfer.DepartedBootSlots.Count));

        foreach (var slot in transfer.DepartedBootSlots) {
            hash.Add(value: ((uint)slot));
        }

        return hash.Value;
    }
    // The rate as a readable decimal beside its exact raw lane. The decimal is for the operator; the raw is the number
    // the comparison actually ran on, printed so a drift the decimal rounds away is still legible in the report.
    private static string Describe(FixedQ4816? rate) {
        return ((rate is { } value)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{((double)value):0.####} (raw {value.Value})"
            )
            : "kit (no claimed rate)"
        );
    }
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
    private static void ReadAddonLanePlaceholder(BinaryReader reader) {
        var wire = reader.ReadByte();

        if (wire != Wire.AddonLaneReceiptConstant) {
            throw new InvalidDataException(message: $"unknown .puckreplay mounted-addon lane-slot wire value {wire} — the lane axis is deleted and this slot is now a pinned constant ({Wire.AddonLaneReceiptConstant}), carried only so the tape shape does not move ahead of its own re-key.");
        }
    }
    private static WorldCommand ReadCommandLeaf(BinaryReader reader) => ReadLeaf<WorldCommand>(
        reader: reader,
        tryDecode: WorldSubmissionCodec.TryDecodeCommand,
        what: "command"
    );

    // The one shape every fixed leaf codec's TryDecodeX follows: a span of bytes decodes to a T or names a
    // WorldCodecFailure. `value is null` is reachable only for the reference-typed leaves (WorldCommand,
    // WorldComposition, WorldMutation, WorldQuery, SessionRequest) — a defensive check against a codec that reports
    // success with no value, always false for the struct-typed leaves (WorldDesignation, WorldGrant).
    private delegate bool TryDecodeLeaf<T>(ReadOnlySpan<byte> bytes, out T? value, out WorldCodecFailure failure);

    private static T ReadLeaf<T>(BinaryReader reader, string what, TryDecodeLeaf<T> tryDecode) {
        var bytes = ReadLeafBytes(
            reader: reader,
            what: $"{what} leaf"
        );

        if (
            !tryDecode(bytes, out var value, out var failure) ||
            (value is null)
        ) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay {what} leaf: {failure}");
        }

        return value;
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

        if (
            stream.CanSeek &&
            ((((long)count) * minimumBytesEach) > (stream.Length - stream.Position))
        ) {
            throw new InvalidDataException(message: $"Truncated .puckreplay recording ({what} count {count} exceeds the bytes remaining).");
        }

        return count;
    }
    private static WorldDesignation ReadDesignationLeaf(BinaryReader reader) => ReadLeaf<WorldDesignation>(
        reader: reader,
        tryDecode: WorldSubmissionCodec.TryDecodeDesignation,
        what: "designation"
    );
    private static WorldComposition ReadCompositionLeaf(BinaryReader reader) => ReadLeaf<WorldComposition>(
        reader: reader,
        tryDecode: WorldSubmissionCodec.TryDecodeComposition,
        what: "composition"
    );
    private static WorldMutation ReadMutationLeaf(BinaryReader reader) => ReadLeaf<WorldMutation>(
        reader: reader,
        tryDecode: WorldSubmissionCodec.TryDecodeMutation,
        what: "mutation"
    );
    private static WorldQuery ReadQueryLeaf(BinaryReader reader) => ReadLeaf<WorldQuery>(
        reader: reader,
        tryDecode: WorldSubmissionCodec.TryDecodeQuery,
        what: "query"
    );
    private static WorldReplayEntry ReadEntry(BinaryReader reader) {
        var kind = reader.ReadByte();

        return kind switch {
            0 => new WorldReplayEntry.Command(Value: ReadCommandLeaf(reader: reader)),
            1 => new WorldReplayEntry.Grant(
            Value: ReadGrantLeaf(
                reader: reader,
                revoke: false
            ),
            Actor: ReadPrincipal(reader: reader)
        ),
            2 => new WorldReplayEntry.Revoke(
            Value: ReadGrantLeaf(
                reader: reader,
                revoke: true
            ),
            Actor: ReadPrincipal(reader: reader)
        ),
            3 => new WorldReplayEntry.PeerAdmitted(Value: ReadPeerAdmitted(reader: reader)),
            4 => new WorldReplayEntry.PeerDisconnected(Value: ReadPeerDisconnected(reader: reader)),
            6 => ReadRebuildEntry(reader: reader),
            7 => ReadScreenOpEntry(reader: reader),
            8 => new WorldReplayEntry.Session(Value: ReadSessionLeaf(reader: reader)),
            9 => new WorldReplayEntry.Designation(
            Value: ReadDesignationLeaf(reader: reader),
            Actor: ReadPrincipal(reader: reader)
        ),
            10 => new WorldReplayEntry.RateLever(Paused: reader.ReadBoolean()),
            11 => ReadTransferEntry(reader: reader),
            12 => new WorldReplayEntry.Mutation(
            Value: ReadMutationLeaf(reader: reader),
            Actor: ReadPrincipal(reader: reader),
            Outcome: reader.ReadBoolean()
        ),
            13 => new WorldReplayEntry.Undo(
            Count: reader.ReadInt32(),
            Actor: ReadPrincipal(reader: reader)
        ),
            14 => new WorldReplayEntry.Composition(
            Value: ReadCompositionLeaf(reader: reader),
            Actor: ReadPrincipal(reader: reader)
        ),
            15 => new WorldReplayEntry.Query(
            Value: ReadQueryLeaf(reader: reader),
            Actor: ReadPrincipal(reader: reader)
        ),
            16 => new WorldReplayEntry.LinkDelivery(Adjacency: reader.ReadString()),
            _ => throw new InvalidDataException(message: $"unknown .puckreplay authority entry discriminant {kind}."),
        };
    }
    private static WorldGrant ReadGrantLeaf(BinaryReader reader, bool revoke) => ReadLeaf<WorldGrant>(
        reader: reader,
        tryDecode: (revoke
            ? WorldSubmissionCodec.TryDecodeRevoke
            : WorldSubmissionCodec.TryDecodeGrant
        ),
        what: (revoke
            ? "revoke"
            : "grant")
    );
    private static IntentSubmission ReadIntent(BinaryReader reader) {
        var tick = reader.ReadUInt64();
        var entityIndex = reader.ReadInt32();
        var intent = WorldWireCodec.ReadIntent(reader: reader);
        var principal = ReadPrincipal(reader: reader);
        var heldChannels = WorldWireCodec.ReadIntent(reader: reader);
        var measuredHoldTicks = reader.ReadInt32();

        return new IntentSubmission(
            EntityIndex: entityIndex,
            HeldChannels: heldChannels,
            Intent: intent,
            MeasuredHoldTicks: measuredHoldTicks,
            Principal: principal,
            Tick: tick
        );
    }
    private static IntentSource ReadIntentSource(BinaryReader reader) {
        if (!WorldWireCodec.TryReadIntentSource(
            reader: reader,
            source: out var source,
            wire: out var wire
        )) {
            throw new InvalidDataException(message: $"unknown .puckreplay {nameof(IntentSource)} wire value {wire}.");
        }

        return source;
    }
    private static byte[] ReadLeafBytes(BinaryReader reader, string what) {
        var length = ReadCount(
            minimumBytesEach: 1,
            reader: reader,
            what: what
        );
        var bytes = reader.ReadBytes(count: length);

        if (bytes.Length != length) {
            throw new InvalidDataException(message: $"Truncated .puckreplay recording ({what}).");
        }

        return bytes;
    }
    // ---------------------------------------------------------------------------------------------------------------

    // The PressLane hold is written as (bool present, float value) so the float slot is always consumed; the value is
    // meaningful only when the present flag is set, else the command carried no explicit hold.
    private static float? ReadNullableSingle(BinaryReader reader) {
        var present = reader.ReadBoolean();
        var value = reader.ReadSingle();

        return (present
            ? value
            : null
        );
    }
    private static WorldServerEvent.PeerAdmitted ReadPeerAdmitted(BinaryReader reader) {
        var value = ReadPeerEvent(
            reader: reader,
            revoked: false
        );

        return new WorldServerEvent.PeerAdmitted(
            Entries: value.Entries,
            MintedGrants: value.Grants
        );
    }
    private static WorldServerEvent.PeerDisconnected ReadPeerDisconnected(BinaryReader reader) {
        var value = ReadPeerEvent(
            reader: reader,
            revoked: true
        );

        return new WorldServerEvent.PeerDisconnected(
            Entries: value.Entries,
            RevokedGrants: value.Grants
        );
    }
    private static (IReadOnlyList<WorldPeerEventEntry> Entries, IReadOnlyList<WorldGrant> Grants) ReadPeerEvent(BinaryReader reader, bool revoked) {
        var entryCount = ReadCount(
            minimumBytesEach: 16,
            reader: reader,
            what: "peer event entry"
        );
        var entries = new List<WorldPeerEventEntry>(capacity: entryCount);

        for (var index = 0; (index < entryCount); index++) {
            var bodyIndex = reader.ReadInt32();
            var generation = reader.ReadInt32();
            var source = ReadIntentSource(reader: reader);
            var identity = ReadPrincipal(reader: reader);
            var identityDomain = reader.ReadString();
            var identitySubject = reader.ReadString();
            var authorityTransferred = reader.ReadBoolean();
            var catalogRig = reader.ReadByte();
            var placementId = (reader.ReadBoolean()
                ? reader.ReadString()
                : null
            );

            if (catalogRig >= WorldLookSource.Catalog.RigCount) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay peer event entry: catalog rig {catalogRig} is outside 0..{(WorldLookSource.Catalog.RigCount - 1)}.");
            }

            if (
                (identity.Kind != PrincipalKind.Peer) ||
                (identity.Index != bodyIndex) ||
                (identity.Generation != generation)
            ) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay peer event entry: identity {identity.Describe()} does not match body {bodyIndex}, generation {generation}.");
            }

            entries.Add(item: new WorldPeerEventEntry(
                AuthorityTransferred: authorityTransferred,
                BodyIndex: bodyIndex,
                CatalogRig: catalogRig,
                Generation: generation,
                Identity: identity,
                IdentityDomain: identityDomain,
                IdentitySubject: identitySubject,
                PlacementId: placementId,
                Source: source
            ));
        }

        var grantCount = ReadCount(
            minimumBytesEach: 5,
            reader: reader,
            what: "peer event grant"
        );
        var grants = new List<WorldGrant>(capacity: grantCount);

        for (var index = 0; (index < grantCount); index++) {
            grants.Add(item: ReadGrantLeaf(
                reader: reader,
                revoke: revoked
            ));
        }

        return (entries, grants);
    }
    private static WorldPrincipal ReadPrincipal(BinaryReader reader) {
        if (!WorldWireCodec.TryReadPrincipal(
            kindWire: out var wire,
            principal: out var principal,
            reader: reader
        )) {
            throw new InvalidDataException(message: $"unknown .puckreplay {nameof(PrincipalKind)} wire value {wire}.");
        }

        return principal;
    }
    private static WorldReplayProfilePin? ReadProfilePin(BinaryReader reader) {
        if (!reader.ReadBoolean()) {
            return null;
        }

        var name = reader.ReadString();
        var moveSpeed = ReadNullableRate(reader: reader);
        var turnSpeed = ReadNullableRate(reader: reader);

        return new WorldReplayProfilePin(
            MoveSpeed: moveSpeed,
            Name: name,
            TurnSpeed: turnSpeed
        );
    }
    private static WorldReplayEntry ReadRebuildEntry(BinaryReader reader) {
        var kind = RebuildKindFromWire(reader: reader);
        var force = reader.ReadBoolean();
        var pathHint = WorldWireCodec.ReadNullableString(reader: reader);
        var contentHash = reader.ReadString();
        var actor = ReadPrincipal(reader: reader);

        if (
            ((kind == WorldRebuildKind.Reset) && (pathHint is not null)) ||
            ((kind != WorldRebuildKind.Reset) && (pathHint is null))
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
    private static WorldReplayEntry ReadScreenOpEntry(BinaryReader reader) {
        var bytes = ReadLeafBytes(
            reader: reader,
            what: "screen-op leaf"
        );

        if (
            !WorldSubmissionCodec.TryDecodeScreenOp(
            bytes: bytes,
            failure: out var failure,
            screenOp: out var screenOp
        ) ||
            (screenOp is null)
        ) {
            throw new InvalidDataException(message: $"Corrupt .puckreplay screen-op leaf: {failure}");
        }

        var contentHash = WorldWireCodec.ReadNullableString(reader: reader);
        var actor = ReadPrincipal(reader: reader);

        return new WorldReplayEntry.ScreenOp(
            Actor: actor,
            ContentHash: contentHash,
            Value: screenOp
        );
    }
    private static SessionRequest ReadSessionLeaf(BinaryReader reader) => ReadLeaf<SessionRequest>(
        reader: reader,
        tryDecode: WorldSubmissionCodec.TryDecodeSession,
        what: "session"
    );
    private static WorldReplayEntry ReadTransferEntry(BinaryReader reader) {
        var transferId = reader.ReadUInt64();
        var destinationName = reader.ReadString();
        var scopeKey = reader.ReadString();
        var generationId = reader.ReadUInt64();
        var outcome = reader.ReadString();
        var departedCount = ReadCount(
            minimumBytesEach: 4,
            reader: reader,
            what: "transfer departed-slot"
        );
        var departedBootSlots = new int[departedCount];

        for (var index = 0; (index < departedCount); index++) {
            var slot = reader.ReadInt32();

            if (((uint)slot) >= WorldBodiesLimits.LocalSeatCount) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: transfer departed-slot {slot} is out of range (expected 0..{(WorldBodiesLimits.LocalSeatCount - 1)}).");
            }

            departedBootSlots[index] = slot;
        }

        var storedSignature = reader.ReadUInt64();
        var entry = new WorldReplayEntry.Transfer(
            DepartedBootSlots: departedBootSlots,
            DestinationName: destinationName,
            GenerationId: generationId,
            Outcome: outcome,
            ScopeKey: scopeKey,
            TransferId: transferId
        );
        var recomputed = ComputeTransferSignature(transfer: entry);

        if (recomputed != storedSignature) {
            throw ReplayRefusal.TransferEventTampered.Raise(message: $"transfer {transferId} -> '{destinationName}' (scope '{scopeKey}', generation {generationId}): stored content signature 0x{storedSignature:x16} disagrees with the recomputed 0x{recomputed:x16} — the tape's transfer event bytes were corrupted or edited after recording (this entry sits outside the pose hash's own coverage, so nothing else on the tape would catch it)");
        }

        return entry;
    }
    private static WorldRebuildKind RebuildKindFromWire(BinaryReader reader) {
        var wire = reader.ReadByte();

        return wire switch {
            Wire.RebuildKindReset => WorldRebuildKind.Reset,
            Wire.RebuildKindLoad => WorldRebuildKind.Load,
            Wire.RebuildKindReload => WorldRebuildKind.Reload,
            _ => throw new InvalidDataException(message: $"unknown .puckreplay {nameof(WorldRebuildKind)} wire value {wire}."),
        };
    }
    private static byte RebuildKindToWire(WorldRebuildKind kind) => kind switch {
        WorldRebuildKind.Reset => Wire.RebuildKindReset,
        WorldRebuildKind.Load => Wire.RebuildKindLoad,
        WorldRebuildKind.Reload => Wire.RebuildKindReload,
        _ => throw new WorldReplayCodecException(message: $"no .puckreplay wire value for {nameof(WorldRebuildKind)}.{kind} — give the new member one in the pinned wire set."),
    };
    // Reports a live/pinned rate drift without refusing: a drifted profile is a perfectly replayable recording, so
    // nothing here throws. Without this, an operator who edited a profile between record and verify would see a
    // MATCH with no way to tell a profile edit from a genuine determinism regression.
    private static void ReportProfileDrift(WorldOwnedWorlds profiles, WorldReplayProfilePin pin) {
        if (profiles.Find(name: pin.Name) is not { } live) {
            // Drift all the way to absent. The re-drive is unaffected — the pinned handle needs no catalog entry — but
            // an operator reading a MATCH for a profile that no longer exists deserves to be told why it still ran.
            Console.Error.WriteLine(value: $"[replay.profile: '{pin.Name}' is pinned by this recording but is no longer in the live catalog; the replay used the pinned rates (move {Describe(rate: pin.MoveSpeed)}, turn {Describe(rate: pin.TurnSpeed)})]");

            return;
        }

        ReportRateDrift(
            name: pin.Name,
            field: "move-speed",
            pinned: pin.MoveSpeed,
            live: live.FixedMoveSpeed
        );
        ReportRateDrift(
            name: pin.Name,
            field: "turn-speed",
            pinned: pin.TurnSpeed,
            live: live.FixedTurnSpeed
        );
    }
    // Compared on the RAW fixed lane, never on the rendered decimal: a drift too small to show in four places is still
    // a different trajectory, and a comparison that reads the display string would miss exactly those.
    private static void ReportRateDrift(string name, string field, FixedQ4816? pinned, FixedQ4816? live) {
        if (pinned?.Value == live?.Value) {
            return;
        }

        Console.Error.WriteLine(value: $"[replay.profile: '{name}' {field} drifted since record-start — pinned {Describe(rate: pinned)}, live {Describe(rate: live)}; the replay used the PINNED value, so this verdict reports the recording, not the edit]");
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
    // The receipt's former lane byte. The tape shape must not move for this alone, so this slot keeps emitting and
    // validating a byte even though WorldAddonReceipt no longer carries a Lane to encode.
    private static void WriteAddonLanePlaceholder(BinaryWriter writer) {
        writer.Write(value: Wire.AddonLaneReceiptConstant);
    }
    private static void WriteCommandLeaf(BinaryWriter writer, WorldCommand command) => WriteLeaf(
        tryEncode: WorldSubmissionCodec.TryEncodeCommand,
        value: command,
        what: "command",
        writer: writer
    );

    // The write-side twin of ReadLeaf: every fixed leaf codec's TryEncodeX turns a T into bytes or names a
    // WorldCodecFailure.
    private delegate bool TryEncodeLeaf<T>(T value, out byte[] bytes, out WorldCodecFailure failure);

    private static void WriteLeaf<T>(BinaryWriter writer, T value, string what, TryEncodeLeaf<T> tryEncode) {
        if (!tryEncode(value, out var bytes, out var failure)) {
            throw new WorldReplayCodecException(message: $"the canonical {what} leaf refused while writing .puckreplay: {failure}");
        }

        WriteLeafBytes(
            bytes: bytes,
            writer: writer
        );
    }
    private static void WriteDesignationLeaf(BinaryWriter writer, WorldDesignation designation) => WriteLeaf(
        tryEncode: WorldSubmissionCodec.TryEncodeDesignation,
        value: designation,
        what: "designation",
        writer: writer
    );
    private static void WriteCompositionLeaf(BinaryWriter writer, WorldComposition composition) => WriteLeaf(
        tryEncode: WorldSubmissionCodec.TryEncodeComposition,
        value: composition,
        what: "composition",
        writer: writer
    );
    private static void WriteMutationLeaf(BinaryWriter writer, WorldMutation mutation) => WriteLeaf(
        tryEncode: WorldSubmissionCodec.TryEncodeMutation,
        value: mutation,
        what: "mutation",
        writer: writer
    );
    private static void WriteQueryLeaf(BinaryWriter writer, WorldQuery query) => WriteLeaf(
        tryEncode: WorldSubmissionCodec.TryEncodeQuery,
        value: query,
        what: "query",
        writer: writer
    );
    // The authority-INPUT tagged union: one discriminant byte, then the entry's own payload. Kept distinct from the
    // command tagged union below — that one discriminates WorldCommand's sealed subtypes, this one discriminates what
    // KIND of authority write crossed the link at all.
    private static void WriteEntry(BinaryWriter writer, WorldReplayEntry entry) {
        switch (entry) {
            case WorldReplayEntry.Command command:
                writer.Write(value: ((byte)0));
                WriteCommandLeaf(
                    writer: writer,
                    command: command.Value
                );

                break;
            case WorldReplayEntry.Grant grant:
                writer.Write(value: ((byte)1));
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
                writer.Write(value: ((byte)2));
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
            case WorldReplayEntry.Session session:
                writer.Write(value: ((byte)8));
                WriteSessionLeaf(
                    writer: writer,
                    request: session.Value
                );

                break;
            case WorldReplayEntry.Designation designation:
                writer.Write(value: ((byte)9));
                WriteDesignationLeaf(
                    writer: writer,
                    designation: designation.Value
                );
                WritePrincipal(
                    writer: writer,
                    principal: designation.Actor
                );

                break;
            case WorldReplayEntry.PeerAdmitted admitted:
                writer.Write(value: ((byte)3));
                WritePeerEvent(
                    writer: writer,
                    entries: admitted.Value.Entries,
                    grants: admitted.Value.MintedGrants,
                    revoked: false
                );

                break;
            case WorldReplayEntry.PeerDisconnected disconnected:
                writer.Write(value: ((byte)4));
                WritePeerEvent(
                    writer: writer,
                    entries: disconnected.Value.Entries,
                    grants: disconnected.Value.RevokedGrants,
                    revoked: true
                );

                break;
            case WorldReplayEntry.Rebuild rebuild:
                writer.Write(value: ((byte)6));
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
                writer.Write(value: ((byte)7));
                WriteScreenOpLeaf(
                    screenOp: screenOp,
                    writer: writer
                );
                WritePrincipal(
                    writer: writer,
                    principal: screenOp.Actor
                );

                break;
            case WorldReplayEntry.RateLever rateLever:
                writer.Write(value: ((byte)10));
                writer.Write(value: rateLever.Paused);

                break;
            case WorldReplayEntry.Transfer transfer:
                writer.Write(value: ((byte)11));
                WriteTransferLeaf(
                    transfer: transfer,
                    writer: writer
                );

                break;
            case WorldReplayEntry.Mutation mutation:
                writer.Write(value: ((byte)12));
                WriteMutationLeaf(
                    writer: writer,
                    mutation: mutation.Value
                );
                WritePrincipal(
                    writer: writer,
                    principal: mutation.Actor
                );
                // The recorded accept/refuse outcome — see WorldReplayEntry.Mutation's own remarks. Every entry this
                // writer ever sees was resolved synchronously within the tick it was recorded on (the same tick
                // MutationOutcomeTap fired), so Outcome is never speculative by the time it reaches here.
                writer.Write(value: mutation.Outcome);

                break;
            case WorldReplayEntry.Undo undo:
                writer.Write(value: ((byte)13));
                writer.Write(value: undo.Count);
                WritePrincipal(
                    writer: writer,
                    principal: undo.Actor
                );

                break;
            case WorldReplayEntry.Composition composition:
                writer.Write(value: ((byte)14));
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
                writer.Write(value: ((byte)15));
                WriteQueryLeaf(
                    writer: writer,
                    query: query.Value
                );
                WritePrincipal(
                    writer: writer,
                    principal: query.Actor
                );

                break;
            case WorldReplayEntry.LinkDelivery linkDelivery:
                writer.Write(value: ((byte)16));
                writer.Write(value: linkDelivery.Adjacency);

                break;
            default:
                throw new WorldReplayCodecException(message: $"no .puckreplay encoding for authority entry kind '{entry.GetType().Name}'.");
        }
    }
    private static void WriteGrantLeaf(BinaryWriter writer, WorldGrant grant, bool revoke) => WriteLeaf(
        tryEncode: (revoke
            ? WorldSubmissionCodec.TryEncodeRevoke
            : WorldSubmissionCodec.TryEncodeGrant
        ),
        value: grant,
        what: (revoke
            ? "revoke"
            : "grant"),
        writer: writer
    );
    private static void WriteIntent(BinaryWriter writer, in IntentSubmission submission) {
        writer.Write(value: submission.Tick);
        writer.Write(value: submission.EntityIndex);
        WorldWireCodec.WriteIntent(
            intent: submission.Intent,
            writer: writer
        );
        WritePrincipal(
            writer: writer,
            principal: submission.Principal
        );
        WorldWireCodec.WriteIntent(
            intent: submission.HeldChannels,
            writer: writer
        );
        writer.Write(value: submission.MeasuredHoldTicks);
    }
    private static void WriteIntentSource(BinaryWriter writer, IntentSource source) {
        if (!WorldWireCodec.TryWriteIntentSource(
            source: source,
            writer: writer
        )) {
            throw new WorldReplayCodecException(message: $"no .puckreplay wire value for {nameof(IntentSource)} '{source}'.");
        }
    }
    private static void WriteLeafBytes(BinaryWriter writer, byte[] bytes) {
        writer.Write(value: bytes.Length);
        writer.Write(buffer: bytes);
    }
    private static void WritePeerEvent(BinaryWriter writer, IReadOnlyList<WorldPeerEventEntry> entries, IReadOnlyList<WorldGrant> grants, bool revoked) {
        writer.Write(value: entries.Count);

        foreach (var entry in entries) {
            writer.Write(value: entry.BodyIndex);
            writer.Write(value: entry.Generation);
            WriteIntentSource(
                writer: writer,
                source: entry.Source
            );
            WritePrincipal(
                writer: writer,
                principal: entry.Identity
            );
            writer.Write(value: entry.IdentityDomain);
            writer.Write(value: entry.IdentitySubject);
            writer.Write(value: entry.AuthorityTransferred);
            writer.Write(value: entry.CatalogRig);
            writer.Write(value: (entry.PlacementId is not null));
            if (entry.PlacementId is { } placementId) {
                writer.Write(value: placementId);
            }
        }

        writer.Write(value: grants.Count);

        foreach (var grant in grants) {
            WriteGrantLeaf(
                grant: grant,
                revoke: revoked,
                writer: writer
            );
        }
    }
    private static void WritePrincipal(BinaryWriter writer, WorldPrincipal principal) {
        if (!WorldWireCodec.TryWritePrincipal(
            principal: principal,
            writer: writer
        )) {
            throw new WorldReplayCodecException(message: $"no .puckreplay wire value for {nameof(PrincipalKind)}.{principal.Kind} — a Document/World principal never rides the tape.");
        }
    }
    // The seat's profile pin rides the same present-flag convention as WorldWireCodec.WriteNullableString: a
    // profileless seat writes the flag and nothing else, and its body falls back to the seat kit's own tuning on the
    // re-drive exactly as it did live. The two rates cross as their RAW fixed-point lanes — the simulation's own
    // currency, never a float — so a recorded rate re-enters WorldBody.Advance bit-identical.
    private static void WriteProfilePin(BinaryWriter writer, WorldReplayProfilePin? pin) {
        writer.Write(value: (pin is not null));

        if (pin is { } value) {
            writer.Write(value: value.Name);
            WriteNullableRate(
                rate: value.MoveSpeed,
                writer: writer
            );
            WriteNullableRate(
                rate: value.TurnSpeed,
                writer: writer
            );
        }
    }
    private static FixedQ4816? ReadNullableRate(BinaryReader reader) =>
        (reader.ReadBoolean()
            ? new FixedQ4816(Value: reader.ReadInt64())
            : null
        );
    private static void WriteNullableRate(BinaryWriter writer, FixedQ4816? rate) {
        writer.Write(value: rate.HasValue);

        if (rate is { } value) {
            writer.Write(value: value.Value);
        }
    }
    // Deliberately its OWN small leaf, never WorldSubmissionCodec's TryEncodeRebuild/TryDecodeRebuild: that leaf's
    // shape REQUIRES an embedded document for Load/Reload (the ordinary submission needs it to cross the loopback),
    // while the tape must NEVER carry one — Drive re-reads PathHint fresh, which is the content-address proof. This
    // codec's own discriminant set for WorldRebuildKind (below, in Wire) is independent of WorldSubmissionCodec's,
    // matching this file's own doctrine on why two frozen surfaces are never welded together by reuse.
    private static void WriteRebuildLeaf(BinaryWriter writer, WorldReplayEntry.Rebuild rebuild) {
        writer.Write(value: RebuildKindToWire(kind: rebuild.Kind));
        writer.Write(value: rebuild.Force);
        WorldWireCodec.WriteNullableString(
            value: rebuild.PathHint,
            writer: writer
        );
        writer.Write(value: rebuild.ContentHash);
    }
    // Reuses WorldSubmissionCodec's canonical screen-op leaf directly — unlike Rebuild, a screen op carries no
    // embedded document either way, so there is no shape asymmetry forcing a fork here. The nullable ContentHash and
    // the actor are tape-only metadata riding beside the shared leaf.
    private static void WriteScreenOpLeaf(BinaryWriter writer, WorldReplayEntry.ScreenOp screenOp) {
        if (!WorldSubmissionCodec.TryEncodeScreenOp(
            screenOp: screenOp.Value,
            bytes: out var bytes,
            failure: out var failure
        )) {
            throw new WorldReplayCodecException(message: $"the canonical screen-op leaf refused while writing .puckreplay: {failure}");
        }

        WriteLeafBytes(
            bytes: bytes,
            writer: writer
        );
        WorldWireCodec.WriteNullableString(
            value: screenOp.ContentHash,
            writer: writer
        );
    }
    private static void WriteSessionLeaf(BinaryWriter writer, SessionRequest request) => WriteLeaf(
        tryEncode: WorldSubmissionCodec.TryEncodeSession,
        value: request,
        what: "session",
        writer: writer
    );
    // The local-transfer leaf: five semantic fields (see WorldReplayEntry.Transfer's own remarks) followed by an
    // FNV-1a content signature folded over those SAME five fields, length-prefixing every string so two distinct
    // field sequences can never fold to the same signature regardless of what any one field contains (the identical
    // netstring argument WorldSessionResolver.ScopedSegment already documents for the same reason). This entry sits
    // OUTSIDE the pose hash's own coverage — a crossing's destination/scope/generation/outcome text is not
    // simulation state — so this signature is the ONLY thing on the tape that would ever catch a tampered byte here;
    // ReadTransferEntry recomputes it from the DECODED fields and refuses BY NAME on a disagreement.
    private static void WriteTransferLeaf(BinaryWriter writer, WorldReplayEntry.Transfer transfer) {
        writer.Write(value: transfer.TransferId);
        writer.Write(value: transfer.DestinationName);
        writer.Write(value: transfer.ScopeKey);
        writer.Write(value: transfer.GenerationId);
        writer.Write(value: transfer.Outcome);
        writer.Write(value: transfer.DepartedBootSlots.Count);

        foreach (var slot in transfer.DepartedBootSlots) {
            writer.Write(value: slot);
        }

        writer.Write(value: ComputeTransferSignature(transfer: transfer));
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
    /// under) — the shadow world's own <see cref="WorldMachineHost"/> boots and steps machines off this exactly like
    /// the live one did, so a tape spanning a CAS-pinned <c>screen.insert</c> re-proves the pinned content still
    /// matches. Disposed at the end of this drive — the shadow
    /// machines exist only for the duration of the re-drive.</param>
    /// <param name="addonHostFactory">Builds a fresh <see cref="IWorldAddonHost"/> over the re-deserialized
    /// definition and the shadow server — a fresh guest set must mount per drive, so this is a factory rather than a
    /// shared instance. Disposed at the end of this drive. The factory must return a host already attached to the
    /// <see cref="WorldServer"/> it is handed (or rely on <see cref="Drive"/> attaching it) — a host that never
    /// reaches <see cref="WorldServer.AttachAddons"/> re-drives with no guests and produces a MATCH that proves
    /// nothing.</param>
    /// <returns>The per-tick pose-hash trace, one entry per recorded tick. <see cref="DriveTraces"/> exposes both
    /// traces and is what verdicts use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profiles"/>, <paramref name="engines"/>, or
    /// <paramref name="addonHostFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The guests this recording pins are not the guests the fresh world would
    /// re-run.</exception>
    /// <exception cref="WorldReplayCodecException">A host-side codec bug: an authority-entry kind the re-drive switch
    /// below does not handle, which would silently drop a recorded input from the re-drive.</exception>
    public ulong[] Drive(WorldOwnedWorlds profiles, IEnumerable<IScreenMachineEngine> engines, Func<WorldDefinition, WorldServer, IWorldAddonHost> addonHostFactory) => DriveTraces(
        profiles: profiles,
        engines: engines,
        addonHostFactory: addonHostFactory
    ).Pose;

    /// <summary>Re-drives once and returns both the pose inspection trace and authoritative state-system trace.</summary>
    public WorldReplayHashTraces DriveTraces(WorldOwnedWorlds profiles, IEnumerable<IScreenMachineEngine> engines, Func<WorldDefinition, WorldServer, IWorldAddonHost> addonHostFactory) {
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: engines);
        ArgumentNullException.ThrowIfNull(argument: addonHostFactory);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: DefinitionJson);

        // The header's SimulationRate must agree with the embedded definition's own SimulationRateHz — the same
        // internal-consistency family as the mount pin below. A disagreement re-driven anyway would produce a
        // different trajectory reporting as an ordinary MISMATCH rather than naming the real cause.
        if (SimulationRate != ((uint)definition.SimulationRateHz)) {
            throw ReplayRefusal.RateMismatch.Raise(message: $"This .puckreplay recording's header pins {SimulationRate} Hz, but its own embedded world definition authors {definition.SimulationRateHz} Hz — re-driving at the wrong step size would produce a different trajectory that reports as an ordinary MISMATCH rather than naming the real cause. This tape is internally inconsistent; re-record it.");
        }

        var population = new WorldPopulation(definition: definition);
        using var machines = new WorldMachineHost(
            screens: definition.Screens,
            engines: engines
        );
        // A fresh, unconfigured render envelope reads as "fits" — the replay applies no render-growing edits, and the
        // authoritative simulation never consults GPU capacity, so no probe is needed offline.
        var server = new WorldServer(
            definition: definition,
            population: population,
            profiles: profiles,
            envelope: new WorldRenderEnvelope(),
            machines: machines
        );

        // Replay verification is side-effect-free: a rule's 'save' effect re-derives deterministically like any
        // other rule effect, but writing the world's own file is engine I/O. Wire an explicit narration-only tap
        // (rather than leaving it null implicitly) so a verify run reports why no file write happened; the pose hash
        // this drive compares never depends on whether the write occurred.
        server.SaveEffectTap = tick => Console.Error.WriteLine(value: $"[replay: save effect suppressed (tick {tick}) — replay verification is side-effect-free]");

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

            var context = new FixedStepContext(
                ElapsedTicks: (((ulong)(tick + 1)) * stepTicks),
                StepTicks: stepTicks,
                Tick: ((ulong)tick)
            );

            server.Step(context: in context);
            VerifyRecordedMutationOutcomes(
                expected: expectedMutationOutcomes,
                replayed: replayedMutationOutcomes,
                tick: tick
            );
            poseHashes[tick] = HashState(population: population);
            authoritativeHashes[tick] = WorldRuntimeStateHash.HashAuthoritative(
                server: server,
                tick: (server.NextInputTick - 1UL)
            );
        }

        return new WorldReplayHashTraces(Pose: poseHashes, Authoritative: authoritativeHashes);
    }
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
                case WorldReplayEntry.Transfer transferEvent:
                    // Acts on the departure half only: this shadow world is the boot instance alone, with no
                    // destination instance to move a body into, so an arrival is structurally unreachable. A
                    // departed body must stop contributing to HashState here exactly as it did live. A slot
                    // already inactive is a no-op by TryDetachSeatForTransfer's own contract, never a throw.
                    foreach (var departedSlot in transferEvent.DepartedBootSlots) {
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
    /// <summary>Re-joins this recording's seats into <paramref name="server"/> and re-seats each profiled one on a
    /// detached handle carrying its pinned locomotion rates — the recorded values, never the live catalog's current
    /// ones, which are only read for the drift report. Shared by the offline <see cref="Drive"/> and the live drive's
    /// boot image.</summary>
    /// <param name="server">A server at its boot image, with no seat joined yet.</param>
    /// <param name="population">That server's population.</param>
    /// <param name="definition">The embedded definition, for the pinned handle's player defaults.</param>
    /// <param name="profiles">The live catalog the drift report reads.</param>
    internal void SeatRecordedSeats(WorldServer server, WorldPopulation population, WorldDefinition definition, WorldOwnedWorlds profiles) {
        foreach (var seat in Seats) {
            // Seat(slot) directly: there is no PlayerRoster (and so no claim) behind this join to ask PrincipalOf of.
            _ = server.ApplySession(request: new SessionRequest.Join(
                Principal: WorldPrincipal.Seat(slot: seat.Slot),
                Slot: seat.Slot,
                IdentityName: seat.Profile?.Name,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            ));

            if (seat.Profile is not { } pin) {
                continue;
            }

            ReportProfileDrift(
                pin: pin,
                profiles: profiles
            );
            population.SetSeatProfile(
                slot: seat.Slot,
                profile: WorldIdentity.Pinned(
                    name: pin.Name,
                    moveSpeed: pin.MoveSpeed,
                    turnSpeed: pin.TurnSpeed,
                    defaults: definition.PlayerDefaults
                )
            );
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
    /// <summary>Returns the deterministic per-tick state hash: every active body's fixed-point pose — position and the full 6DOF
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
        }

        return hash.Value;
    }
    /// <summary>Reads a recording from a stream.</summary>
    /// <param name="stream">The source stream.</param>
    /// <returns>The deserialized recording.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The stream is not a <c>.puckreplay</c> tape, or is an older shape this
    /// build does not read (refused outright — greenfield keeps no read-side tolerance for a foreign shape); is
    /// truncated/corrupt (including a truncated or malformed length-prefixed string, normalized here from the BCL's own
    /// <see cref="EndOfStreamException"/>/<see cref="FormatException"/> so every corruption this reader detects throws
    /// the same exception type); carries a value no pinned wire set names; pins one addon name twice; or pins a seat
    /// slot out of range or twice.</exception>
    public static WorldReplaySnapshot Read(Stream stream) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        using var reader = new BinaryReader(
            input: stream,
            encoding: Encoding.UTF8,
            leaveOpen: true
        );

        try {
            var magic = reader.ReadUInt32();
            var shapeToken = reader.ReadUInt32();

            if (
                (magic != Magic) ||
                (shapeToken != ShapeToken)
            ) {
                throw ReplayRefusal.ShapeMismatch.Raise(message: $"Not a Puck replay tape, or an older shape this build does not read — re-record it. (found magic 0x{magic:x8}, shape token {shapeToken}; this build reads magic 0x{Magic:x8}, shape token {ShapeToken} only)");
            }

            var simulationRate = reader.ReadUInt32();
            WorldReplayForkProvenance? forkedFrom = null;

            if (reader.ReadBoolean()) {
                var parentName = reader.ReadString();
                var forkTick = reader.ReadInt32();

                if (string.IsNullOrWhiteSpace(value: parentName)) {
                    throw new InvalidDataException(message: "Corrupt .puckreplay recording: the fork provenance names an empty parent tape.");
                }

                if (forkTick < 0) {
                    throw new InvalidDataException(message: $"Corrupt .puckreplay recording: the fork provenance tick {forkTick} is negative.");
                }

                forkedFrom = new WorldReplayForkProvenance(
                    ParentName: parentName,
                    Tick: forkTick
                );
            }

            var hashCount = ReadCount(
                minimumBytesEach: 8,
                reader: reader,
                what: "hash"
            );
            var recordedHashes = new ulong[hashCount];

            for (var index = 0; (index < hashCount); index++) {
                recordedHashes[index] = reader.ReadUInt64();
            }

            var authoritativeHashCount = ReadCount(
                minimumBytesEach: 8,
                reader: reader,
                what: "authoritative hash"
            );
            var recordedAuthoritativeHashes = new ulong[authoritativeHashCount];
            for (var index = 0; index < authoritativeHashCount; index++) {
                recordedAuthoritativeHashes[index] = reader.ReadUInt64();
            }

            var definitionLength = ReadCount(
                minimumBytesEach: 1,
                reader: reader,
                what: "definition"
            );
            var definitionJson = reader.ReadBytes(count: definitionLength);

            if (definitionJson.Length != definitionLength) {
                throw new InvalidDataException(message: "Truncated .puckreplay recording (definition).");
            }

            // 11 = the smallest possible receipt: two 1-byte string length prefixes, the u64 fuel, and the placeholder lane byte.
            var addonCount = ReadCount(
                minimumBytesEach: 11,
                reader: reader,
                what: "mounted addon"
            );
            var mountedAddons = new List<WorldAddonReceipt>(capacity: addonCount);

            for (var index = 0; (index < addonCount); index++) {
                var name = reader.ReadString();
                var hash = reader.ReadString();
                var fuel = reader.ReadUInt64();

                ReadAddonLanePlaceholder(reader: reader);

                // The set is compared BY NAME at re-drive, so two receipts under one name make the pin ambiguous: whichever
                // the comparison happened to reach first would decide, and the other would be silently unenforced.
                if (Find(
                    name: name,
                    receipts: mountedAddons
                ) is not null) {
                    throw new InvalidDataException(message: $"Corrupt .puckreplay recording: addon '{name}' is pinned twice in the mounted set — a name identifies exactly one mounted guest.");
                }

                mountedAddons.Add(item: new WorldAddonReceipt(
                    Fuel: fuel,
                    Hash: hash,
                    Name: name
                ));
            }

            var seatCount = ReadCount(
                minimumBytesEach: 5,
                reader: reader,
                what: "seat"
            );
            var seats = new List<WorldReplaySeat>(capacity: seatCount);

            for (var index = 0; (index < seatCount); index++) {
                var slot = reader.ReadInt32();
                var profile = ReadProfilePin(reader: reader);

                // An out-of-range slot indexes straight into WorldPopulation's local-seat array during Drive
                // (Join's own range check only refuses the session reply; SetSeatProfile does not check again),
                // so it is refused here, before that reach, rather than crashing the host with an index exception.
                if (((uint)slot) >= WorldBodiesLimits.LocalSeatCount) {
                    throw new InvalidDataException(message: $"Corrupt .puckreplay recording: seat slot {slot} is out of range (expected 0..{(WorldBodiesLimits.LocalSeatCount - 1)}).");
                }

                // The set is compared BY SLOT at re-drive (Drive re-joins each recorded slot once), so two seats
                // pinning the same slot make the pin ambiguous — the same ambiguity the mounted-addon duplicate-name
                // guard above refuses for names.
                if (FindSeat(
                    seats: seats,
                    slot: slot
                ) is not null) {
                    throw new InvalidDataException(message: $"Corrupt .puckreplay recording: seat slot {slot} is pinned twice in the seat set — a slot identifies exactly one seat.");
                }

                seats.Add(item: new WorldReplaySeat(
                    Profile: profile,
                    Slot: slot
                ));
            }

            var tickCount = ReadCount(
                minimumBytesEach: 8,
                reader: reader,
                what: "tick"
            );
            var ticks = new List<WorldReplayTickInput>(capacity: tickCount);

            for (var index = 0; (index < tickCount); index++) {
                // 2 = the smallest possible entry: RateLever's discriminant byte plus its one bool. Every other kind
                // (Command's minimal principal, a Grant/Revoke leaf, ...) is strictly larger.
                var entryCount = ReadCount(
                    minimumBytesEach: 2,
                    reader: reader,
                    what: "authority entry"
                );
                var authority = new List<WorldReplayEntry>(capacity: entryCount);

                for (var entry = 0; (entry < entryCount); entry++) {
                    authority.Add(item: ReadEntry(reader: reader));
                }

                var intentCount = ReadCount(
                    minimumBytesEach: 60,
                    reader: reader,
                    what: "intent"
                );
                var intents = new List<IntentSubmission>(capacity: intentCount);

                for (var intent = 0; (intent < intentCount); intent++) {
                    intents.Add(item: ReadIntent(reader: reader));
                }

                ticks.Add(item: new WorldReplayTickInput(
                    Authority: authority,
                    Intents: intents
                ));
            }

            // The two lengths are equal BY CONSTRUCTION on the record side (one hash sampled per tick appended), so a file
            // where they disagree is doctored or truncated between the two sections. Reject it here rather than letting the
            // shorter one silently bound the comparison — a trace cut short would otherwise read as "matched everywhere it
            // was checked", which is exactly the shape of a verification that cannot fail.
            if (recordedHashes.Length != ticks.Count) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: {recordedHashes.Length} recorded hashes for {ticks.Count} ticks.");
            }
            if (recordedAuthoritativeHashes.Length != ticks.Count) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: {recordedAuthoritativeHashes.Length} recorded authoritative hashes for {ticks.Count} ticks.");
            }

            // A child carries its copied prefix in its own Ticks, so a provenance claiming more copied ticks than the
            // tape holds is doctored or truncated after the header.
            if (forkedFrom is { } provenance && (provenance.Tick > ticks.Count)) {
                throw new InvalidDataException(message: $"Corrupt .puckreplay recording: the fork provenance claims {provenance.Tick} tick(s) copied from '{provenance.ParentName}', but the tape carries only {ticks.Count}.");
            }

            return new WorldReplaySnapshot {
                DefinitionJson = definitionJson,
                ForkedFrom = forkedFrom,
                MountedAddons = mountedAddons,
                RecordedHashes = recordedHashes,
                RecordedAuthoritativeHashes = recordedAuthoritativeHashes,
                Seats = seats,
                SimulationRate = simulationRate,
                Ticks = ticks,
            };
        } catch (Exception exception) when ((exception is EndOfStreamException or FormatException)) {
            // BinaryReader.ReadString throws THESE directly on a truncated or malformed length-prefixed string — never
            // InvalidDataException — so without this normalization they would escape every caller's catch list (the
            // codec's own claim, stated at the class level, is that a corrupt or truncated tape is refused, never
            // crashes the host). Every OTHER corruption path in this reader already throws InvalidDataException by
            // hand (ReadCount, the pinned wire sets, the duplicate-name guards); this is the one BCL-thrown exception
            // shape this codec does not otherwise control, normalized here so the read side's catch list stays short
            // and its no-host-kill claim stays true.
            throw new InvalidDataException(
                message: $"Corrupt .puckreplay recording (truncated or malformed while reading a length-prefixed field): {exception.Message}",
                innerException: exception
            );
        }
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
    /// <summary>Serializes a recording to a stream in the <c>.puckreplay</c> binary form.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="recording">The recording to write.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="WorldReplayCodecException">A host-side codec bug: the recording carries a value no pinned
    /// wire set or discriminated encoding covers, or pins one mounted-addon name twice.</exception>
    public static void Write(Stream stream, WorldReplaySnapshot recording) {
        ArgumentNullException.ThrowIfNull(argument: recording);
        ArgumentNullException.ThrowIfNull(argument: stream);

        using var writer = new BinaryWriter(
            output: stream,
            encoding: Encoding.UTF8,
            leaveOpen: true
        );

        writer.Write(value: Magic);
        writer.Write(value: ShapeToken);
        // Right after the shape header, before anything else: the rate is simulation INPUT the same way the
        // definition and seats are, and Drive needs it before it can honestly derive a step size.
        writer.Write(value: recording.SimulationRate);
        // (bool present, string parent, int32 tick) — the fork provenance slot, right behind the rate it shares a
        // header with; absent for a tape recorded from boot.
        writer.Write(value: recording.ForkedFrom.HasValue);

        if (recording.ForkedFrom is { } forkedFrom) {
            if (
                string.IsNullOrWhiteSpace(value: forkedFrom.ParentName) ||
                (forkedFrom.Tick < 0) ||
                (forkedFrom.Tick > recording.Ticks.Count)
            ) {
                throw new WorldReplayCodecException(message: $"a .puckreplay recording's fork provenance is inconsistent (parent '{forkedFrom.ParentName}', {forkedFrom.Tick} copied tick(s) of {recording.Ticks.Count}) — a host bug, not tape data.");
            }

            writer.Write(value: forkedFrom.ParentName);
            writer.Write(value: forkedFrom.Tick);
        }

        writer.Write(value: recording.RecordedHashes.Length);

        foreach (var hash in recording.RecordedHashes) {
            writer.Write(value: hash);
        }

        writer.Write(value: recording.RecordedAuthoritativeHashes.Length);
        foreach (var hash in recording.RecordedAuthoritativeHashes) {
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
                if (string.Equals(
                    a: receipt.Name,
                    b: recording.MountedAddons[other].Name,
                    comparisonType: StringComparison.Ordinal
                )) {
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
            WriteProfilePin(
                writer: writer,
                pin: seat.Profile
            );
        }

        writer.Write(value: recording.Ticks.Count);

        foreach (var input in recording.Ticks) {
            writer.Write(value: input.Authority.Count);

            foreach (var entry in input.Authority) {
                WriteEntry(
                    entry: entry,
                    writer: writer
                );
            }

            writer.Write(value: input.Intents.Count);

            foreach (var intent in input.Intents) {
                WriteIntent(
                    submission: in intent,
                    writer: writer
                );
            }
        }
    }
    /// <summary>Serializes a recording to <paramref name="path"/> in one write: the whole tape is encoded to an
    /// in-memory buffer first (where every write-side throw in <see cref="Write"/> can still fire — an unmapped enum
    /// member, a duplicate mounted-addon name, any host-side codec bug — see their remarks), and only a complete
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

        Write(
            recording: recording,
            stream: buffer
        );

        File.WriteAllBytes(
            path: path,
            bytes: buffer.ToArray()
        );
    }

    // ---------------------------------------------------------------------------------------------------------------
    // This codec's OWN pinned wire set — the discriminants no shared table owns. Every enum crosses as a value
    // declared here or in Puck.World.Protocol (WorldWireTags for the grant vocabulary, WorldWireCodec for the leaf
    // layouts this tape shares with the submission wire), mapped by an exhaustive switch in both directions — never
    // by a cast, since a cast pins whatever ordinals the enum happens to have and a reorder/insert/delete would
    // silently change every saved tape's meaning.
    //
    // Frozen wire values: changing one invalidates every saved tape. The write side throws by name on a member the
    // set does not cover; the read side throws InvalidDataException naming the value it found, so a doctored or
    // drifted tape is refused rather than decoded as garbage.
    private static class Wire {
        // A fixed placeholder byte in the receipt's (now-unused) lane slot, kept constant so the receipt shape does
        // not move: the writer always emits it and the reader validates it as this constant.
        public const byte AddonLaneReceiptConstant = 1;
        public const byte RebuildKindLoad = 1;
        public const byte RebuildKindReload = 2;
        // WorldRebuildKind — this codec's OWN discriminant set, independent of WorldSubmissionCodec's identically-
        // numbered one (see WriteRebuildLeaf's remarks on why the two are never welded together).
        public const byte RebuildKindReset = 0;
    }
}
