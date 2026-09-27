using Puck.Commands;
using System.Reflection;

using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>
/// The laws behind <see cref="WorldSubmissionPrincipals.NamesSession"/>, the check the peer and federated wires refuse a
/// session principal by. The census walks every type a submission payload can carry and pins each field that holds an
/// identity, so a new principal-bearing field fails here until the check covers it; the second law puts a session in
/// each pinned field and requires the check to see it, against the same payload naming a seat.
/// </summary>
public sealed class SubmissionPrincipalCensusLawTests {
    private static readonly Principal Session = Principal.Session(
        epoch: 1,
        ordinal: 0
    );
    private static readonly Principal Seat = Principal.Seat(slot: 0);
    private static readonly Type[] IdentityTypes = [typeof(Principal), typeof(Principal?), typeof(Grantee), typeof(WorldGrant), typeof(WorldHandle)];
    private static readonly Assembly Protocol = typeof(WorldSubmissionPayload).Assembly;

    private static string Label(Type type) => ((type.DeclaringType is { } outer)
        ? $"{Label(type: outer)}.{type.Name}"
        : type.Name);
    private static void Walk(Type type, HashSet<Type> seen, SortedSet<string> fields) {
        if (Nullable.GetUnderlyingType(nullableType: type) is { } underlying) {
            type = underlying;
        }

        if (type.IsGenericType && (type.GetGenericArguments() is [var element])) {
            Walk(
                fields: fields,
                seen: seen,
                type: element
            );

            return;
        }

        if ((type.Assembly != Protocol) || !seen.Add(item: type)) {
            return;
        }

        foreach (var derived in Protocol.GetTypes().Where(predicate: candidate => ((candidate != type) && type.IsAssignableFrom(c: candidate)))) {
            Walk(
                fields: fields,
                seen: seen,
                type: derived
            );
        }

        foreach (var property in type.GetProperties(bindingAttr: BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
            if (IdentityTypes.Contains(value: property.PropertyType)) {
                _ = fields.Add(item: $"{Label(type: type)}.{property.Name}");

                continue;
            }

            Walk(
                fields: fields,
                seen: seen,
                type: property.PropertyType
            );
        }
    }
    private static WorldGrant GrantTo(Principal grantee) => new(
        Grantee: grantee,
        Capability: WorldCapability.Observe,
        Subject: GrantSubject.Body(index: 0),
        Exclusive: false
    );
    // One payload per pinned field, with the field under test naming the given principal and every other identity a
    // seat.
    private static IEnumerable<(string Field, WorldSubmissionPayload Payload)> Payloads(Principal named) {
        var group = new OwnershipSubject(
            Id: "crew",
            Kind: OwnershipSubjectKind.Group
        );

        yield return ("WorldCommand.Principal", new WorldSubmissionPayload.Command(Value: new WorldCommand.Stop(
            EntityIndex: 0,
            Principal: named
        )));
        yield return ("WorldCommand.ComposeControl.TargetPrincipal", new WorldSubmissionPayload.Command(Value: new WorldCommand.ComposeControl(
            EntityIndex: 0,
            Exclusive: false,
            Principal: Seat,
            Target: GrantSubject.Body(index: 0),
            TargetPrincipal: named
        )));
        yield return ("WorldCommand.DissolveControl.TargetPrincipal", new WorldSubmissionPayload.Command(Value: new WorldCommand.DissolveControl(
            EntityIndex: 0,
            Principal: Seat,
            TargetPrincipal: named
        )));
        yield return ("WorldSubmissionPayload.Grant.Value", new WorldSubmissionPayload.Grant(Value: GrantTo(grantee: named)));
        yield return ("WorldSubmissionPayload.Revoke.Value", new WorldSubmissionPayload.Revoke(Value: GrantTo(grantee: named)));
        yield return ("SessionRequest.Principal", new WorldSubmissionPayload.Session(Value: new SessionRequest.Leave(
            Principal: named,
            Slot: 0
        )));
        yield return ("WorldMutation.Principal", new WorldSubmissionPayload.Mutation(Value: new WorldMutation.RemovePlacement(
            Id: "slot",
            Principal: named
        )));
        yield return ("WorldMutation.Batch.Mutations", new WorldSubmissionPayload.Mutation(Value: new WorldMutation.Batch(
            Mutations: [new WorldMutation.RemovePlacement(
                Id: "slot",
                Principal: named
            )],
            Principal: Seat
        )));
        yield return ("WorldMutation.UpsertGrant.Row", new WorldSubmissionPayload.Mutation(Value: new WorldMutation.UpsertGrant(
            Principal: Seat,
            Row: GrantTo(grantee: named)
        )));
        yield return ("WorldMutation.RemoveGrant.Target", new WorldSubmissionPayload.Mutation(Value: new WorldMutation.RemoveGrant(
            Principal: Seat,
            Target: GrantTo(grantee: named)
        )));
        yield return ("WorldMutation.JoinGroup.Member", new WorldSubmissionPayload.Mutation(Value: new WorldMutation.JoinGroup(
            GroupId: "crew",
            Member: named,
            Principal: Seat
        )));
        yield return ("WorldMutation.LeaveGroup.Member", new WorldSubmissionPayload.Mutation(Value: new WorldMutation.LeaveGroup(
            GroupId: "crew",
            Member: named,
            Principal: Seat
        )));
        yield return ("WorldMutation.KickMember.Member", new WorldSubmissionPayload.Mutation(Value: new WorldMutation.KickMember(
            GroupId: "crew",
            Member: named,
            Principal: Seat
        )));
        yield return ("WorldMutation.OfferOwnership.Recipient", new WorldSubmissionPayload.Mutation(Value: new WorldMutation.OfferOwnership(
            DeadlineTick: 60,
            Principal: Seat,
            Recipient: named,
            Subject: group
        )));
        yield return ("WorldQuery.GrantAllows.Principal", new WorldSubmissionPayload.Query(Value: new WorldQuery.GrantAllows(
            Capability: WorldCapability.Drive,
            Principal: named,
            Subject: GrantSubject.Body(index: 0)
        )));
        yield return ("WorldQuery.GrantHandleMint.Principal", new WorldSubmissionPayload.Query(Value: new WorldQuery.GrantHandleMint(
            Capability: WorldCapability.Observe,
            Index: 0,
            Principal: named
        )));
        yield return ("WorldQuery.GrantHandleResolve.Handle", new WorldSubmissionPayload.Query(Value: new WorldQuery.GrantHandleResolve(Handle: new WorldHandle(
            Generation: 1,
            Index: 0,
            TableCapability: WorldCapability.Observe,
            TablePrincipal: named
        ))));
    }

    [Fact]
    public void EveryIdentityFieldAPayloadCarries_IsOneTheSessionCheckReads() {
        var fields = new SortedSet<string>(comparer: StringComparer.Ordinal);

        Walk(
            fields: fields,
            seen: [],
            type: typeof(WorldSubmissionPayload)
        );

        // Batch.Mutations reaches identities through its rows; the walk sees the row type once, so its field is pinned
        // by the per-field law below rather than by the census.
        var covered = Payloads(named: Session)
            .Select(selector: static entry => entry.Field)
            .Where(predicate: static field => (field != "WorldMutation.Batch.Mutations"))
            .ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: fields,
            expected: covered.Order(comparer: StringComparer.Ordinal)
        );
    }
    [Fact]
    public void ASessionInAnyIdentityField_IsSeen_TheSamePayloadNamingASeatIsNot() {
        var seen = Payloads(named: Session).Where(predicate: static entry => !WorldSubmissionPrincipals.NamesSession(payload: entry.Payload)).Select(selector: static entry => entry.Field);
        var falsePositives = Payloads(named: Seat).Where(predicate: static entry => WorldSubmissionPrincipals.NamesSession(payload: entry.Payload)).Select(selector: static entry => entry.Field);

        Assert.Empty(collection: seen);
        Assert.Empty(collection: falsePositives);
    }
}
