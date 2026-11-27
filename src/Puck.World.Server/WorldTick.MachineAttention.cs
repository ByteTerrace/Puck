using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

// MACHINES FOLLOW ATTENTION. A machine that authors an attention (WorldMachine.Attention) is advanced only on the ticks
// it is attended, and is suspended on every other: its clock stops and it resumes from the state it held.
//
// The invariant: whether a machine advances on a tick is a function of that tick's authority state, so two runs of one
// document and one input advance each machine on the same ticks, whatever either run shows or whether it shows
// anything. What enforces it:
// - One reading. MachineAttended is the only place a machine's attention is derived. It reads the document's screens,
//   where the bodies humans occupy stand (the population's occupied image, in fixed point) and the grant table's
//   control applications. This assembly references no presentation, so no camera, frustum or frame can enter it.
// - One door. GateMachines is the only caller of IWorldMachineHost.GateRunning, and the host advances an instance only
//   through the verdicts that call leaves it.
// - Nothing stored. The verdict is derived on every tick and kept by no checkpoint, tape or hash, as RunsWhen's is.
// Where it stops: an observer in another world (a session looking in through a painting or a portal) attends nothing
// here, since how near anyone stands to its surface is a fact of the observing world, which this tick does not read.
public sealed partial class WorldTick {
    // A screen that shows a machine, and the way its face looks (WorldScreen.FixedFacing).
    private readonly record struct MachineSite(WorldScreen Screen, FixedVector3 Facing);

    // The screens that show each machine, by instance name, as of the screens list they were read from.
    private readonly Dictionary<string, List<MachineSite>> m_machineSites = new(comparer: StringComparer.Ordinal);
    private IReadOnlyList<WorldScreen>? m_machineSiteScreens;
    // The principals engaged on one screen, reused across ticks.
    private readonly List<Principal> m_machineEngagers = [];

    // Whether anyone attends a machine this tick: a body a human occupies stands within its attention's reach of a
    // screen that shows it, on the side the screen faces where the attention says so, or a participant holds a control
    // application on one. The occupied image is read once a tick, by the first machine that asks, after the
    // population's advance: occupiedRead carries that across a sweep.
    private bool MachineAttended(WorldDefinition definition, string machine, WorldMachineAttention attention, ref bool occupiedRead) {
        if (!ReferenceEquals(objA: m_machineSiteScreens, objB: definition.Screens)) {
            IndexMachineSites(screens: definition.Screens);
        }
        if (!m_machineSites.TryGetValue(key: machine, value: out var sites)) {
            return false;
        }
        if (!occupiedRead) {
            Host.Population.ReadOccupied();
            occupiedRead = true;
        }

        var reach = FixedQ4816.FromDouble(value: attention.Within);

        foreach (var site in sites) {
            if (!TryEngageCentre(centre: out var centre, holder: out var holder, screen: site.Screen)) {
                continue;
            }
            // A carried screen stands where its holder does, so no side of it is behind.
            if (((attention.Behind is { } behind) && (holder < 0))
                ? (Host.Population.NearestOccupiedBeforeSquared(behind: FixedQ4816.FromDouble(value: behind), facing: site.Facing, point: in centre, reach: reach) is not null)
                : (Host.Population.NearestOccupiedSquared(point: in centre, reach: reach) is not null)
            ) {
                return true;
            }
        }
        foreach (var site in sites) {
            Host.GrantTable.CollectApplicationHolders(
                into: m_machineEngagers,
                target: GrantSubject.Screen(index: site.Screen.Index)
            );
            if (m_machineEngagers.Count != 0) {
                m_machineEngagers.Clear();

                return true;
            }
        }

        return false;
    }
    private void IndexMachineSites(IReadOnlyList<WorldScreen> screens) {
        m_machineSiteScreens = screens;
        m_machineSites.Clear();
        for (var index = 0; (index < screens.Count); index++) {
            if (screens[index] is not { Source: WorldScreenSource.Machine source } screen) { continue; }
            if (!m_machineSites.TryGetValue(key: source.Instance, value: out var sites)) {
                m_machineSites.Add(key: source.Instance, value: (sites = []));
            }
            sites.Add(item: new MachineSite(Facing: screen.FixedFacing(), Screen: screen));
        }
    }
}
