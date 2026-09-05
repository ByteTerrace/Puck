using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;
using Puck.Physics;

namespace Puck.World;

/// <summary>The motion space a local steering preference may use.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldFlockSpace>))]
public enum WorldFlockSpace : byte {
    /// <summary>The body's current support/gravity tangent plane, including walls or curved worlds.</summary>
    Tangent,
    /// <summary>All three dimensions; the steering preference itself does not establish traversability.</summary>
    Volume,
}

/// <summary>Author-owned local perception and flock steering. No relationships or group names are implicit.</summary>
/// <param name="Range">Inclusive sensing radius, in world units.</param>
/// <param name="SeparationRadius">Personal-space radius, no greater than Range.</param>
/// <param name="CandidateBudget">Maximum inspected candidates, including rejected ones, per sensing update. A sensed target and local neighbors share this one sample, using the larger of their ranges.</param>
/// <param name="MaxNeighbors">Maximum nearest sampled neighbors retained, no greater than CandidateBudget.</param>
/// <param name="UpdateSeconds">Neighbor and sensed-target refresh interval; zero means every simulation step. Sensed targets hold their last observed position between samples. Designations, route goals, and heading blend every step.</param>
/// <param name="Space">Tangent-plane or three-dimensional motion.</param>
/// <param name="Separation">Local repulsion weight in [0,1].</param>
/// <param name="Alignment">Neighbor mean-heading weight in [0,1].</param>
/// <param name="Cohesion">Neighbor centroid-attraction weight in [0,1].</param>
/// <param name="Goal">Selected target or route waypoint weight in [0,1].</param>
/// <param name="Inertia">Current heading persistence weight in [0,1].</param>
/// <param name="ArrivalDistance">Distance inside which the goal term becomes zero.</param>
/// <param name="HalfAngleDegrees">Forward sensing half-angle in (0,180]. Coincident neighbors remain perceptible.</param>
/// <param name="RequiresLineOfSight">Whether every sampled neighbor must pass a deterministic solid-field sight test.</param>
/// <param name="MovementDomain">Optional volume/medium navigation domain constraining the body's integrated locomotion while this producer runs. Its agentRadius must enclose the kit's collider about the body root. Invalid steps stop, without teleporting or finding an escape route. Later external impulses, contacts, and authority teleports remain separate.</param>
/// <param name="CohesionAffinity">Optional Fixed expression weighting each retained neighbor in the centroid. Left is the observer, right the neighbor. State-backed facts only; sampled at UpdateSeconds, clamped to [0,1], arithmetic failure reads zero, absent reads one. This does not filter separation or increase the candidate budget.</param>
/// <param name="AlignmentAffinity">Independent Fixed expression weighting each retained neighbor's heading, under the same contract as CohesionAffinity. Affinities select relative influence; the outer Alignment and Cohesion weights set term strength.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFlockProfile(float Range, float SeparationRadius, int CandidateBudget, int MaxNeighbors,
    float UpdateSeconds, WorldFlockSpace Space, float Separation, float Alignment, float Cohesion, float Goal,
    float Inertia, float ArrivalDistance, float HalfAngleDegrees, bool RequiresLineOfSight,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MovementDomain = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldValueExpression? CohesionAffinity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldValueExpression? AlignmentAffinity = null);

/// <summary>The fixed-point, allocation-free runtime arguments for one validated flock profile.</summary>
public sealed class FixedWorldFlockProfile {
    /// <summary>Compiles a validated profile at the document boundary.</summary>
    /// <param name="source">The authored profile.</param>
    /// <param name="navigation">The compiled domain table, required when MovementDomain is named.</param>
    public FixedWorldFlockProfile(WorldFlockProfile source, WorldNavigationDomainTable? navigation = null) {
        Source = source;
        MovementDomainIndex = source.MovementDomain is { } name
            ? navigation is not null && navigation.TryGetIndex(name, out var index) ? index
                : throw new ArgumentException($"Flock movement domain '{name}' is not declared.", nameof(source))
            : -1;
        Range = FixedQ4816.FromDouble(source.Range);
        ArrivalDistance = FixedQ4816.FromDouble(source.ArrivalDistance);
        MinimumDot = FixedQ4816.Cos(FixedQ4816.FromDouble(source.HalfAngleDegrees * (Math.PI / 180.0)));
        PeriodEngineTicks = WorldSimulationTickConversion.DurationTicks(source.UpdateSeconds, (uint)FixedTickConversion.TicksPerSecond);
        Weights = new FixedFlockWeights(FixedQ4816.FromDouble(source.SeparationRadius), FixedQ4816.FromDouble(source.Separation),
            FixedQ4816.FromDouble(source.Alignment), FixedQ4816.FromDouble(source.Cohesion), FixedQ4816.FromDouble(source.Goal), FixedQ4816.FromDouble(source.Inertia));
    }
    /// <summary>Gets the authored discrete limits and policies.</summary>
    public WorldFlockProfile Source { get; }
    /// <summary>Gets the volume/medium locomotion constraint's compiled domain index, or -1 when unbounded.</summary>
    public int MovementDomainIndex { get; }
    /// <summary>Gets the sensing radius.</summary>
    public FixedQ4816 Range { get; }
    /// <summary>Gets the goal stopping radius.</summary>
    public FixedQ4816 ArrivalDistance { get; }
    /// <summary>Gets the sensing cone's minimum forward dot product.</summary>
    public FixedQ4816 MinimumDot { get; }
    /// <summary>Gets the sensing period on the engine's exact clock, zero for every step.</summary>
    public ulong PeriodEngineTicks { get; }
    /// <summary>Gets the deterministic steering weights.</summary>
    public FixedFlockWeights Weights { get; }
}
