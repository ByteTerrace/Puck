using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// How closely a world is attended, as the host that steps it reads it from authority facts alone: who stands in
/// the world, and which worlds observe it through a session and how closely each of those is attended itself. It
/// orders from least to most, so a world's attention is the greatest any observer gives it.
/// </summary>
/// <remarks>Attention is never read from a presentation: no camera, frustum or frame decides it. A world's document
/// states the least attention it steps at (<see cref="WorldSimulationDefaults.StepsAt"/>), and an observing surface
/// states how far its attention reaches (<see cref="WorldSessionAttention"/>).</remarks>
[JsonConverter(typeof(StrictEnumConverter<WorldAttention>))]
public enum WorldAttention : byte {
    /// <summary>Nobody stands in the world and no observation reaches it.</summary>
    Unobserved,

    /// <summary>The world is seen from afar or at a remove: through a surface no one stands near, through an infinity
    /// layer, or through a world that is itself only observed.</summary>
    Glimpsed,

    /// <summary>A world someone stands in observes it through a surface they stand near, or an observer the host knows
    /// nothing more about (another process) holds a session on it.</summary>
    Watched,

    /// <summary>Someone stands in the world.</summary>
    Occupied,
}
/// <summary>
/// How far an observing surface's attention reaches: the distances, from the surface to the nearest body a human
/// occupies in the world that declares it, within which the destination is watched and within which it is glimpsed.
/// </summary>
/// <remarks>The distances are measured in the declaring world's own fixed-point coordinates, from each occupied
/// body's position to the surface's frame, so the reading is an authority fact. A surface with no place in the world
/// (a screen row no placement carries, an infinity layer) stands at no distance from anyone: it watches only when
/// <paramref name="WatchedWithin"/> is absent, and otherwise glimpses.</remarks>
/// <param name="WatchedWithin">The distance in metres within which the destination is
/// <see cref="WorldAttention.Watched"/>; absent watches at any distance.</param>
/// <param name="GlimpsedWithin">The distance in metres within which the destination is at least
/// <see cref="WorldAttention.Glimpsed"/>; absent glimpses at any distance. It is never less than
/// <paramref name="WatchedWithin"/>.</param>
public sealed record WorldSessionAttention(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? WatchedWithin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? GlimpsedWithin = null
) {
    /// <summary>Reads the attention this surface gives its destination from the squared distance to the nearest
    /// occupied body, in the declaring world's fixed-point coordinates.</summary>
    /// <param name="attention">The surface's authored reach, or <see langword="null"/> for one that authors none and
    /// so watches at any distance.</param>
    /// <param name="nearestSquared">The squared distance from the surface to the nearest body a human occupies, or
    /// <see langword="null"/> when the surface has no place or nobody stands in the declaring world.</param>
    /// <returns>The attention the surface gives.</returns>
    public static WorldAttention At(WorldSessionAttention? attention, FixedQ4816? nearestSquared) {
        if (attention is null) {
            return WorldAttention.Watched;
        }
        if (Within(reach: attention.WatchedWithin, nearestSquared: nearestSquared)) {
            return WorldAttention.Watched;
        }

        return (Within(reach: attention.GlimpsedWithin, nearestSquared: nearestSquared) ? WorldAttention.Glimpsed : WorldAttention.Unobserved);
    }

    // An absent reach holds at any distance, an absent distance lies beyond every authored reach.
    private static bool Within(float? reach, FixedQ4816? nearestSquared) {
        if (reach is not { } metres) {
            return true;
        }
        if (nearestSquared is not { } squared) {
            return false;
        }

        var limit = FixedQ4816.FromDouble(value: metres);

        return (squared <= (limit * limit));
    }
}
