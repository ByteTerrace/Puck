namespace Puck.World.Server;

public sealed partial class WorldTransferEscrow {
    private static bool TryCopyReservation(WorldTransferReservationRequest request, out WorldTransferReservationRequest owned,
        out WorldSocialObserverImport[] histories, out string reason) {
        owned = request; histories = [];
        var count = request.Members?.Count ?? 0;
        if (count <= 0 || count > WorldBodiesLimits.CapacityCeiling) { reason = "reservation traveler count is invalid"; return false; }
        var members = new WorldTransferReservationMember[count];
        histories = new WorldSocialObserverImport[count];
        var index = 0;
        foreach (var member in request.Members!) {
            if (index == count) { reason = "reservation traveler collection count changed"; return false; }
            var detached = member;
            if (member.Social is { } social) {
                if (member.Mobility is not { } mobility) { reason = "social traveler has no mobility incarnation"; return false; }
                if (!WorldSocialMemory.TryReadObserverImport(mobility.Incarnation, social, out histories[index], out reason)) { return false; }
                detached = member with { Social = histories[index].Memory };
            }
            members[index++] = detached;
        }
        if (index != count) { reason = "reservation traveler collection count changed"; return false; }
        owned = request with { Members = members }; reason = string.Empty; return true;
    }

    private static WorldTransferReservationRequest CopyOwnedReservation(WorldTransferReservationRequest request) => request with {
        Members = request.Members.Select(static member => member with {
            Social = member.Social is { } social ? WorldSocialMemory.CopyObserverSnapshot(social) : null,
        }).ToArray(),
    };

    private static bool TryPrepareSocialReservation(WorldSocialMemory? bank, WorldTransferReservationRequest request,
        WorldSocialObserverImport[] parsed, out WorldSocialObserverImport[]? histories,
        out WorldSocialImportAllowance[]? allowances, out string reason) {
        histories = null; allowances = null;
        if (bank is null) {
            if (request.Members.Any(static member => member.Social is not null)) {
                reason = "destination has no social policy for the traveler's memory"; return false;
            }
            reason = string.Empty; return true;
        }
        histories = parsed;
        allowances = new WorldSocialImportAllowance[parsed.Length];
        for (var index = 0; index < parsed.Length; index++) {
            if (request.Members[index].Mobility is not { } mobility) { reason = "social traveler has no mobility incarnation"; return false; }
            if (request.Members[index].Social is null) {
                histories[index] = new(mobility.Incarnation, bank.Policy, new(bank.Policy.Identity, bank.EngineTick, 0, 0, 0, [], []));
            } else if (!bank.AcceptsMemorySemantics(parsed[index].SourcePolicy)) {
                reason = "destination social policy gives the traveler's memory different meanings"; return false;
            }
            var memory = histories[index].Memory;
            allowances[index] = new(mobility.Incarnation, memory.Impressions.Count, memory.Receipts.Count);
        }
        reason = string.Empty; return true;
    }

    // Called before authority restore writes anything. Every live body lease must have its matching social quota;
    // additional component reservations are permitted, but cannot impersonate or underfund a body-transfer lease.
    internal static void ValidateSocialCheckpoint(WorldTransferEscrowCheckpoint checkpoint, WorldSocialMemory? bank) {
        foreach (var lease in checkpoint.Leases) {
            if (lease.Key != new WorldTransferKey(lease.Request.SourceAuthority, lease.Request.TransferId) ||
                !TryCopyReservation(lease.Request, out var request, out var parsed, out var reason)) {
                throw new InvalidOperationException("invalid social transfer lease checkpoint");
            }
            if (!TryPrepareSocialReservation(bank, request, parsed, out _, out var allowances, out reason) ||
                (bank is not null && !bank.HasImportReservation(lease.Key, allowances!))) {
                throw new InvalidOperationException("social transfer lease checkpoint is missing its compatible history or reserved quota");
            }
        }
    }
}
