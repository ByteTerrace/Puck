using System.Numerics;
using System.Text;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>Whether a participant has confirmed its profile (<see cref="Active"/>) or is still choosing one
/// (<see cref="Pending"/> — it has a viewport and a desaturated candidate avatar, and its movement inputs drive the
/// profile picker instead of locomotion).</summary>
internal enum ParticipantState {
    /// <summary>Still choosing a profile: the avatar renders desaturated and movement inputs cycle the candidate.</summary>
    Pending,

    /// <summary>Profile confirmed: the avatar takes full color and movement inputs drive locomotion.</summary>
    Active,
}

/// <summary>How a slot came to be filled — which decides whether it dissolves when its last device leaves.
/// <see cref="Permanent"/> (slot 0) never leaves even deviceless; <see cref="Device"/> dissolves when its last device
/// is reassigned away; <see cref="Script"/> stays until an explicit <c>player.leave</c>.</summary>
internal enum ParticipantOrigin {
    /// <summary>Slot 0 — always joined from boot, never leaves (still tape-drivable when deviceless).</summary>
    Permanent,

    /// <summary>Created by a device claiming a free slot; dissolves when its last device is reassigned away.</summary>
    Device,

    /// <summary>Created by a console <c>player.join</c>; stays until an explicit <c>player.leave</c>.</summary>
    Script,
}

/// <summary>The outcome of a device-reassignment gesture, for the verb to echo.</summary>
internal enum AssignOutcome {
    /// <summary>The roster was full — an unmapped device found no free slot to join at all (see
    /// <see cref="PlayerRoster.ResolveDeviceSlot"/>) — nothing changed.</summary>
    Ignored,

    /// <summary>The moving device is itself under an exclusive <see cref="PlayerRoster.TryClaimSlot"/> hold — a
    /// device-reassignment gesture has no meaning for it (see <see cref="PlayerRoster.AssignDevice"/>'s own remarks).
    /// Nothing changed.</summary>
    DeviceClaimed,

    /// <summary>The target slot is under an exclusive <see cref="PlayerRoster.TryClaimSlot"/> hold — a human device
    /// must never end up sharing, or silently acting through, a claimant's identity. Nothing changed.</summary>
    TargetClaimed,

    /// <summary>The device already owned the target slot — a friendly no-op.</summary>
    NoOp,

    /// <summary>The device moved onto an occupied slot, joining that team instantly.</summary>
    JoinedTeam,

    /// <summary>The device moved onto an empty slot, creating a pending player (a profile must be chosen).</summary>
    CreatedPending,

    /// <summary>The acting principal lacked Drive over the target body — refused loudly, nothing changed. Distinct
    /// from <see cref="Ignored"/> (which means "no room") so a caller never reports "roster full" for a plain
    /// authority refusal.</summary>
    Denied,
}

/// <summary>The outcome of a <c>player.confirm</c>, for the verb to echo.</summary>
internal enum ConfirmOutcome {
    /// <summary>The device could not be mapped (the roster was full) — nothing changed.</summary>
    Ignored,

    /// <summary>The device was unmapped and this press mapped it onto a pending slot (a first press joins; a second
    /// confirms) — a profile choice is owed.</summary>
    Joined,

    /// <summary>The device was unmapped and this press seated it with an already-active player (the share-player-1
    /// default) — no profile choice is owed, so it is not a pending join.</summary>
    Seated,

    /// <summary>A pending participant owning the device was promoted to active on its candidate profile.</summary>
    Confirmed,

    /// <summary>The device's participant was already active — a friendly no-op.</summary>
    AlreadyActive,

    /// <summary>The acting principal lacked Drive over the target body — refused loudly by the server. The
    /// participant stayed Pending; nothing changed.</summary>
    Denied,
}

/// <summary>The outcome of a <c>player.identity</c> identity set, for the verb to echo.</summary>
internal enum SetProfileOutcome {
    /// <summary>The target slot is not joined.</summary>
    NotJoined,

    /// <summary>The profile is already in use by another active player.</summary>
    InUse,

    /// <summary>The acting principal lacks Drive over the target slot's body — refused loudly by the server. Nothing
    /// changed.</summary>
    Denied,

    /// <summary>The profile was assigned and the participant is active.</summary>
    Ok,
}

/// <summary>The roster's recorded preferred-profile decision for a first-seen device.</summary>
internal enum DevicePreselectionKind {
    /// <summary>The keyboard has no durable controller preference.</summary>
    Keyboard,

    /// <summary>The transport cannot identify the same physical controller after reconnect.</summary>
    ConnectionOnly,

    /// <summary>No profile lists this device for the local machine.</summary>
    NoLocalPreference,

    /// <summary>The preferred profile became the pending candidate.</summary>
    Preferred,

    /// <summary>The preferred profile was already active, so ordinary seating applied.</summary>
    AlreadySeated,

    /// <summary>No unoccupied roster slot was available for the preferred profile, so ordinary seating applied.</summary>
    NoFreeSeat,
}

/// <summary>The outcome of a join attempt against a specific or auto-picked slot — three distinct failure shapes so
/// a caller (and its verb's echo) never conflates "no room", "already occupied", and "the actor was refused" (see
/// <see cref="PlayerRoster.JoinPending(int, ParticipantOrigin, WorldPrincipal)"/>).</summary>
internal enum JoinResult {
    /// <summary>A specific target slot was already occupied (or out of range). Nothing changed.</summary>
    Occupied,

    /// <summary>A next-free request found no empty slot anywhere in the roster. Nothing changed.</summary>
    Full,

    /// <summary>The server refused the acting principal's Drive grant over the target body. Nothing changed.</summary>
    Denied,

    /// <summary>The join was accepted and installed.</summary>
    Ok,
}

/// <summary>
/// The client's participant table: up to four slots, each a seat (its <see cref="SeatController"/> device-intent
/// producer plus a viewport on the layout ladder), carrying its confirmed-or-pending <see cref="ParticipantState"/>,
/// its <see cref="ParticipantOrigin"/>, and the <see cref="WorldIdentity"/> it selects (color and look-invert client
/// side; the authoritative body reads speeds off the same profile object). Seat occupancy mirrors to the server over
/// the session wire (join/leave/profile), so the entity table's seats match. A slot fills the same way whether driven
/// by the keyboard, a pad, or a scripted console verb.
/// </summary>
/// <remarks>
/// <para>
/// A player owns a device set (the keyboard is a device like any pad — its id is <see cref="InputDeviceId"/>
/// <see langword="default"/>, mapped to slot 0 from boot). Reassignment moves a device between slots: onto an occupied
/// slot it joins that team; onto an empty slot it creates a pending player (a profile must be chosen). A device joins
/// on its first routed signal (stick activity or a South/confirm press): the first pad seats with player 1 alongside
/// the keyboard (attaching to an already-seated player is not a join, so no profile choice is owed), and each later pad
/// takes the next free slot as a pending player.
/// </para>
/// <para>
/// Slot 0 (player 1) is always joined from boot and never leaves; it starts on the first authored profile. A stable
/// controller preference is applied separately when that device first produces input.
/// </para>
/// <para>
/// Single-threaded: every mutator runs during the command pump's <c>Collect</c>, and the frame source reads during
/// produce — both on the launcher's window-pump thread, so no lock guards this state. <see cref="Revision"/> bumps
/// whenever a slot's occupancy, state, or color changes; the frame source watches it to rebuild the program.
/// </para>
/// </remarks>
internal sealed class PlayerRoster : IInputSlotResolver, ICommandPrincipalResolver {
    /// <summary>The maximum number of local participants — a quad viewport's worth (the server table's seat count).</summary>
    public const int MaxSlots = WorldPopulation.LocalSeatCount;

    /// <summary>The <see cref="DriveTarget"/> sentinel for "drives nothing": a claimed slot whose principal has never
    /// once resolved a concrete driven body (e.g. a replay device holding no Drive grant at all — the shape rule in
    /// <see cref="Server.WorldGrants"/> lets a principal outside the trust boundary hold only a concrete
    /// <c>body:&lt;n&gt;</c>, never the wildcard, so "granted nothing yet" is the only way a claimant reaches this case
    /// today).
    /// <see cref="Server.WorldServer.Body(int)"/> answers <see langword="null"/> for any negative index, so a
    /// submission targeting this index is silently dropped before the Drive check ever runs — never denied against,
    /// and never coincidentally aimed at whatever occupies the roster slot.</summary>
    public const int NoBody = -1;

    /// <summary>The keyboard's device id — the one device the roster names by identity. A device id is a content-addressed
    /// <see cref="InputDeviceId"/>; the keyboard alone rides the <see langword="default"/> (all-zero) id, mapped to slot 0
    /// from boot. Comparisons that mean "the keyboard" spell this rather than a bare <c>default</c>.</summary>
    public static InputDeviceId KeyboardDevice => default;

    /// <inheritdoc/>
    public event Action<InputDeviceId>? DeviceSlotChanging;

    /// <summary>The 1-based display number for a 0-based slot (slot 0 is "player 1").</summary>
    /// <param name="slot">The slot index (0-based).</param>
    internal static int DisplayNumber(int slot) => (slot + 1);

    /// <summary>The 0-based slot for a 1-based display number (the inverse of <see cref="DisplayNumber"/>).</summary>
    /// <param name="number">The display number (1-based).</param>
    internal static int SlotFromDisplay(int number) => (number - 1);

    // The stick deflection a pending player must cross to cycle its candidate profile (edge-detected), compared
    // against the ALREADY-QUANTIZED stick sample — the fence wins (§10.2): a live float sample one ulp below the
    // pre-quantization threshold now lands on the SAME fixed raw as this constant and crosses it, a deliberate
    // mapping correction under determinism-pins-the-mapping, never a preserved-old-boundary regression.
    private readonly FixedQ4816 m_pickerThreshold;
    private readonly Vector3 m_pickerNeutralColor;
    private readonly float m_pickerNeutralBlend;
    private readonly float m_noseFactor;
    private readonly Participant?[] m_slots = new Participant?[MaxSlots];
    // Device → slot map. Reconnect stability is explicit on InputDeviceId; connection-only transports still route for
    // the session but never participate in durable profile preferences.
    private readonly Dictionary<InputDeviceId, int> m_deviceToSlot = new();
    // First-seen device order — the stable basis for the kbd/pad<N> tokens the reassignment verbs speak. Append-only
    // so a token never shifts under a player.
    private readonly List<InputDeviceId> m_deviceOrder = [];
    // First-seen preferred-profile decisions, retained so the read-back reports the decision that actually governed
    // arrival even after an explicit confirmation updates durable ownership.
    private readonly Dictionary<InputDeviceId, DevicePreselection> m_devicePreselections = new();
    // LOOPBACK-ONLY: the client selects from the server-owned catalog by direct reference; a socket transport replaces
    // this with a profile-catalog query/stream over the link.
    private readonly WorldOwnedWorlds m_profiles;
    // The session wire seat occupancy mirrors over, and — LOOPBACK-ONLY — the server the pose read-backs
    // (world.players) resolve through in-process; a socket transport replaces the m_server reads with link queries.
    private readonly IServerLink m_link;
    private readonly WorldServer m_server;
    // Installed by WorldInstanceHost once the process-wide instance registry exists. Before that composition-root
    // handoff (and in server-only fixtures that never build a host), Leave keeps using m_link exactly as it always
    // did. Once installed, every departure resolves the traveler's CURRENT instance before touching either server
    // or roster state, so device-orphan and console leaves share the same body/roster/router transaction.
    private Func<int, WorldPrincipal, bool>? m_leave;
    // The per-seat input resolver, pushed a seat's selected-profile binding layer whenever its identity settles so the
    // seat's composed mapping (default ⊕ overlays ⊕ profile ⊕ session) recompiles once, off the frame path.
    private readonly WorldSeatBindings m_seatBindings;
    // Devices that claimed their slot PROGRAMMATICALLY (via TryClaimSlot) rather than through a join/confirm/cycle
    // GESTURE — the editor session, a replay-playback device, a network peer stand-in, and a test-harness driver all
    // want the identical treatment: never eligible for a device-driven roster-IDENTITY gesture (confirm/cycle), because those
    // verbs reassign device-to-slot routing, which has no meaning for a caller that did not arrive by pressing a real
    // button and would otherwise let it hijack a human pad's slot assignment.
    private readonly HashSet<InputDeviceId> m_programmaticDevices = new();
    // A slot's overridden acting identity, if something other than the ordinary seat claimed it via TryClaimSlot; null
    // means the slot submits under its own WorldPrincipal.Seat as usual. WorldClient.SubmitSeatIntents reads this
    // (through PrincipalOf) so the write-boundary separation — a claimed slot's submission is checked under ITS OWN
    // principal, never silently promoted to the seat's — is a first-class roster property, not a per-caller carve-out.
    private readonly WorldPrincipal?[] m_slotPrincipal = new WorldPrincipal?[MaxSlots];
    // The last body a claimed slot's principal was actually seen driving (see DriveTarget) — remembered across ticks so
    // a revoked claim's NEXT submission still targets the body it was driving rather than silently retargeting the
    // roster slot: the server's Drive denial must be attributed to the body that lost its grant, not to a slot the
    // claimant never held Drive over in the first place. Cleared whenever the slot's claim itself changes (a fresh
    // claimant has driven nothing yet).
    private readonly int?[] m_slotDrivenBody = new int?[MaxSlots];
    // The Drive handle DriveTarget last minted for a claimed slot, held across ticks — carrying it forward
    // (rather than minting index 0 fresh every call) is what gives WorldHandleTable.TryResolve's generation
    // check a real caller: a handle that is still valid (the table rebuilt from an unrelated write, but this
    // index's designation did not change — see WorldHandleTable.EnsureFresh) resolves to the same body with
    // no re-mint, and a handle a revoke or a re-sort actually invalidated resolves false here, at the only
    // call site that ever checks it. Cleared alongside m_slotDrivenBody whenever the slot's claim itself
    // changes (a fresh claimant starts with nothing cached).
    private readonly WorldHandle?[] m_slotDriveHandle = new WorldHandle?[MaxSlots];
    // The subject m_slotDriveHandle was minted against — independent of WorldHandleTable's generation
    // bookkeeping rather than trusting it: DriveTarget re-verifies a resolved subject against this
    // remembered value every tick, and on disagreement it refuses — holds the remembered body and keeps the
    // disagreeing cache as evidence — rather than falling through to a re-mint. A re-mint of index 0
    // resolves the same slot of the same table, so a naive fallthrough would let a subject that slipped the
    // generation check be adopted anyway. Cleared alongside m_slotDriveHandle whenever the slot's claim
    // itself changes.
    private readonly GrantSubject?[] m_slotDriveSubject = new GrantSubject?[MaxSlots];
    // The last subject this slot's belt alarmed on — reporting state, deliberately separate from the belt's
    // decision state above: the belt decides from m_slotDriveSubject alone, and this only throttles the
    // loud line to once per distinct disagreement instead of once per tick while one persists. Cleared with
    // the claim like the rest.
    private readonly GrantSubject?[] m_slotDriveAlarm = new GrantSubject?[MaxSlots];
    private int m_revision;

    /// <summary>Initializes a new instance of the <see cref="PlayerRoster"/> class with the world definition's
    /// eager seats already active (each boot seat mirrored to the server as a session join). Player 1 owns the
    /// keyboard, uses the first authored profile, and is unconditionally eager regardless of the document — a
    /// session always needs a first player. Every other seat activates at boot only when
    /// <c>population.seatActivation</c> declares it <see cref="SeatActivationPolicy.Eager"/>; an
    /// <see cref="SeatActivationPolicy.OnDemand"/> seat stays empty until a later <c>player.join</c> or a
    /// controller's own hot-plug first touch (<see cref="ResolveDeviceSlot"/>) claims it through the identical
    /// session-join door this constructor uses — the boot fill below is not a separate activation path, it is this
    /// same door called once per eager seat before the first tick. An eager seat takes a distinct unused profile in
    /// catalog order and begins deviceless so a connected pad can still claim it.</summary>
    /// <param name="profiles">The live profile catalog participants seat on.</param>
    /// <param name="definition">The world definition supplying the local census.</param>
    /// <param name="link">The client→server link session requests ride.</param>
    /// <param name="server">The authoritative server the pose read-backs resolve through.</param>
    /// <param name="seatBindings">The per-seat input resolver the roster pushes profile binding layers into.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public PlayerRoster(WorldOwnedWorlds profiles, WorldDefinition definition, IServerLink link, WorldServer server, WorldSeatBindings seatBindings) {
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: link);
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: seatBindings);

        m_profiles = profiles;
        m_link = link;
        m_server = server;
        m_seatBindings = seatBindings;
        m_pickerThreshold = FixedQ4816.FromDouble(value: definition.PlayerDefaults.PickerThreshold);
        m_pickerNeutralColor = WorldIdentity.ParseColor(hex: definition.PlayerDefaults.PickerNeutralColor, fallbackHex: definition.PlayerDefaults.NeutralColor);
        m_pickerNeutralBlend = definition.PlayerDefaults.PickerNeutralBlend;
        m_noseFactor = definition.PlayerDefaults.NoseFactor;
        // The keyboard is a device like any pad — its sentinel id, owned by slot 0 from boot and listed first.
        m_deviceOrder.Add(item: KeyboardDevice);
        m_deviceToSlot[KeyboardDevice] = 0;
        m_devicePreselections[KeyboardDevice] = new DevicePreselection(
            Kind: DevicePreselectionKind.Keyboard,
            Profile: null,
            Seat: -1
        );
        // The boot census self-provisions: EXPLICIT at each call site (SelfProvisioned(slot)), never an omitted
        // default — see SelfProvisioned's own remarks for why this is a deliberate choice, not a laundered actor.
        Fill(slot: 0, profile: m_profiles.BootProfile, state: ParticipantState.Active, origin: ParticipantOrigin.Permanent, actingPrincipal: SelfProvisioned(slot: 0));

        for (var slot = 1; (slot < MaxSlots); slot++) {
            if (definition.Population.SeatActivation[slot] != SeatActivationPolicy.Eager) {
                continue;
            }

            Fill(slot: slot, profile: FirstUnusedProfile(exceptSlot: -1), state: ParticipantState.Active, origin: ParticipantOrigin.Permanent, actingPrincipal: SelfProvisioned(slot: slot));
        }
    }

    /// <summary>A monotonically increasing counter bumped whenever a slot's occupancy, state, or color changes. The
    /// frame source rebuilds the program (avatar colors + <c>Active</c> flags) and re-lays-out the viewports on change.</summary>
    public int Revision => m_revision;

    /// <summary>Installs the process-local departure router that resolves a roster seat's current world instance.
    /// Exactly one <see cref="WorldInstanceHost"/> owns this handoff; a second installation is a composition error.</summary>
    internal void ConfigureLeave(Func<int, WorldPrincipal, bool> leave) {
        ArgumentNullException.ThrowIfNull(argument: leave);

        if (m_leave is not null) {
            throw new InvalidOperationException(message: "the roster departure router is already configured");
        }

        m_leave = leave;
    }

    /// <summary>The number of filled slots (pending or active; always at least player 1).</summary>
    public int Count {
        get {
            var count = 0;

            for (var slot = 0; (slot < MaxSlots); slot++) {
                if (m_slots[slot] is not null) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Finds a profile by name (case-insensitive), or <see langword="null"/>.</summary>
    /// <param name="name">The profile name to look up.</param>
    public WorldIdentity? FindProfile(string name) => m_profiles.Find(name: name);

    /// <summary>Whether the slot (0-based) currently holds a participant (pending or active).</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public bool IsJoined(int slot) => (((uint)slot < MaxSlots) && (m_slots[slot] is not null));

    /// <summary>The seat controller in the slot (0-based), or <see langword="null"/> if the slot is empty or out of
    /// range. The seat's authoritative body lives on the server.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public SeatController? Seat(int slot) => (((uint)slot < MaxSlots) ? m_slots[slot]?.Seat : null);

    /// <summary>The profile the slot's participant selects, or <see langword="null"/> for an empty slot.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public WorldIdentity? ProfileAt(int slot) => (((uint)slot < MaxSlots) ? m_slots[slot]?.Seat.Profile : null);

    /// <summary>Whether the slot's participant is still choosing a profile.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public bool IsPending(int slot) => (((uint)slot < MaxSlots) && (m_slots[slot]?.State == ParticipantState.Pending));

    /// <summary>The slot (0-based) a device currently owns, or <see langword="null"/> if it is unmapped.</summary>
    /// <param name="device">The device id.</param>
    public int? DeviceSlot(InputDeviceId device) => (m_deviceToSlot.TryGetValue(key: device, value: out var slot) ? slot : null);

    /// <summary>Claims a roster slot for <paramref name="principal"/> to drive exclusively — input-routing/target-
    /// resolution bookkeeping only (mirrors <see cref="CommitSlot"/>: no <see cref="Participant"/> is created or
    /// touched). Marks <paramref name="device"/> as programmatically claimed — never eligible for a device-driven
    /// roster-identity gesture (see <see cref="Confirm(InputDeviceId, WorldPrincipal)"/>/<see cref="CycleDevice"/>), the same exclusion
    /// a replay-playback device or a network peer stand-in wants, not something specific to any one caller — and, on
    /// success, overrides the slot's acting identity for <see cref="PrincipalOf"/> to report. Honors
    /// <paramref name="preferredSlot"/> when given (it must already carry a local seat, hold no live device, and not
    /// already be claimed); otherwise takes the first unclaimed slot with a local seat and no device attached — slot 0
    /// never qualifies for the automatic pick because the keyboard sentinel occupies it from boot, which is the correct
    /// exclusion (a claim should never silently share the keyboard's body).</summary>
    /// <param name="device">The claiming caller's content-addressed device id.</param>
    /// <param name="principal">The acting identity the claimed slot submits under from now on (see
    /// <see cref="PrincipalOf"/>) — e.g. a replay device's or a network peer stand-in's.</param>
    /// <param name="preferredSlot">The 0-based slot the caller declared, or <see langword="null"/> to take the first
    /// free slot not already claimed by a seat.</param>
    /// <param name="slot">The claimed slot (0-based) on success; <c>-1</c> on failure.</param>
    /// <param name="fault">On failure, a human-readable reason; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when a slot was claimed.</returns>
    public bool TryClaimSlot(InputDeviceId device, WorldPrincipal principal, int? preferredSlot, out int slot, out string? fault) {
        if (preferredSlot is { } requested) {
            if ((uint)requested >= MaxSlots) {
                slot = -1;
                fault = $"slot {DisplayNumber(slot: requested)} is out of range (1..{MaxSlots})";

                return false;
            }

            if (m_slotPrincipal[requested] is { } holder) {
                slot = -1;
                fault = $"slot {DisplayNumber(slot: requested)} is already claimed by {holder.Describe()}";

                return false;
            }

            if (m_slots[requested] is null) {
                slot = -1;
                fault = $"slot {DisplayNumber(slot: requested)} has no local seat to drive";

                return false;
            }

            if (CountDevices(slot: requested, excludeKeyboard: false) != 0) {
                slot = -1;
                fault = $"slot {DisplayNumber(slot: requested)} is already driven by a human device";

                return false;
            }

            slot = requested;
        } else {
            var found = -1;

            for (var candidate = 0; (candidate < MaxSlots); candidate++) {
                if ((m_slotPrincipal[candidate] is null) && (m_slots[candidate] is not null) && (CountDevices(slot: candidate, excludeKeyboard: false) == 0)) {
                    found = candidate;

                    break;
                }
            }

            if (found < 0) {
                slot = -1;
                fault = $"no free slot — all {MaxSlots} are human-driven or already claimed";

                return false;
            }

            slot = found;
        }

        m_slotPrincipal[slot] = principal;
        // A fresh claim has driven nothing yet — never inherit a prior claimant's remembered target, cached handle,
        // locked subject, or alarm (see each field's own remarks and DriveTarget).
        m_slotDrivenBody[slot] = null;
        m_slotDriveHandle[slot] = null;
        m_slotDriveSubject[slot] = null;
        m_slotDriveAlarm[slot] = null;
        _ = m_programmaticDevices.Add(item: device);
        TrackDeviceOrder(device: device);
        m_deviceToSlot[device] = slot;
        fault = null;

        return true;
    }

    /// <summary>The entity index a slot's per-tick intent submission should target: the slot's own body for an
    /// ordinary (unclaimed) seat, or — for a slot a <see cref="TryClaimSlot"/> call claimed — whatever body the
    /// claimant currently holds a <see cref="WorldCapability.Drive"/> grant over, resolved through the claimant's own
    /// Drive <see cref="Server.WorldHandleTable"/> (<see cref="Server.WorldGrants.HandleTable"/>) rather than a raw
    /// grant-table lookup. <see cref="TryClaimSlot"/> itself accepts any <see cref="WorldPrincipal"/> — it is a
    /// caller-discipline fact, not an enforced one, that a claimant is
    /// <see cref="PrincipalKind.Addon"/> or <see cref="PrincipalKind.Peer"/>; a
    /// caller that claimed a slot under <see cref="PrincipalKind.Console"/> or <see cref="PrincipalKind.Seat"/> would
    /// not fail there — it would throw <see cref="ArgumentException"/> here, out of the <see cref="Server.WorldGrants.HandleTable"/>
    /// constructor, inside the per-tick submit loop, the first time this method resolved that slot. Handle 0 is the
    /// lowest body the claimant holds Drive over — but the part that makes it a body is the grant table's own
    /// subject-shape rule (<c>IsLegitimateSubject</c>), which refuses an untrusted principal's <c>Drive</c> over the
    /// <see cref="GrantSubject.All"/> wildcard; <see cref="Server.WorldGrants.ProjectSubjects"/> additionally never
    /// projects the wildcard into any handle table at all, for any capability, so index 0 could never resolve to
    /// "the whole domain" here even if a future trusted tier admitted <c>drive all</c> for a claimant. The roster
    /// slot is an input-routing lane only; the grant table is what says what it drives.
    /// <para>
    /// The resolved handle is cached across ticks (see <see cref="m_slotDriveHandle"/>), not merely the body index:
    /// resolving the same handle every tick (rather than minting index 0 fresh every call) is what makes
    /// <see cref="Server.WorldHandleTable.TryResolve"/>'s generation check load-bearing — the property that a stale
    /// handle resolves to nothing is now exercised at this, its only call site, instead of holding only in isolation.
    /// A cached handle that still resolves (nothing changed for this index — see
    /// <c>Server.WorldHandleTable.EnsureFresh</c>, private) and still names the same subject the lease locked in when it
    /// was minted (see <see cref="m_slotDriveSubject"/> — the second belt: an independent re-check that does not
    /// merely trust the generation bookkeeping) costs one dictionary-free resolve and no re-mint. A cached handle a
    /// revoke or a re-sort invalidated resolves as dead here and falls through to a fresh mint of index 0 below,
    /// which picks up whatever the claimant holds now (a re-grant, a different lowest body, or nothing). A cached
    /// handle that resolves but names a different subject than the lease locked in is neither: that is an invariant
    /// violation in the table's own bookkeeping, and the belt refuses — alarms once per distinct disagreement, holds
    /// the remembered body, and keeps the disagreeing cache so it stands guard next tick — because falling through to
    /// a re-mint of the same index would adopt the slipped subject anyway, making the belt a no-op.
    /// </para>
    /// The resolved body is also remembered across ticks by value (see <see cref="m_slotDrivenBody"/>) so a claim
    /// that just had its only Drive grant revoked still targets the body it was actually driving on its next
    /// submission — the server's denial then attributes loudly to the body that lost its grant rather than silently
    /// retargeting the slot, which the claimant may never have held Drive over at all.
    /// <b>A claim that has never once resolved a driven body</b> (e.g. a replay device that has not yet been granted
    /// any concrete <c>body:&lt;n&gt;</c> hold — a Drive handle table only ever projects Body subjects, and the
    /// Drive-shape rule never lets an untrusted principal hold the wildcard in the first place) drives
    /// <see cref="NoBody"/>, never the slot itself: the <c>?? slot</c> fallback is correct for the unclaimed case above
    /// (an unclaimed slot's own occupant drives its own body) and wrong here (a claimant that never named a body has no
    /// business inheriting whatever happens to sit in its input-routing lane — that is the hijack this sentinel
    /// closes). <see cref="NoBody"/> resolves to no body server-side and is silently dropped before any Drive check
    /// runs, never denied against and never coincidentally driven.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public int DriveTarget(int slot) {
        if (((uint)slot >= MaxSlots) || (m_slotPrincipal[slot] is not { } claimant)) {
            return slot;
        }

        var handles = m_server.Grants.HandleTable(principal: claimant, capability: WorldCapability.Drive);

        if ((m_slotDriveHandle[slot] is { } cached) && handles.TryResolve(handle: cached, subject: out var cachedSubject)) {
            // Second belt: trust a generation-matched resolve only when it names the IDENTICAL subject this lease
            // locked in at mint time (m_slotDriveSubject), never merely "some Body subject" — an independent check of
            // the same fact WorldHandleTable's own generation bookkeeping already guarantees.
            if ((cachedSubject.Kind == GrantSubjectKind.Body) && (cachedSubject == m_slotDriveSubject[slot])) {
                m_slotDrivenBody[slot] = cachedSubject.Value;

                return cachedSubject.Value;
            }

            // A resolve that succeeded — the generation matched — yet names a different subject than the
            // lease locked in is an invariant violation in WorldHandleTable's bookkeeping, not a revocation
            // (a revoke kills the generation and lands in the fall-through below). Refuse: hold the
            // remembered body — the server still gates every submission through Allows, so a lease whose
            // grant genuinely died gets the loud server-side denial — and keep the disagreeing cache as
            // evidence, so the belt stands guard again next tick instead of falling through to a fresh mint
            // that would adopt the slipped subject anyway. The alarm below is once per distinct disagreement
            // (reporting state, separate from the decision — see m_slotDriveAlarm), never once per tick.
            if (m_slotDriveAlarm[slot] != cachedSubject) {
                m_slotDriveAlarm[slot] = cachedSubject;
                Console.Error.WriteLine(value: $"[roster: slot {slot} ({claimant.Describe()}) drive lease locked {(m_slotDriveSubject[slot]?.Describe() ?? "nothing")} but its handle now resolves {cachedSubject.Describe()} at the SAME generation — WorldHandleTable invariant violated; holding the leased body, not retargeting]");
            }

            return (m_slotDrivenBody[slot] ?? NoBody);
        }

        if (handles.TryMint(index: 0, out var handle) && handles.TryResolve(handle: handle, subject: out var subject) && (subject.Kind == GrantSubjectKind.Body)) {
            m_slotDriveHandle[slot] = handle;
            m_slotDriveSubject[slot] = subject;
            m_slotDrivenBody[slot] = subject.Value;

            return subject.Value;
        }

        // Nothing resolves any more (the only Drive grant was revoked, or the claimant never held one) — drop the
        // stale cache entries so the next tick re-mints rather than repeatedly re-resolving a handle already known
        // dead, and close any alarm episode with them (a future disagreement is a new episode and alarms fresh).
        m_slotDriveHandle[slot] = null;
        m_slotDriveSubject[slot] = null;
        m_slotDriveAlarm[slot] = null;

        return (m_slotDrivenBody[slot] ?? NoBody);
    }

    /// <summary>The slot's acting identity: the <see cref="WorldPrincipal"/> a claim recorded via
    /// <see cref="TryClaimSlot"/>, or its ordinary <see cref="WorldPrincipal.Seat"/> when nothing has claimed it. The
    /// single read seam <see cref="Client.WorldClient.SubmitSeatIntents"/> uses to decide which identity a slot's
    /// per-tick submission is checked under — the write-boundary separation is a property of the slot, not a
    /// per-caller carve-out this type has to know the shape of.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public WorldPrincipal PrincipalOf(int slot) {
        return ((((uint)slot < MaxSlots) ? m_slotPrincipal[slot] : null) ?? WorldPrincipal.Seat(slot: slot));
    }

    /// <inheritdoc/>
    /// <remarks>The roster is the host's answer to <see cref="ICommandPrincipalResolver"/>: the snapshot mixer asks
    /// this for every lane it assembles, so a claimed slot's bound input is stamped with the claimant's identity
    /// rather than the seat it displaced. This is the same answer <see cref="PrincipalOf"/> gives the write-boundary
    /// guards, mapped into the ingress layer's shape — one truth, two vocabularies.</remarks>
    CommandPrincipal ICommandPrincipalResolver.PrincipalOf(int slot) {
        return WorldPrincipalMapping.ToCommand(principal: PrincipalOf(slot: slot));
    }

    /// <summary>Whether the slot is under an exclusive <see cref="TryClaimSlot"/> hold. A claimed slot's own protocol
    /// (its claimant's explicit commands) is its only legitimate driver — the guard a pushed/context-routed roster
    /// gesture (confirm, seat, cycle) checks before treating a claimed slot as an ordinary human target.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public bool IsClaimed(int slot) => (((uint)slot < MaxSlots) && (m_slotPrincipal[slot] is not null));

    /// <inheritdoc/>
    public int ResolveSlot(InputDeviceId device) {
        if (m_deviceToSlot.TryGetValue(key: device, value: out var mapped)) {
            return mapped;
        }

        // A usable preferred profile needs a pending seat of its own; attaching the device to an unrelated active
        // participant would discard the hint before the picker can show it. The seat remains only a viewport: this
        // chooses the lowest available seat and carries the profile separately.
        if ((EvaluatePreselection(device: device) is { Kind: DevicePreselectionKind.Preferred }) &&
            (FirstFreeUnclaimedSlot() is var preferredSlot) && (preferredSlot >= 0)) {
            return preferredSlot;
        }

        // Probe the seating policy without mutating routing or simulation state. InputRouter commits this proposal only
        // after it finds a binding on an active command map. Slot 0 gets the SAME TryClaimSlot exclusion as 1..3
        // below: a claimed slot 0 (e.g. a replay device claiming the keyboard's seat) must never be silently offered to
        // an ordinary arriving pad.
        if ((device != KeyboardDevice) && (m_slotPrincipal[0] is null) && (m_slots[0] is { State: ParticipantState.Active }) && (CountDevices(slot: 0, excludeKeyboard: true) == 0)) {
            return 0;
        }

        if ((m_slotPrincipal[0] is null) && (CountDevices(slot: 0, excludeKeyboard: false) == 0)) {
            return 0;
        }

        // The built-in census may have already created players 2..4 as active, deviceless local-human seats. Prefer
        // claiming those existing seats in slot order before proposing a new participant, so four arriving pads map to
        // p1..p4 without requiring a roster verb or replacing the avatars already visible in split screen. A slot
        // TryClaimSlot has claimed is excluded from this ordinary device-arrival policy regardless: "exclusively" means
        // a ordinary pad plugging in later must never be silently offered the same slot.
        for (var slot = 1; (slot < MaxSlots); slot++) {
            if ((m_slotPrincipal[slot] is null) && (m_slots[slot] is not null) && (CountDevices(slot: slot, excludeKeyboard: false) == 0)) {
                return slot;
            }
        }

        for (var slot = 1; (slot < MaxSlots); slot++) {
            // A prior source signal in this same collection pass may have reserved an otherwise-empty lane through
            // CommitSlot before its simulation-routed join reaches the roster. Treat that device-map reservation as
            // occupancy so two first-seen devices cannot probe and commit the same slot in one tick.
            if ((m_slotPrincipal[slot] is null) && (m_slots[slot] is null) && (CountDevices(slot: slot, excludeKeyboard: false) == 0)) {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Commits a probed slot as a local device-routing annotation, called from <c>InputRouter.Collect</c>
    /// pre-dispatch — before the handler this lane's signal will reach even runs, let alone joins anything.
    /// Participant occupancy is deliberately untouched here: this call alone never creates simulation state, only a
    /// device→slot routing entry. That is not the same claim as "routing-only, no persistent effect" — the entry
    /// this writes is exactly what a downstream Device-origin join (e.g. <see cref="RouteMove(int, FixedVector2, WorldPrincipal)"/>'s or
    /// <see cref="AssignDevice"/>'s) is expected to make real, and <see cref="ResolveSlot"/> already counts it as
    /// occupancy for every other device the moment it lands. A join that reservation was written to enable can still
    /// be denied after the fact (a narrowed grant on that exact body), and a denial that left this entry in place
    /// would strand the lane permanently — <see cref="JoinPending"/>'s own denial path is what rolls it back
    /// (<c>RollbackStaleReservation</c>), so this write's persistence is conditional on the join it feeds actually
    /// landing, never on this call alone.</summary>
    /// <param name="device">The live device whose signal produced the lane.</param>
    /// <param name="slot">The recorded logical lane.</param>
    /// <returns><see langword="true"/> when the device was newly assigned; otherwise <see langword="false"/>.</returns>
    public bool CommitSlot(InputDeviceId device, int slot) {
        if (m_deviceToSlot.ContainsKey(key: device) || (ResolveSlot(device: device) != slot)) {
            return false;
        }

        TrackDeviceOrder(device: device);
        m_deviceToSlot[device] = slot;

        return true;
    }

    /// <summary>The active slot (0-based) whose participant seats on the profile, or -1 if none do.</summary>
    /// <param name="profile">The profile to look for.</param>
    public int ActiveSlotUsing(WorldIdentity profile) {
        for (var slot = 0; (slot < MaxSlots); slot++) {
            if ((m_slots[slot] is { State: ParticipantState.Active } participant) && ReferenceEquals(objA: participant.Seat.Profile, objB: profile)) {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>The render body color for the slot: an active player's full profile color, a pending player's
    /// candidate color lerped halfway to gray, or gray for an empty slot.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public Vector3 BodyColor(int slot) {
        if (m_slots[slot] is not { } participant) {
            return m_pickerNeutralColor;
        }

        var color = (participant.Seat.Profile?.Color ?? m_pickerNeutralColor);

        return ((participant.State == ParticipantState.Active)
            ? color
            : Vector3.Lerp(value1: color, value2: m_pickerNeutralColor, amount: m_pickerNeutralBlend));
    }
    /// <summary>The render nose color for the slot.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public Vector3 NoseColor(int slot) => (BodyColor(slot: slot) * m_noseFactor);

    /// <summary>Routes a movement-stick sample from a deterministic command lane to its logical player slot. The
    /// value arrives already quantized to fixed point at the router seam (see
    /// <see cref="Puck.Commands.CommandValueQuantization.QuantizeAxis"/>) — this is the router seam alone; there is
    /// no separate device-facing door.</summary>
    /// <param name="slot">The logical player slot recorded in the command snapshot.</param>
    /// <param name="value">The already-quantized stick sample (+Y forward, +X strafe right).</param>
    /// <param name="actingPrincipal">The principal asking — <c>context.ActingPrincipal()</c>, which already resolves
    /// through <see cref="PrincipalOf"/> for this same slot, so it is correct with no separate self-provisioning
    /// branch. Mandatory.</param>
    public void RouteMove(int slot, FixedVector2 value, WorldPrincipal actingPrincipal) {
        if ((uint)slot >= MaxSlots) {
            return;
        }

        // An unmapped device's first touch joins the SAME lane it is arriving on, under its own acting identity —
        // actingPrincipal already resolves through PrincipalOf(slot) at the caller (context.ActingPrincipal()), so
        // it names the correct self-service identity (a claimant's, if this slot was claimed) with no separate
        // self-provisioning branch needed here. A DENIED join (a narrowed grant on this exact slot) must not fall
        // through to the null-forgiving read below: m_slots[slot] can still be null here, and reading through it
        // would crash.
        if ((m_slots[slot] is null) && (JoinPending(slot: slot, origin: ParticipantOrigin.Device, actingPrincipal: actingPrincipal) != JoinResult.Ok)) {
            return;
        }

        var participant = m_slots[slot]!;

        RouteMove(slot: slot, participant: participant, value: value);
    }

    private void RouteMove(int slot, Participant participant, FixedVector2 value) {

        // Always stash the sample so player.sticks reads truthfully even for a pending pad — a script most wants to
        // observe the new pad exactly when it is still choosing a profile. Submission is gated on Active, so a pending
        // player never MOVES from this sample; its move stick doubles as the profile picker below.
        participant.Seat.SetAnalogMove(move: value);

        if (participant.State == ParticipantState.Pending) {
            PendingPicker(participant: participant, slot: slot, stickX: value.X);
        }
    }
    /// <summary>Routes a look-stick sample from a deterministic command lane to its logical player slot. Ignored for
    /// a pending player (the move stick is its picker). The value arrives already quantized to fixed point at the
    /// router seam (see <see cref="Puck.Commands.CommandValueQuantization.QuantizeAxis"/>) — this is the router seam
    /// alone; there is no separate device-facing door.</summary>
    /// <param name="slot">The logical player slot recorded in the command snapshot.</param>
    /// <param name="value">The already-quantized stick sample (+X turns the body right, +Y pitches the presentation camera up).</param>
    /// <param name="actingPrincipal">The principal asking — see <see cref="RouteMove(int, FixedVector2, WorldPrincipal)"/>'s
    /// identical remark. Mandatory.</param>
    public void RouteLook(int slot, FixedVector2 value, WorldPrincipal actingPrincipal) {
        if ((uint)slot >= MaxSlots) {
            return;
        }

        if (m_slots[slot] is null) {
            // An unmapped device's first touch joins under its own acting identity, exactly like RouteMove; the
            // outcome is intentionally unread here — a fresh join is always Pending, never Active, so the pattern
            // match below rejects it (and any denial) identically either way.
            _ = JoinPending(slot: slot, origin: ParticipantOrigin.Device, actingPrincipal: actingPrincipal);
        }

        if (m_slots[slot] is not { State: ParticipantState.Active } participant) {
            return;
        }

        participant.Seat.SetAnalogLook(look: value);
    }
    /// <summary>Wipes every joined seat's tick-local analog staging and resets a pending player's picker edge state
    /// when its stick went untouched this tick. The snapshot router re-dispatches carried analog values next tick.</summary>
    public void ClearAnalog() {
        for (var slot = 0; (slot < MaxSlots); slot++) {
            if (m_slots[slot] is not { } participant) {
                continue;
            }

            participant.Seat.ClearAnalog();

            if (!participant.StickSeenThisFrame) {
                participant.PendingPrevStickX = FixedQ4816.Zero;
            }

            participant.StickSeenThisFrame = false;
        }
    }

    /// <summary>The self-provisioning acting principal for a device/boot-origin roster op on <paramref name="slot"/>:
    /// a physical device (or the boot census) joining, leaving, confirming, or relocating onto its own slot
    /// legitimately acts as that slot's own seat identity — there is no other principal to attribute a bare device
    /// signal to, and the checks below trivially pass by the very same default seed (<c>WorldGrants</c>'s
    /// constructor) that gives every seat Drive over its own body. Spelled as an explicit call at every device/boot
    /// call site — never a silently-applied default parameter value — so self-provisioning reads as a deliberate
    /// choice a reviewer can see, never an omitted actor. A console-typed verb
    /// (<c>player.join</c>/<c>leave</c>/<c>profile</c>/<c>confirm</c>/<c>assign</c>) passes
    /// <c>context.ActingPrincipal()</c> instead, so a third party's action is checked under the real submitter,
    /// never laundered into the target's own identity.</summary>
    /// <param name="slot">The slot index (0-based) the device/boot op targets.</param>
    private static WorldPrincipal SelfProvisioned(int slot) => WorldPrincipal.Seat(slot: slot);

    /// <summary>Joins a specific slot (0-based) as a pending player (a candidate profile is chosen; the player must
    /// confirm) — the scripted <c>player.join &lt;n&gt;</c> path. The server's verdict is checked before any local
    /// mutation: a denied join installs nothing.</summary>
    /// <param name="slot">The slot index (0-based) to join.</param>
    /// <param name="origin">Why the slot is being filled (script or device).</param>
    /// <param name="actingPrincipal">The principal asking to join — <c>context.ActingPrincipal()</c> for a console
    /// dispatch, or <see cref="SelfProvisioned"/> for a device gesture. Mandatory and never defaulted: a caller must
    /// say, explicitly, who is asking.</param>
    /// <returns><see cref="JoinResult.Occupied"/> if out of range or already joined, <see cref="JoinResult.Denied"/>
    /// if the server refused the actor, else <see cref="JoinResult.Ok"/>.</returns>
    public JoinResult JoinPending(int slot, ParticipantOrigin origin, WorldPrincipal actingPrincipal) {
        return JoinPendingCandidate(slot: slot, origin: origin, actingPrincipal: actingPrincipal,
            candidate: PreferredCandidateFor(slot: slot));
    }

    private JoinResult JoinPendingCandidate(int slot, ParticipantOrigin origin, WorldPrincipal actingPrincipal, WorldIdentity? candidate) {
        if (((uint)slot >= MaxSlots) || (m_slots[slot] is not null)) {
            return JoinResult.Occupied;
        }

        var profile = (((candidate is not null) && !IsProfileActiveElsewhere(profile: candidate, exceptSlot: slot))
            ? candidate
            : FirstUnusedProfile(exceptSlot: -1));

        if (!Fill(slot: slot, profile: profile, state: ParticipantState.Pending, origin: origin, actingPrincipal: actingPrincipal)) {
            // A Device-origin attempt may have ALREADY written m_deviceToSlot for this exact slot before this join
            // was even attempted: InputRouter's CommitSlot commits the routing annotation pre-dispatch, once per
            // lane, purely to keep two devices from probing and committing the same free slot in one collection pass
            // (see CommitSlot's own remarks) — it has no way to know whether the join this reservation exists to
            // enable will itself be denied. A denial here must roll that reservation back: left in place, the device
            // is stranded (mapped to a slot with no participant), ResolveSlot keeps counting the slot as occupied for
            // every OTHER device, and this SAME device repeats the identical denial forever with no way out.
            if (origin == ParticipantOrigin.Device) {
                RollbackStaleReservation(slot: slot);
            }

            return JoinResult.Denied;
        }

        m_revision++;

        return JoinResult.Ok;
    }

    // Removes whatever device is currently mapped to `slot` in m_deviceToSlot, if any — safe to call unconditionally
    // on a JoinPending denial because the early Occupied check above already proved m_slots[slot] was null coming
    // in: a null-participant slot can never legitimately carry a mapped device (Fill only ever writes m_slots[slot]
    // and m_deviceToSlot[device] together, on success), so any mapping found here is exactly the stale
    // pre-dispatch reservation this denial must not strand.
    private void RollbackStaleReservation(int slot) {
        InputDeviceId? stale = null;

        foreach (var pair in m_deviceToSlot) {
            if (pair.Value == slot) {
                stale = pair.Key;

                break;
            }
        }

        if (stale is { } device) {
            _ = m_deviceToSlot.Remove(key: device);
        }
    }
    /// <summary>Joins the lowest free slot as a pending player.</summary>
    /// <param name="origin">Why the slot is being filled.</param>
    /// <param name="actingPrincipal">Computes the principal asking to join from the slot <see cref="FirstFreeSlot"/>
    /// resolves (not known until then) — <c>SelfProvisioned</c> (the method group) for a device gesture, or a
    /// constant-returning lambda over <c>context.ActingPrincipal()</c> for a console dispatch. Mandatory.</param>
    /// <returns>The result, plus the joined slot index (0-based, valid only when the result is
    /// <see cref="JoinResult.Ok"/>).</returns>
    public (JoinResult Result, int Slot) JoinPendingNextFree(ParticipantOrigin origin, Func<int, WorldPrincipal> actingPrincipal) {
        var slot = FirstFreeSlot();

        if (slot < 0) {
            return (Result: JoinResult.Full, Slot: -1);
        }

        // slot is always meaningful past this point (Ok or Denied) — FirstFreeSlot just confirmed it empty, and
        // nothing reenters between that read and this call in this single-threaded roster, so JoinPending can only
        // answer Ok or Denied here, never Occupied. Reporting the attempted slot even on Denied lets a caller name
        // which slot the actor was refused, rather than collapsing a denial into the same "-1" a full roster reports.
        return (Result: JoinPending(slot: slot, origin: origin, actingPrincipal: actingPrincipal(arg: slot)), Slot: slot);
    }
    /// <summary>Joins a specific slot (0-based) directly active on a chosen profile — the one-shot
    /// <c>player.join &lt;profile&gt; &lt;n&gt;</c> path. The server's verdict is checked before any local mutation.</summary>
    /// <param name="slot">The slot index (0-based) to join.</param>
    /// <param name="profile">The profile to seat on.</param>
    /// <param name="origin">Why the slot is being filled.</param>
    /// <param name="actingPrincipal">The principal asking to join (see <see cref="JoinPending"/>). Mandatory.</param>
    /// <returns><see cref="JoinResult.Occupied"/> if out of range or already joined, <see cref="JoinResult.Denied"/>
    /// if the server refused the actor, else <see cref="JoinResult.Ok"/>.</returns>
    public JoinResult JoinActive(int slot, WorldIdentity profile, ParticipantOrigin origin, WorldPrincipal actingPrincipal) {
        if (((uint)slot >= MaxSlots) || (m_slots[slot] is not null)) {
            return JoinResult.Occupied;
        }

        if (!Fill(slot: slot, profile: profile, state: ParticipantState.Active, origin: origin, actingPrincipal: actingPrincipal)) {
            return JoinResult.Denied;
        }

        m_revision++;

        return JoinResult.Ok;
    }
    /// <summary>Joins the lowest free slot directly active on a chosen profile.</summary>
    /// <param name="profile">The profile to seat on.</param>
    /// <param name="origin">Why the slot is being filled.</param>
    /// <param name="actingPrincipal">Computes the principal asking to join from the resolved slot (see
    /// <see cref="JoinPendingNextFree"/>). Mandatory.</param>
    /// <returns>The result, plus the joined slot index (valid only when the result is <see cref="JoinResult.Ok"/>).</returns>
    public (JoinResult Result, int Slot) JoinActiveNextFree(WorldIdentity profile, ParticipantOrigin origin, Func<int, WorldPrincipal> actingPrincipal) {
        var slot = FirstFreeSlot();

        if (slot < 0) {
            return (Result: JoinResult.Full, Slot: -1);
        }

        // slot is always meaningful past this point — see JoinPendingNextFree's identical remark.
        return (Result: JoinActive(slot: slot, profile: profile, origin: origin, actingPrincipal: actingPrincipal(arg: slot)), Slot: slot);
    }
    /// <summary>Removes a scripted or device player from the slot (0-based), unmapping any devices that owned it and
    /// mirroring the leave to the server (dropping the seat's body). Player 1 (slot 0) never leaves. The server's
    /// verdict is checked before any local mutation: a denied leave changes nothing client-side.</summary>
    /// <param name="slot">The slot index (0-based) to free.</param>
    /// <param name="actingPrincipal">The principal asking to leave — <c>context.ActingPrincipal()</c> for a console
    /// dispatch, or <see cref="SelfProvisioned"/> for a device dropping itself. Mandatory.</param>
    /// <returns><see langword="true"/> if the slot was freed; <see langword="false"/> for slot 0, an out-of-range slot,
    /// an already-empty slot, or a server denial.</returns>
    public bool Leave(int slot, WorldPrincipal actingPrincipal) {
        if ((slot <= 0) || (slot >= MaxSlots) || (m_slots[slot] is null)) {
            return false;
        }

        if (m_leave is { } leave) {
            return leave(arg1: slot, arg2: actingPrincipal);
        }

        // Checked BEFORE any local mutation below, and the reply is no longer discarded: a denial leaves the roster
        // untouched instead of desyncing client and server state. actingPrincipal is EXPLICIT at every call site —
        // see SelfProvisioned's own remarks for why a device/boot self-leave is a deliberate choice, never a
        // fabricated target identity a console dispatch could hide behind. The local mutation below runs from INSIDE
        // the completion (fires inline over loopback), gated on the reply — never a post-submit live read.
        var accepted = false;

        m_link.SubmitSession(request: new SessionRequest.Leave(Principal: actingPrincipal, Slot: slot), completion: reply => {
            if (!reply.Accepted) {
                Console.Error.WriteLine(value: $"[player.leave denied: {reply.Reason}]");

                return;
            }

            accepted = VacateSeat(slot: slot);
        });

        return accepted;
    }

    /// <summary>The client-visible seat-vacated fact: the slot stops holding a participant, its claim and every
    /// per-claim cache die with it, and the devices that were driving it are unmapped. Server-side teardown is not
    /// part of this — the caller has already decided (and performed) whatever the server half of the departure is,
    /// which is exactly why this is a fact rather than a verb.</summary>
    /// <remarks>One fact, two producers. <see cref="Leave"/> emits it after the server accepts its
    /// <see cref="SessionRequest.Leave"/> (park-with-grace, reap-on-empty and the never-leaves-slot-0 policy are all
    /// that method's own, and stay there). A same-process world transfer emits it after its departure becomes certain
    /// (<c>WorldInstanceHost.TryTransferMember</c>), whose server half is deliberately a non-parking, non-reaping
    /// detach — so it must reach the roster here rather than acquire leave's teardown, and the roster must not carry a
    /// transfer-shaped special case to notice it. Presentation state only: nothing here touches the simulation, and no
    /// value it writes ever flows back into a tick.</remarks>
    /// <param name="slot">The slot index (0-based).</param>
    /// <returns><see langword="true"/> when a participant was removed; <see langword="false"/> for an out-of-range or
    /// already-empty slot.</returns>
    public bool VacateSeat(int slot) {
        if (((uint)slot >= MaxSlots) || (m_slots[slot] is null)) {
            return false;
        }

        m_slots[slot] = null;

        // Release the slot's claim (if any): a claim is a property of THIS occupancy, and a vacated slot rejoined by
        // an ordinary human must report its own WorldPrincipal.Seat from PrincipalOf, never the departed claimant's.
        m_slotPrincipal[slot] = null;
        // The departed claimant's remembered target, cached handle, locked subject, and alarm ALL die with the claim —
        // see each field's own remarks. The subject was originally left out of this block (only TryClaimSlot cleared
        // it), which was unobservable solely because every re-claim happened to route through TryClaimSlot; a future
        // path re-seating a vacated slot any other way would have inherited the departed claimant's locked-in subject
        // and had the belt "confirm" the new claimant's handle against the old claimant's body.
        m_slotDrivenBody[slot] = null;
        m_slotDriveHandle[slot] = null;
        m_slotDriveSubject[slot] = null;
        m_slotDriveAlarm[slot] = null;

        // Drop any devices that were driving this slot so a reconnecting pad re-joins cleanly.
        foreach (var device in m_deviceToSlot.Where(predicate: pair => (pair.Value == slot)).Select(selector: pair => pair.Key).ToArray()) {
            DeviceSlotChanging?.Invoke(obj: device);
            _ = m_deviceToSlot.Remove(key: device);
        }

        m_revision++;

        return true;
    }

    /// <summary>The client-visible seat-occupied fact — <see cref="VacateSeat"/>'s mirror: the slot starts holding an
    /// active participant on <paramref name="profile"/>, and that identity's binding layer is pushed to the seat.
    /// Emits no session request, because the caller has already seated this body on the server; the ordinary
    /// join/confirm path (<see cref="JoinPending"/>/<see cref="JoinActive"/>) is what asks the server first and then
    /// installs. Its only producer today is the arrival half of a same-process world transfer landing back in the
    /// instance this client mirrors (<c>WorldInstanceHost.TryTransferMember</c>) — a traveler that already crossed,
    /// never a fresh joiner.</summary>
    /// <remarks><see cref="ParticipantOrigin.Script"/>, deliberately: an arrival is an explicit act, so the seat
    /// stays until an explicit departure rather than dissolving when a device count hits zero (a traveler arrives
    /// deviceless). Presentation state only — see <see cref="VacateSeat"/>.</remarks>
    /// <param name="slot">The slot index (0-based).</param>
    /// <param name="profile">The arriving identity, or <see langword="null"/> for an anonymous traveler.</param>
    /// <returns><see langword="true"/> when a participant was installed; <see langword="false"/> for an out-of-range
    /// or already-occupied slot.</returns>
    public bool OccupySeat(int slot, WorldIdentity? profile) {
        if (((uint)slot >= MaxSlots) || (m_slots[slot] is not null)) {
            return false;
        }

        m_slots[slot] = new Participant {
            Origin = ParticipantOrigin.Script,
            Seat = new SeatController {
                Profile = profile,
                // The same table Fill seats a fresh participant with — the world document's own compiled channel
                // shapes, never re-derived here.
                Channels = m_server.Population.Channels,
            },
            State = ParticipantState.Active,
        };
        m_seatBindings.SetProfileLayers(slot: slot, bindings: profile?.Bindings);
        m_revision++;

        return true;
    }

    /// <summary>Confirms the pending participant owning a device (the <c>player.confirm</c> verb). An unmapped device
    /// is first mapped by this press (a first press joins, a second confirms — always self-provisioned, see
    /// <see cref="ResolveDeviceSlot"/>); an already-active participant is a no-op.</summary>
    /// <param name="device">The device that pressed confirm.</param>
    /// <param name="actingPrincipal">The principal asking to confirm — <c>context.ActingPrincipal()</c>. Only
    /// consulted when the device already owns a pending participant (the confirm/Activate step below); a fresh
    /// device's own first-touch join always self-provisions regardless, since <see cref="ResolveDeviceSlot"/> never
    /// reaches a third party's slot. Mandatory.</param>
    /// <returns>The confirm outcome and the affected slot (0-based; -1 when none).</returns>
    public (ConfirmOutcome Outcome, int Slot) Confirm(InputDeviceId device, WorldPrincipal actingPrincipal) {
        // A roster-identity gesture has no meaning for a device that claimed its slot PROGRAMMATICALLY rather than by
        // pressing a real button — its own commands are dispatched by explicit SLOT (see TryClaimSlot's callers),
        // never through this device-keyed path, so reaching here at all means a binding resolved player.confirm for
        // it; treat it as inert rather than mapping/promoting a participant on its behalf.
        if (m_programmaticDevices.Contains(item: device)) {
            return (Outcome: ConfirmOutcome.Ignored, Slot: -1);
        }

        if (!m_deviceToSlot.ContainsKey(key: device)) {
            if (ResolveDeviceSlot(device: device) is not { } joined) {
                return (Outcome: ConfirmOutcome.Ignored, Slot: -1);
            }

            // A first press that SEATED the device with an already-active player (the share-player-1 default) owes no
            // profile choice — report Seated, not a pending Joined, so the echo is truthful.
            return ((joined.State == ParticipantState.Active)
                ? (Outcome: ConfirmOutcome.Seated, Slot: m_deviceToSlot[device])
                : (Outcome: ConfirmOutcome.Joined, Slot: m_deviceToSlot[device]));
        }

        return Confirm(slot: m_deviceToSlot[device], actingPrincipal: actingPrincipal, device: device);
    }

    /// <summary>Confirms a pending participant by deterministic logical slot.</summary>
    /// <param name="slot">The logical player slot recorded in the command snapshot.</param>
    /// <param name="actingPrincipal">The principal asking to confirm — <c>context.ActingPrincipal()</c> for a
    /// console dispatch, or <see cref="SelfProvisioned"/> for a physical press. Mandatory.</param>
    /// <param name="device">The physical controller performing an explicit confirmation, or <see langword="null"/>
    /// for a slot-addressed script action. Only reconnect-stable controller identities are remembered.</param>
    /// <returns>The confirm outcome and affected slot.</returns>
    public (ConfirmOutcome Outcome, int Slot) Confirm(int slot, WorldPrincipal actingPrincipal, InputDeviceId? device) {
        if (((uint)slot >= MaxSlots) || (m_slots[slot] is not { } participant)) {
            return (Outcome: ConfirmOutcome.Ignored, Slot: -1);
        }

        if (participant.State != ParticipantState.Pending) {
            return (Outcome: ConfirmOutcome.AlreadyActive, Slot: slot);
        }

        return (Activate(slot: slot, participant: participant, actingPrincipal: actingPrincipal, device: device)
            ? (Outcome: ConfirmOutcome.Confirmed, Slot: slot)
            : (Outcome: ConfirmOutcome.Denied, Slot: slot));
    }

    /// <summary>Sets a specific profile on the slot's participant and makes it active (the <c>player.identity</c> verb):
    /// a live identity switch on an active player, or a choose-and-confirm on a pending one. The server's verdict is
    /// checked before any local mutation.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    /// <param name="profile">The profile to seat on.</param>
    /// <param name="actingPrincipal">The principal asking to set the profile — <c>context.ActingPrincipal()</c> for a
    /// console dispatch, or <see cref="SelfProvisioned"/> for a device/confirm gesture. Mandatory.</param>
    /// <returns>The set outcome.</returns>
    public SetProfileOutcome SetProfile(int slot, WorldIdentity profile, WorldPrincipal actingPrincipal) {
        if (m_slots[slot] is not { } participant) {
            return SetProfileOutcome.NotJoined;
        }

        if (IsProfileActiveElsewhere(profile: profile, exceptSlot: slot)) {
            return SetProfileOutcome.InUse;
        }

        // Checked BEFORE any local mutation, and the reply is no longer discarded: a denial leaves the participant
        // untouched. The mutation runs from INSIDE the completion (fires inline over loopback), never a post-submit
        // live read.
        var outcome = SetProfileOutcome.Denied;

        m_link.SubmitSession(request: new SessionRequest.SetIdentity(Principal: actingPrincipal, Slot: slot, IdentityName: profile.Name), completion: reply => {
            if (!reply.Accepted) {
                Console.Error.WriteLine(value: $"[player.identity denied: {reply.Reason}]");

                return;
            }

            participant.Seat.Profile = profile;
            participant.State = ParticipantState.Active;
            m_seatBindings.SetProfileLayers(slot: slot, bindings: profile.Bindings);

            m_revision++;

            outcome = SetProfileOutcome.Ok;
        });

        return outcome;
    }

    /// <summary>Cycles a pending participant's candidate profile by one step through the unused profiles (a picker
    /// gesture). A no-op if the slot is not pending or every profile is in use.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    /// <param name="direction">+1 for the next unused profile, -1 for the previous.</param>
    public void CycleCandidate(int slot, int direction) {
        if (m_slots[slot] is not { State: ParticipantState.Pending } participant) {
            return;
        }

        var all = m_profiles.All;
        var count = all.Count;

        if (count == 0) {
            return;
        }

        var start = IndexOfProfile(profile: participant.Seat.Profile);

        for (var step = 1; (step <= count); step++) {
            var index = ((((start + (direction * step)) % count) + count) % count);
            var candidate = all[index];

            if (!IsProfileActiveElsewhere(profile: candidate, exceptSlot: slot)) {
                // A candidate is client-side identity (color); the server body reseats on confirm/SetProfile.
                participant.Seat.Profile = candidate;
                m_revision++;

                return;
            }
        }
    }

    /// <summary>The one picker entry point both the stick picker and the keyboard turn keys route through while a slot
    /// is pending: it cycles the candidate profile by <paramref name="direction"/> and returns whether the slot was
    /// pending (and thus consumed the input as a pick). An active slot returns <see langword="false"/>, so the caller
    /// drives locomotion instead — the roster, not the input surface, owns the pending-vs-locomotion decision. A
    /// direction of 0 (a non-turn axis pressed while pending) is consumed with no cycle: the other axes stay inert
    /// during a pick.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    /// <param name="direction">+1 for the next candidate, -1 for the previous, 0 for an inert (non-turn) press.</param>
    /// <returns><see langword="true"/> when the slot was pending and consumed the input as a picker gesture.</returns>
    public bool TryPickerStep(int slot, int direction) {
        if (!IsPending(slot: slot)) {
            return false;
        }

        if (direction != 0) {
            CycleCandidate(slot: slot, direction: direction);
        }

        return true;
    }

    /// <summary>Cycles a device to the next slot (wrapping player 1→2→3→4→1) — the pad-Start gesture. An unmapped
    /// device is joined instead. A slot under an exclusive <see cref="TryClaimSlot"/> hold is skipped rather than
    /// halting the ring: <see cref="AssignDevice"/> refuses a claimed target outright, so stepping past it (rather
    /// than stopping on the first claimed neighbor) is what keeps the gesture usable on a table where some slot is
    /// claimed (the editor, a replay device) — the ring lands back on the device's own slot (a no-op) only when every
    /// other slot is claimed.</summary>
    /// <param name="device">The device to cycle.</param>
    /// <param name="actingPrincipal">The principal asking to cycle — <c>context.ActingPrincipal()</c>, the caller's
    /// ingress-stamped identity, consumed here and never reconstructed: for an already-bound device this is that
    /// device's own source-seat stamp (relocation authorizes as the source — see
    /// <see cref="AssignDevice(InputDeviceId, int, WorldPrincipal)"/>'s remarks); for a wholly unbound device this
    /// call never reaches <see cref="AssignDevice(InputDeviceId, int, WorldPrincipal)"/> at all (the first-touch
    /// branch below routes through <see cref="ResolveDeviceSlot"/> instead, which self-provisions internally).
    /// Mandatory.</param>
    /// <returns>The reassignment outcome and the resulting slot (0-based; -1 when none).</returns>
    public (AssignOutcome Outcome, int Slot) CycleDevice(InputDeviceId device, WorldPrincipal actingPrincipal) {
        // Same exclusion as Confirm(InputDeviceId) above: cycling reassigns device-to-slot routing, which must never
        // move a programmatically-claimed device off the slot it exclusively claimed (or worse, onto a human's).
        if (m_programmaticDevices.Contains(item: device)) {
            return (Outcome: AssignOutcome.DeviceClaimed, Slot: -1);
        }

        if (!m_deviceToSlot.TryGetValue(key: device, value: out var current)) {
            if (ResolveDeviceSlot(device: device) is not { } joined) {
                return (Outcome: AssignOutcome.Ignored, Slot: -1);
            }

            // A first cycle press that SEATED the device onto an already-active player joined that team; only an empty
            // slot became a fresh pending player, so echo JoinedTeam vs CreatedPending truthfully.
            return ((joined.State == ParticipantState.Active)
                ? (Outcome: AssignOutcome.JoinedTeam, Slot: m_deviceToSlot[device])
                : (Outcome: AssignOutcome.CreatedPending, Slot: m_deviceToSlot[device]));
        }

        for (var step = 1; (step <= MaxSlots); step++) {
            var candidate = ((current + step) % MaxSlots);

            if (IsClaimed(slot: candidate)) {
                continue;
            }

            // A physical Start press relocates an ALREADY-BOUND device onto its own next candidate slot —
            // self-service, never a third party's action — authorized under actingPrincipal, the caller's
            // ingress-stamped SOURCE identity (never a handler/primitive-constructed one; AssignDevice's own logic
            // decides whether that identity or self-provisioning governs the target, since it alone knows this
            // device was already bound here).
            return (Outcome: AssignDevice(device: device, targetSlot: candidate, actingPrincipal: actingPrincipal), Slot: candidate);
        }

        // Every other slot is claimed — nothing to cycle onto. Unreachable in practice today (a claim requires an
        // already-vacated slot; the device's own current slot can never itself be claimed), kept as a defensive floor
        // rather than an infinite loop.
        return (Outcome: AssignOutcome.TargetClaimed, Slot: -1);
    }

    /// <summary>Moves a device onto a slot (the F-key claim / console <c>player.assign</c> primitive): onto an
    /// occupied slot it joins that team; onto an empty slot it creates a pending player; onto its own slot it is a
    /// no-op. An emptied device-origin source slot dissolves. A target slot under an exclusive
    /// <see cref="TryClaimSlot"/> hold refuses the move outright (see <see cref="IsClaimed"/>) — a human device must
    /// never end up sharing, or silently acting through, a claimant's identity. Provisional until accepted: the
    /// source mapping is not released, and the target mapping is not written, until every affected body's
    /// authorization clears — see the remarks below for what each one asks.</summary>
    /// <remarks><b>Why the source needs its own check.</b> Relocating an already-bound device off a slot that would
    /// then have zero devices left orphans its participant, which <see cref="DissolveIfOrphanedDevice"/> then
    /// dissolves via <see cref="Leave"/> — a real mutation on a body the moving actor may hold no authority over if
    /// only the target were authorized. Both bodies are authorized under the same
    /// <paramref name="actingPrincipal"/>, before either is touched.</remarks>
    /// <param name="device">The device to move.</param>
    /// <param name="targetSlot">The destination slot (0-based).</param>
    /// <param name="actingPrincipal">The principal asking to move this device — the caller's ingress-stamped identity
    /// (<c>context.ActingPrincipal()</c>), consumed here, never constructed: Console for a console
    /// <c>player.assign</c> dispatch (an operator command that can name any device), or the source seat's own
    /// stamped principal for a physical claim/cycle relocation of an already-bound device — a device relocating
    /// itself is self-service, not a third party's action, so the source's own identity is what authorizes it
    /// (checked against both the source and target bodies below). An unbound device (no current mapping at all —
    /// the bootstrap case) has no source seat to speak of and self-provisions as the target instead, computed
    /// internally via <see cref="SelfProvisioned"/> regardless of what <paramref name="actingPrincipal"/> carries,
    /// because the caller's ingress stamp for a brand-new device's first press reflects whatever slot the arrival
    /// policy proposed for an unrelated purpose (<see cref="ResolveSlot"/>), not this gesture's own explicit
    /// target. Mandatory.</param>
    /// <returns>The reassignment outcome.</returns>
    public AssignOutcome AssignDevice(InputDeviceId device, int targetSlot, WorldPrincipal actingPrincipal) {
        if ((uint)targetSlot >= MaxSlots) {
            return AssignOutcome.Ignored;
        }

        // The primitive Confirm(InputDeviceId) and CycleDevice funnel a resolved reassignment into, and the
        // one player.claim/player.assign call directly — so the programmatic-device exclusion belongs here,
        // not duplicated at every caller: a device that claimed its slot via TryClaimSlot (the editor, a
        // replay device, a test harness) never moves itself — or anything else — via the ordinary
        // device-reassignment gesture surface. Nothing may move into a slot TryClaimSlot claimed either, or
        // an ordinary human device ends up sharing the slot and, through PrincipalOf, silently submitting
        // under the claimant's identity. ResolveSlot's and ResolveDeviceSlot's own slot-keyed checks already
        // prevent that on the arrival door (an unmapped device never gets offered a claimed slot);
        // CycleDevice's and Confirm's own m_programmaticDevices exclusions are device-keyed and only stop
        // the claimed device itself from moving — never a different, ordinary human device moving into the
        // slot a claim owns. This is the one door those checks do not reach, so it has to be gated here.
        if (m_programmaticDevices.Contains(item: device)) {
            return AssignOutcome.DeviceClaimed;
        }

        if (IsClaimed(slot: targetSlot)) {
            return AssignOutcome.TargetClaimed;
        }

        var hadCurrent = m_deviceToSlot.TryGetValue(key: device, value: out var current);

        if (hadCurrent && (current == targetSlot)) {
            return AssignOutcome.NoOp;
        }

        // An unbound device (the bootstrap case) has no source seat — see this method's own <paramref> remarks for
        // why it self-provisions as the target explicitly rather than trusting the caller's stamp. A bound device
        // relocating elsewhere authorizes under that stamp for BOTH bodies this move can affect.
        var effectivePrincipal = (hadCurrent ? actingPrincipal : SelfProvisioned(slot: targetSlot));

        // If this relocation would ORPHAN the source (its last device leaving, about to dissolve the
        // participant there), actingPrincipal must hold Drive over the SOURCE body too — checked BEFORE any
        // mutation, exactly like the target check below, so a principal with authority over only the destination
        // cannot delete an unrelated source body through the dissolution cascade.
        if (hadCurrent && WouldOrphanOnMove(slot: current) &&
            (m_server.Grants.Allows(principal: actingPrincipal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: current)) is { IsAllowed: false } sourceVerdict)) {
            Console.Error.WriteLine(value: $"[player.assign denied: {actingPrincipal.Describe()} cannot drive body:{current} ({sourceVerdict.DescribeDenial()}) — relocating this device would dissolve it]");

            return AssignOutcome.Denied;
        }

        AssignOutcome outcome;

        if (m_slots[targetSlot] is not null) {
            // Joining an ALREADY-OCCUPIED slot's team never rounds through SubmitSession (nothing new is minted —
            // the same body just gains a second input source), so this is the ONLY branch that has to ask
            // WorldGrants directly rather than inheriting the check from a session reply. The identical
            // (effectivePrincipal, Drive, body:targetSlot) pair SessionRequest.Join checks in the other branch.
            if (m_server.Grants.Allows(principal: effectivePrincipal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: targetSlot)) is { IsAllowed: false } verdict) {
                Console.Error.WriteLine(value: $"[player.assign denied: {effectivePrincipal.Describe()} cannot drive body:{targetSlot} ({verdict.DescribeDenial()})]");

                return AssignOutcome.Denied;
            }

            outcome = AssignOutcome.JoinedTeam;
        } else {
            // JoinPending checks the server's reply BEFORE installing a participant — trust ITS verdict, not a
            // separate pre-check, since it is the one call that actually mutates m_slots[targetSlot].
            if (JoinPending(slot: targetSlot, origin: ParticipantOrigin.Device, actingPrincipal: effectivePrincipal) != JoinResult.Ok) {
                return AssignOutcome.Denied;
            }

            outcome = AssignOutcome.CreatedPending;
        }

        // Only now — after EVERY affected body is known good — track the device and unmap the source. Provisional:
        // a denied request above returns before any of this runs, so the device stays exactly where it was.
        TrackDeviceOrder(device: device);

        if (hadCurrent) {
            DeviceSlotChanging?.Invoke(obj: device);
        }

        // When the keyboard leaves a slot, free the movement axes it was holding on the source seat: a still-down
        // key's release edge routes to the keyboard's new slot, so without this the source would walk forever (an
        // authored tape on the source is left intact). Pads are immune — ClearAnalog wipes their transient analog each
        // frame.
        if (hadCurrent && (device == KeyboardDevice)) {
            m_slots[current]?.Seat.ReleaseAllHeld();
        }

        if (hadCurrent) {
            _ = m_deviceToSlot.Remove(key: device);
        }

        m_deviceToSlot[device] = targetSlot;

        if (hadCurrent) {
            // The cascade's Leave carries the SAME actingPrincipal the source check above already cleared — never
            // a fabricated self-provisioned identity (see DissolveIfOrphanedDevice's own remarks).
            DissolveIfOrphanedDevice(slot: current, actingPrincipal: actingPrincipal);
        }

        return outcome;
    }

    // Predicts — WITHOUT mutating — whether relocating the device currently on `slot` away from it would leave that
    // slot's device-origin participant orphaned (the same condition DissolveIfOrphanedDevice checks AFTER the move,
    // computed here before it, so AssignDevice can authorize the dissolution before causing it). True exactly when
    // the slot holds a device-origin participant and exactly one device (the one about to move) currently maps there.
    private bool WouldOrphanOnMove(int slot) {
        return ((m_slots[slot] is { Origin: ParticipantOrigin.Device }) && (CountDevices(slot: slot, excludeKeyboard: false) == 1));
    }

    /// <summary>Resolves a device token (<c>kbd</c> or <c>pad&lt;N&gt;</c>) to its device id.</summary>
    /// <param name="token">The token (case-insensitive).</param>
    /// <param name="device">The resolved device id.</param>
    /// <returns><see langword="true"/> if the token names a known device.</returns>
    public bool TryResolveDeviceToken(string token, out InputDeviceId device) {
        device = default;

        if (string.IsNullOrWhiteSpace(value: token)) {
            return false;
        }

        if (string.Equals(a: token, b: "kbd", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            device = KeyboardDevice;

            return m_deviceOrder.Contains(item: KeyboardDevice);
        }

        if (token.StartsWith(value: "pad", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s: token.AsSpan(start: 3), result: out var ordinal) && (ordinal >= 1)) {
            var seen = 0;

            foreach (var candidate in m_deviceOrder) {
                if (candidate == KeyboardDevice) {
                    continue;
                }

                if (++seen == ordinal) {
                    device = candidate;

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Formats the roster for the <c>world.players</c> verb — one segment per slot, each joined slot carrying
    /// its profile name, state, owned devices (or origin), and pose.</summary>
    /// <returns>A line of the form <c>[world.players: p1 amber active(kbd) pos=(...) yaw=...° | p2 empty | ...]</c>.</returns>
    public string Describe() {
        var segments = new string[MaxSlots];

        for (var slot = 0; (slot < MaxSlots); slot++) {
            if (m_slots[slot] is not { } participant) {
                segments[slot] = $"p{DisplayNumber(slot: slot)} empty";

                continue;
            }

            var name = (participant.Seat.Profile?.Name ?? "?");
            var state = ((participant.State == ParticipantState.Active) ? "active" : "PENDING");
            var owners = DeviceTokensFor(slot: slot);
            var inside = ((owners.Length > 0) ? owners : OriginWord(origin: participant.Origin));
            var body = m_server.Body(index: slot);
            // A compact source marker beside the seat state, so a world.players glance shows a seat whose gaps are not
            // device-driven. Only off-Live seats carry it; a live seat adds nothing (the common, quiet case).
            var possessed = (((body is { } liveBody) && (liveBody.Source != IntentSource.Live)) ? ((liveBody.Source == IntentSource.Idle) ? " idle" : " wander") : "");
            var pose = (body?.DescribePose() ?? "pos=(?, ?) yaw=?°");

            segments[slot] = $"p{DisplayNumber(slot: slot)} {name} {state}({inside}){possessed} {pose}";
        }

        return $"[world.players: {string.Join(separator: " | ", values: segments)}]";
    }

    /// <summary>Formats the device table for the <c>world.devices</c> verb — every seen device token in first-seen
    /// order and the player it currently drives.</summary>
    /// <returns>A line of the form <c>[world.devices: kbd=p1 | pad1=p2]</c>.</returns>
    public string DescribeDevices() {
        var segments = new List<string>(capacity: m_deviceOrder.Count);

        foreach (var device in m_deviceOrder) {
            var owner = (m_deviceToSlot.TryGetValue(key: device, value: out var slot) ? $"p{DisplayNumber(slot: slot)}" : "unassigned");

            segments.Add(item: $"{DeviceToken(device: device)}={owner}");
        }

        return $"[world.devices: {string.Join(separator: " | ", values: segments)}]";
    }

    /// <summary>Formats the preferred-profile decision recorded when each connected device was first seen.</summary>
    /// <returns>A line naming every device's selected profile, or why no preference applied.</returns>
    public string DescribeDeviceProfiles() {
        var segments = new List<string>(capacity: m_deviceOrder.Count);

        foreach (var device in m_deviceOrder) {
            var token = DeviceToken(device: device);
            var decision = m_devicePreselections[device];
            var description = decision.Kind switch {
                DevicePreselectionKind.Keyboard => "none (keyboard has no controller preference)",
                DevicePreselectionKind.ConnectionOnly => "none (connection-only identity; XInput ids name slots, not physical pads)",
                DevicePreselectionKind.NoLocalPreference => "none (no preference on this machine)",
                DevicePreselectionKind.Preferred => $"{decision.Profile!.Name} (preferred controller match)",
                DevicePreselectionKind.AlreadySeated => $"none ({decision.Profile!.Name} already active on p{DisplayNumber(slot: decision.Seat)}; ordinary seating applied)",
                _ => $"none ({decision.Profile!.Name} matched, but no free seat could present it; ordinary seating applied)",
            };

            segments.Add(item: $"{token}={description}");
        }

        return $"[world.device-profiles: {string.Join(separator: " | ", values: segments)}]";
    }

    // Resolve the participant a device drives, joining it per the roster rules if unmapped: the first pad seats with
    // player 1 alongside the keyboard (attaching to an already-active player is not a join, so no profile choice is
    // owed; a deviceless slot 0 is claimed the same way); otherwise the next free slot as a pending player. A full
    // roster returns null. Tracks first-seen order for the token vocabulary.
    private Participant? ResolveDeviceSlot(InputDeviceId device) {
        // A mapped device is already in the first-seen order, so skip the List.Contains scan for it: register
        // first-seen order only on the unmapped branches below.
        if (m_deviceToSlot.TryGetValue(key: device, value: out var mapped)) {
            return m_slots[mapped];
        }

        TrackDeviceOrder(device: device);

        if ((m_devicePreselections[device] is { Kind: DevicePreselectionKind.Preferred, Profile: { } preferred }) &&
            (FirstFreeUnclaimedSlot() is var preferredSlot) && (preferredSlot >= 0)) {
            if (JoinPendingCandidate(slot: preferredSlot, origin: ParticipantOrigin.Device,
                actingPrincipal: SelfProvisioned(slot: preferredSlot), candidate: preferred) != JoinResult.Ok) {
                return null;
            }

            m_deviceToSlot[device] = preferredSlot;

            return m_slots[preferredSlot];
        }

        // Slot 0 gets the SAME TryClaimSlot exclusion the loop below applies to 1..3 — see ResolveSlot's matching guard.
        if ((device != KeyboardDevice) && (m_slotPrincipal[0] is null) && (m_slots[0] is { State: ParticipantState.Active }) && (CountDevices(slot: 0, excludeKeyboard: true) == 0)) {
            // Device mapping is not render state, so seating the first pad with player 1 does not bump the revision.
            m_deviceToSlot[device] = 0;

            return m_slots[0];
        }

        if ((m_slotPrincipal[0] is null) && (CountDevices(slot: 0, excludeKeyboard: false) == 0)) {
            m_deviceToSlot[device] = 0;

            return m_slots[0];
        }

        // Claim an already-active, deviceless local-human seat before creating a pending participant. This is the
        // mutating twin of ResolveSlot's proposal and preserves the deterministic p1..p4 pad-arrival order. A slot
        // TryClaimSlot has claimed is excluded — see ResolveSlot's matching guard.
        for (var existing = 1; (existing < MaxSlots); existing++) {
            if ((m_slotPrincipal[existing] is null) && (m_slots[existing] is not null) && (CountDevices(slot: existing, excludeKeyboard: false) == 0)) {
                m_deviceToSlot[device] = existing;

                return m_slots[existing];
            }
        }

        // A fresh, wholly unmapped device claiming the next free slot is the canonical self-provisioning case: it
        // becomes the very slot it is filling, explicitly (SelfProvisioned — never an omitted default).
        var (result, slot) = JoinPendingNextFree(origin: ParticipantOrigin.Device, actingPrincipal: SelfProvisioned);

        if (result != JoinResult.Ok) {
            return null;
        }

        m_deviceToSlot[device] = slot;

        return m_slots[slot];
    }

    // Consume a pending player's stick sample as its picker: a threshold crossing (edge-detected against the prior
    // sample) cycles the candidate — deflect left for the previous profile, right for the next. Compares the
    // ALREADY-QUANTIZED fixed sample against m_pickerThreshold (see its own remarks for the boundary-value
    // consequence).
    private void PendingPicker(Participant participant, int slot, FixedQ4816 stickX) {
        participant.StickSeenThisFrame = true;

        var wasPast = (FixedQ4816.Abs(value: participant.PendingPrevStickX) >= m_pickerThreshold);
        var isPast = (FixedQ4816.Abs(value: stickX) >= m_pickerThreshold);

        if (isPast && !wasPast) {
            CycleCandidate(slot: slot, direction: ((stickX < FixedQ4816.Zero) ? -1 : 1));
        }

        participant.PendingPrevStickX = stickX;
    }

    // Promote a pending participant to active on its candidate, first computing the final profile (bumped off any
    // profile now taken by another active player) WITHOUT installing it, then reseating the server body on that
    // choice, and only mutating the participant when the server accepts — the same submit-before-mutate shape
    // every other roster op follows. actingPrincipal is the ASKING principal (context.ActingPrincipal() for a
    // console dispatch, SelfProvisioned for a physical press) — mandatory and threaded from Confirm.
    private bool Activate(int slot, Participant participant, WorldPrincipal actingPrincipal, InputDeviceId? device) {
        var finalProfile = (IsProfileActiveElsewhere(profile: participant.Seat.Profile, exceptSlot: slot)
            ? FirstUnusedProfile(exceptSlot: slot)
            : participant.Seat.Profile)!;

        var accepted = false;

        // The mutation runs from INSIDE the completion (fires inline over loopback), never a post-submit live read.
        m_link.SubmitSession(request: new SessionRequest.SetIdentity(Principal: actingPrincipal, Slot: slot, IdentityName: finalProfile.Name), completion: reply => {
            if (!reply.Accepted) {
                Console.Error.WriteLine(value: $"[player.confirm denied: {reply.Reason}]");

                return;
            }

            participant.Seat.Profile = finalProfile;
            participant.State = ParticipantState.Active;
            m_seatBindings.SetProfileLayers(slot: slot, bindings: finalProfile.Bindings);

            if (device is { } confirmedBy) {
                m_profiles.RememberPreferredController(profile: finalProfile, device: confirmedBy);
            }

            m_revision++;

            accepted = true;
        });

        return accepted;
    }

    // Dissolve a slot whose last device just left, but only when it exists to be dissolved by a device leaving: a
    // device-origin slot with no devices left. Permanent (slot 0) and scripted slots stay. An internal cascade with
    // no ingress context of its own — the vacating seat leaving ITSELF self-provisions, explicitly.
    private void DissolveIfOrphanedDevice(int slot, WorldPrincipal actingPrincipal) {
        if ((m_slots[slot] is { Origin: ParticipantOrigin.Device }) && (CountDevices(slot: slot, excludeKeyboard: false) == 0)) {
            // actingPrincipal is the SAME identity AssignDevice already authorized the relocation under — NEVER a
            // fabricated SelfProvisioned(slot): the cascade dissolving this ORPHANED source is downstream of one
            // relocation gesture, and the acting principal that gesture already checked must be the one this Leave
            // is checked under too (AssignDevice's own pre-check already refused the relocation outright when this
            // principal lacked Drive over the source body, so reaching here means it was already proven to hold it —
            // this call is the mutation that check authorized, not a second independent decision).
            _ = Leave(slot: slot, actingPrincipal: actingPrincipal);
        }
    }

    // Fill a slot with a fresh participant selecting a profile, mirroring the join to the server (which mints the
    // seat's body at its staggered spawn, facing -Z toward the boulders). The server's verdict is checked BEFORE any
    // local mutation: a denied join leaves the roster untouched instead of installing a participant the server never
    // minted a body for. actingPrincipal is MANDATORY and EXPLICIT at every call site — context.ActingPrincipal()
    // for a console dispatch, or SelfProvisioned(slot) for a device/boot self-provisioning as the very slot it is
    // filling (exact rather than merely convenient: TryClaimSlot requires a slot to ALREADY hold a participant, so
    // a slot reaching Fill, still empty, can never carry a claim — PrincipalOf(slot) would return the identical
    // value).
    private bool Fill(int slot, WorldIdentity profile, ParticipantState state, ParticipantOrigin origin, WorldPrincipal actingPrincipal) {
        var accepted = false;

        // The completion fires inline (loopback drains its ordered domain synchronously before SubmitSession
        // returns), so the local mutation below runs from inside the completion, gated on the reply it
        // received, before Fill returns.
        m_link.SubmitSession(request: new SessionRequest.Join(Principal: actingPrincipal, Slot: slot, IdentityName: profile.Name, WireProtocolKey: WorldProtocol.WireProtocolKey), completion: reply => {
            if (!reply.Accepted) {
                Console.Error.WriteLine(value: $"[player.join denied: {reply.Reason}]");

                return;
            }

            m_slots[slot] = new Participant {
                Origin = origin,
                Seat = new SeatController {
                    Profile = profile,
                    // The same table the server compiled from the identical world document (WorldPopulation.CompileFixedTables
                    // runs in WorldServer's own constructor dependency, always ahead of this DI-resolved roster) — the
                    // shape data HeldChannels' end clamp needs (bipolar/unipolar/binary), never re-derived here.
                    Channels = m_server.Population.Channels,
                },
                State = state,
            };
            // The seat resolves through its selected profile's binding layer (null = the engine default) — pushed once at
            // fill so the seat's composed mapping is right from its first tick.
            m_seatBindings.SetProfileLayers(slot: slot, bindings: profile.Bindings);

            accepted = true;
        });

        return accepted;
    }

    // How many mapped devices a slot owns. With excludeKeyboard false every device counts; with it true only gamepads
    // count (the keyboard's default id is skipped), so the first-pad-seats-with-player-1 test reads "no pad yet".
    private int CountDevices(int slot, bool excludeKeyboard) {
        var count = 0;

        foreach (var pair in m_deviceToSlot) {
            if ((pair.Value == slot) && (!excludeKeyboard || (pair.Key != KeyboardDevice))) {
                count++;
            }
        }

        return count;
    }

    // Whether the profile is seated on by an ACTIVE participant other than the excepted slot (a pending candidate is
    // tentative and does not reserve a profile).
    private bool IsProfileActiveElsewhere(WorldIdentity? profile, int exceptSlot) {
        if (profile is null) {
            return false;
        }

        for (var slot = 0; (slot < MaxSlots); slot++) {
            if ((slot != exceptSlot) && (m_slots[slot] is { State: ParticipantState.Active } participant) && ReferenceEquals(objA: participant.Seat.Profile, objB: profile)) {
                return true;
            }
        }

        return false;
    }

    // The first profile not seated on by an active participant (other than the excepted slot), or the first profile
    // when every one is taken (there are as many profiles as slots in the default catalog).
    private WorldIdentity FirstUnusedProfile(int exceptSlot) {
        foreach (var profile in m_profiles.All) {
            if (!IsProfileActiveElsewhere(profile: profile, exceptSlot: exceptSlot)) {
                return profile;
            }
        }

        return m_profiles.All[0];
    }

    // The catalog index of a profile (by reference), or 0 when it is not found.
    private int IndexOfProfile(WorldIdentity? profile) {
        var all = m_profiles.All;

        for (var index = 0; (index < all.Count); index++) {
            if (ReferenceEquals(objA: all[index], objB: profile)) {
                return index;
            }
        }

        return 0;
    }

    // The lowest empty slot index (0-based), or -1 if the roster is full.
    private int FirstFreeSlot() {
        for (var slot = 0; (slot < MaxSlots); slot++) {
            if (m_slots[slot] is null) {
                return slot;
            }
        }

        return -1;
    }
    private int FirstFreeUnclaimedSlot() {
        for (var slot = 1; (slot < MaxSlots); slot++) {
            if ((m_slots[slot] is null) && (m_slotPrincipal[slot] is null) &&
                (CountDevices(slot: slot, excludeKeyboard: false) == 0)) {
                return slot;
            }
        }

        return -1;
    }
    private DevicePreselection EvaluatePreselection(InputDeviceId device) {
        if (device == KeyboardDevice) {
            return new DevicePreselection(Kind: DevicePreselectionKind.Keyboard, Profile: null, Seat: -1);
        }

        if (device.Persistence != InputDeviceIdentityPersistence.Reconnect) {
            return new DevicePreselection(Kind: DevicePreselectionKind.ConnectionOnly, Profile: null, Seat: -1);
        }

        if (m_profiles.PreferredProfile(device: device) is not { } preferred) {
            return new DevicePreselection(Kind: DevicePreselectionKind.NoLocalPreference, Profile: null, Seat: -1);
        }

        var activeSeat = ActiveSlotUsing(profile: preferred);

        if (activeSeat >= 0) {
            return new DevicePreselection(Kind: DevicePreselectionKind.AlreadySeated, Profile: preferred, Seat: activeSeat);
        }

        return new DevicePreselection(
            Kind: ((FirstFreeUnclaimedSlot() >= 0) ? DevicePreselectionKind.Preferred : DevicePreselectionKind.NoFreeSeat),
            Profile: preferred,
            Seat: -1
        );
    }
    private WorldIdentity? PreferredCandidateFor(int slot) {
        foreach (var device in m_deviceOrder) {
            if (m_deviceToSlot.TryGetValue(key: device, value: out var mapped) && (mapped == slot) &&
                m_devicePreselections.TryGetValue(key: device, value: out var decision) &&
                (decision.Kind == DevicePreselectionKind.Preferred)) {
                return decision.Profile;
            }
        }

        return null;
    }

    // Record a device the first time it is seen, so the kbd/pad<N> token order is stable.
    private void TrackDeviceOrder(InputDeviceId device) {
        if (!m_deviceOrder.Contains(item: device)) {
            m_deviceOrder.Add(item: device);
            m_devicePreselections[device] = EvaluatePreselection(device: device);
        }
    }

    /// <summary>The stable token for a device: the keyboard is <c>kbd</c>; each pad is <c>pad&lt;N&gt;</c> by first-seen
    /// order. Public so a verb echo can name the device a gesture acted on (e.g. "pad1 seated with player 1").</summary>
    /// <param name="device">The device id.</param>
    public string DeviceToken(InputDeviceId device) {
        if (device == KeyboardDevice) {
            return "kbd";
        }

        var ordinal = 0;

        foreach (var candidate in m_deviceOrder) {
            if (candidate == KeyboardDevice) {
                continue;
            }

            ordinal++;

            if (candidate == device) {
                return $"pad{ordinal}";
            }
        }

        return "pad?";
    }

    // The tokens of every device currently mapped to the slot, joined with "+" (first-seen order), or empty.
    private string DeviceTokensFor(int slot) {
        var builder = new StringBuilder();

        foreach (var device in m_deviceOrder) {
            if (m_deviceToSlot.TryGetValue(key: device, value: out var mapped) && (mapped == slot)) {
                if (builder.Length > 0) {
                    _ = builder.Append(value: '+');
                }

                _ = builder.Append(value: DeviceToken(device: device));
            }
        }

        return builder.ToString();
    }
    private static string OriginWord(ParticipantOrigin origin) {
        return origin switch {
            ParticipantOrigin.Script => "script",
            ParticipantOrigin.Device => "device",
            _ => "none",
        };
    }

    private readonly record struct DevicePreselection(DevicePreselectionKind Kind, WorldIdentity? Profile, int Seat);

    // A slot's participant: the seat controller staging its device intent, its confirm state, its origin, and the
    // transient picker edge state a pending player's stick uses. A mutable class (not a record) so State and the
    // picker fields flip in place.
    private sealed class Participant {
        public required ParticipantOrigin Origin { get; init; }
        public FixedQ4816 PendingPrevStickX { get; set; }
        public required SeatController Seat { get; init; }
        public ParticipantState State { get; set; }
        public bool StickSeenThisFrame { get; set; }
    }
}
