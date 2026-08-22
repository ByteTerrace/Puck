using System.Text;
using Puck.Commands;

namespace Puck.World.Client;

/// <summary>The roster's device-kind bookkeeping: per-kind (<c>keyboard&lt;N&gt;</c>/<c>mouse&lt;N&gt;</c>/
/// <c>gamepad&lt;N&gt;</c>/<c>camera&lt;N&gt;</c>) token minting and resolution, the camera default-seating policy,
/// and the <c>world.devices</c>/<c>world.device-profiles</c> read-backs. Split from <see cref="PlayerRoster"/>'s
/// main file purely for length (LEN001); every member here shares that type's single-threaded, no-lock discipline
/// and its fields.</summary>
public sealed partial class PlayerRoster {
    private static readonly (string Prefix, InputDeviceKind Kind)[] DeviceTokenPrefixes = [
        ("keyboard", InputDeviceKind.Keyboard),
        ("mouse", InputDeviceKind.Mouse),
        ("gamepad", InputDeviceKind.Gamepad),
        ("camera", InputDeviceKind.Camera),
    ];

    // How many mapped devices a slot owns, for presence/activity bookkeeping. A camera never counts — it is a
    // passive attachment, not player input — regardless of matchKind. matchKind, when given, additionally narrows
    // the count to just that one kind: the couch-sharing rule (ResolveDeviceSlot/ResolveSlot's slot-0 branch) asks
    // "does this slot already have a device of MY OWN kind", which is exactly
    // CountDevices(slot, matchKind: myKind) == 0 — the same generalized test for a keyboard, a mouse, or a gamepad
    // claiming the shared seat alongside player 1.
    private int CountDevices(int slot, InputDeviceKind? matchKind = null) {
        var count = 0;

        foreach (var pair in m_deviceToSlot) {
            if (
                (pair.Value == slot) &&
                (DeviceKindOf(device: pair.Key) != InputDeviceKind.Camera) &&
                ((matchKind is null) || (DeviceKindOf(device: pair.Key) == matchKind))
            ) {
                count++;
            }
        }

        return count;
    }
    // A device's recorded kind, defaulting to Gamepad for a device this roster has never classified. Every device
    // reaching the roster through the router's per-signal first touch (InputRouter.ObserveDeviceKind, the
    // IInputSlotResolver companion) or an explicit ObserveDevice call (a camera) is classified before this is ever
    // consulted; the default is a defensive floor for a programmatic claim (TryClaimSlot) that never produced an
    // ordinary routed signal.
    private InputDeviceKind DeviceKindOf(InputDeviceId device) => (m_deviceKind.TryGetValue(
        key: device,
        value: out var kind
    )
        ? kind
        : InputDeviceKind.Gamepad
    );
    // The tokens of every device currently mapped to the slot, joined with "+" (first-seen order), or empty.
    private string DeviceTokensFor(int slot) {
        var builder = new StringBuilder();

        foreach (var device in m_deviceOrder) {
            if (
                m_deviceToSlot.TryGetValue(
                key: device,
                value: out var mapped
            ) &&
                (mapped == slot)
            ) {
                if (builder.Length > 0) {
                    _ = builder.Append(value: '+');
                }

                _ = builder.Append(value: DeviceToken(device: device));
            }
        }

        return builder.ToString();
    }
    // Every device→slot write funnels through here so the per-device assignment stamp (TryGetSeatDevice's
    // most-recently-assigned tie-break among several devices of one kind sharing a slot) advances exactly once per
    // assignment, never duplicated at each call site.
    private void MapDevice(InputDeviceId device, int slot) {
        m_deviceToSlot[device] = slot;
        m_deviceAssignStamp[device] = m_nextAssignStamp++;
    }
    // The camera default-seating policy ObserveDevice applies the first time a camera is seen: attach to the
    // lowest slot that already holds a participant (active or pending) but no camera yet, player 1 first, skipping
    // any TryClaimSlot-held slot. Leaves the device unmapped (unassigned) when every occupied, unclaimed slot
    // already has a camera or none is occupied at all — this policy never creates a participant.
    private void SeatCameraDefault(InputDeviceId device) {
        for (var slot = 0; (slot < MaxSlots); slot++) {
            if (
                (m_slots[slot] is null) ||
                IsClaimed(slot: slot) ||
                TryGetSeatDevice(
                    slot: slot,
                    kind: InputDeviceKind.Camera,
                    device: out _
                )
            ) {
                continue;
            }

            MapDevice(device: device, slot: slot);

            return;
        }
    }
    // Record a device the first time it is seen, so its per-kind token order is stable. Kind is classified before
    // this ever runs — InputRouter.ObserveDeviceKind for an ordinary router-routed device (keyboard, mouse,
    // gamepad), ObserveDevice for a camera — except a programmatic claim (TryClaimSlot), which defaults through
    // DeviceKindOf's own floor.
    private void TrackDeviceOrder(InputDeviceId device) {
        if (!m_deviceOrder.Contains(item: device)) {
            m_deviceOrder.Add(item: device);
            m_devicePreselections[device] = EvaluatePreselection(device: device);
        }
    }
    // The shared body of TryResolveDeviceToken's per-kind branches: the ordinal-th device of `kind` in first-seen
    // order.
    private bool TryResolveKindToken(ReadOnlySpan<char> ordinalText, InputDeviceKind kind, out InputDeviceId device) {
        device = default;

        if (
            !int.TryParse(
            s: ordinalText,
            result: out var ordinal
        ) ||
            (ordinal < 1)
        ) {
            return false;
        }

        var seen = 0;

        foreach (var candidate in m_deviceOrder) {
            if (DeviceKindOf(device: candidate) != kind) {
                continue;
            }

            if (++seen == ordinal) {
                device = candidate;

                return true;
            }
        }

        return false;
    }

    /// <summary>Formats the preferred-profile decision recorded when each connected device was first seen.</summary>
    /// <returns>A line naming every device's selected profile, or why no preference applied.</returns>
    public string DescribeDeviceProfiles() {
        var segments = new List<string>(capacity: m_deviceOrder.Count);

        foreach (var device in m_deviceOrder) {
            // A camera never recorded a preselection decision (it has no controller-profile preference concept) —
            // skip it rather than fault on a missing entry.
            if (!m_devicePreselections.TryGetValue(
                key: device,
                value: out var decision
            )) {
                continue;
            }

            var token = DeviceToken(device: device);
            var description = decision.Kind switch {
                DevicePreselectionKind.NoControllerConcept => $"none ({DeviceKindOf(device: device).ToString().ToLowerInvariant()} has no controller preference)",
                DevicePreselectionKind.ConnectionOnly => "none (connection-only identity; XInput ids name slots, not physical pads)",
                DevicePreselectionKind.NoLocalPreference => "none (no preference on this machine)",
                DevicePreselectionKind.Preferred => $"{decision.Profile!.Name} (preferred controller match)",
                DevicePreselectionKind.AlreadySeated => $"none ({decision.Profile!.Name} already active on p{DisplayNumber(slot: decision.Seat)}; ordinary seating applied)",
                _ => $"none ({decision.Profile!.Name} matched, but no free seat could present it; ordinary seating applied)",
            };

            segments.Add(item: $"{token}={description}");
        }

        return $"[world.device-profiles: {string.Join(
            separator: " | ",
            values: segments
        )}]";
    }
    /// <summary>Formats the device table for the <c>world.devices</c> verb — every seen device token in first-seen
    /// order, its name when known, and the player it currently drives. A device that is its slot's most-recently-
    /// assigned device of its own kind (what <see cref="TryGetSeatDevice"/> would resolve for that kind) is marked
    /// <c>*</c> — the one that matters when several devices of one kind (e.g. two cameras) share a team.</summary>
    /// <returns>A line of the form
    /// <c>[world.devices: keyboard1=p1* | mouse1=p1* | gamepad1=p2* | camera1 'Logitech BRIO'=p1 | camera2 'HD Pro Webcam C920'=p1*]</c>.</returns>
    public string DescribeDevices() {
        var segments = new List<string>(capacity: m_deviceOrder.Count);

        foreach (var device in m_deviceOrder) {
            var hasSlot = m_deviceToSlot.TryGetValue(
                key: device,
                value: out var slot
            );
            var owner = (hasSlot
                ? $"p{DisplayNumber(slot: slot)}"
                : "unassigned"
            );
            var token = DeviceToken(device: device);
            var label = ((DeviceName(device: device) is { } name)
                ? $"{token} '{name}'"
                : token
            );
            var isResolved = (
                hasSlot &&
                TryGetSeatDevice(
                slot: slot,
                kind: DeviceKindOf(device: device),
                device: out var resolved
            ) &&
                (resolved == device)
            );

            segments.Add(item: $"{label}={owner}{(isResolved ? "*" : string.Empty)}");
        }

        return $"[world.devices: {string.Join(
            separator: " | ",
            values: segments
        )}]";
    }
    /// <summary>The recorded display name for a device, or <see langword="null"/> when none is known. A camera
    /// always carries one (see <see cref="ObserveDevice"/>); a keyboard or mouse carries one only on a platform
    /// that reports physical device names (Windows Raw Input) — a platform that collapses every physical keyboard
    /// or mouse into one identity carries none.</summary>
    /// <param name="device">The device id.</param>
    public string? DeviceName(InputDeviceId device) => (m_deviceName.TryGetValue(
        key: device,
        value: out var name
    )
        ? name
        : null
    );
    /// <summary>The slot (0-based) a device currently owns, or <see langword="null"/> if it is unmapped.</summary>
    /// <param name="device">The device id.</param>
    public int? DeviceSlot(InputDeviceId device) => (m_deviceToSlot.TryGetValue(
        key: device,
        value: out var slot
    )
        ? slot
        : null
    );
    /// <summary>The stable token for a device: <c>keyboard&lt;N&gt;</c>, <c>mouse&lt;N&gt;</c>,
    /// <c>gamepad&lt;N&gt;</c>, or <c>camera&lt;N&gt;</c>, numbered independently by first-seen order within its own
    /// kind. Public so a verb echo can name the device a gesture acted on (e.g. "gamepad1 seated with player 1").</summary>
    /// <param name="device">The device id.</param>
    public string DeviceToken(InputDeviceId device) {
        var kind = DeviceKindOf(device: device);
        var prefix = (kind switch {
            InputDeviceKind.Keyboard => "keyboard",
            InputDeviceKind.Mouse => "mouse",
            InputDeviceKind.Camera => "camera",
            _ => "gamepad",
        });
        var ordinal = 0;

        foreach (var candidate in m_deviceOrder) {
            if (DeviceKindOf(device: candidate) != kind) {
                continue;
            }

            ordinal++;

            if (candidate == device) {
                return $"{prefix}{ordinal}";
            }
        }

        return $"{prefix}?";
    }
    /// <summary>Records a device seen outside the router's own first-touch discovery (today, only a physical
    /// camera) — its kind and display name — and, the first time this exact device is observed, applies the
    /// camera default-seating policy: attach to the lowest slot that already holds an active or pending
    /// participant but no camera yet (player 1 first), skipping any slot a <see cref="TryClaimSlot"/> hold governs;
    /// otherwise the device stays unassigned until an explicit <see cref="AssignDevice"/>. A camera attachment
    /// never creates a participant and is not render state, so it raises no <see cref="DeviceSlotChanging"/> and
    /// does not bump <see cref="Revision"/> — exactly like a keyboard or gamepad's first-touch seating. A device
    /// already observed only refreshes its recorded kind and name (a reconnect or a repeat enumeration), never
    /// re-applies the seating policy.</summary>
    /// <param name="device">The device id — reconnect-stable for a camera (<see cref="InputDeviceId.FromKey"/>).</param>
    /// <param name="kind">The device's kind.</param>
    /// <param name="name">The device's display name (e.g. the camera's MediaFoundation group name).</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    public void ObserveDevice(InputDeviceId device, InputDeviceKind kind, string name) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);

        var firstSeen = !m_deviceKind.ContainsKey(key: device);

        m_deviceKind[device] = kind;
        m_deviceName[device] = name;

        if (!firstSeen) {
            return;
        }

        if (!m_deviceOrder.Contains(item: device)) {
            m_deviceOrder.Add(item: device);
        }

        if (kind == InputDeviceKind.Camera) {
            SeatCameraDefault(device: device);

            return;
        }

        m_devicePreselections[device] = EvaluatePreselection(device: device);
    }
    /// <summary>The slot's most-recently-assigned device of <paramref name="kind"/> — the per-frame read every
    /// seat-scoped consumer (a camera's HUD/probe feed, a seat's resolved gamepad) makes to resolve a seat to its
    /// device. Several devices of the same kind can share a slot (a second camera assigned onto a seat that already
    /// has one, a second gamepad passed to the same team) — an earlier one stays mapped and still counts toward the
    /// team, but this always answers whichever was assigned to the slot most recently
    /// (<see cref="AssignDevice"/>'s or a first-touch seating's own <see cref="MapDevice"/> call). Allocation-free.</summary>
    /// <param name="slot">The slot index (0-based).</param>
    /// <param name="kind">The device kind to look for.</param>
    /// <param name="device">The resolved device id.</param>
    /// <returns><see langword="true"/> when a device of that kind is mapped to the slot.</returns>
    public bool TryGetSeatDevice(int slot, InputDeviceKind kind, out InputDeviceId device) {
        device = default;

        if (((uint)slot) >= MaxSlots) {
            return false;
        }

        var found = false;
        var bestStamp = -1;

        foreach (var candidate in m_deviceOrder) {
            if (
                !m_deviceToSlot.TryGetValue(
                key: candidate,
                value: out var mapped
            ) ||
                (mapped != slot) ||
                (DeviceKindOf(device: candidate) != kind)
            ) {
                continue;
            }

            var stamp = (m_deviceAssignStamp.TryGetValue(
                key: candidate,
                value: out var recorded
            )
                ? recorded
                : -1
            );

            if (
                !found ||
                (stamp > bestStamp)
            ) {
                found = true;
                bestStamp = stamp;
                device = candidate;
            }
        }

        return found;
    }
    /// <summary>Resolves a device token (<c>keyboard&lt;N&gt;</c>, <c>mouse&lt;N&gt;</c>, <c>gamepad&lt;N&gt;</c>,
    /// or <c>camera&lt;N&gt;</c>) to its device id.</summary>
    /// <param name="token">The token (case-insensitive).</param>
    /// <param name="device">The resolved device id.</param>
    /// <returns><see langword="true"/> if <paramref name="token"/> names a known device.</returns>
    public bool TryResolveDeviceToken(string token, out InputDeviceId device) {
        device = default;

        if (string.IsNullOrWhiteSpace(value: token)) {
            return false;
        }

        foreach (var (prefix, kind) in DeviceTokenPrefixes) {
            if (!token.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: prefix
            )) {
                continue;
            }

            return TryResolveKindToken(
                device: out device,
                kind: kind,
                ordinalText: token.AsSpan(start: prefix.Length)
            );
        }

        return false;
    }
}
