using Puck.Scripting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    // The world's four collected event families, filtered per-addon, plus this guest's own machine-memory watches
    // (addon-scoped, materializing through the same requested ∧ granted rule — see WorldAddonRow.MemoryWatches'
    // own doc). OVERFLOW DOCTRINE: edges arrive already in PINNED SIM ORDER (WorldEventFeed.Collect's own order —
    // seats, regions, collisions, routes — then this guest's own watches in declaration order); once the ring runs
    // out of room the REST of this tick's qualifying edges drop, NEWEST first by construction, so the guest always
    // sees a consistent ORDERED PREFIX, never a mid-stream hole. Each qualifying edge also charges the first gate row
    // with remaining EventBudget; a row whose allowance is spent drops the edge through the same gap path. Every
    // drop increments the per-mount, saturating, LIFETIME EventGapCount; whenever that count has moved since the
    // last batch that reported it, ONE EventGap summary cell is appended — a nonzero count is the guest's "resync by
    // polling the level state you already observe" signal, never a request to replay the missed edges.
    private void EmitEvents(MountedAddon addon, int budget) {
        var edges = m_server.Events.Edges;
        var dropped = 0;

        for (var index = 0; (index < edges.Count); ++index) {
            var edge = edges[index];
            var verb = MapEventVerb(family: edge.Family);

            if (verb < 0) {
                // Unreachable: every WorldEventFamily maps to a verb. Defensive rather than throwing — a guest's
                // batch must never fault over a host-side bug in an unrelated family.
                continue;
            }

            var gateStatus = SelectEventGate(
                addon: addon,
                gateA: edge.GateA,
                gateB: edge.GateB,
                chargedSubject: out var chargedSubject
            );

            if (gateStatus == EventGateStatus.None) {
                continue;
            }

            if (
                (gateStatus == EventGateStatus.Exhausted) ||
                ((budget - addon.PendingCount) <= 0)
            ) {
                ++dropped;

                continue;
            }

            addon.EventCounts[chargedSubject] = (addon.EventCounts.GetValueOrDefault(key: chargedSubject) + 1);

            addon.Pending[addon.PendingCount++] = new AddonInCell(
                Kind: AddonInCellKind.Observation,
                Channel: ((byte)addon.ResponseChannel),
                Ordinal: 0,
                HandleIndex: 0,
                HandleGeneration: 0,
                Verdict: AddonVerdict.None,
                Verb: ((byte)verb),
                A: edge.A,
                B: edge.B
            );
            addon.EventCellsDelivered = ((addon.EventCellsDelivered == ulong.MaxValue)
                ? ulong.MaxValue
                : (addon.EventCellsDelivered + 1UL)
            );

            if (edge.Family is WorldEventFamily.CollisionBegin or WorldEventFamily.CollisionEnd) {
                addon.CollisionEventsDelivered = ((addon.CollisionEventsDelivered == ulong.MaxValue)
                    ? ulong.MaxValue
                    : (addon.CollisionEventsDelivered + 1UL)
                );
            } else if (edge.Family is WorldEventFamily.RouteEngaged or WorldEventFamily.RouteDisengaged) {
                addon.RouteEventsDelivered = ((addon.RouteEventsDelivered == ulong.MaxValue)
                    ? ulong.MaxValue
                    : (addon.RouteEventsDelivered + 1UL)
                );
            }
        }

        dropped += EmitMemoryWatchEvents(
            addon: addon,
            budget: budget
        );

        if (dropped > 0) {
            addon.EventGapCount = ((addon.EventGapCount > (ulong.MaxValue - ((ulong)dropped)))
                ? ulong.MaxValue
                : (addon.EventGapCount + ((ulong)dropped))
            );
        }

        if (
            (addon.EventGapCount != addon.LastReportedEventGap) &&
            ((budget - addon.PendingCount) > 0)
        ) {
            addon.Pending[addon.PendingCount++] = new AddonInCell(
                Kind: AddonInCellKind.Observation,
                Channel: ((byte)addon.ResponseChannel),
                Ordinal: 0,
                HandleIndex: 0,
                HandleGeneration: 0,
                Verdict: AddonVerdict.None,
                Verb: ((byte)AddonAbi.ObservationVerbs.EventGap),
                A: ((long)addon.EventGapCount),
                B: 0L
            );
            addon.LastReportedEventGap = addon.EventGapCount;
        }
        // else: no room this tick even for the summary cell — the count is saturating and monotonic, so the next
        // batch with any room reports the up-to-date total; nothing is lost, only delayed.
    }
    // Machine-memory watches: addon-scoped (each row declares its own), materializing through Observe/screen:<n>
    // WITH an event budget — the same requested ∧ granted rule every other capability here enforces.
    // m_server.Machines is always present (machines boot and step server-side in every boot shape), so this family
    // publishes in a headless host too. Returns the number of changed-value edges dropped for event-budget or
    // ring-capacity reasons, folded into the SAME gap counter EmitEvents tracks — one gap surface per mount, not
    // one per family.
    private int EmitMemoryWatchEvents(MountedAddon addon, int budget) {
        if (addon.MemoryWatches is not { Count: > 0 } watches) {
            return 0;
        }


        var dropped = 0;

        for (var index = 0; (index < watches.Count); ++index) {
            var watch = watches[index];

            var subject = GrantSubject.Screen(index: watch.Screen);
            var gateStatus = SelectEventGate(
                addon: addon,
                chargedSubject: out var chargedSubject,
                gateA: subject,
                gateB: null
            );

            if (gateStatus == EventGateStatus.None) {
                continue;
            }

            if (!TryReadWatch(
                peek: m_server.Machines,
                watch: watch,
                value: out var value
            )) {
                continue;
            }

            ref var state = ref addon.MemoryWatchState![index];

            if (!state.Initialized) {
                // The first successful peek establishes the baseline WITHOUT emitting — there is no "previous"
                // value to have changed from.
                state = new MountedAddon.WatchState(
                    Initialized: true,
                    Value: value
                );

                continue;
            }

            if (state.Value == value) {
                continue;
            }

            state = new MountedAddon.WatchState(
                Initialized: true,
                Value: value
            );

            if (
                (gateStatus == EventGateStatus.Exhausted) ||
                ((budget - addon.PendingCount) <= 0)
            ) {
                ++dropped;

                continue;
            }

            addon.EventCounts[chargedSubject] = (addon.EventCounts.GetValueOrDefault(key: chargedSubject) + 1);

            addon.Pending[addon.PendingCount++] = new AddonInCell(
                Kind: AddonInCellKind.Observation,
                Channel: ((byte)addon.ResponseChannel),
                Ordinal: 0,
                HandleIndex: 0,
                HandleGeneration: 0,
                Verdict: AddonVerdict.None,
                Verb: ((byte)AddonAbi.ObservationVerbs.EventMachineMemoryChanged),
                A: (((long)watch.Screen) << 32) | ((uint)watch.Address),
                B: value
            );
            addon.EventCellsDelivered = ((addon.EventCellsDelivered == ulong.MaxValue)
                ? ulong.MaxValue
                : (addon.EventCellsDelivered + 1UL)
            );
        }

        return dropped;
    }
    private EventGateStatus EventGate(MountedAddon addon, GrantSubject subject) {
        if (
            !IsRequested(
            addon: addon,
            capability: WorldCapability.Observe,
            subject: subject
        ) ||
            !m_server.Grants.Allows(
            principal: addon.Principal,
            capability: WorldCapability.Observe,
            subject: subject
        ).IsAllowed ||
            !m_server.Grants.TryGetEventBudget(
            principal: addon.Principal,
            capability: WorldCapability.Observe,
            subject: subject,
            out var eventBudget
        )
        ) {
            return EventGateStatus.None;
        }

        return ((addon.EventCounts.GetValueOrDefault(key: subject) < eventBudget)
            ? EventGateStatus.Available
            : EventGateStatus.Exhausted
        );
    }
    private static int MapEventVerb(WorldEventFamily family) => family switch {
        WorldEventFamily.RegionEnter => AddonAbi.ObservationVerbs.EventRegionEnter,
        WorldEventFamily.RegionExit => AddonAbi.ObservationVerbs.EventRegionExit,
        WorldEventFamily.SeatJoin => AddonAbi.ObservationVerbs.EventSeatJoin,
        WorldEventFamily.SeatLeave => AddonAbi.ObservationVerbs.EventSeatLeave,
        WorldEventFamily.CollisionBegin => AddonAbi.ObservationVerbs.EventCollisionBegin,
        WorldEventFamily.CollisionEnd => AddonAbi.ObservationVerbs.EventCollisionEnd,
        WorldEventFamily.RouteEngaged => AddonAbi.ObservationVerbs.EventRouteEngaged,
        WorldEventFamily.RouteDisengaged => AddonAbi.ObservationVerbs.EventRouteDisengaged,
        WorldEventFamily.LinkEstablished => AddonAbi.ObservationVerbs.EventLinkEstablished,
        WorldEventFamily.LinkDropped => AddonAbi.ObservationVerbs.EventLinkDropped,
        _ => -1,
    };
    // Picks the first requested, granted event row with remaining allowance. GateA precedes GateB, so an edge visible
    // through both rows charges one cell to A until A is full, then B. None means the guest cannot observe the edge;
    // Exhausted means it could, but every qualifying row spent its allowance and the edge must enter the gap count.
    private EventGateStatus SelectEventGate(MountedAddon addon, GrantSubject gateA, GrantSubject? gateB, out GrantSubject chargedSubject) {
        var statusA = EventGate(
            addon: addon,
            subject: gateA
        );

        if (statusA == EventGateStatus.Available) {
            chargedSubject = gateA;
            return statusA;
        }

        var statusB = ((gateB is { } subjectB)
            ? EventGate(
                addon: addon,
                subject: subjectB
            )
            : EventGateStatus.None
        );

        if (statusB == EventGateStatus.Available) {
            chargedSubject = gateB!.Value;
            return statusB;
        }

        chargedSubject = default;
        return (((statusA == EventGateStatus.Exhausted) || (statusB == EventGateStatus.Exhausted))
            ? EventGateStatus.Exhausted
            : EventGateStatus.None
        );
    }
    // Reads a watch's whole byte range as one little-endian, zero-extended i64 — fails the WHOLE watch (no partial
    // value, no baseline update) if any byte in the range cannot be peeked, so a transient "screen has no machine"
    // state never smuggles a half-composed value into the comparison.
    private static bool TryReadWatch(IWorldMachineMemoryPeek peek, WorldAddonMemoryWatch watch, out long value) {
        value = 0L;

        for (var offset = 0; (offset < watch.Length); ++offset) {
            if (!peek.TryPeek(
                screen: watch.Screen,
                address: (watch.Address + offset),
                value: out var b
            )) {
                return false;
            }

            value |= (((long)b) << (offset * 8));
        }

        return true;
    }

    private enum EventGateStatus : byte {
        None,
        Available,
        Exhausted,
    }
}
