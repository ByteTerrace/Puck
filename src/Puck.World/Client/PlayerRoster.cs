using System.Numerics;
using System.Text;
using Puck.Commands;
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
    /// <summary>The roster was full (an empty-target join could not be made) — nothing changed.</summary>
    Ignored,

    /// <summary>The device already owned the target slot — a friendly no-op.</summary>
    NoOp,

    /// <summary>The device moved onto an occupied slot, joining that team instantly.</summary>
    JoinedTeam,

    /// <summary>The device moved onto an empty slot, creating a pending player (a profile must be chosen).</summary>
    CreatedPending,
}

/// <summary>The outcome of a <c>player.confirm</c>, for the verb to echo.</summary>
internal enum ConfirmOutcome {
    /// <summary>The device could not be mapped (the roster was full) — nothing changed.</summary>
    Ignored,

    /// <summary>The device was unmapped and this press mapped it onto a PENDING slot (a first press joins; a second
    /// confirms) — a profile choice is owed.</summary>
    Joined,

    /// <summary>The device was unmapped and this press SEATED it with an already-active player (the share-player-1
    /// default) — no profile choice is owed, so it is not a pending join.</summary>
    Seated,

    /// <summary>A pending participant owning the device was promoted to active on its candidate profile.</summary>
    Confirmed,

    /// <summary>The device's participant was already active — a friendly no-op.</summary>
    AlreadyActive,
}

/// <summary>The outcome of a <c>player.profile</c> identity set, for the verb to echo.</summary>
internal enum SetProfileOutcome {
    /// <summary>The target slot is not joined.</summary>
    NotJoined,

    /// <summary>The profile is already in use by another active player.</summary>
    InUse,

    /// <summary>The profile was assigned and the participant is active.</summary>
    Ok,
}

/// <summary>
/// The client's participant table: up to four slots, each a seat (its <see cref="SeatController"/> device-intent
/// producer plus a viewport on the layout ladder), carrying its confirmed-or-pending <see cref="ParticipantState"/>,
/// its <see cref="ParticipantOrigin"/>, and the <see cref="WorldProfile"/> it selects (color and look-invert client
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
/// Slot 0 (player 1) is always joined from boot and never leaves; it wakes seated on the profiles' boot seat
/// (<see cref="WorldProfiles.BootProfile"/>).
/// </para>
/// <para>
/// Single-threaded: every mutator runs during the command pump's <c>Collect</c>, and the frame source reads during
/// produce — both on the launcher's window-pump thread, so no lock guards this state. <see cref="Revision"/> bumps
/// whenever a slot's occupancy, state, or color changes; the frame source watches it to rebuild the program.
/// </para>
/// </remarks>
internal sealed class PlayerRoster : IInputSlotResolver {
    /// <summary>The maximum number of local participants — a quad viewport's worth (the server table's seat count).</summary>
    public const int MaxSlots = WorldPopulation.LocalSeatCount;

    /// <summary>The keyboard's device id — the one device the roster names by identity. A device id is a content-addressed
    /// <see cref="InputDeviceId"/>; the keyboard alone rides the <see langword="default"/> (all-zero) id, mapped to slot 0
    /// from boot. Comparisons that MEAN "the keyboard" spell this rather than a bare <c>default</c>.</summary>
    public static InputDeviceId KeyboardDevice => default;

    /// <inheritdoc/>
    public event Action<InputDeviceId>? DeviceSlotChanging;

    /// <summary>The 1-based display number for a 0-based slot (slot 0 is "player 1").</summary>
    /// <param name="slot">The slot index (0-based).</param>
    internal static int DisplayNumber(int slot) => (slot + 1);

    /// <summary>The 0-based slot for a 1-based display number (the inverse of <see cref="DisplayNumber"/>).</summary>
    /// <param name="number">The display number (1-based).</param>
    internal static int SlotFromDisplay(int number) => (number - 1);

    // The stick deflection a pending player must cross to cycle its candidate profile (edge-detected).
    private const float PickerThreshold = 0.6f;
    // The pending avatar's desaturation target (a neutral gray, #8C8C8C): its candidate color is lerped halfway here.
    private static readonly Vector3 s_pendingGray = new(value: 0.549f);
    private readonly Participant?[] m_slots = new Participant?[MaxSlots];
    // Device → slot map. A pad's id is stable across reconnects (content-addressed), so a controller keeps its slot;
    // the keyboard (default id) is mapped to slot 0 from boot.
    private readonly Dictionary<InputDeviceId, int> m_deviceToSlot = new();
    // First-seen device order — the stable basis for the kbd/pad<N> tokens the reassignment verbs speak. Append-only
    // so a token never shifts under a player.
    private readonly List<InputDeviceId> m_deviceOrder = [];
    // LOOPBACK-ONLY: the client selects from the server-owned catalog by direct reference; a socket transport replaces
    // this with a profile-catalog query/stream over the link.
    private readonly WorldProfiles m_profiles;
    // The session wire seat occupancy mirrors over, and — LOOPBACK-ONLY — the server the pose read-backs
    // (world.players) resolve through in-process; a socket transport replaces the m_server reads with link queries.
    private readonly IServerLink m_link;
    private readonly WorldServer m_server;
    // The per-seat input resolver, pushed a seat's selected-profile binding layer whenever its identity settles so the
    // seat's composed mapping (default ⊕ overlays ⊕ profile ⊕ session) recompiles once, off the frame path.
    private readonly WorldSeatBindings m_seatBindings;
    private int m_revision;

    /// <summary>Initializes a new instance of the <see cref="PlayerRoster"/> class with the world definition's local
    /// census already active (each boot seat mirrored to the server as a session join). Player 1 owns the keyboard and
    /// uses the profiles' boot seat; the remaining configured seats take distinct unused profiles in catalog order and
    /// begin deviceless so connected pads can claim them.</summary>
    /// <param name="profiles">The live profile catalog participants seat on.</param>
    /// <param name="definition">The world definition supplying the local census.</param>
    /// <param name="link">The client→server link session requests ride.</param>
    /// <param name="server">The authoritative server the pose read-backs resolve through.</param>
    /// <param name="seatBindings">The per-seat input resolver the roster pushes profile binding layers into.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public PlayerRoster(WorldProfiles profiles, WorldDefinition definition, IServerLink link, WorldServer server, WorldSeatBindings seatBindings) {
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: link);
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: seatBindings);

        m_profiles = profiles;
        m_link = link;
        m_server = server;
        m_seatBindings = seatBindings;
        // The keyboard is a device like any pad — its sentinel id, owned by slot 0 from boot and listed first.
        m_deviceOrder.Add(item: KeyboardDevice);
        m_deviceToSlot[KeyboardDevice] = 0;
        Fill(slot: 0, profile: m_profiles.BootProfile, state: ParticipantState.Active, origin: ParticipantOrigin.Permanent);

        for (var slot = 1; (slot < definition.Population.LocalPlayers); slot++) {
            Fill(slot: slot, profile: FirstUnusedProfile(exceptSlot: -1), state: ParticipantState.Active, origin: ParticipantOrigin.Permanent);
        }
    }

    /// <summary>A monotonically increasing counter bumped whenever a slot's occupancy, state, or color changes. The
    /// frame source rebuilds the program (avatar colors + <c>Active</c> flags) and re-lays-out the viewports on change.</summary>
    public int Revision => m_revision;

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
    public WorldProfile? FindProfile(string name) => m_profiles.Find(name: name);

    /// <summary>Whether the slot (0-based) currently holds a participant (pending or active).</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public bool IsJoined(int slot) => (((uint)slot < MaxSlots) && (m_slots[slot] is not null));

    /// <summary>The seat controller in the slot (0-based), or <see langword="null"/> if the slot is empty or out of
    /// range. The seat's authoritative body lives on the server.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public SeatController? Seat(int slot) => (((uint)slot < MaxSlots) ? m_slots[slot]?.Seat : null);

    /// <summary>The profile the slot's participant selects, or <see langword="null"/> for an empty slot.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public WorldProfile? ProfileAt(int slot) => (((uint)slot < MaxSlots) ? m_slots[slot]?.Seat.Profile : null);

    /// <summary>Whether the slot's participant is still choosing a profile.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public bool IsPending(int slot) => (((uint)slot < MaxSlots) && (m_slots[slot]?.State == ParticipantState.Pending));

    /// <summary>The slot (0-based) a device currently owns, or <see langword="null"/> if it is unmapped.</summary>
    /// <param name="device">The device id.</param>
    public int? DeviceSlot(InputDeviceId device) => (m_deviceToSlot.TryGetValue(key: device, value: out var slot) ? slot : null);

    /// <inheritdoc/>
    public int ResolveSlot(InputDeviceId device) {
        if (m_deviceToSlot.TryGetValue(key: device, value: out var mapped)) {
            return mapped;
        }

        // Probe the seating policy without mutating routing or simulation state. InputRouter commits this proposal only
        // after it finds a binding on an active command map.
        if ((device != KeyboardDevice) && (m_slots[0] is { State: ParticipantState.Active }) && (CountDevices(slot: 0, excludeKeyboard: true) == 0)) {
            return 0;
        }

        if (CountDevices(slot: 0, excludeKeyboard: false) == 0) {
            return 0;
        }

        // The built-in census may have already created players 2..4 as active, deviceless local-human seats. Prefer
        // claiming those existing seats in slot order before proposing a new participant, so four arriving pads map to
        // p1..p4 without requiring a roster verb or replacing the avatars already visible in split screen.
        for (var slot = 1; (slot < MaxSlots); slot++) {
            if ((m_slots[slot] is not null) && (CountDevices(slot: slot, excludeKeyboard: false) == 0)) {
                return slot;
            }
        }

        for (var slot = 1; (slot < MaxSlots); slot++) {
            // A prior source signal in this same collection pass may have reserved an otherwise-empty lane through
            // CommitSlot before its simulation-routed join reaches the roster. Treat that device-map reservation as
            // occupancy so two first-seen devices cannot probe and commit the same slot in one tick.
            if ((m_slots[slot] is null) && (CountDevices(slot: slot, excludeKeyboard: false) == 0)) {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Commits a probed slot as a local device-routing annotation. Participant occupancy is deliberately
    /// untouched: only the recorded command lane may create simulation state.</summary>
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
    public int ActiveSlotUsing(WorldProfile profile) {
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
            return s_pendingGray;
        }

        var color = (participant.Seat.Profile?.Color ?? s_pendingGray);

        return ((participant.State == ParticipantState.Active)
            ? color
            : Vector3.Lerp(value1: color, value2: s_pendingGray, amount: 0.5f));
    }
    /// <summary>The render nose color for the slot — the body color darkened by <see cref="WorldProfile.NoseFactor"/>.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    public Vector3 NoseColor(int slot) => WorldColor.Nose(body: BodyColor(slot: slot));

    /// <summary>Routes a movement-stick (left) sample to the device's player, joining the device per the roster rules
    /// if it is unmapped. The device is resolved only at the live-input boundary; established and replayed input uses
    /// the logical-slot overload below.</summary>
    /// <param name="device">The device that produced the sample.</param>
    /// <param name="value">The stick sample (+Y forward, +X strafe right).</param>
    public void RouteMove(InputDeviceId device, Vector2 value) {
        if (ResolveDeviceSlot(device: device) is not { } participant) {
            return;
        }

        RouteMove(slot: m_deviceToSlot[device], participant: participant, value: value);
    }
    /// <summary>Routes a movement-stick sample from a deterministic command lane to its logical player slot.</summary>
    /// <param name="slot">The logical player slot recorded in the command snapshot.</param>
    /// <param name="value">The stick sample (+Y forward, +X strafe right).</param>
    public void RouteMove(int slot, Vector2 value) {
        if ((uint)slot >= MaxSlots) {
            return;
        }

        if (m_slots[slot] is null) {
            _ = JoinPending(slot: slot, origin: ParticipantOrigin.Device);
        }

        var participant = m_slots[slot]!;

        RouteMove(slot: slot, participant: participant, value: value);
    }

    private void RouteMove(int slot, Participant participant, Vector2 value) {

        // Always stash the sample so player.sticks reads truthfully even for a pending pad — a script most wants to
        // observe the new pad exactly when it is still choosing a profile. Submission is gated on Active, so a pending
        // player never MOVES from this sample; its move stick doubles as the profile picker below.
        participant.Seat.SetAnalogMove(move: value);

        if (participant.State == ParticipantState.Pending) {
            PendingPicker(participant: participant, slot: slot, stickX: value.X);
        }
    }
    /// <summary>Routes a look-stick (right) sample to the device's player, joining the device per the roster rules if
    /// it is unmapped. Ignored for a pending player (the move stick is its picker).</summary>
    /// <param name="device">The device that produced the sample.</param>
    /// <param name="value">The stick sample (+X turns right).</param>
    public void RouteLook(InputDeviceId device, Vector2 value) {
        if (ResolveDeviceSlot(device: device) is not { } participant) {
            return;
        }

        RouteLook(slot: m_deviceToSlot[device], value: value);
    }
    /// <summary>Routes a look-stick sample from a deterministic command lane to its logical player slot.</summary>
    /// <param name="slot">The logical player slot recorded in the command snapshot.</param>
    /// <param name="value">The stick sample (+X turns right).</param>
    public void RouteLook(int slot, Vector2 value) {
        if ((uint)slot >= MaxSlots) {
            return;
        }

        if (m_slots[slot] is null) {
            _ = JoinPending(slot: slot, origin: ParticipantOrigin.Device);
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
                participant.PendingPrevStickX = 0f;
            }

            participant.StickSeenThisFrame = false;
        }
    }

    /// <summary>Joins a specific slot (0-based) as a PENDING player (a candidate profile is chosen; the player must
    /// confirm) — the scripted <c>player.join &lt;n&gt;</c> path.</summary>
    /// <param name="slot">The slot index (0-based) to join.</param>
    /// <param name="origin">Why the slot is being filled (script or device).</param>
    /// <returns><see langword="true"/> if the slot was joined; <see langword="false"/> if out of range or already joined.</returns>
    public bool JoinPending(int slot, ParticipantOrigin origin) {
        if (((uint)slot >= MaxSlots) || (m_slots[slot] is not null)) {
            return false;
        }

        Fill(slot: slot, profile: FirstUnusedProfile(exceptSlot: -1), state: ParticipantState.Pending, origin: origin);
        m_revision++;

        return true;
    }
    /// <summary>Joins the lowest free slot as a pending player.</summary>
    /// <param name="origin">Why the slot is being filled.</param>
    /// <returns>The joined slot index (0-based), or -1 if the roster is full.</returns>
    public int JoinPendingNextFree(ParticipantOrigin origin) {
        var slot = FirstFreeSlot();

        return (((slot >= 0) && JoinPending(slot: slot, origin: origin)) ? slot : -1);
    }
    /// <summary>Joins a specific slot (0-based) directly ACTIVE on a chosen profile — the one-shot
    /// <c>player.join &lt;profile&gt; &lt;n&gt;</c> path.</summary>
    /// <param name="slot">The slot index (0-based) to join.</param>
    /// <param name="profile">The profile to seat on.</param>
    /// <param name="origin">Why the slot is being filled.</param>
    /// <returns><see langword="true"/> if the slot was joined; <see langword="false"/> if out of range or already joined.</returns>
    public bool JoinActive(int slot, WorldProfile profile, ParticipantOrigin origin) {
        if (((uint)slot >= MaxSlots) || (m_slots[slot] is not null)) {
            return false;
        }

        Fill(slot: slot, profile: profile, state: ParticipantState.Active, origin: origin);
        m_revision++;

        return true;
    }
    /// <summary>Joins the lowest free slot directly active on a chosen profile.</summary>
    /// <param name="profile">The profile to seat on.</param>
    /// <param name="origin">Why the slot is being filled.</param>
    /// <returns>The joined slot index (0-based), or -1 if the roster is full.</returns>
    public int JoinActiveNextFree(WorldProfile profile, ParticipantOrigin origin) {
        var slot = FirstFreeSlot();

        return (((slot >= 0) && JoinActive(slot: slot, profile: profile, origin: origin)) ? slot : -1);
    }
    /// <summary>Removes a scripted or device player from the slot (0-based), unmapping any devices that owned it and
    /// mirroring the leave to the server (dropping the seat's body). Player 1 (slot 0) never leaves.</summary>
    /// <param name="slot">The slot index (0-based) to free.</param>
    /// <returns><see langword="true"/> if the slot was freed; <see langword="false"/> for slot 0, an out-of-range slot, or an already-empty slot.</returns>
    public bool Leave(int slot) {
        if ((slot <= 0) || (slot >= MaxSlots) || (m_slots[slot] is null)) {
            return false;
        }

        m_slots[slot] = null;
        _ = m_link.SubmitSession(request: new SessionRequest.Leave(Principal: WorldPrincipal.Seat(slot: slot), Slot: slot));

        // Drop any devices that were driving this slot so a reconnecting pad re-joins cleanly.
        foreach (var device in m_deviceToSlot.Where(predicate: pair => (pair.Value == slot)).Select(selector: pair => pair.Key).ToArray()) {
            DeviceSlotChanging?.Invoke(obj: device);
            _ = m_deviceToSlot.Remove(key: device);
        }

        m_revision++;

        return true;
    }

    /// <summary>Confirms the pending participant owning a device (the <c>player.confirm</c> verb). An unmapped device
    /// is first mapped by this press (a first press joins, a second confirms); an already-active participant is a
    /// no-op.</summary>
    /// <param name="device">The device that pressed confirm.</param>
    /// <returns>The confirm outcome and the affected slot (0-based; -1 when none).</returns>
    public (ConfirmOutcome Outcome, int Slot) Confirm(InputDeviceId device) {
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

        return Confirm(slot: m_deviceToSlot[device]);
    }

    /// <summary>Confirms a pending participant by deterministic logical slot.</summary>
    /// <param name="slot">The logical player slot recorded in the command snapshot.</param>
    /// <returns>The confirm outcome and affected slot.</returns>
    public (ConfirmOutcome Outcome, int Slot) Confirm(int slot) {
        if (((uint)slot >= MaxSlots) || (m_slots[slot] is not { } participant)) {
            return (Outcome: ConfirmOutcome.Ignored, Slot: -1);
        }

        if (participant.State != ParticipantState.Pending) {
            return (Outcome: ConfirmOutcome.AlreadyActive, Slot: slot);
        }

        Activate(slot: slot, participant: participant);

        return (Outcome: ConfirmOutcome.Confirmed, Slot: slot);
    }

    /// <summary>Sets a specific profile on the slot's participant and makes it active (the <c>player.profile</c> verb):
    /// a live identity switch on an active player, or a choose-and-confirm on a pending one. Persists the boot seat
    /// when slot 0's identity changes.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    /// <param name="profile">The profile to seat on.</param>
    /// <returns>The set outcome.</returns>
    public SetProfileOutcome SetProfile(int slot, WorldProfile profile) {
        if (m_slots[slot] is not { } participant) {
            return SetProfileOutcome.NotJoined;
        }

        if (IsProfileActiveElsewhere(profile: profile, exceptSlot: slot)) {
            return SetProfileOutcome.InUse;
        }

        participant.Seat.Profile = profile;
        participant.State = ParticipantState.Active;
        _ = m_link.SubmitSession(request: new SessionRequest.SetProfile(Principal: WorldPrincipal.Seat(slot: slot), Slot: slot, ProfileName: profile.Name));
        m_seatBindings.SetProfileBindings(slot: slot, bindings: profile.Bindings);

        if (slot == 0) {
            m_profiles.SetLastUsed(id: profile.Id);
        }

        m_revision++;

        return SetProfileOutcome.Ok;
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

    /// <summary>The ONE picker entry point both the stick picker and the keyboard turn keys route through while a slot
    /// is PENDING: it cycles the candidate profile by <paramref name="direction"/> and returns whether the slot was
    /// pending (and thus consumed the input as a pick). An ACTIVE slot returns <see langword="false"/>, so the caller
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
    /// device is joined instead.</summary>
    /// <param name="device">The device to cycle.</param>
    /// <returns>The reassignment outcome and the resulting slot (0-based; -1 when none).</returns>
    public (AssignOutcome Outcome, int Slot) CycleDevice(InputDeviceId device) {
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

        var next = ((current + 1) % MaxSlots);

        return (Outcome: AssignDevice(device: device, targetSlot: next), Slot: next);
    }

    /// <summary>Moves a device onto a slot (the F-key claim / console <c>player.assign</c> primitive): onto an
    /// occupied slot it joins that team; onto an empty slot it creates a pending player; onto its own slot it is a
    /// no-op. An emptied device-origin source slot dissolves.</summary>
    /// <param name="device">The device to move.</param>
    /// <param name="targetSlot">The destination slot (0-based).</param>
    /// <returns>The reassignment outcome.</returns>
    public AssignOutcome AssignDevice(InputDeviceId device, int targetSlot) {
        if ((uint)targetSlot >= MaxSlots) {
            return AssignOutcome.Ignored;
        }

        TrackDeviceOrder(device: device);

        var hadCurrent = m_deviceToSlot.TryGetValue(key: device, value: out var current);

        if (hadCurrent && (current == targetSlot)) {
            return AssignOutcome.NoOp;
        }

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

        AssignOutcome outcome;

        if (m_slots[targetSlot] is not null) {
            m_deviceToSlot[device] = targetSlot;
            outcome = AssignOutcome.JoinedTeam;
        } else {
            _ = JoinPending(slot: targetSlot, origin: ParticipantOrigin.Device);
            m_deviceToSlot[device] = targetSlot;
            outcome = AssignOutcome.CreatedPending;
        }

        if (hadCurrent) {
            DissolveIfOrphanedDevice(slot: current);
        }

        return outcome;
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
            var possessed = ((body is { } liveBody && (liveBody.Source != IntentSource.Live)) ? ((liveBody.Source == IntentSource.Idle) ? " idle" : " wander") : "");
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

        if ((device != KeyboardDevice) && (m_slots[0] is { State: ParticipantState.Active }) && (CountDevices(slot: 0, excludeKeyboard: true) == 0)) {
            // Device mapping is not render state, so seating the first pad with player 1 does not bump the revision.
            m_deviceToSlot[device] = 0;

            return m_slots[0];
        }

        if (CountDevices(slot: 0, excludeKeyboard: false) == 0) {
            m_deviceToSlot[device] = 0;

            return m_slots[0];
        }

        // Claim an already-active, deviceless local-human seat before creating a pending participant. This is the
        // mutating twin of ResolveSlot's proposal and preserves the deterministic p1..p4 pad-arrival order.
        for (var existing = 1; (existing < MaxSlots); existing++) {
            if ((m_slots[existing] is not null) && (CountDevices(slot: existing, excludeKeyboard: false) == 0)) {
                m_deviceToSlot[device] = existing;

                return m_slots[existing];
            }
        }

        var slot = JoinPendingNextFree(origin: ParticipantOrigin.Device);

        if (slot < 0) {
            return null;
        }

        m_deviceToSlot[device] = slot;

        return m_slots[slot];
    }

    // Consume a pending player's stick sample as its picker: a threshold crossing (edge-detected against the prior
    // sample) cycles the candidate — deflect left for the previous profile, right for the next.
    private void PendingPicker(Participant participant, int slot, float stickX) {
        participant.StickSeenThisFrame = true;

        var wasPast = (MathF.Abs(x: participant.PendingPrevStickX) >= PickerThreshold);
        var isPast = (MathF.Abs(x: stickX) >= PickerThreshold);

        if (isPast && !wasPast) {
            CycleCandidate(slot: slot, direction: ((stickX < 0f) ? -1 : 1));
        }

        participant.PendingPrevStickX = stickX;
    }

    // Promote a pending participant to active on its candidate, first bumping the candidate off any profile now taken
    // by another active player, and reseat the server body on the final choice. Persists the boot seat when slot 0
    // confirms (it never pends today, but the rule holds).
    private void Activate(int slot, Participant participant) {
        if (IsProfileActiveElsewhere(profile: participant.Seat.Profile, exceptSlot: slot)) {
            participant.Seat.Profile = FirstUnusedProfile(exceptSlot: slot);
        }

        participant.State = ParticipantState.Active;
        _ = m_link.SubmitSession(request: new SessionRequest.SetProfile(Principal: WorldPrincipal.Seat(slot: slot), Slot: slot, ProfileName: participant.Seat.Profile!.Name));
        m_seatBindings.SetProfileBindings(slot: slot, bindings: participant.Seat.Profile!.Bindings);

        if (slot == 0) {
            m_profiles.SetLastUsed(id: participant.Seat.Profile!.Id);
        }

        m_revision++;
    }

    // Dissolve a slot whose last device just left, but only when it exists to be dissolved by a device leaving: a
    // device-origin slot with no devices left. Permanent (slot 0) and scripted slots stay.
    private void DissolveIfOrphanedDevice(int slot) {
        if ((m_slots[slot] is { Origin: ParticipantOrigin.Device }) && (CountDevices(slot: slot, excludeKeyboard: false) == 0)) {
            _ = Leave(slot: slot);
        }
    }

    // Fill a slot with a fresh participant selecting a profile, mirroring the join to the server (which mints the
    // seat's body at its staggered spawn, facing -Z toward the boulders).
    private void Fill(int slot, WorldProfile profile, ParticipantState state, ParticipantOrigin origin) {
        m_slots[slot] = new Participant {
            Origin = origin,
            Seat = new SeatController {
                Profile = profile,
            },
            State = state,
        };
        // The seat resolves through its selected profile's binding layer (null = the engine default) — pushed once at
        // fill so the seat's composed mapping is right from its first tick.
        m_seatBindings.SetProfileBindings(slot: slot, bindings: profile.Bindings);
        _ = m_link.SubmitSession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: slot), Slot: slot, ProfileName: profile.Name, ProtocolVersion: WorldProtocol.Version));
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
    private bool IsProfileActiveElsewhere(WorldProfile? profile, int exceptSlot) {
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
    private WorldProfile FirstUnusedProfile(int exceptSlot) {
        foreach (var profile in m_profiles.All) {
            if (!IsProfileActiveElsewhere(profile: profile, exceptSlot: exceptSlot)) {
                return profile;
            }
        }

        return m_profiles.All[0];
    }

    // The catalog index of a profile (by reference), or 0 when it is not found.
    private int IndexOfProfile(WorldProfile? profile) {
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

    // Record a device the first time it is seen, so the kbd/pad<N> token order is stable.
    private void TrackDeviceOrder(InputDeviceId device) {
        if (!m_deviceOrder.Contains(item: device)) {
            m_deviceOrder.Add(item: device);
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

    // A slot's participant: the seat controller staging its device intent, its confirm state, its origin, and the
    // transient picker edge state a pending player's stick uses. A mutable class (not a record) so State and the
    // picker fields flip in place.
    private sealed class Participant {
        public required ParticipantOrigin Origin { get; init; }
        public float PendingPrevStickX { get; set; }
        public required SeatController Seat { get; init; }
        public ParticipantState State { get; set; }
        public bool StickSeenThisFrame { get; set; }
    }
}
