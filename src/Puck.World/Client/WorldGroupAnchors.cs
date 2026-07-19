using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.World.Client;

/// <summary>
/// Resolves every <see cref="WorldAnchor.Group"/> once per frame into its smoothed <c>(Centroid, Spread)</c> — the
/// establishing-shot anchor's live pose. The centroid is exponentially smoothed at the anchor's <c>SmoothRate</c> against
/// the presentation delta and seeded un-smoothed on first resolve (so a camera does not fly in from the origin); the
/// spread (mean distance from the centroid) is what <see cref="WorldRig.Chase"/>'s <c>SpreadPullback</c> consumes to
/// widen as the group scatters. Presentation-only, client-side, no simulation feedback.
/// </summary>
internal sealed class WorldGroupAnchors {
    private readonly Dictionary<string, State> m_states = new(comparer: StringComparer.Ordinal);

    /// <summary>Resolves a group anchor's smoothed pose, keyed by <paramref name="key"/> (the consuming camera's name, so
    /// each camera smooths its own state). The first resolve for a key seeds from the raw value.</summary>
    /// <param name="key">The smoothing-state key (the consuming camera name).</param>
    /// <param name="group">The group anchor.</param>
    /// <param name="client">The pose source (entity positions + active mask).</param>
    /// <param name="maxPopulation">The entity-table ceiling.</param>
    /// <param name="deltaSeconds">The presentation delta since the last resolve.</param>
    /// <returns>The smoothed centroid and spread.</returns>
    public (Vector3 Centroid, float Spread) Resolve(string key, WorldAnchor.Group group, WorldClient client, int maxPopulation, float deltaSeconds) {
        var (rawCentroid, rawSpread) = ComputeRaw(group: group, client: client, maxPopulation: maxPopulation);

        ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_states, key: key, exists: out _);

        if (!state.Seeded) {
            state.Centroid = rawCentroid;
            state.Spread = rawSpread;
            state.Seeded = true;

            return (state.Centroid, state.Spread);
        }

        // Frame-rate-independent exponential ease: a = 1 - e^(-rate * dt).
        var alpha = (1f - MathF.Exp(x: (-MathF.Max(x: group.SmoothRate, y: 0f) * MathF.Max(x: deltaSeconds, y: 0f))));

        state.Centroid = Vector3.Lerp(value1: state.Centroid, value2: rawCentroid, amount: alpha);
        state.Spread += ((rawSpread - state.Spread) * alpha);

        return (state.Centroid, state.Spread);
    }

    /// <summary>The un-smoothed centroid + spread of a group's active members (empty set → origin, zero spread) — the
    /// one-shot resolve a binder-filmed group camera seeds from.</summary>
    /// <param name="group">The group anchor.</param>
    /// <param name="client">The pose source.</param>
    /// <param name="maxPopulation">The entity-table ceiling.</param>
    /// <returns>The raw centroid and spread.</returns>
    public static (Vector3 Centroid, float Spread) ComputeRaw(WorldAnchor.Group group, WorldClient client, int maxPopulation) {
        var sum = Vector3.Zero;
        var count = 0;

        if (group.Indices is { } indices) {
            foreach (var index in indices) {
                if (((uint)index < (uint)maxPopulation) && client.IsActive(index: index)) {
                    sum += client.Position(index: index);
                    count++;
                }
            }
        } else {
            for (var index = 0; (index < maxPopulation); index++) {
                if (client.IsActive(index: index)) {
                    sum += client.Position(index: index);
                    count++;
                }
            }
        }

        if (count == 0) {
            return (Vector3.Zero, 0f);
        }

        var centroid = (sum / count);
        var spread = 0f;

        if (group.Indices is { } members) {
            foreach (var index in members) {
                if (((uint)index < (uint)maxPopulation) && client.IsActive(index: index)) {
                    spread += Vector3.Distance(value1: client.Position(index: index), value2: centroid);
                }
            }
        } else {
            for (var index = 0; (index < maxPopulation); index++) {
                if (client.IsActive(index: index)) {
                    spread += Vector3.Distance(value1: client.Position(index: index), value2: centroid);
                }
            }
        }

        return (centroid, (spread / count));
    }

    private struct State {
        public Vector3 Centroid;
        public float Spread;
        public bool Seeded;
    }
}
