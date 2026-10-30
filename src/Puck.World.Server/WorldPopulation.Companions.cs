using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The companion binding of one body: the role it holds, the body it follows in this authority, the
/// authority it calls home and the driver it prefers. A home inhabitant names neither a home nor a driver: its home is
/// the authority it stands in, and its driver is its own role row's.</summary>
/// <param name="Role">The <see cref="WorldRole.Name"/> the body holds.</param>
/// <param name="Follows">The body index of the player it follows here, or -1 when that player is not here.</param>
/// <param name="Home">The authority identity of the home that re-adopts it, or <see langword="null"/> at home.</param>
/// <param name="Driver">The pin (<c>sha256/…</c>) of the driver it prefers, or <see langword="null"/> at home.</param>
public readonly record struct WorldCompanionBinding(string Role, int Follows, string? Home, string? Driver);
/// <summary>A home companion away in another authority. Its home placement counts it present, so neither a reload nor
/// a reconcile spawns a replacement, and its slot is held for its return.</summary>
/// <param name="Slot">The home body index it left and returns to.</param>
/// <param name="PlacementId">The inhabited placement it belongs to.</param>
/// <param name="Incarnation">The mobility incarnation it travels under, which home re-adopts it by.</param>
public readonly record struct WorldAwayCompanion(int Slot, string PlacementId, WorldEntityAddress Incarnation);
public sealed partial class WorldPopulation {
    // Slot order, so a capture and every scan read it in one deterministic order.
    private readonly List<WorldAwayCompanion> m_awayCompanions = [];

    /// <summary>Gets the home companions away in another authority, in slot order.</summary>
    public IReadOnlyList<WorldAwayCompanion> AwayCompanions => m_awayCompanions;

    /// <summary>Returns the companion binding the body at <paramref name="index"/> holds, or <see langword="null"/>
    /// for a body that holds no role or an inactive slot.</summary>
    /// <param name="index">The body index.</param>
    /// <returns>The binding, or <see langword="null"/>.</returns>
    public WorldCompanionBinding? Companion(int index) => (((((uint)index) < ((uint)m_entries.Length)) && m_entries[index].Active)
        ? m_entries[index].Companion
        : null);
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
    /// returns the body as the companion it left as.</summary>
    /// <param name="slot">The restored body index.</param>
    /// <param name="binding">The binding it departed with.</param>
    /// <param name="placementId">The placement it inhabited, or <see langword="null"/>.</param>
    public void RestoreCompanion(int slot, WorldCompanionBinding binding, string? placementId) {
        var entry = m_entries[slot];

        if (!entry.Active) {
            return;
        }

        entry.Companion = binding;
        entry.PlacementId = (placementId ?? entry.PlacementId);
        m_revision++;
    }
    /// <summary>Records that this world's own companion at <paramref name="slot"/> left for another authority: its
    /// placement keeps counting it and its slot is held for it.</summary>
    /// <param name="slot">The body index it left.</param>
    /// <param name="placementId">The placement it inhabits.</param>
    /// <param name="incarnation">The mobility incarnation it departs under.</param>
    public void MarkAway(int slot, string placementId, WorldEntityAddress incarnation) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: placementId);
        _ = m_awayCompanions.RemoveAll(match: away => (away.Slot == slot) || (away.Incarnation == incarnation));
        var row = new WorldAwayCompanion(Incarnation: incarnation, PlacementId: placementId, Slot: slot);
        var at = m_awayCompanions.FindIndex(match: away => (away.Slot > slot));

        if (at < 0) {
            m_awayCompanions.Add(item: row);
        } else {
            m_awayCompanions.Insert(index: at, item: row);
        }
        m_revision++;
    }
    /// <summary>Forgets an away companion without re-adopting it: a rolled-back departure that put the body back.</summary>
    /// <param name="incarnation">The incarnation it departed under.</param>
    public void ClearAway(WorldEntityAddress incarnation) {
        if (m_awayCompanions.RemoveAll(match: away => (away.Incarnation == incarnation)) > 0) {
            m_revision++;
        }
    }
    /// <summary>Returns the home slot an away companion of this world holds, by the incarnation it travels under.</summary>
    /// <param name="incarnation">The returning traveller's incarnation.</param>
    /// <param name="slot">The held home slot, or -1.</param>
    /// <returns><see langword="true"/> when the traveller is this world's own companion coming home.</returns>
    public bool TryFindAway(WorldEntityAddress incarnation, out int slot) {
        foreach (var away in m_awayCompanions) {
            if (away.Incarnation == incarnation) {
                slot = away.Slot;
                return true;
            }
        }

        slot = -1;
        return false;
    }
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
    /// <returns><see langword="true"/> when the slot was held for this traveller and its placement still inhabits.</returns>
    public bool TryReadoptAway(WorldDefinition definition, int slot, WorldEntityAddress incarnation) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var at = m_awayCompanions.FindIndex(match: away => (away.Slot == slot) && (away.Incarnation == incarnation));

        if (at < 0) {
            return false;
        }

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
        entry.Companion = ((inhabit.Role is { } role)
            ? new WorldCompanionBinding(Driver: null, Follows: (inhabit.Follows ?? -1), Home: null, Role: role)
            : null);
        body.SetIntentSource(source: inhabit.Source);
        if ((ResolveInhabitKit(definition: definition, placement: placement) is { } kitName) && (ResolveKitOrNull(name: kitName) is { } kitIndex)) {
            entry.KitIndex = kitIndex;
            RecompileBodyKit(body: body, kitIndex: kitIndex);
        }
        entry.LookIndex = ResolveInhabitLook(placement: placement);
        RebuildPlacementOrdinalTable(definition: definition);
        return true;
    }
    /// <summary>Returns the inhabited placement's spawn pose for a re-adopted companion set down at home without a
    /// mapped arrival: the placement's own frame at its first distribution sample.</summary>
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
    /// <summary>Resolves the followed body of every companion in this authority from the incarnation its player
    /// travels under, and returns the body index found or -1.</summary>
    /// <param name="incarnation">The followed player's incarnation.</param>
    /// <returns>The active body index holding that incarnation, or -1.</returns>
    public int FindIncarnation(WorldEntityAddress incarnation) {
        for (var index = 0; (index < m_entries.Length); index++) {
            if (m_entries[index] is { Active: true, Mobility: { } mobility } && (mobility.Incarnation == incarnation)) {
                return index;
            }
        }

        return -1;
    }
    /// <summary>Re-dresses every visiting companion after a structural install: a role's kit and look are rows of the
    /// document, so a reload re-resolves them by name.</summary>
    /// <param name="definition">The installed definition.</param>
    private void RedressCompanions(WorldDefinition definition) {
        for (var index = LocalSeatCount; (index < m_entries.Length); index++) {
            var entry = m_entries[index];

            if (
                (entry.PlacementId is null) &&
                (entry.Body is { } body) &&
                (entry.Companion is { } binding) &&
                (definition.FindRole(name: binding.Role) is { } role)
            ) {
                Dress(body: body, entry: entry, kit: role.Kit, look: role.Look);
            }
        }
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
    private void RestoreAwayCompanions(IReadOnlyList<WorldAwayCompanion>? rows) {
        m_awayCompanions.Clear();
        if (rows is not null) {
            m_awayCompanions.AddRange(collection: rows.OrderBy(keySelector: static row => row.Slot));
        }
    }
}
