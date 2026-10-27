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
        for (var index = 0; index < history.Length; index++) { history[index] = Record(index); }
        var switches = new WorldInputSwitch[m_switchCount];
        for (var index = 0; index < switches.Length; index++) { switches[index] = SwitchRecord(index); }
        var claims = new List<WorldInputClaim>();
        for (var group = 0; group < m_claimed.Length; group++) {
            foreach (var record in history) {
                if (m_claimed[group][(int)(record.Id % (ulong)m_history.Length)] == record.Id) { claims.Add(new(group, record.Id)); }
            }
        }
        return new(Revision, Epoch, Mapping.Id, Image, Calibration, LastSwitch, RefusedBatches, m_sequence, m_eventId,
            m_lastCapture, m_lastOffset, m_step, m_stepEvents, (WorldInputSourceState[])m_sources.Clone(),
            m_holds.Where(static hold => hold.Press != 0).OrderBy(static hold => hold.Channel).ToArray(), history, (bool[])m_crossingHeld.Clone(), claims.ToArray(), switches);
    }

    /// <summary>Restores an admitted image, without attaching devices or synthesizing edges.</summary>
    public void Restore(WorldInputOwnerCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Revision != Revision || checkpoint.Epoch == 0 || checkpoint.Mapping >= m_catalog.Mappings.Length ||
            checkpoint.History.Length > m_history.Length || checkpoint.Switches.Length > m_switches.Length || checkpoint.Sources.Length != m_sources.Length || checkpoint.Crossings.Length != m_crossings.Length ||
            checkpoint.Holds.Length > ChannelLimits.MaxChannels || checkpoint.Calibration < -m_calibrationLimit ||
            checkpoint.Calibration > m_calibrationLimit || checkpoint.StepEvents < 0 || checkpoint.StepEvents > m_policy.MaximumEventsPerStep) {
            throw new ArgumentException("input checkpoint violates admitted owner bounds", nameof(checkpoint));
        }
        var mapping = m_catalog.Mappings[checkpoint.Mapping];
        for (var index = 0; index < m_sources.Length; index++) {
            if (checkpoint.Sources[index].Source != m_sources[index].Source) { throw new ArgumentException("input checkpoint source registry differs", nameof(checkpoint)); }
        }
        Array.Clear(m_holdScratch);
        foreach (var hold in checkpoint.Holds) {
            if (hold.Channel >= m_catalog.Channels.Length || mapping.Address(hold.Channel) < 0 || hold.Press == 0 ||
                hold.Origin.Id != hold.Press || hold.Origin.Event.Kind != WorldInputEventKind.Press ||
                hold.Origin.Event.Channel != hold.Channel || m_holdScratch[mapping.Address(hold.Channel)].Press != 0) {
                throw new ArgumentException("input checkpoint has an invalid or duplicate hold origin", nameof(checkpoint));
            }
            m_holdScratch[mapping.Address(hold.Channel)] = hold;
        }
        var previousId = 0UL;
        foreach (var record in checkpoint.History) {
            if (record.Id <= previousId || record.Id > checkpoint.EventId || record.Epoch > checkpoint.Epoch ||
                record.Event.Channel >= m_catalog.Channels.Length || !record.Capture.Contains(record.Event.Offset)) {
                throw new ArgumentException("input checkpoint history is malformed", nameof(checkpoint));
            }
            previousId = record.Id;
        }
        var claimKeys = new HashSet<WorldInputClaim>();
        var switchEpoch = 0UL;
        foreach (var boundary in checkpoint.Switches) {
            if (boundary.Epoch <= switchEpoch || boundary.Epoch > checkpoint.Epoch || boundary.From >= m_catalog.Mappings.Length || boundary.To >= m_catalog.Mappings.Length ||
                boundary.Decisions.Length > ChannelLimits.MaxChannels || boundary.ConsumedActivations.Length > WorldInputSwitch.MaximumConsumedActivations) { throw new ArgumentException("input checkpoint boundary is malformed", nameof(checkpoint)); }
            foreach (var decision in boundary.Decisions.Span) {
                if (decision.Channel >= m_catalog.Channels.Length || !Enum.IsDefined(decision.Reason)) { throw new ArgumentException("input checkpoint continuation is malformed", nameof(checkpoint)); }
            }
            switchEpoch = boundary.Epoch;
        }
        foreach (var claim in checkpoint.Claims) {
            if ((uint)claim.Group >= (uint)m_claimed.Length || !claimKeys.Add(claim) ||
                !checkpoint.History.Any(record => record.Epoch == checkpoint.Epoch && record.Id == claim.Event)) {
                throw new ArgumentException("input checkpoint claim is duplicate or outside retained history", nameof(checkpoint));
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
        checkpoint.Sources.CopyTo(m_sources, 0);
        checkpoint.Crossings.CopyTo(m_crossingHeld, 0);
        m_holdScratch.CopyTo(m_holds, 0);
        Array.Clear(m_history);
        checkpoint.History.CopyTo(m_history, 0);
        m_head = 0;
        m_count = checkpoint.History.Length;
        Array.Clear(m_switches);
        checkpoint.Switches.CopyTo(m_switches, 0);
        m_switchHead = 0;
        m_switchCount = checkpoint.Switches.Length;
        foreach (var group in m_claimed) { Array.Clear(group); }
        foreach (var claim in checkpoint.Claims) { m_claimed[claim.Group][(int)(claim.Event % (ulong)m_history.Length)] = claim.Event; }
        Array.Clear(m_matchCached);
    }

    /// <summary>Hashes canonical future-decision state independently of ring storage layout.</summary>
    public void AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(Revision); hash.Add(Epoch); hash.Add(Mapping.Id);
        for (var index = 0; index < ChannelLimits.MaxAxes; index++) { hash.Add(Image.Channels.Axes[index]); }
        hash.Add(Image.Channels.Buttons);
        hash.Add((byte)(Image.SourceRay.HasValue ? 1 : 0));
        if (Image.SourceRay is { } ray) {
            hash.Add(ray.Origin.X.Value); hash.Add(ray.Origin.Y.Value); hash.Add(ray.Origin.Z.Value);
            hash.Add(ray.Direction.X.Value); hash.Add(ray.Direction.Y.Value); hash.Add(ray.Direction.Z.Value);
        }
        hash.Add(Calibration.Value); hash.Add(RefusedBatches); hash.Add(m_sequence); hash.Add(m_eventId);
        AddInterval(ref hash, m_lastCapture); hash.Add(m_lastOffset.Value); hash.Add(m_step); hash.Add((uint)m_stepEvents);
        AddSwitch(ref hash, LastSwitch);
        hash.Add((uint)m_switchCount);
        for (var index = 0; index < m_switchCount; index++) { AddSwitch(ref hash, SwitchRecord(index)); }
        hash.Add((uint)m_sources.Length);
        foreach (var source in m_sources) {
            hash.Add(source.Source.Handle); hash.Add(source.Source.Channel); hash.Add((byte)source.Source.Kind);
            hash.Add(source.Source.Name); hash.Add(source.Source.Binding);
            hash.Add(source.RawValue); hash.Add(source.Activation); hash.Add((byte)(source.AwaitingNeutral ? 1 : 0));
        }
        foreach (var hold in m_holds) {
            hash.Add(hold.Press);
            if (hold.Press == 0) { continue; }
            hash.Add(hold.Channel); hash.Add((byte)(hold.Consumed ? 1 : 0)); AddRecord(ref hash, hold.Origin);
        }
        hash.Add((uint)m_count);
        for (var index = 0; index < m_count; index++) { AddRecord(ref hash, Record(index)); }
        hash.Add((uint)m_crossingHeld.Length);
        foreach (var held in m_crossingHeld) { hash.Add((byte)(held ? 1 : 0)); }
        hash.Add((uint)m_claimed.Length);
        for (var group = 0; group < m_claimed.Length; group++) {
            for (var index = 0; index < m_count; index++) {
                var record = Record(index);
                hash.Add((byte)(m_claimed[group][(int)(record.Id % (ulong)m_history.Length)] == record.Id ? 1 : 0));
            }
        }
    }
    private static void AddInterval(ref Fnv1aHash hash, WorldInputInterval interval) {
        interval.AppendStateHash(ref hash);
    }
    private static void AddSwitch(ref Fnv1aHash hash, WorldInputSwitch boundary) {
        boundary.Interval.AppendStateHash(ref hash);
        hash.Add(boundary.Epoch); hash.Add(boundary.From); hash.Add(boundary.To);
        hash.Add((uint)boundary.ConsumedActivations.Length);
        foreach (var activation in boundary.ConsumedActivations.Span) { hash.Add(activation); }
        hash.Add((byte)(boundary.Reset ? 1 : 0));
        hash.Add((uint)boundary.Decisions.Length);
        foreach (var decision in boundary.Decisions.Span) {
            hash.Add(decision.Channel); hash.Add(decision.PreviousRaw); hash.Add(decision.CurrentRaw); hash.Add((byte)decision.Reason);
            hash.Add(decision.HoldPress); hash.Add(decision.HeldSince.Value);
        }
    }
    private static void AddRecord(ref Fnv1aHash hash, WorldInputRecord record) {
        hash.Add(record.Id); hash.Add(record.Epoch); hash.Add(record.Event.Channel); hash.Add((byte)record.Event.Kind);
        hash.Add(record.Event.RawValue); hash.Add(record.Event.CaptureTick); hash.Add(record.Event.Offset.Value);
        hash.Add(record.Event.Sequence); hash.Add(record.Event.Activation); AddInterval(ref hash, record.Capture);
        AddInterval(ref hash, record.Admission); hash.Add(record.HoldPress); hash.Add(record.Crossing); hash.Add(record.Calibration.Value);
    }
}
