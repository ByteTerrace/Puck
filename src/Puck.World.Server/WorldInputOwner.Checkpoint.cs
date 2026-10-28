using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The admitted owner leaf. Arrays are captured at checkpoint boundaries, never on steady input ticks.</summary>
public sealed record WorldInputOwnerCheckpoint(uint Revision, ulong Epoch, ushort Mapping, PlayerIntent Image,
    FixedQ4816 Calibration, WorldInputSwitch LastSwitch, ulong RefusedBatches, uint Sequence, ulong EventId,
    WorldInputInterval LastCapture, FixedQ4816 LastOffset, ulong Step, int StepEvents,
    WorldInputSourceState[] Sources, WorldInputHold[] Holds, WorldInputRecord[] History, bool[] Crossings, WorldInputClaim[] Claims,
    WorldInputSwitch[] Switches);
public sealed partial class WorldInputOwner {
    /// <summary>Captures future-decision state in canonical source and history order.</summary>
    public WorldInputOwnerCheckpoint Capture() {
        var history = new WorldInputRecord[m_count];

        for (var index = 0; (index < history.Length); index++) { history[index] = Record(index: index); }
        var switches = new WorldInputSwitch[m_switchCount];

        for (var index = 0; (index < switches.Length); index++) { switches[index] = SwitchRecord(index: index); }
        var claims = new List<WorldInputClaim>();

        for (var group = 0; (group < m_claimed.Length); group++) {
            foreach (var record in history) {
                if (m_claimed[group][((int)(record.Id % ((ulong)m_history.Length)))] == record.Id) { claims.Add(item: new(group, record.Id)); }
            }
        }
        return new(Revision, Epoch, Mapping.Id, Image, Calibration, LastSwitch, RefusedBatches, m_sequence, m_eventId,
            m_lastCapture, m_lastOffset, m_step, m_stepEvents, ((WorldInputSourceState[])m_sources.Clone()),
            m_holds.Where(predicate: static hold => (hold.Press != 0)).OrderBy(keySelector: static hold => hold.Channel).ToArray(), history, ((bool[])m_crossingHeld.Clone()), claims.ToArray(), switches);
    }
    /// <summary>Restores an admitted image, without attaching devices or synthesizing edges.</summary>
    public void Restore(WorldInputOwnerCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if ((checkpoint.Revision != Revision) || (checkpoint.Epoch == 0) || (checkpoint.Mapping >= m_catalog.Mappings.Length) ||
            (checkpoint.History.Length > m_history.Length) || (checkpoint.Switches.Length > m_switches.Length) || (checkpoint.Sources.Length != m_sources.Length) || (checkpoint.Crossings.Length != m_crossings.Length) ||
            (checkpoint.Holds.Length > ChannelLimits.MaxChannels) || (checkpoint.Calibration < -m_calibrationLimit) ||
            (checkpoint.Calibration > m_calibrationLimit) || (checkpoint.StepEvents < 0) || (checkpoint.StepEvents > m_policy.MaximumEventsPerStep)) {
            throw new ArgumentException(message: "input checkpoint violates admitted owner bounds", paramName: nameof(checkpoint));
        }
        var mapping = m_catalog.Mappings[checkpoint.Mapping];

        for (var index = 0; (index < m_sources.Length); index++) {
            if (checkpoint.Sources[index].Source != m_sources[index].Source) { throw new ArgumentException(message: "input checkpoint source registry differs", paramName: nameof(checkpoint)); }
        }
        Array.Clear(array: m_holdScratch);
        foreach (var hold in checkpoint.Holds) {
            if ((hold.Channel >= m_catalog.Channels.Length) || (mapping.Address(logicalId: hold.Channel) < 0) || (hold.Press == 0) ||
                (hold.Origin.Id != hold.Press) || (hold.Origin.Event.Kind != WorldInputEventKind.Press) ||
                (hold.Origin.Event.Channel != hold.Channel) || (m_holdScratch[mapping.Address(logicalId: hold.Channel)].Press != 0)) {
                throw new ArgumentException(message: "input checkpoint has an invalid or duplicate hold origin", paramName: nameof(checkpoint));
            }
            m_holdScratch[mapping.Address(logicalId: hold.Channel)] = hold;
        }
        var previousId = 0UL;

        foreach (var record in checkpoint.History) {
            if ((record.Id <= previousId) || (record.Id > checkpoint.EventId) || (record.Epoch > checkpoint.Epoch) ||
                (record.Event.Channel >= m_catalog.Channels.Length) || !record.Capture.Contains(offset: record.Event.Offset)) {
                throw new ArgumentException(message: "input checkpoint history is malformed", paramName: nameof(checkpoint));
            }
            previousId = record.Id;
        }
        var claimKeys = new HashSet<WorldInputClaim>();
        var switchEpoch = 0UL;

        foreach (var boundary in checkpoint.Switches) {
            if ((boundary.Epoch <= switchEpoch) || (boundary.Epoch > checkpoint.Epoch) || (boundary.From >= m_catalog.Mappings.Length) || (boundary.To >= m_catalog.Mappings.Length) ||
                (boundary.Decisions.Length > ChannelLimits.MaxChannels) || (boundary.ConsumedActivations.Length > WorldInputSwitch.MaximumConsumedActivations)) { throw new ArgumentException(message: "input checkpoint boundary is malformed", paramName: nameof(checkpoint)); }
            foreach (var decision in boundary.Decisions.Span) {
                if ((decision.Channel >= m_catalog.Channels.Length) || !Enum.IsDefined(value: decision.Reason)) { throw new ArgumentException(message: "input checkpoint continuation is malformed", paramName: nameof(checkpoint)); }
            }
            switchEpoch = boundary.Epoch;
        }
        foreach (var claim in checkpoint.Claims) {
            if ((((uint)claim.Group) >= ((uint)m_claimed.Length)) || !claimKeys.Add(item: claim) ||
                !checkpoint.History.Any(predicate: record => ((record.Epoch == checkpoint.Epoch) && (record.Id == claim.Event)))) {
                throw new ArgumentException(message: "input checkpoint claim is duplicate or outside retained history", paramName: nameof(checkpoint));
            }
        }
        Mapping = mapping;
        Epoch = checkpoint.Epoch;
        Image = checkpoint.Image;
        Calibration = checkpoint.Calibration;
        LastSwitch = checkpoint.LastSwitch;
        RefusedBatches = checkpoint.RefusedBatches;
        m_sequence = checkpoint.Sequence;
        m_eventId = checkpoint.EventId;
        m_lastCapture = checkpoint.LastCapture;
        m_lastOffset = checkpoint.LastOffset;
        m_step = checkpoint.Step;
        m_stepEvents = checkpoint.StepEvents;
        checkpoint.Sources.CopyTo(array: m_sources, index: 0);
        checkpoint.Crossings.CopyTo(array: m_crossingHeld, index: 0);
        m_holdScratch.CopyTo(array: m_holds, index: 0);
        Array.Clear(array: m_history);
        checkpoint.History.CopyTo(array: m_history, index: 0);
        m_head = 0;
        m_count = checkpoint.History.Length;
        Array.Clear(array: m_switches);
        checkpoint.Switches.CopyTo(array: m_switches, index: 0);
        m_switchHead = 0;
        m_switchCount = checkpoint.Switches.Length;
        foreach (var group in m_claimed) { Array.Clear(array: group); }
        foreach (var claim in checkpoint.Claims) { m_claimed[claim.Group][((int)(claim.Event % ((ulong)m_history.Length)))] = claim.Event; }
        Array.Clear(array: m_matchCached);
    }
    /// <summary>Hashes canonical future-decision state independently of ring storage layout.</summary>
    public void AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(value: Revision); hash.Add(value: Epoch); hash.Add(value: Mapping.Id);
        for (var index = 0; (index < ChannelLimits.MaxAxes); index++) { hash.Add(value: Image.Channels.Axes[index]); }
        hash.Add(value: Image.Channels.Buttons);
        hash.Add(value: ((byte)(Image.SourceRay.HasValue ? 1 : 0)));
        if (Image.SourceRay is { } ray) {
            hash.Add(value: ray.Origin.X.Value); hash.Add(value: ray.Origin.Y.Value); hash.Add(value: ray.Origin.Z.Value);
            hash.Add(value: ray.Direction.X.Value); hash.Add(value: ray.Direction.Y.Value); hash.Add(value: ray.Direction.Z.Value);
        }
        hash.Add(value: Calibration.Value); hash.Add(value: RefusedBatches); hash.Add(value: m_sequence); hash.Add(value: m_eventId);
        AddInterval(hash: ref hash, interval: m_lastCapture); hash.Add(value: m_lastOffset.Value); hash.Add(value: m_step); hash.Add(value: ((uint)m_stepEvents));
        AddSwitch(ref hash, LastSwitch);
        hash.Add(value: ((uint)m_switchCount));
        for (var index = 0; (index < m_switchCount); index++) { AddSwitch(ref hash, SwitchRecord(index: index)); }
        hash.Add(value: ((uint)m_sources.Length));
        foreach (var source in m_sources) {
            hash.Add(value: source.Source.Handle); hash.Add(value: source.Source.Channel); hash.Add(value: ((byte)source.Source.Kind));
            hash.Add(value: source.Source.Name); hash.Add(value: source.Source.Binding);
            hash.Add(value: source.RawValue); hash.Add(value: source.Activation); hash.Add(value: ((byte)(source.AwaitingNeutral ? 1 : 0)));
        }
        foreach (var hold in m_holds) {
            hash.Add(value: hold.Press);
            if (hold.Press == 0) { continue; }
            hash.Add(value: hold.Channel); hash.Add(value: ((byte)(hold.Consumed ? 1 : 0))); AddRecord(hash: ref hash, record: hold.Origin);
        }
        hash.Add(value: ((uint)m_count));
        for (var index = 0; (index < m_count); index++) { AddRecord(hash: ref hash, record: Record(index: index)); }
        hash.Add(value: ((uint)m_crossingHeld.Length));
        foreach (var held in m_crossingHeld) { hash.Add(value: ((byte)(held ? 1 : 0))); }
        hash.Add(value: ((uint)m_claimed.Length));
        for (var group = 0; (group < m_claimed.Length); group++) {
            for (var index = 0; (index < m_count); index++) {
                var record = Record(index: index);

                hash.Add(value: ((byte)((m_claimed[group][((int)(record.Id % ((ulong)m_history.Length)))] == record.Id) ? 1 : 0)));
            }
        }
    }

    private static void AddInterval(ref Fnv1aHash hash, WorldInputInterval interval) {
        interval.AppendStateHash(hash: ref hash);
    }
    private static void AddSwitch(ref Fnv1aHash hash, WorldInputSwitch boundary) {
        boundary.Interval.AppendStateHash(hash: ref hash);
        hash.Add(value: boundary.Epoch); hash.Add(value: boundary.From); hash.Add(value: boundary.To);
        hash.Add(value: ((uint)boundary.ConsumedActivations.Length));
        foreach (var activation in boundary.ConsumedActivations.Span) { hash.Add(value: activation); }
        hash.Add(value: ((byte)(boundary.Reset ? 1 : 0)));
        hash.Add(value: ((uint)boundary.Decisions.Length));
        foreach (var decision in boundary.Decisions.Span) {
            hash.Add(value: decision.Channel); hash.Add(value: decision.PreviousRaw); hash.Add(value: decision.CurrentRaw); hash.Add(value: ((byte)decision.Reason));
            hash.Add(value: decision.HoldPress); hash.Add(value: decision.HeldSince.Value);
        }
    }
    private static void AddRecord(ref Fnv1aHash hash, WorldInputRecord record) {
        hash.Add(value: record.Id); hash.Add(value: record.Epoch); hash.Add(value: record.Event.Channel); hash.Add(value: ((byte)record.Event.Kind));
        hash.Add(value: record.Event.RawValue); hash.Add(value: record.Event.CaptureTick); hash.Add(value: record.Event.Offset.Value);
        hash.Add(value: record.Event.Sequence); hash.Add(value: record.Event.Activation); AddInterval(hash: ref hash, interval: record.Capture);
        AddInterval(hash: ref hash, interval: record.Admission); hash.Add(value: record.HoldPress); hash.Add(value: record.Crossing); hash.Add(value: record.Calibration.Value);
    }
}
