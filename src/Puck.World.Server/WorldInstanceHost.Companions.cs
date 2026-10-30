using Puck.Assets;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    // Companions whose trip home was refused or has no colocated home to reach, by instance and incarnation, so the
    // separation sweep does not queue them again every tick.
    private readonly HashSet<(string Instance, WorldEntityAddress Incarnation)> m_strandedCompanions = [];

    /// <summary>Gets or sets the receiver of a player's companion notice: the line a player reads when the world it
    /// crosses into does not take its companion, which goes or stays home. The composition root shows it on the HUD; it
    /// also narrates on the <c>world.companion</c> channel.</summary>
    public Action<string>? CompanionNotice { get; set; }

    // What a crossing companion carries: its role, the player it follows, its home (this authority when it is home) and
    // its preferred driver, published to the process content store from this world's own role driver when it is home.
    private static WorldTransferCompanion? CompanionMember(WorldInstance source, int slot, WorldEntityAddress? follows) {
        if (source.Server.Population.Companion(index: slot) is not { } binding) {
            return null;
        }

        return new WorldTransferCompanion(
            Driver: (binding.Driver ?? PublishRoleDriver(role: binding.Role, server: source.Server)),
            Follows: follows,
            Home: (binding.Home ?? source.Server.AuthorityIdentity),
            Role: binding.Role
        );
    }
    // The pin of this world's own driver for a role, with its module bytes placed in the process content store, where
    // a destination that accepts visiting drivers resolves and verifies them. Null when the role names no driver of
    // this world's own or its module cannot be read and matched to its pin.
    private static string? PublishRoleDriver(WorldServer server, string role) {
        if (
            (server.Definition.FindRole(name: role) is not { Driver: { } driverName }) ||
            (server.Definition.Addons.FirstOrDefault(predicate: addon => string.Equals(a: addon.Name, b: driverName, comparisonType: StringComparison.Ordinal)) is not { } row) ||
            !ContentPin.TryParse(text: row.Hash, pin: out var pin)
        ) {
            return null;
        }
        if (WorldProjectionContent.Shared.Contains(pin: pin)) {
            return row.Hash;
        }
        if (
            (row.ModulePath.Length == 0) ||
            !WorldDocumentPaths.TryResolve(
                documentDirectory: server.Definition.DocumentDirectory,
                path: row.ModulePath,
                reason: out _,
                resolved: out var modulePath
            )
        ) {
            return null;
        }

        try {
            var bytes = File.ReadAllBytes(path: modulePath);

            if (ContentPin.Compute(content: bytes) != pin) {
                return null;
            }

            _ = WorldProjectionContent.Shared.Put(content: bytes);
            return row.Hash;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return null;
        }
    }
    // Each player a committed transfer moved takes its companions along: one crossing per companion to the same
    // destination through the same door, after the player's own. A companion's own crossing carries none.
    private void CarryCompanions(InDoubtTransfer pending) {
        var transfer = pending.Transfer;

        if (
            (transfer.Companion is not null) ||
            !m_instances.TryGetValue(key: transfer.SourceInstance, value: out var source)
        ) {
            return;
        }

        var population = source.Server.Population;
        var destination = ((pending.TargetAuthority?.Remote is { } remote)
            ? TransferDestination.Remote(
                authority: remote.Endpoint,
                documentPath: transfer.Destination.DocumentPath!,
                name: pending.TargetName
            )
            : TransferDestination.Existing(name: pending.TargetName));

        foreach (var member in pending.Landed) {
            for (var slot = population.LocalSeatCount; (slot < population.Capacity); slot++) {
                if (population.Companion(index: slot) is not { } binding || (binding.Follows != member.SourceSlot)) {
                    continue;
                }

                m_pendingTransfers.Enqueue(item: (transfer with {
                    Arrival = ((transfer.Posed is null) ? transfer.Arrival : WorldPortalArrival.Spawn),
                    Attempt = 0,
                    Companion = new CompanionPassage(Follows: member.Mobility.Incarnation, Home: false),
                    Destination = destination,
                    FrozenCohortSlots = [slot],
                    PartyAllOrNothing = true,
                    Posed = null,
                    Scope = TransferScope.Body,
                    ScopeProofAlreadyVerified = true,
                    Source = source,
                    SourceSlot = slot,
                    TestForceJoinRefusalOrdinal = null,
                    TransferId = MintTransferId(source: source),
                    Travellers = [OccupantIncarnation(instance: source, slot: slot)],
                }));
                if (m_narration.HasNarrationSink) {
                    m_narration.Narrate(
                        channel: "world.companion",
                        text: $"[world.companion: '{source.Name}' body:{slot} ({binding.Role}) follows body:{member.SourceSlot} to '{pending.TargetName}']"
                    );
                }
            }
        }
    }
    // A destination's role policy refused these companions: each one's player is told, and one that is away from home
    // goes home rather than stay where its player no longer is.
    private void RefuseCompanions(int[] members, WorldInstance source, string targetName, string reason, in PendingTransfer transfer) {
        foreach (var slot in members) {
            if (source.Server.Population.Companion(index: slot) is not { } binding) {
                continue;
            }

            var atHome = ((binding.Home is null) || string.Equals(a: binding.Home, b: source.Server.AuthorityIdentity, comparisonType: StringComparison.Ordinal));
            var message = (atHome
                ? $"Your {binding.Role} cannot follow you into '{targetName}' — {reason}. It waits at home."
                : $"Your {binding.Role} cannot follow you into '{targetName}' — {reason}. It is going home.");

            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.companion",
                    text: $"[world.companion: '{source.Name}' body:{slot} refused by '{targetName}' ({reason})]"
                );
            }
            if (transfer.Companion is { Home: false }) {
                CompanionNotice?.Invoke(obj: message);
            }
            if (!atHome) {
                EnqueueCompanionHome(binding: binding, instance: source, slot: slot);
            }
        }
    }
    // A companion away from home whose player is no longer beside it (the player crossed on without it, or left the
    // game) goes home. A companion already queued to cross, or whose trip home cannot be made, is left where it is.
    private void SendSeparatedCompanionsHome() {
        foreach (var instance in m_instances.Values) {
            var population = instance.Server.Population;

            for (var slot = population.LocalSeatCount; (slot < population.Capacity); slot++) {
                if (
                    (population.Companion(index: slot) is not { Home: { } home } binding) ||
                    string.Equals(a: home, b: instance.Server.AuthorityIdentity, comparisonType: StringComparison.Ordinal) ||
                    ((binding.Follows >= 0) && population.IsActive(index: binding.Follows)) ||
                    IsCrossingQueued(instance: instance, slot: slot)
                ) {
                    continue;
                }

                EnqueueCompanionHome(binding: binding, instance: instance, slot: slot);
            }
        }
    }
    private bool IsCrossingQueued(WorldInstance instance, int slot) {
        foreach (var transfer in m_pendingTransfers) {
            if (
                string.Equals(a: transfer.SourceInstance, b: instance.Name, comparisonType: StringComparison.Ordinal) &&
                ((transfer.SourceSlot == slot) || (transfer.FrozenCohortSlots?.Contains(value: slot) ?? false))
            ) {
                return true;
            }
        }
        foreach (var pending in m_inDoubtTransfers) {
            if (
                string.Equals(a: pending.Transfer.SourceInstance, b: instance.Name, comparisonType: StringComparison.Ordinal) &&
                (pending.Transfer.FrozenCohortSlots?.Contains(value: slot) ?? false)
            ) {
                return true;
            }
        }
        if (OccupantIncarnation(instance: instance, slot: slot) is { } incarnation) {
            return m_strandedCompanions.Contains(item: (instance.Name, incarnation));
        }
        return false;
    }
    // Queues one companion's trip home, set down at its home placement. Its home must be an authority this host runs;
    // a companion whose home is elsewhere is reported and stays.
    private void EnqueueCompanionHome(WorldInstance instance, int slot, WorldCompanionBinding binding) {
        var home = m_instances.Values.FirstOrDefault(predicate: candidate => string.Equals(
            a: candidate.Server.AuthorityIdentity,
            b: binding.Home,
            comparisonType: StringComparison.Ordinal
        ));

        if (home is null) {
            if (OccupantIncarnation(instance: instance, slot: slot) is { } incarnation) {
                _ = m_strandedCompanions.Add(item: (instance.Name, incarnation));
            }
            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.companion",
                    text: $"[world.companion: '{instance.Name}' body:{slot} ({binding.Role}) cannot go home — its home '{binding.Home}' is not an authority this host runs]"
                );
            }
            return;
        }

        var transferId = EnqueuePending(
            actingPrincipal: Principal.World,
            adjacencyCounterpart: null,
            arrival: WorldPortalArrival.Spawn,
            border: "companion-home",
            borderCapacity: null,
            companion: new CompanionPassage(Follows: null, Home: true),
            continuum: null,
            counterpart: null,
            destination: TransferDestination.Existing(name: home.Name),
            explicitTransferId: null,
            frozenCohortSlots: [slot],
            frozenGenerationId: null,
            frozenScopeKey: null,
            fullPolicy: WorldTransferFullPolicy.Refuse,
            holdSeconds: 2.0,
            partyAllOrNothing: true,
            posed: null,
            resolvedDestinationRow: null,
            scope: TransferScope.Body,
            sourceCrossingPoint: default,
            sourceFrame: null,
            sourceInstance: instance.Name,
            sourceSlot: slot,
            testForceJoinRefusalOrdinal: null
        );

        if (m_narration.HasNarrationSink) {
            m_narration.Narrate(
                channel: "world.companion",
                text: $"[world.companion: '{instance.Name}' body:{slot} ({binding.Role}) is separated from its player and goes home to '{home.Name}' (transfer={transferId})]"
            );
        }
    }
}
