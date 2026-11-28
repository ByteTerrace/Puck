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
        m_nets = WorldNets.Build(contactReach: m_contactReach, definition: definition, previous: m_nets);
    }

    /// <summary>Steps every net once, after the tick's bodies have moved: each body near a net that holds it hangs its
    /// mass on it with its weight and its lever (<see cref="FixedNetLoad"/>), and each volume of a body near it that does
    /// not hold it pushes the net's nodes out of it. A net whose touch stays the same sleeps (<see cref="FixedNet.Step"/>),
    /// so with no body near any net a tick costs one box test per active body, and a sleeping net one fingerprint.</summary>
    /// <param name="definition">The document whose state a net's opening cell reads (<see cref="WorldNetOpening.By"/>).</param>
    /// <param name="tick">The authority tick the step lands on.</param>
    /// <param name="engineTick">The engine tick the step starts at, which an advancing cell reads.</param>
    /// <param name="stepTicks">The step's length, in engine ticks.</param>
    /// <remarks>A net with an opening reads its cell first and is told to open while it reads nonzero, so a net that is
    /// parting or closing steps awake until it settles. A body's push reaches past its collider by the net's strand and the contact reach, the band a body
    /// resting against the net's cell colliders stands in, so a body leaning on the net moves its nodes, and one that only
    /// stands near it neither moves nor wakes it (<see cref="FixedNet.Pressers"/>). A body holding the net presses it only
    /// through its load, never as a pusher: its hold keeps it its standoff from the face it rides, so a push from its
    /// volumes would move the face it then follows, a tick at a time, until the nodes met their reach whatever the body
    /// weighed. A shrunk body loads the net with the cube of its size.</remarks>
    public void StepNets(WorldDefinition definition, ulong tick, ulong engineTick, ulong stepTicks) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var panels = m_nets.Panels;

        if (panels.IsEmpty) { return; }
        foreach (var panel in panels) {
            if (panel.Facet.Opening is { } opening) {
                panel.Net.SetOpening(open: (opening.By.ExactAt(definition: definition, engineTick: engineTick, fallback: FixedQ4816.Zero, tick: tick) != FixedQ4816.Zero));
            }
        }
        m_netBodies.Clear();
        for (var index = 0; (index < m_entries.Length); index++) {
            if (m_entries[index] is not { Active: true, Body: { } body } || body.ScaledColliderVolumes().IsEmpty) { continue; }
            if (m_nets.Near.Grow(margin: body.BroadphaseRadius).Contains(point: body.FixedPosition)) { m_netBodies.Add(item: body); }
        }
        foreach (var panel in panels) {
            panel.Loads.Clear();
            panel.Pushing.Clear();
            panel.Holding = 0;
            foreach (var body in m_netBodies) {
                if (!panel.Near.Grow(margin: body.BroadphaseRadius).Contains(point: body.FixedPosition)) { continue; }
                if (!CollectHold(body: body, panel: panel)) { CollectPushers(body: body, panel: panel); }
            }
            _ = panel.Net.Step(gravity: panel.Gravity, loads: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: panel.Loads),
                pushers: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: panel.Pushing), stepTicks: stepTicks);
            panel.Holders = panel.Holding;
            panel.Pushers = panel.Net.Pressers;
        }
    }
    // The active bodies near any net this tick, gathered once so each net tests only them.
    private readonly List<WorldBody> m_netBodies = [];

    // A body holding a face of the net (its hold's anchor on the grid, within the net and within the strand, the reach
    // and the contact band of where the net stands there) hangs on it at its hands and, where they stand on the net, its
    // feet. A body hanging with its middle a radius out from the face and its hands at its middle's height above its feet
    // balances that lever with an outward pull of its weight times radius over that height along the hold's normal and an
    // equal push in at its feet, so the net carries the weight alone, bellying out under the hands and in under the feet.
    // The body is lumped at its two contacts, half its mass and weight at each, so every node a load moves carries the
    // mass that load's force accelerates; with its feet off the net, its hands carry it whole and the lever's push goes
    // to whatever the feet stand on.
    private bool CollectHold(WorldBody body, WorldNets.Panel panel) {
        if ((body.HoldName is null) || (body.HoldNormal == FixedVector3.Zero)) { return false; }
        if (!panel.TryLocate(column: out var column, contactReach: m_contactReach, point: body.HoldAnchor, row: out var row)) { return false; }

        var size = body.Scale;
        var mass = ((FixedQ4816.FromDouble(value: panel.Facet.BodyMass) * size) * (size * size));
        var weight = (panel.Gravity * mass);
        var volumes = body.ScaledColliderVolumes();
        var pull = FixedVector3.Zero;

        if (volumes[0] is { Kind: FixedBodyColliderKind.Capsule } capsule) {
            var middle = ((capsule.Center.Y + capsule.Endpoint.Y) / FixedQ4816.FromInteger(value: 2));

            if (middle > FixedQ4816.Zero) { pull = (body.HoldNormal * ((weight.Length * capsule.Radius) / middle)); }
        }
        panel.Holding++;
        if ((pull != FixedVector3.Zero) && panel.TryLocateOnGrid(column: out var footColumn, point: body.FixedPosition, row: out var footRow)) {
            var half = (mass / FixedQ4816.FromInteger(value: 2));
            var halfWeight = (weight / FixedQ4816.FromInteger(value: 2));

            panel.Loads.Add(item: new FixedNetLoad(Column: column, Force: (halfWeight + pull), Mass: half, Row: row));
            panel.Loads.Add(item: new FixedNetLoad(Column: footColumn, Force: (halfWeight - pull), Mass: half, Row: footRow));
            return true;
        }
        panel.Loads.Add(item: new FixedNetLoad(Column: column, Force: (weight + pull), Mass: mass, Row: row));
        return true;
    }
    // Each collider volume of the body pushes the strands it meets until they stand its radius, half the strand and the
    // contact reach off it: the band a body resting against a strand stands in.
    private void CollectPushers(WorldBody body, WorldNets.Panel panel) {
        var position = body.FixedPosition;
        var orientation = body.FixedOrientation;
        var margin = ((panel.Strand / FixedQ4816.FromInteger(value: 2)) + m_contactReach);

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
