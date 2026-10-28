using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldInputOwner {
    /// <summary>Recompiles one owner's current episode by semantic name, preserving unchanged held origins.</summary>
    /// <remarks>The tape retains the old definition. Retired names and its mapping ids do not enter the new
    /// definition's history; surviving names are translated before the new epoch barrier is installed.</remarks>
    public void InheritDefinition(WorldInputOwner previous, WorldDefinition oldDefinition, WorldDefinition definition,
        WorldInputInterval interval, ReadOnlySpan<ulong> consumedActivations = default) {
        var oldCatalog = previous.m_catalog;
        var channels = new int[oldCatalog.Channels.Length];

        Array.Fill(array: channels, value: -1);
        var keep = new bool[m_catalog.Channels.Length];
        var calibration = previous.Calibration;
        var timingContinues = ((calibration >= -m_calibrationLimit) && (calibration <= m_calibrationLimit));

        if (!timingContinues) { calibration = default; }
        var oldTuple = m_catalog.TryMapTuple(oldCatalog, previous.Mapping, out var oldInNew);
        var newTuple = oldCatalog.TryMapTuple(m_catalog, Mapping, out var newInOld);
        var tuples = (oldTuple && newTuple);

        for (var old = 0; (old < channels.Length); old++) {
            var declaration = oldCatalog.Channels[old];

            if (!m_catalog.TryGetLogicalId(declaration.Name, out var current)) { continue; }
            channels[old] = current;
            keep[current] = (timingContinues && tuples && WorldInputCatalog.SameAction(oldDefinition, definition, declaration.Name) &&
                oldCatalog.Keeps(previous.Mapping, newInOld!, ((ushort)old)) && m_catalog.Keeps(oldInNew!, Mapping, current));
        }
        bool SameBinding(WorldInputSource old, WorldInputSource current) => ((channels[old.Channel] == current.Channel) &&
            (old.Name == current.Name) && (old.Kind == current.Kind) && (old.Binding == current.Binding));
        bool SameSource(WorldInputSource old, WorldInputSource current) => (SameBinding(current: current, old: old) && keep[current.Channel]);
        var rebound = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var old in previous.m_sources) {
            if ((old.Source.Kind == WorldInputContributionKind.Level) && !m_sources.Any(predicate: row => SameSource(old.Source, row.Source))) {
                rebound.Add(item: old.Source.Name);
            }
        }
        foreach (var current in m_sources) {
            if ((current.Source.Kind == WorldInputContributionKind.Level) && !previous.m_sources.Any(predicate: row => SameSource(row.Source, current.Source))) {
                rebound.Add(item: current.Source.Name);
            }
        }
        var sources = new WorldInputSourceState[m_sources.Length];

        for (var index = 0; (index < sources.Length); index++) {
            var source = m_sources[index].Source;

            sources[index] = new(Activation: 0, AwaitingNeutral: true, RawValue: 0, Source: source);
            foreach (var old in previous.m_sources) {
                // A policy edit cancels a hold, not an already neutral, still-live binding. Preserve that
                // accepted neutral without making an untouched key require a gratuitous press/release.
                // An inactive destination or a held/rearming sibling cannot prove physical neutral.
                if (SameBinding(old.Source, source) && previous.Mapping.IsLive(logicalId: old.Source.Channel) && Mapping.IsLive(logicalId: source.Channel) &&
                    (old.RawValue == 0) && !old.AwaitingNeutral && !previous.m_sources.Any(predicate: sibling =>
                        ((sibling.Source.Name == source.Name) && ((sibling.RawValue != 0) || sibling.AwaitingNeutral)))) {
                    sources[index] = old with { Source = source };
                    break;
                }
                if (!SameSource(old.Source, source) || ((source.Kind == WorldInputContributionKind.Level) && rebound.Contains(item: source.Name))) { continue; }
                sources[index] = old with { Source = source };
                break;
            }
        }
        WorldInputRecord Translate(WorldInputRecord record, int channel) => record with {
            Event = record.Event with { Channel = ((ushort)channel) },
        };
        var holds = new List<WorldInputHold>();
        var inactive = new List<WorldInputHold>();
        var image = default(PlayerIntent);

        foreach (var old in previous.Mapping.Live) {
            var current = channels[old];
            var address = ((current < 0) ? -1 : Mapping.Address(logicalId: current));
            var priorAddress = previous.Mapping.Address(logicalId: old);

            if ((address < 0) || (priorAddress < 0)) { continue; }
            image = image.WithChannel(ordinal: address, value: previous.Image[priorAddress]);
        }
        foreach (var old in previous.m_holds) {
            if ((old.Press == 0) || (channels[old.Channel] < 0)) { continue; }
            var current = channels[old.Channel];
            var hold = old with { Channel = ((ushort)current), Origin = Translate(old.Origin, current) };

            if (Mapping.Address(logicalId: current) >= 0) { holds.Add(item: hold); } else { inactive.Add(item: hold); }
        }
        var history = new List<WorldInputRecord>();

        for (var index = 0; (index < previous.Count); index++) {
            var record = previous.Record(index: index);

            if (channels[record.Event.Channel] >= 0) { history.Add(item: Translate(record, channels[record.Event.Channel])); }
        }
        var retained = Math.Min(val1: history.Count, val2: Math.Max(val1: 0, val2: ((m_history.Length - holds.Count) - inactive.Count)));

        Restore(checkpoint: new(Revision, previous.Epoch, Mapping.Id, image, calibration, default, previous.RefusedBatches,
            0, previous.m_eventId, default, default, interval.Tick, 0, sources, holds.ToArray(),
            history.Skip(count: (history.Count - retained)).ToArray(), new bool[m_crossings.Length], [], []));
        Switch(Mapping, interval, consumedActivations: consumedActivations);
        var decisions = LastSwitch.Decisions.ToArray().ToList();

        foreach (var old in previous.Mapping.Live) {
            var current = channels[old];

            if ((current < 0) || keep[current] || decisions.Any(predicate: row => (row.Channel == current))) { continue; }
            decisions.Add(item: new(Channel: ((ushort)current), CurrentRaw: 0, HeldSince: default, HoldPress: 0, PreviousRaw: 0, Reason: WorldInputContinuationReason.Rebound));
        }
        LastSwitch = LastSwitch with { Decisions = decisions.ToArray() };
        m_switches[(((m_switchHead + m_switchCount) - 1) % m_switches.Length)] = LastSwitch;
        foreach (var hold in inactive) {
            var input = new WorldInputEvent(hold.Channel, WorldInputEventKind.Cancel, 0, interval.Tick, default, 0, 0);

            m_history[((m_head + m_count++) % m_history.Length)] = new(checked(++m_eventId), Epoch, input, interval, interval,
                hold.Press, Calibration: Calibration);
        }
    }
}
