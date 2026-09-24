using System.Text;
using Puck.Assets;

namespace Puck.World.Server;

/// <summary>The closed-group policy carried by every rewindable root. This is enforced from capture onward,
/// rather than inferred later from an empty transfer table.</summary>
public static class WorldReleaseRewindBoundary {
    public const string Contract = "puck.world.rewind.closed-group.v1";

    /// <summary>Pins the policy and exact owner/world inventory independently of release metadata.</summary>
    public static string Compute(Guid owner, string group, IEnumerable<string> worlds) {
        var names = worlds.Order(comparer: StringComparer.Ordinal).ToArray();

        if (
            (owner == Guid.Empty) ||
            (names.Length == 0) ||
            (names.Distinct(comparer: StringComparer.Ordinal).Count() != names.Length)
        ) {
            throw new ArgumentException(message: "rewind requires a non-empty, unique owned inventory");
        }
        _ = SafeName.Parse(candidate: group);
        foreach (var name in names) { _ = SafeName.Parse(candidate: name); }
        return ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: $"{Contract}\n{owner:D}\n{group}\n{string.Join(
            '\n',
            names
        )}")).ToString();
    }
    /// <summary>Rejects obligations that predate enforcement and cannot be restored with this group.</summary>
    public static void RequireContained(WorldAuthorityCheckpoint checkpoint, Func<string, bool> containsAuthority) {
        if (
            (checkpoint.HostRow.InDoubtTransfers.Count != 0) ||
            (checkpoint.Escrow.Leases.Count != 0) ||
            (checkpoint.Escrow.MobilityLeases.Count != 0)
        ) {
            throw new InvalidOperationException(message: "rewind requires settled transfer obligations");
        }
        if (
            checkpoint.HostRow.ForwardedBodies.Any(predicate: row => ((row.DestinationEndpoint is not null) ||
                !containsAuthority(row.SourceAuthority) || !containsAuthority(row.DestinationAddress.Authority))) ||
            checkpoint.Escrow.Committed.Any(predicate: row => !containsAuthority(row.Key.SourceAuthority)) ||
            checkpoint.Escrow.MobilityAdmissions.Any(predicate: row => !containsAuthority(row.SourceAuthority))
        ) {
            throw new InvalidOperationException(message: "rewind checkpoint contains an obligation outside the closed group");
        }
    }
}
