using Puck.Hosting;

namespace Puck.World.Client;

/// <summary>A named clock's session-only presentation controls.</summary>
public enum WorldTimelineOperation {
    /// <summary>Keep the current presentation reading.</summary>
    Hold,
    /// <summary>Continue from the held tick at the selected rate.</summary>
    Run,
    /// <summary>Present an exact engine tick and hold it.</summary>
    At,
    /// <summary>Change the nonnegative presentation rate without moving the current reading.</summary>
    Rate,
}
public sealed partial class WorldStateMirror {
    private readonly Dictionary<string, ClockPreview> m_clockPreviews = new(comparer: StringComparer.Ordinal);

    private sealed class ClockPreview {
        public PresentedTick Origin;
        public PresentedTick Tick;
        public double Rate = 1d;
        public bool Held;
        public bool HasPhase;
        public double Phase;
    }

    /// <summary>Returns a named clock's session tick, leaving the mirror's state presentation tick untouched.</summary>
    /// <param name="name">The authored clock name.</param>
    /// <returns>The preview tick, or the ordinary presented tick.</returns>
    public PresentedTick ClockTick(string name) => (m_clockPreviews.TryGetValue(key: name, value: out var preview)
        ? PreviewTick(preview: preview)
        : Presented);
    /// <summary>Returns whether a named clock is held and its selected rate.</summary>
    /// <param name="name">The authored clock name.</param>
    /// <param name="rate">The selected nonnegative rate, one without a preview.</param>
    /// <returns>Whether the clock is held.</returns>
    public bool ClockHeld(string name, out double rate) {
        var found = m_clockPreviews.TryGetValue(key: name, value: out var preview);

        rate = (found ? preview!.Rate : 1d);
        return (found && preview!.Held);
    }
    /// <summary>Controls one presentation clock. No document, state slot, authoritative tick or replay is written.</summary>
    /// <param name="name">The clock name in the installed presentation manifest.</param>
    /// <param name="operation">The control to apply.</param>
    /// <param name="tick">The exact engine tick for <see cref="WorldTimelineOperation.At"/>.</param>
    /// <param name="rate">The finite nonnegative rate for <see cref="WorldTimelineOperation.Rate"/>.</param>
    /// <param name="refusal">The reason an invalid control was left unapplied.</param>
    /// <returns>Whether the control was applied.</returns>
    public bool ControlClock(string name, WorldTimelineOperation operation, ulong tick, double rate, out string? refusal) {
        refusal = null;
        if (!Manifest.TryClock(clock: out var clock, name: name)) {
            refusal = $"unknown clock '{name}'";
            return false;
        }
        if (!Enum.IsDefined(value: operation) || !double.IsFinite(d: rate) || (rate < 0d)) {
            refusal = "expected a clock operation and a finite nonnegative rate";
            return false;
        }
        var currentTick = ClockTick(name: name);
        var hasPhase = TryReadPhase(clock: out _, name: name, phase: out var phase);
        var preview = (m_clockPreviews.GetValueOrDefault(key: name) ?? new ClockPreview());

        preview.Tick = ((operation == WorldTimelineOperation.At) ? new PresentedTick(Fraction: 0d, Whole: tick) : currentTick);
        preview.Origin = Presented;
        preview.Held = operation switch {
            WorldTimelineOperation.Hold or WorldTimelineOperation.At => true,
            WorldTimelineOperation.Run => false,
            _ => preview.Held,
        };
        if (operation == WorldTimelineOperation.Rate) { preview.Rate = rate; }
        if (operation == WorldTimelineOperation.At) {
            hasPhase = TryPhaseAt(clock: clock, phase: out phase, tick: preview.Tick);
        }
        preview.HasPhase = hasPhase;
        preview.Phase = phase;
        m_clockPreviews[name] = preview;
        return true;
    }

    private PresentedTick PreviewTick(ClockPreview preview) {
        if (preview.Held || (preview.Rate == 0d)) { return preview.Tick; }
        var now = Presented;
        var elapsed = ((((double)(((Int128)now.Whole) - preview.Origin.Whole)) + now.Fraction) - preview.Origin.Fraction);
        var offset = ((elapsed * preview.Rate) + preview.Tick.Fraction);

        if (!double.IsFinite(d: offset) || (offset >= ulong.MaxValue)) { return new PresentedTick(Fraction: 0d, Whole: ulong.MaxValue); }
        if (offset <= -((double)preview.Tick.Whole)) { return default; }
        var whole = Math.Floor(d: offset);
        var sum = (((Int128)preview.Tick.Whole) + ((Int128)whole));

        return ((sum >= ulong.MaxValue) ? new PresentedTick(Fraction: 0d, Whole: ulong.MaxValue) : new PresentedTick(Fraction: (offset - whole), Whole: ((ulong)sum)));
    }

    /// <summary>Reads a clock without charging a keyed-value resolution, for dependency checks and readouts.</summary>
    /// <param name="name">The authored clock name.</param>
    /// <param name="clock">The authored clock, when present.</param>
    /// <param name="phase">The presented phase, or zero when unavailable.</param>
    /// <returns>Whether a phase is available.</returns>
    public bool TryReadPhase(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldClock? clock, out double phase) {
        phase = 0d;
        if (!Manifest.TryClock(clock: out clock, name: name)) { return false; }
        if (!m_clockPreviews.TryGetValue(key: name, value: out var preview)) {
            return WorldKeyResolver.TryPhase(clock: clock, phase: out phase, source: this);
        }
        if (preview.Held || (preview.Rate == 0d)) {
            if (!clock.IsStateClock) { return TryPhaseAt(clock: clock, phase: out phase, tick: preview.Tick); }
            phase = preview.Phase;
            return preview.HasPhase;
        }
        return TryPhaseAt(clock, PreviewTick(preview: preview), out phase);
    }

    private bool TryPhaseAt(WorldClock clock, PresentedTick tick, out double phase) {
        phase = 0d;
        if (clock.IsTickClock) {
            phase = WorldClocks.Phase(clock: clock, tick: tick);
            return true;
        }
        if (clock.IsAnchored) {
            phase = (clock.Anchor?.PhaseAt(tick: tick) ?? 0d);
            return (clock.Anchor is not null);
        }
        var binding = WorldPresentationManifest.ClockBinding(clock: clock);
        var slot = SlotOf(binding: in binding, conversion: WorldStateConversion.Number);

        if (slot < 0) { return false; }
        ref readonly var entry = ref m_slots[slot];

        m_reads.Increment();
        var simulationTick = ((Manifest.SimulationRateHz > 0) ? (tick.Whole / EngineTicks.PerRate(ratePerSecond: ((uint)Manifest.SimulationRateHz))) : Tick);

        if (!m_view.TryRead(entry.Ordinal, binding.Key, binding.Target, simulationTick, tick.Whole, out var sample) || !sample.Value.HasValue) { return false; }
        phase = WorldClockAnchor.ToTurn(phase: WorldClockAnchor.PhaseOf(kind: sample.Value.Kind, raw: sample.Value.Raw));
        return (sample.Value.Kind is CellKind.Fixed or CellKind.Int);
    }
}
