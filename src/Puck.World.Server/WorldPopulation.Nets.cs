using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    private WorldNets m_nets = WorldNets.Empty;

    /// <summary>Gets the authority's nets (<see cref="WorldNets"/>): one per placement carrying a net facet.</summary>
    public WorldNets Nets => m_nets;

    // Builds the nets the definition declares, keeping the nodes of every net whose row and layout stood, and hands them
    // to the analytic provider, whose net rows pose their colliders from them.
    private void BuildNets(WorldDefinition definition) {
        m_nets = WorldNets.Build(definition: definition, previous: m_nets);
    }

    /// <summary>Steps every net once, after the tick's bodies have moved: each body near a net that holds it loads it with
    /// its weight at its hold's anchor, and each of its collider volumes pushes the net's nodes out of it. A net no body
    /// touches sleeps (<see cref="FixedNet.Step"/>), so a sleeping net costs one box test per body.</summary>
    /// <param name="stepTicks">The step's length, in engine ticks.</param>
    /// <remarks>A body's push reaches past its collider by the net's strand and the contact reach, the band a body
    /// resting against the net's cell colliders stands in, so a body leaning on the net moves its nodes and one that only
    /// stands near it does not. A shrunk body loads the net with the cube of its size.</remarks>
    public void StepNets(ulong stepTicks) {
        foreach (var panel in m_nets.Panels) {
            var net = panel.Net;
            var near = net.RestBox.Grow(margin: ((net.Spec.Reach + panel.Strand) + m_contactReach));

            panel.Loads.Clear();
            panel.Pushing.Clear();
            for (var index = 0; (index < m_entries.Length); index++) {
                if (m_entries[index] is not { Active: true, Body: { } body } || body.ScaledColliderVolumes().IsEmpty) { continue; }
                if (!near.Grow(margin: body.BroadphaseRadius).Contains(point: body.FixedPosition)) { continue; }
                CollectHold(body: body, panel: panel);
                CollectPushers(body: body, panel: panel);
            }
            panel.Holders = panel.Loads.Count;
            panel.Pushers = panel.Pushing.Count;
            _ = net.Step(gravity: panel.Gravity, loads: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: panel.Loads),
                pushers: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: panel.Pushing), stepTicks: stepTicks);
        }
    }
    // A body holding a face of the net loads it at the hold's anchor (its place on the grid, within the net and within the
    // strand, the reach and the contact band of where the net stands there) with its weight, and with the pull its hands
    // take off the face: a body hanging with its middle a radius out from the face and its hands at its middle's height
    // above its feet balances that lever with an outward pull of its weight times radius over that height, along the hold's
    // normal (the feet's push back in is its volumes', CollectPushers).
    private void CollectHold(WorldBody body, WorldNets.Panel panel) {
        if ((body.HoldName is null) || (body.HoldNormal == FixedVector3.Zero)) { return; }
        if (!panel.TryLocate(column: out var column, contactReach: m_contactReach, point: body.HoldAnchor, row: out var row)) { return; }

        var size = body.Scale;
        var mass = ((FixedQ4816.FromDouble(value: panel.Facet.BodyMass) * size) * (size * size));
        var weight = (panel.Gravity * mass);
        var volumes = body.ScaledColliderVolumes();
        var pull = FixedVector3.Zero;

        if (volumes[0] is { Kind: FixedBodyColliderKind.Capsule } capsule) {
            var middle = ((capsule.Center.Y + capsule.Endpoint.Y) / FixedQ4816.FromInteger(value: 2));

            if (middle > FixedQ4816.Zero) { pull = (body.HoldNormal * ((weight.Length * capsule.Radius) / middle)); }
        }
        panel.Loads.Add(item: new FixedNetLoad(Column: column, Row: row, Force: (weight + pull)));
    }
    // Each collider volume of the body pushes the net's nodes out to its radius plus the strand and the contact reach.
    private void CollectPushers(WorldBody body, WorldNets.Panel panel) {
        var position = body.FixedPosition;
        var orientation = body.FixedOrientation;
        var margin = (panel.Strand + m_contactReach);

        foreach (ref readonly var volume in body.ScaledColliderVolumes()) {
            var start = (position + orientation.Rotate(vector: volume.Center));
            var (end, radius) = volume.Kind switch {
                FixedBodyColliderKind.Capsule => ((position + orientation.Rotate(vector: volume.Endpoint)), volume.Radius),
                FixedBodyColliderKind.Sphere => (start, volume.Radius),
                _ => (start, FixedVector3.MaxComponent(value: volume.HalfExtents)),
            };

            panel.Pushing.Add(item: new FixedNetPusher(End: end, Radius: (radius + margin), Start: start));
        }
    }
    private WorldNetNode[] m_netNodes = [];
    private int[] m_netChanged = [];

    /// <summary>Takes the nodes every net moved since the last take, or every node for a full take, as snapshot records
    /// (<see cref="WorldNetNode"/>), and forgets them.</summary>
    /// <param name="full">Whether to take every node of every net, as a snapshot that primes a reader does.</param>
    /// <returns>The nodes, in net then node order, borrowed until the next take.</returns>
    public ReadOnlyMemory<WorldNetNode> TakeNetNodes(bool full) {
        var nodes = m_nets.NodeCount;

        if (nodes == 0) { return default; }
        if (m_netNodes.Length < nodes) { m_netNodes = new WorldNetNode[nodes]; }

        var count = 0;

        foreach (var panel in m_nets.Panels) {
            var net = panel.Net;

            if (m_netChanged.Length < net.NodeCount) { m_netChanged = new int[net.NodeCount]; }

            var changed = net.TakeChanged(full: full, nodes: m_netChanged);
            var offsets = net.Offsets;

            for (var index = 0; (index < changed); index++) {
                var node = m_netChanged[index];

                m_netNodes[count++] = new WorldNetNode(Node: node, Offset: offsets[node], Placement: panel.Id);
            }
        }
        return m_netNodes.AsMemory(length: count, start: 0);
    }
    /// <summary>Folds every net's deterministic state into a hash (<see cref="WorldNets.AppendStateHash"/>).</summary>
    /// <param name="hash">The hash.</param>
    public void AppendNetStateHash(ref Fnv1aHash hash) => m_nets.AppendStateHash(hash: ref hash);
}
