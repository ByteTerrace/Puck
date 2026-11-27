using System.Numerics;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>The net node offsets a presentation draws a net by (<see cref="WorldNetNode"/>): for each net, every node's
/// offset at the latest delivered tick and at the tick before it, so a frame between two ticks reads them at the same
/// interpolation fraction as its body poses.</summary>
public sealed class WorldNetMirror {
    private readonly Dictionary<string, (Vector3[] Previous, Vector3[] Current)> m_nets = new(comparer: StringComparer.Ordinal);

    /// <summary>Copies one tick's delivered nodes: every net's current offsets become its previous ones, then each
    /// delivered node takes its new offset. A net first seen holds the most nodes a net takes.</summary>
    /// <param name="nodes">The tick's delivered nodes.</param>
    public void Deliver(ReadOnlySpan<WorldNetNode> nodes) {
        foreach (var (previous, current) in m_nets.Values) { current.CopyTo(array: previous, index: 0); }
        if (nodes.IsEmpty) { return; }
        foreach (var node in nodes) {
            if (((uint)node.Node) >= (uint)(WorldPlacementNet.MaxNodesPerEdge * WorldPlacementNet.MaxNodesPerEdge)) { continue; }
            if (!m_nets.TryGetValue(key: node.Placement, value: out var net)) {
                net = (new Vector3[(WorldPlacementNet.MaxNodesPerEdge * WorldPlacementNet.MaxNodesPerEdge)], new Vector3[(WorldPlacementNet.MaxNodesPerEdge * WorldPlacementNet.MaxNodesPerEdge)]);
                m_nets.Add(key: node.Placement, value: net);
            }
            net.Current[node.Node] = node.Offset.ToVector3();
        }
    }
    /// <summary>Reads a net's node offsets at an interpolation fraction between the previous delivered tick and the
    /// latest, or zero for a net no delivery has named.</summary>
    /// <param name="placementId">The placement carrying the net.</param>
    /// <param name="alpha">The fraction, from 0 at the previous tick to 1 at the latest.</param>
    /// <param name="destination">Receives one offset per node, row by row.</param>
    public void Read(string placementId, float alpha, Span<Vector3> destination) {
        if (!m_nets.TryGetValue(key: placementId, value: out var net)) {
            destination.Clear();
            return;
        }
        var count = Math.Min(val1: destination.Length, val2: net.Current.Length);

        for (var node = 0; (node < count); node++) { destination[node] = Vector3.Lerp(value1: net.Previous[node], value2: net.Current[node], amount: alpha); }
    }
}
