using System.Security.Cryptography;
using System.Text;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The companion binding of one body: the role it holds, the body it follows in this authority, the
/// authority it calls home, the driver it prefers and the custody token its home issued when it left. A home
/// inhabitant names neither a home, a driver nor a token: its home is the authority it stands in, and its driver is its
/// own role row's.</summary>
/// <param name="Role">The <see cref="WorldRole.Name"/> the body holds.</param>
/// <param name="Follows">The body index of the player it follows here, or -1 when that player is not here.</param>
/// <param name="Home">The authority identity of the home that re-adopts it, or <see langword="null"/> at home.</param>
/// <param name="Driver">The pin (<c>sha256/…</c>) of the driver it prefers, or <see langword="null"/> at home.</param>
/// <param name="FollowsIncarnation">The mobility incarnation of the player at <paramref name="Follows"/> when this
/// authority verified it, or <see langword="null"/> at home, where it follows a local seat.</param>
/// <param name="Custody">The custody token its home issued at departure, or <see langword="null"/> at home.</param>
public readonly record struct WorldCompanionBinding(string Role, int Follows, string? Home, string? Driver, WorldEntityAddress? FollowsIncarnation = null, string? Custody = null);
/// <summary>A home companion away in another authority. Its home placement counts it present, so neither a reload nor
/// a reconcile spawns a replacement, and its slot is held for its return, which only a traveller presenting the custody
/// token issued for this incarnation and departure epoch claims.</summary>
/// <param name="Slot">The home body index it left and returns to.</param>
/// <param name="PlacementId">The inhabited placement it belongs to.</param>
/// <param name="Incarnation">The mobility incarnation it travels under, which home re-adopts it by.</param>
/// <param name="Epoch">The mobility epoch it departed at, which its custody token is bound to.</param>
public readonly record struct WorldAwayCompanion(int Slot, string PlacementId, WorldEntityAddress Incarnation, ulong Epoch);
public sealed partial class WorldPopulation {
    // Slot order, so a capture and every scan read it in one deterministic order.
    private readonly List<WorldAwayCompanion> m_awayCompanions = [];
    // The secret this authority binds its companions' custody tokens with: never disclosed, checkpointed with the
    // population so a restore and a replay check the tokens the live authority issued.
    private byte[] m_custodyKey = RandomNumberGenerator.GetBytes(count: 32);

    /// <summary>Gets the home companions away in another authority, in slot order.</summary>
    public IReadOnlyList<WorldAwayCompanion> AwayCompanions => m_awayCompanions;

    /// <summary>Returns the companion binding the body at <paramref name="index"/> holds, or <see langword="null"/>
    /// for a body that holds no role or an inactive slot.</summary>
    /// <param name="index">The body index.</param>
    /// <returns>The binding, or <see langword="null"/>.</returns>
    public WorldCompanionBinding? Companion(int index) => (((((uint)index) < ((uint)m_entries.Length)) && m_entries[index].Active)
        ? m_entries[index].Companion
        : null);
    /// <summary>Returns the body a companion's player is here, verified: a home companion's followed local seat while
    /// it is occupied, and a visiting companion's followed body only while it still holds the incarnation this
    /// authority verified. -1 when the player is not beside it.</summary>
    /// <param name="binding">The companion's binding.</param>
    /// <returns>The followed body index, or -1.</returns>
    public int FollowedBody(in WorldCompanionBinding binding) {
        if ((binding.Follows < 0) || !IsActive(index: binding.Follows)) {
            return -1;
        }
        if (binding.FollowsIncarnation is not { } incarnation) {
            return ((binding.Home is null) ? binding.Follows : -1);
        }

        return ((m_entries[binding.Follows].Mobility is { } mobility) && (mobility.Incarnation == incarnation))
            ? binding.Follows
            : -1;
    }
    /// <summary>Binds an active body as a companion of its role, dressed in the named kit and look when this world
    /// declares them. A companion re-adopted by its own home placement is bound through
    /// <see cref="TryReadoptAway"/> instead.</summary>
    /// <param name="slot">The body index.</param>
    /// <param name="binding">The binding.</param>
    /// <param name="kit">The kit name to move under, or <see langword="null"/> to keep the body's kit.</param>
    /// <param name="look">The look name to wear, or <see langword="null"/> to keep the body's look.</param>
    /// <exception cref="InvalidOperationException">The slot holds no active body.</exception>
    public void BindCompanion(int slot, WorldCompanionBinding binding, string? kit, string? look) {
        var entry = m_entries[slot];

        if (!entry.Active || (entry.Body is not { } body)) {
            throw new InvalidOperationException(message: $"body:{slot} is not active");
        }

        entry.Companion = binding;
        Dress(body: body, entry: entry, kit: kit, look: look);
        m_revision++;
    }
    /// <summary>Puts a restored departure's companion binding and placement back on its slot: a rolled-back crossing
    /// returns the body as the companion it left as, and its home no longer holds a slot for it away.</summary>
    /// <param name="slot">The restored body index.</param>
    /// <param name="binding">The binding it departed with.</param>
    /// <param name="placementId">The placement it inhabited, or <see langword="null"/>.</param>
    /// <param name="incarnation">The incarnation it departed under.</param>
    public void RestoreCompanion(int slot, WorldCompanionBinding binding, string? placementId, WorldEntityAddress incarnation) {
        ClearAway(incarnation: incarnation);

        var entry = m_entries[slot];

        if (!entry.Active) {
            return;
        }

        entry.Companion = binding;
        entry.PlacementId = placementId;
        m_revision++;
    }
    /// <summary>Returns the custody token this authority issues a companion of its own leaving under
    /// <paramref name="incarnation"/> at <paramref name="epoch"/>: a keyed digest only this authority can produce,
    /// which its return must present.</summary>
    /// <param name="incarnation">The departing companion's incarnation.</param>
    /// <param name="epoch">The mobility epoch it departs at.</param>
    /// <returns>The token.</returns>
    public string CustodyToken(WorldEntityAddress incarnation, ulong epoch) => Convert.ToHexStringLower(inArray: HMACSHA256.HashData(
        key: m_custodyKey,
        source: Encoding.UTF8.GetBytes(s: $"{incarnation.Authority}\n{incarnation.Index}\n{incarnation.Generation}\n{epoch}")
    ));
    /// <summary>Records that this world's own companion at <paramref name="slot"/> left for another authority: its
    /// placement keeps counting it and its slot is held for it.</summary>
    /// <param name="slot">The body index it left.</param>
    /// <param name="placementId">The placement it inhabits.</param>
    /// <param name="incarnation">The mobility incarnation it departs under.</param>
    /// <param name="epoch">The mobility epoch it departs at.</param>
    public void MarkAway(int slot, string placementId, WorldEntityAddress incarnation, ulong epoch) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: placementId);
        _ = m_awayCompanions.RemoveAll(match: away => (away.Slot == slot) || (away.Incarnation == incarnation));
        var row = new WorldAwayCompanion(Epoch: epoch, Incarnation: incarnation, PlacementId: placementId, Slot: slot);
        var at = m_awayCompanions.FindIndex(match: away => (away.Slot > slot));

        if (at < 0) {
            m_awayCompanions.Add(item: row);
        } else {
            m_awayCompanions.Insert(index: at, item: row);
        }
        m_revision++;
    }
    /// <summary>Forgets an away companion without re-adopting it: a rolled-back departure that put the body back, or a
    /// traveller its home knows is lost.</summary>
    /// <param name="incarnation">The incarnation it departed under.</param>
    /// <returns><see langword="true"/> when an away row was forgotten.</returns>
    public bool ClearAway(WorldEntityAddress incarnation) {
        if (m_awayCompanions.RemoveAll(match: away => (away.Incarnation == incarnation)) > 0) {
            m_revision++;
            return true;
        }

        return false;
    }
    /// <summary>Returns the home slot an away companion of this world holds, by the incarnation it travels under, when
    /// it presents the custody token issued at its departure.</summary>
    /// <param name="incarnation">The returning traveller's incarnation.</param>
    /// <param name="custody">The custody token it presents, or <see langword="null"/>.</param>
    /// <param name="slot">The held home slot, or -1.</param>
    /// <returns><see langword="true"/> when the traveller is this world's own companion coming home.</returns>
    public bool TryFindAway(WorldEntityAddress incarnation, string? custody, out int slot) {
        foreach (var away in m_awayCompanions) {
            if (
                (away.Incarnation == incarnation) &&
                (custody is not null) &&
                CryptographicOperations.FixedTimeEquals(
                    left: Encoding.ASCII.GetBytes(s: custody),
                    right: Encoding.ASCII.GetBytes(s: CustodyToken(epoch: away.Epoch, incarnation: away.Incarnation))
                )
            ) {
                slot = away.Slot;
                return true;
            }
        }

        slot = -1;
        return false;
    }
    /// <summary>Returns whether an away companion of this world travels under <paramref name="incarnation"/>, whatever
    /// token a traveller presents.</summary>
    /// <param name="incarnation">The incarnation.</param>
    /// <returns><see langword="true"/> when the incarnation names an away companion.</returns>
    public bool IsAwayIncarnation(WorldEntityAddress incarnation) => m_awayCompanions.Exists(match: away => (away.Incarnation == incarnation));
    /// <summary>Returns whether <paramref name="slot"/> is held for an away companion.</summary>
    /// <param name="slot">The body index.</param>
    /// <returns><see langword="true"/> when an away companion holds the slot.</returns>
    public bool IsAwaySlot(int slot) {
        foreach (var away in m_awayCompanions) {
            if (away.Slot == slot) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Re-adopts this world's own companion, landed at its held slot, into the placement it left: that
    /// placement's kit, look, role and followed seat, whatever the traveller arrived wearing.</summary>
    /// <param name="definition">The live definition.</param>
    /// <param name="slot">The body index it landed at.</param>
    /// <param name="incarnation">The incarnation it travels under.</param>
    /// <param name="custody">The custody token it presents.</param>
    /// <returns><see langword="true"/> when the slot was held for this traveller and its placement still inhabits.</returns>
    public bool TryReadoptAway(WorldDefinition definition, int slot, WorldEntityAddress incarnation, string? custody) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!TryFindAway(custody: custody, incarnation: incarnation, slot: out var held) || (held != slot)) {
            return false;
        }

        var at = m_awayCompanions.FindIndex(match: away => (away.Slot == slot) && (away.Incarnation == incarnation));
        var away = m_awayCompanions[at];
        var entry = m_entries[slot];

        m_awayCompanions.RemoveAt(index: at);
        m_revision++;

        if (
            !entry.Active ||
            (entry.Body is not { } body) ||
            (FindInhabited(definition: definition, placementId: away.PlacementId) is not { Inhabit: { } inhabit } placement)
        ) {
            return false;
        }

        entry.PlacementId = placement.Id;
        entry.Companion = HomeBinding(inhabit: inhabit);
        body.SetIntentSource(source: inhabit.Source);
        if ((ResolveInhabitKit(definition: definition, placement: placement) is { } kitName) && (ResolveKitOrNull(name: kitName) is { } kitIndex)) {
            entry.KitIndex = kitIndex;
            RecompileBodyKit(body: body, kitIndex: kitIndex);
        }
        entry.LookIndex = ResolveInhabitLook(placement: placement);
        RebuildPlacementOrdinalTable(definition: definition);
        return true;
    }
    /// <summary>Sets a re-adopted companion down at its placement's spawn pose, for a return without a mapped arrival:
    /// the placement's own frame at its first distribution sample.</summary>
    /// <param name="definition">The live definition.</param>
    /// <param name="slot">The re-adopted body index.</param>
    public void PoseAtPlacement(WorldDefinition definition, int slot) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var entry = m_entries[slot];

        if (
            (entry.Body is not { } body) ||
            (entry.PlacementId is not { } placementId) ||
            (FindInhabited(definition: definition, placementId: placementId) is not { Inhabit: { Distribution: { } distribution } } placement)
        ) {
            return;
        }

        var frame = WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement);
        var spawn = InhabitantSpawn(count: 1, distribution: distribution, frame: frame, ordinal: 0);
        var position = (spawn with { Y = Puck.Maths.FixedQ4816.FromDouble(value: frame.Position.Y) });

        body.Pose(
            pitchRadians: Puck.Maths.FixedQ4816.Zero,
            position: position,
            rollRadians: Puck.Maths.FixedQ4816.Zero,
            yawRadians: Puck.Maths.FixedQ4816.FromDegrees(degrees: frame.YawDegrees)
        );
        body.SetHome(home: position);
    }
    /// <summary>Returns the mobility epoch the active body at <paramref name="index"/> holds, or <see langword="null"/>
    /// for one without a mobility identity.</summary>
    /// <param name="index">The body index.</param>
    /// <returns>The epoch, or <see langword="null"/>.</returns>
    public ulong? MobilityEpoch(int index) => ((((uint)index) < ((uint)m_entries.Length)) && m_entries[index].Active)
        ? m_entries[index].Mobility?.Epoch
        : null;
    /// <summary>Returns the active body holding <paramref name="incarnation"/>, or -1.</summary>
    /// <param name="incarnation">The incarnation.</param>
    /// <returns>The active body index holding that incarnation, or -1.</returns>
    public int FindIncarnation(WorldEntityAddress incarnation) {
        for (var index = 0; (index < m_entries.Length); index++) {
            if (m_entries[index] is { Active: true, Mobility: { } mobility } && (mobility.Incarnation == incarnation)) {
                return index;
            }
        }

        return -1;
    }
    private static WorldCompanionBinding? HomeBinding(WorldPlacementInhabit inhabit) => ((inhabit.Role is { } role)
        ? new WorldCompanionBinding(Driver: null, Follows: (inhabit.Follows ?? -1), Home: null, Role: role)
        : null);
    /// <summary>Re-derives every companion's binding after a structural install. A home inhabitant takes its
    /// placement's role and followed seat as the document now authors them; a visiting companion is re-dressed in its
    /// role's kit and look by name; an away row whose placement no longer inhabits a role is forgotten, freeing its
    /// slot.</summary>
    /// <param name="definition">The installed definition.</param>
    private void RebindCompanions(WorldDefinition definition) {
        for (var index = LocalSeatCount; (index < m_entries.Length); index++) {
            var entry = m_entries[index];

            if ((entry.PlacementId is { } placementId) && (FindInhabited(definition: definition, placementId: placementId) is { Inhabit: { } inhabit })) {
                entry.Companion = HomeBinding(inhabit: inhabit);
            } else if (
                (entry.PlacementId is null) &&
                (entry.Body is { } body) &&
                (entry.Companion is { } binding) &&
                (definition.FindRole(name: binding.Role) is { } role)
            ) {
                Dress(body: body, entry: entry, kit: role.Kit, look: role.Look);
            }
        }

        _ = m_awayCompanions.RemoveAll(match: away => FindInhabited(definition: definition, placementId: away.PlacementId) is not { Inhabit.Role: not null });
    }
    private void Dress(WorldBody body, Entry entry, string? kit, string? look) {
        if ((kit is not null) && (ResolveKitOrNull(name: kit) is { } kitIndex) && (kitIndex != entry.KitIndex)) {
            entry.KitIndex = kitIndex;
            RecompileBodyKit(body: body, kitIndex: kitIndex);
        }
        if ((look is not null) && (ResolveLookOrNull(name: look) is { } lookIndex)) {
            entry.LookIndex = lookIndex;
        }
    }
    private void RestoreAwayCompanions(IReadOnlyList<WorldAwayCompanion>? rows, byte[]? custodyKey) {
        m_awayCompanions.Clear();
        if (rows is not null) {
            m_awayCompanions.AddRange(collection: rows.OrderBy(keySelector: static row => row.Slot));
        }
        if (custodyKey is { Length: 32 }) {
            m_custodyKey = [.. custodyKey];
        }
    }
}
