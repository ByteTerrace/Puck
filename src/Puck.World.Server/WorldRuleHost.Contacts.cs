using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The rule host's surface contacts: the <c>forEach: $contacts:&lt;screen&gt;</c> sweep and its
/// <c>contact(each, …)</c> reads, over the contact events each participant's input owner admitted this tick
/// (<see cref="WorldInputOwner.ContactsAt"/>).</summary>
public sealed partial class WorldRuleHost : IWorldSurfaceContacts {
    /// <inheritdoc/>
    public void CollectContacts(WorldContactIteration sweep, List<CellKey> into) {
        ArgumentNullException.ThrowIfNull(argument: sweep);
        ArgumentNullException.ThrowIfNull(argument: into);

        for (var participant = 0; (participant < sweep.Participants); participant++) {
            var contacts = ParticipantContacts(participant: participant, principal: out var principal);

            if (
                contacts.IsEmpty ||
                !Host.GrantTable.Allows(
                    capability: WorldCapability.Control,
                    principal: principal,
                    subject: sweep.Subject
                ).IsAllowed
            ) {
                continue;
            }

            for (var index = 0; (index < contacts.Length); index++) {
                if (
                    sweep.Mapping.MapRay(ray: contacts[index].Ray).IsOnSource &&
                    Arena.Keys.TryResolveIndex(index: WorldContactIteration.Encode(eventIndex: index, participant: participant), key: out var key)
                ) {
                    into.Add(item: key);
                }
            }
        }
    }
    /// <inheritdoc/>
    public long ContactGeneration(CellKey key) => checked(((long)Host.Input.Interval.Tick) + 1L);
    /// <inheritdoc/>
    public Fact Read(ContactOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        if (!Arena.Keys.TryCandidateIndex(key: BoundEachKey, index: out var index)) {
            return Fact.Absent(kind: operand.ValueKind);
        }

        var (participant, eventIndex) = WorldContactIteration.Decode(index: index);
        var contacts = ParticipantContacts(participant: participant, principal: out _);

        return ((eventIndex < contacts.Length)
            ? Fact.Finite(kind: operand.ValueKind, value: operand.Read(contact: in contacts[eventIndex], seat: (participant + 1)))
            : Fact.Absent(kind: operand.ValueKind));
    }

    // The contact events one participant slot admitted this tick: a local seat a human occupies, then the live
    // sessions by ordinal after the local seats. An empty slot, or a session ordinal past the slots the document
    // prices, admits none.
    private ReadOnlySpan<SurfaceContact> ParticipantContacts(int participant, out Principal principal) {
        var local = Host.Population.LocalSeatCount;

        principal = default;

        if (participant < local) {
            if (!Host.Population.IsHumanOccupied(bodyIndex: participant)) {
                return [];
            }

            principal = Principal.Seat(slot: participant);
        } else if (!Host.GrantTable.TryReadSessionPointer(ordinal: (participant - local), ray: out _, session: out principal)) {
            return [];
        }

        return (Host.Input.TryGet(input: out var owner, principal: principal)
            ? owner!.ContactsAt(tick: Host.Input.Interval.Tick)
            : []);
    }
}
