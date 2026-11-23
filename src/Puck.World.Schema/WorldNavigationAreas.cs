using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Navigation;

namespace Puck.World;

/// <summary>A world's navigation area types, marked areas and cost profiles, compiled once from a validated definition
/// into the kernel's shapes: the <see cref="NavigationAreaSet"/> a <see cref="NavigationRuntime"/> marks its cells with
/// and the <see cref="NavigationCostProfile"/> each authored profile prices them by. A function of the definition alone,
/// so the authority and a process that holds only the delivered document price routes alike.</summary>
public sealed class WorldNavigationAreas {
    private readonly string[] m_types;
    private readonly NavigationCostProfile[] m_profiles;

    private WorldNavigationAreas(string[] types, NavigationAreaSet set, NavigationCostProfile[] profiles) {
        m_types = types;
        Set = set;
        m_profiles = profiles;
    }

    /// <summary>Gets the compiled area set.</summary>
    public NavigationAreaSet Set { get; }

    /// <summary>Compiles a definition's navigation areas and profiles. An area naming an undeclared type or a placement
    /// with no static region, a cost naming an undeclared type, and a duplicate profile are the validator's to refuse; here
    /// they are skipped.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The compiled areas.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldNavigationAreas Compile(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var section = definition.Navigation;
        var types = section.AreaTypeRows.ToArray();
        var volumes = new List<NavigationArea>();
        var placements = ((section.AreaRows.Count == 0) ? null : WorldPlacementIndex.ById(placements: definition.Placements));

        foreach (var area in section.AreaRows) {
            var type = Array.IndexOf(array: types, value: area.Type);

            if ((type >= 0) && TryVolume(area: area, definition: definition, placements: placements!, shape: out var shape)) {
                volumes.Add(item: new NavigationArea(Shape: shape, Type: type));
            }
        }

        var profiles = section.ProfileRows;
        var compiled = new NavigationCostProfile[profiles.Count];

        for (var index = 0; (index < compiled.Length); index++) {
            var costs = new FixedQ4816?[types.Length];

            for (var type = 0; (type < costs.Length); type++) {
                costs[type] = ((profiles[index].Exclude?.Contains(value: types[type]) ?? false)
                    ? null
                    : (((profiles[index].Costs is { } authored) && authored.TryGetValue(key: types[type], value: out var cost))
                        ? FixedQ4816.FromDouble(value: cost)
                        : FixedQ4816.One));
            }
            compiled[index] = new NavigationCostProfile(costs: costs);
        }

        return new WorldNavigationAreas(
            profiles: compiled,
            set: new NavigationAreaSet(typeCount: Math.Min(val1: types.Length, val2: NavigationAreaSet.MaxTypes), volumes: volumes),
            types: types
        );
    }
    /// <summary>Returns an area type's ordinal.</summary>
    /// <param name="type">The type's name, or <see langword="null"/>.</param>
    /// <returns>The ordinal, or -1 for <see langword="null"/> or an undeclared name.</returns>
    public int TypeOf(string? type) => ((type is null) ? -1 : Array.IndexOf(array: m_types, value: type));
    /// <summary>Gets an area type's name.</summary>
    /// <param name="type">The type's ordinal.</param>
    /// <returns>The name, or <c>none</c> for -1.</returns>
    public string TypeName(int type) => ((((uint)type) < ((uint)m_types.Length)) ? m_types[type] : "none");
    /// <summary>Gets a profile by ordinal.</summary>
    /// <param name="index">The profile's ordinal (<see cref="WorldNavigationDomainTable.ProfileOf"/>), or -1.</param>
    /// <returns>The profile, or <see cref="NavigationCostProfile.Neutral"/> for -1.</returns>
    public NavigationCostProfile Profile(int index) => ((((uint)index) < ((uint)m_profiles.Length)) ? m_profiles[index] : NavigationCostProfile.Neutral);

    // An area's world volume: its own region in world space, or the region of the static placement it names.
    private static bool TryVolume(WorldNavigationArea area, WorldDefinition definition, WorldRowIndex<WorldPlacement, string> placements, out FixedSpatialShape shape) {
        if (area.Region is { } region) {
            shape = region.ShapeAt(origin: FixedVector3.Zero, yawRadians: FixedQ4816.Zero);
            return true;
        }
        if ((area.Placement is { } id) && (placements.GetValueOrDefault(key: id) is { Region: { } placed, Attach: null, Deal: null } placement)) {
            var frame = WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement);

            shape = placed.ShapeAt(origin: frame.FixedPosition, yawRadians: frame.FixedYawRadians);
            return true;
        }
        shape = default;
        return false;
    }
}
