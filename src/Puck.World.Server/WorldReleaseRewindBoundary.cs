using Puck.Assets;

namespace Puck.World.Server;

/// <summary>The closed-group policy carried by every rewindable root. This is enforced from capture onward,
/// rather than inferred later from an empty transfer table.</summary>
public static class WorldReleaseRewindBoundary {
    /// <summary>The policy's contract name, part of every boundary pin.</summary>
    public const string Contract = "puck.world.rewind.closed-group.v1";

    /// <summary>Returns the authority namespaces one world of a group answers to: its row name, and the namespace its
    /// definition declares when it declares one. Activation, capture and restore inspection all build the group's
    /// inventory from this, so none can hold a name another lacks.</summary>
    /// <param name="world">The world's row name.</param>
    /// <param name="authorityNamespace">The definition's <c>host.authorityNamespace</c>, or <see langword="null"/>.</param>
    /// <returns>The namespaces.</returns>
    public static IEnumerable<string> Authorities(string world, string? authorityNamespace) {
        yield return world;
        if ((authorityNamespace is { Length: > 0 } declared) && !string.Equals(a: declared, b: world, comparisonType: StringComparison.Ordinal)) {
            yield return declared;
        }
    }
    /// <summary>Marks a checkpoint as a recovery point of a closed rewind group: restoring it ends every traveler
    /// minted outside <paramref name="authorities"/> and every row of a source outside them, inside the restore
    /// (<see cref="WorldTransferEscrowCheckpoint.RewindBaseline"/>). Nothing else of the image changes.</summary>
    /// <param name="checkpoint">The captured checkpoint.</param>
    /// <param name="authorities">The group's authority namespaces.</param>
    /// <returns>The baseline.</returns>
    /// <exception cref="ArgumentException"><paramref name="authorities"/> is empty.</exception>
    public static WorldAuthorityCheckpoint Baseline(WorldAuthorityCheckpoint checkpoint, IEnumerable<string> authorities) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);
        var names = authorities.Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).ToArray();

        if (names.Length == 0) {
            throw new ArgumentException(message: "a rewind baseline names its group's authorities", paramName: nameof(authorities));
        }
        return checkpoint with { Escrow = checkpoint.Escrow with { RewindBaseline = names } };
    }
    /// <summary>Returns the host row a checkpoint restores. A rewind baseline's is without the forwarding routes of
    /// travelers minted outside its group: the world a route led to ends that traveler in its own restore.</summary>
    /// <param name="checkpoint">The checkpoint.</param>
    /// <returns>The host row slice to restore.</returns>
    public static WorldAuthorityHostRowCheckpoint HostRow(WorldAuthorityCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);
        if (checkpoint.Escrow.RewindBaseline is not { Count: > 0 } baseline) {
            return checkpoint.HostRow;
        }
        var group = new HashSet<string>(collection: baseline, comparer: StringComparer.Ordinal);

        return checkpoint.HostRow with {
            ForwardedBodies = [.. checkpoint.HostRow.ForwardedBodies.Where(predicate: row => group.Contains(item: row.SourceIncarnation.Authority))],
        };
    }
    /// <summary>Pins the policy and exact owner/world inventory independently of release metadata.</summary>
    /// <param name="owner">The owner of the group's worlds.</param>
    /// <param name="group">The deployment group's name.</param>
    /// <param name="worlds">The group's world names.</param>
    /// <returns>The boundary pin.</returns>
    /// <exception cref="ArgumentException">The owner is empty, or the inventory is empty or repeats a name.</exception>
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
        return ContentPin.OfText(text: $"{Contract}\n{owner:D}\n{group}\n{string.Join(
            '\n',
            names
        )}").ToString();
    }
    /// <summary>Proves the part of a checkpoint a rewind baseline keeps: this world's own departures are settled and
    /// no world of the group holds an open reservation here, every forwarding route stays inside the group, and every
    /// body minted outside the group is a transferred human seat, which is what a baseline's restore ends. Rows of a
    /// source outside the group are no reason to refuse, because the restore drops them.</summary>
    /// <param name="checkpoint">The captured checkpoint.</param>
    /// <param name="containsAuthority">Whether an authority namespace is a world of the group.</param>
    /// <exception cref="InvalidOperationException">The checkpoint holds an unsettled obligation inside the group, a
    /// route leaving it, or an outside traveler a restore could not end.</exception>
    public static void RequireContained(WorldAuthorityCheckpoint checkpoint, Func<string, bool> containsAuthority) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);
        ArgumentNullException.ThrowIfNull(argument: containsAuthority);
        if (
            (checkpoint.HostRow.InDoubtTransfers.Count != 0) ||
            checkpoint.Escrow.Leases.Any(predicate: lease => containsAuthority(lease.Key.SourceAuthority)) ||
            checkpoint.Escrow.MobilityLeases.Any(predicate: lease => containsAuthority(lease.Transfer.SourceAuthority))
        ) {
            throw new InvalidOperationException(message: "rewind requires settled transfer obligations");
        }
        if (checkpoint.HostRow.ForwardedBodies.Any(predicate: row => ((row.DestinationEndpoint is not null) ||
            !containsAuthority(row.SourceAuthority) || !containsAuthority(row.DestinationAddress.Authority)))) {
            throw new InvalidOperationException(message: "rewind checkpoint contains an obligation outside the closed group");
        }
        foreach (var entry in checkpoint.Population.Entries) {
            var admission = entry.Admission;

            if (
                (admission.Mobility is { } mobility) &&
                (admission.MobilityGeneration == admission.Generation) &&
                !containsAuthority(mobility.Incarnation.Authority) &&
                !(admission.IsRemoteHuman && admission.IsAuthorityTransferred)
            ) {
                throw new InvalidOperationException(message: $"rewind checkpoint holds body:{entry.Index}, a traveler from outside the closed group that is not a player's seat");
            }
        }
    }
}
