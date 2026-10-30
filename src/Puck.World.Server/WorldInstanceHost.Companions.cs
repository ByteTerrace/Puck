using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    // One companion a crossing carries: the body it leaves, the ordinal of the member it follows in the cohort, and the
    // leaf it carries (its role, home and driver; the ticket comes from its title authority's grant).
    private readonly record struct CompanionMember(int Slot, int FollowsOrdinal, WorldTransferCompanion Leaf);

    // The companions that follow this crossing's travelers. At home a companion follows the seat its placement names,
    // while its title is home. Abroad it follows, by member id, the member it arrived with, bound to the slot and
    // generation that member landed at, while that slot still holds that member at that generation.
    private static List<CompanionMember> CompanionsFollowing(WorldInstance source, int[] members) => source.Server.ExecuteAuthorityOperation(operation: () => {
        var server = source.Server;
        var authority = server.AuthorityIdentity;
        var custody = server.TransferEscrow.Custody;
        var population = server.Population;
        var found = new List<CompanionMember>();

        for (var slot = 0; (slot < population.Capacity); slot++) {
            if (
                !population.IsActive(index: slot) ||
                members.Contains(value: slot) ||
                (population.ResolveIncarnation(authority: authority, index: slot) is not { } incarnation)
            ) {
                continue;
            }
            if (string.Equals(a: incarnation.Authority, b: authority, comparisonType: StringComparison.Ordinal)) {
                var placementId = population.InhabitantPlacementId(index: slot);
                var inhabit = server.Definition.Placements.FirstOrDefault(predicate: placement => string.Equals(a: placement.Id, b: placementId, comparisonType: StringComparison.Ordinal))?.Inhabit;

                if (
                    (inhabit is not { Role: { } role, Follows: { } followedSeat }) ||
                    (custody.TitleOf(incarnation: incarnation) is not { Kind: WorldCustodyKind.Home }) ||
                    (Array.IndexOf(array: members, value: followedSeat) is var seatOrdinal && (seatOrdinal < 0))
                ) {
                    continue;
                }
                found.Add(item: new CompanionMember(
                    FollowsOrdinal: seatOrdinal,
                    Leaf: new WorldTransferCompanion(
                        Driver: null,
                        FollowsMember: seatOrdinal,
                        Home: authority,
                        Role: role,
                        Ticket: null
                    ),
                    Slot: slot
                ));
                continue;
            }
            if (!custody.TryBinding(
                arrival: out _,
                binding: out var binding,
                carried: out var carried,
                follows: out var follows,
                incarnation: incarnation
            ) || (carried is null)) {
                continue;
            }

            var ordinal = Array.IndexOf(array: members, value: binding.Slot);

            if (
                (ordinal < 0) ||
                (population.Generation(index: binding.Slot) != binding.Generation) ||
                (population.ResolveIncarnation(authority: authority, index: binding.Slot) != follows)
            ) {
                continue;
            }
            found.Add(item: new CompanionMember(
                FollowsOrdinal: ordinal,
                Leaf: carried with {
                    FollowsMember = ordinal,
                    Ticket = null,
                },
                Slot: slot
            ));
        }
        return found;
    });
    // Asks each companion's title authority to grant its hop under this crossing's key: this authority's own title is
    // granted by one record here; another authority's is asked through the host's custody door. A granted companion
    // carries the ticket home recorded; any other is left out of the cohort and stays where it is.
    private void AuthorizeCompanions(WorldInstance source, WorldTransferKey key, string destination, WorldTransferReservationMember[] reservationMembers, bool[] keep) {
        var server = source.Server;
        var authority = server.AuthorityIdentity;
        var ordinals = Enumerable.Range(start: 0, count: reservationMembers.Length).Where(predicate: ordinal => reservationMembers[ordinal].Companion is not null);

        foreach (var home in ordinals.GroupBy(keySelector: ordinal => reservationMembers[ordinal].Companion!.Home, comparer: StringComparer.Ordinal)) {
            var group = home.ToArray();
            var incarnations = group.Select(selector: ordinal => reservationMembers[ordinal].Mobility!.Value.Incarnation).ToArray();
            IReadOnlyList<WorldHopAnswer> answers;

            if (string.Equals(a: home.Key, b: authority, comparisonType: StringComparison.Ordinal)) {
                answers = server.AuthorizeOwnHop(
                    destination: destination,
                    incarnations: incarnations,
                    key: key
                );
            } else {
                answers = ((server.CustodyHomes?.Invoke(arg: home.Key) is { } door)
                    ? door.RequestHop(
                        destination: destination,
                        key: key,
                        members: [.. group.Select(selector: ordinal => new WorldHopRequestMember(
                            Epoch: reservationMembers[ordinal].Mobility!.Value.Epoch,
                            Incarnation: reservationMembers[ordinal].Mobility!.Value.Incarnation
                        ))],
                        source: authority
                    )
                    : [.. incarnations.Select(selector: static incarnation => new WorldHopAnswer(Incarnation: incarnation, Next: 0UL, Verdict: WorldHopVerdict.Unavailable))]);
            }
            for (var index = 0; (index < group.Length); index++) {
                var ordinal = group[index];
                var member = reservationMembers[ordinal];
                var answer = ((index < answers.Count) ? answers[index] : default);
                var expected = checked((member.Mobility!.Value.Epoch + 1UL));

                if ((answer.Verdict == WorldHopVerdict.Granted) && (answer.Incarnation == incarnations[index]) && (answer.Next == expected)) {
                    reservationMembers[ordinal] = member with {
                        Companion = member.Companion! with {
                            Ticket = new WorldCustodyTicket(
                                Epoch: answer.Next,
                                Home: home.Key,
                                Hop: key,
                                Incarnation: incarnations[index]
                            ),
                        },
                    };
                    continue;
                }
                keep[ordinal] = false;
                if (m_narration.HasNarrationSink) {
                    m_narration.Narrate(
                        channel: "world.transfer",
                        text: $"[world.transfer: transfer={key.TransferId} companion {incarnations[index]} stays — its title authority answered {((answer.Incarnation == incarnations[index]) ? answer.Verdict.ToString() : "nothing")}]"
                    );
                }
            }
        }
    }
    // Settles every hop this authority granted for itself whose departure it settled or never made: one record per
    // companion, each its own operation. A departure in doubt waits for its settlement.
    private void SettleOwnHops() {
        foreach (var row in m_instances.Values.ToArray()) {
            var owed = row.Server.ExecuteAuthorityOperation(operation: () => row.Server.TransferEscrow.Custody.OwedOwnHopSettlements());

            foreach (var (key, incarnation, arrived) in owed) {
                if (row.Server.RecordCustody(
                    reason: out _,
                    record: new WorldCrossingRecord.HopDone(
                        Arrived: arrived,
                        Incarnation: incarnation,
                        Key: key
                    )
                ) != WorldCrossingDurability.Durable) {
                    break;
                }
            }
        }
    }
}
