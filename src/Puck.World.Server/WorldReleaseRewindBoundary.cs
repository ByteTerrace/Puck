using System.Text;

namespace Puck.World.Server;

/// <summary>The closed-group policy carried by every rewindable root. This is enforced from capture onward,
/// rather than inferred later from an empty transfer table.</summary>
public static class WorldReleaseRewindBoundary {
    public const string Contract = "puck.world.rewind.closed-group.v1";

    /// <summary>Pins the policy and exact owner/world inventory independently of release metadata.</summary>
    public static string Compute(Guid owner, string group, IEnumerable<string> worlds) {
        var names = worlds.Order(StringComparer.Ordinal).ToArray();
        if (owner == Guid.Empty || names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length) {
            throw new ArgumentException("rewind requires a non-empty, unique owned inventory");
        }
        _ = SafeName.Parse(group);
        foreach (var name in names) { _ = SafeName.Parse(name); }
        return WorldAuthorityRecoveryRootCodec.ComputePin(Encoding.UTF8.GetBytes($"{Contract}\n{owner:D}\n{group}\n{string.Join('\n', names)}"));
    }

    /// <summary>Rejects obligations that predate enforcement and cannot be restored with this group.</summary>
    public static void RequireContained(WorldAuthorityCheckpoint checkpoint, Func<string, bool> containsAuthority) {
        if (checkpoint.HostRow.InDoubtTransfers.Count != 0 || checkpoint.Escrow.Leases.Count != 0 ||
            checkpoint.Escrow.MobilityLeases.Count != 0) {
            throw new InvalidOperationException("rewind requires settled transfer obligations");
        }
        if (checkpoint.HostRow.ForwardedBodies.Any(row => row.DestinationEndpoint is not null ||
                !containsAuthority(row.SourceAuthority) || !containsAuthority(row.DestinationAddress.Authority)) ||
            checkpoint.Escrow.Committed.Any(row => !containsAuthority(row.Key.SourceAuthority)) ||
            checkpoint.Escrow.MobilityAdmissions.Any(row => !containsAuthority(row.SourceAuthority))) {
            throw new InvalidOperationException("rewind checkpoint contains an obligation outside the closed group");
        }
    }
}
