namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>Reads the mount facet of the kit an active body runs.</summary>
    /// <param name="index">The 0-based body index.</param>
    /// <returns>The facet, or <see langword="null"/> when the index holds no active body or its kit carries no
    /// rider.</returns>
    public FixedWorldMount? MountFacet(int index) => (((((uint)index) < ((uint)Capacity)) && m_entries[index].Active && (m_entries[index].Body is not null))
        ? m_kits[ResolveKitIndex(index: index)].Mount
        : null
    );
    /// <summary>Reads the name of the kit row a body runs.</summary>
    /// <param name="index">The 0-based body index.</param>
    /// <returns>The kit's name.</returns>
    public string KitName(int index) => m_kitRows[ResolveKitIndex(index: index)].Name;
    /// <summary>Sets the body an active body rides this tick, or none.</summary>
    /// <param name="rider">The riding body's 0-based index.</param>
    /// <param name="mount">The ridden body's 0-based index, or -1 for none.</param>
    public void SetMount(int rider, int mount) => m_entries[rider].Body?.SetMount(index: mount);
    /// <summary>Seats a body on the body it rides at once: it rides <paramref name="mount"/> from now, at the seat of
    /// that body's kit. A mount whose kit carries no rider leaves the body where it is, riding nothing.</summary>
    /// <param name="rider">The riding body's 0-based index.</param>
    /// <param name="mount">The ridden body's 0-based index.</param>
    public void SeatRider(int rider, int mount) {
        if ((m_entries[rider] is not { Active: true, Body: { } body }) || (MountFacet(index: mount) is not { } facet)) {
            return;
        }

        body.SetMount(index: mount);
        body.FollowMount(
            facet: in facet,
            mount: m_entries[mount].Body!
        );
    }
    /// <summary>Reads whether any active body rides <paramref name="mount"/> this tick.</summary>
    /// <param name="mount">The ridden body's 0-based index.</param>
    /// <returns><see langword="true"/> when a body rides it.</returns>
    public bool HasRider(int mount) {
        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is { Active: true, Body: { MountedOn: { } ridden } } && (ridden == mount)) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Collects the active bodies riding <paramref name="mount"/>, in index order.</summary>
    /// <param name="mount">The ridden body's 0-based index.</param>
    /// <param name="into">The list the riders' indices are added to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="into"/> is <see langword="null"/>.</exception>
    public void CollectRiders(int mount, List<int> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is { Active: true, Body: { MountedOn: { } ridden } } && (ridden == mount)) {
                into.Add(item: index);
            }
        }
    }
    /// <summary>Seats every riding body on the body it rides, after movement, contact and carrying have settled the
    /// ridden bodies' poses. A rider whose mount is gone, or whose kit no longer carries a rider, keeps the pose it has
    /// and moves on its own from the next tick.</summary>
    public void FollowMounts() {
        for (var index = 0; (index < Capacity); index++) {
            if (
                (m_entries[index] is not { Active: true, Body: { MountedOn: { } ridden } rider }) ||
                (MountFacet(index: ridden) is not { } facet)
            ) {
                continue;
            }

            rider.FollowMount(
                facet: in facet,
                mount: m_entries[ridden].Body!
            );
        }
    }
    /// <summary>Sets a rider down at the exit of the body it rode. A rider that rides nothing, or whose mount is gone,
    /// is left where it is.</summary>
    /// <param name="rider">The riding body's 0-based index.</param>
    /// <param name="mount">The ridden body's 0-based index.</param>
    public void LeaveMount(int rider, int mount) {
        if (
            (m_entries[rider] is not { Active: true, Body: { } body }) ||
            (MountFacet(index: mount) is not { } facet)
        ) {
            return;
        }

        body.LeaveMount(
            facet: in facet,
            mount: m_entries[mount].Body!
        );
    }
}
