using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Commands;
using Puck.Hosting;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.Shaders;

namespace Puck.World;

/// <summary>
/// The <c>probes</c> document section's live counterpart: services every declared probe (starting/restarting a
/// camera-input probe's kernel run against the binder's live shared feed, advancing a track-input probe's
/// player) and every declared binding (conditioning an axis into a per-tick <c>probe.&lt;name&gt;</c> command source,
/// writing a parameter into its composed extension pass, writing a control onto the camera control surface). One
/// instance per boot; every deep check the document's shallow validator deferred (channel names, extension config
/// fields, control names) runs here, in the constructor, and fails the boot loudly by name — the same precedent an
/// invalid <c>render.extensions</c> config follows.
/// </summary>
/// <remarks>
/// Registered as an <see cref="ISnapshotInputCapture"/> contribution serviced once per host frame, ahead of due
/// fixed ticks, by both the windowed and the headless host loop — exactly like <c>GamepadSnapshotInputCapture</c>.
/// Headless, a camera-input probe faults by name (no camera feed) and a parameter binding finds no composed
/// pass, while a track-input probe and every axis binding run in full. A world declaring no <c>probes</c>
/// section constructs zero probes and zero bindings, and <see cref="CaptureFrame"/> then does nothing and
/// writes nothing to any stream — the empty-section boot is byte-identical to a boot that never heard of probes.
/// </remarks>
internal sealed partial class WorldProbes : ISnapshotInputCapture, IDisposable {
    private readonly Dictionary<string, int> m_probeIndexById;
    private readonly ProbeState[] m_probes;
    private readonly AxisState[] m_axisBindings;
    private readonly IInputClock m_clock;
    private readonly ControlState[] m_controlBindings;
    private readonly IInputFocus m_focus;
    private readonly WorldPostRenderExtensionPasses m_passes;
    private readonly ParameterState[] m_parameterBindings;
    private readonly InputRouter m_router;
    private readonly WorldScreenBinder m_screens;

    private bool m_disposed;
    private RecordingState? m_recording;

    /// <summary>Builds every probe and binding row's live state, resolving and deep-validating everything the
    /// document's shallow validator left to boot: a kind's manifest and config, a track document, a binding's
    /// probe/channel reference, an extension's config field, and a control name.</summary>
    /// <param name="clock">The shared input capture clock an axis binding's captured signal is stamped with.</param>
    /// <param name="definitionSource">The booted document and its source path, for the <c>probes</c> section and
    /// resolving a track binding's path against the document's own directory.</param>
    /// <param name="focus">The terminal input focus an axis binding's capture is gated through.</param>
    /// <param name="passes">The composed <c>render.extensions</c> passes a parameter binding writes into.</param>
    /// <param name="router">The command router an axis binding's conditioned sample is captured into.</param>
    /// <param name="screens">The screen binder a camera-input probe reads its live attachment through and a
    /// texture-writing probe publishes its output through.</param>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A <c>probes</c> row fails a deep check: an unloadable or
    /// model-class kind, a camera sensor other than the kind's trigger, an invalid config, an unreadable or
    /// channel-count-mismatched track document, an unresolved channel name, an unresolved or range-incompatible
    /// extension or probe config field, an unresolved control name, or a screen showing a probe whose kind writes no
    /// texture.</exception>
    public WorldProbes(IInputClock clock, WorldDefinitionSource definitionSource, IInputFocus focus, WorldPostRenderExtensionPasses passes, InputRouter router, WorldScreenBinder screens) {
        ArgumentNullException.ThrowIfNull(argument: clock);
        ArgumentNullException.ThrowIfNull(argument: definitionSource);
        ArgumentNullException.ThrowIfNull(argument: focus);
        ArgumentNullException.ThrowIfNull(argument: passes);
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: screens);

        m_clock = clock;
        m_focus = focus;
        m_passes = passes;
        m_router = router;
        m_screens = screens;

        var probes = definitionSource.Definition.Probes;
        var documentDirectory = (Path.GetDirectoryName(path: Path.GetFullPath(path: definitionSource.SourcePath)) ?? "");

        m_probeIndexById = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        m_probes = new ProbeState[probes.Count];

        var axisBindings = new List<AxisState>();
        var parameterBindings = new List<ParameterState>();
        var controlBindings = new List<ControlState>();

        // Every probe exists before any binding resolves: a parameter binding may steer a probe declared after its
        // own row.
        for (var index = 0; (index < probes.Count); index++) {
            var row = probes[index];

            m_probes[index] = BuildProbe(
                documentDirectory: documentDirectory,
                index: index,
                row: row
            );
            m_probeIndexById[row.Id] = index;

            if (m_probes[index].Manifest.Output is not null) {
                screens.DeclareProbeOutput(id: row.Id);
            }
        }

        foreach (var screen in definitionSource.Definition.Screens) {
            if ((screen.Source is WorldScreenSource.Probe probeSource) && (m_probes[m_probeIndexById[probeSource.Id]].Manifest.Output is null)) {
                throw new InvalidOperationException(message: $"screens[{screen.Index}] shows probe '{probeSource.Id}', whose kind writes no texture output.");
            }
        }

        for (var index = 0; (index < probes.Count); index++) {
            var row = probes[index];

            if (row.Bindings is not { } bindings) {
                continue;
            }

            for (var bindingIndex = 0; (bindingIndex < bindings.Count); bindingIndex++) {
                var path = $"probes[{index}].bindings[{bindingIndex}]";

                switch (bindings[bindingIndex]) {
                    case WorldProbeBinding.Axis axis:
                        axisBindings.Add(item: BuildAxis(axis: axis, path: path, probeIndex: index));

                        break;
                    case WorldProbeBinding.Parameter parameter:
                        parameterBindings.Add(item: BuildParameter(parameter: parameter, path: path, probeIndex: index));

                        break;
                    case WorldProbeBinding.Control control:
                        controlBindings.Add(item: BuildControl(control: control, path: path, probeIndex: index));

                        break;
                }
            }
        }

        m_axisBindings = [.. axisBindings];
        m_controlBindings = [.. controlBindings];
        m_parameterBindings = [.. parameterBindings];
    }

    /// <inheritdoc/>
    public void CaptureFrame(ulong frameKey) {
        if (m_disposed || (0 == m_probes.Length)) {
            return;
        }

        ServiceProbes();
        ServiceAxes(frameKey: frameKey);
        ServiceParameters();
        ServiceControls(frameKey: frameKey);
        ServiceRecording();
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var probe in m_probes) {
            probe.Run?.Dispose();

            if (probe.Manifest.Output is not null) {
                m_screens.ReleaseProbeOutput(id: probe.Row.Id);
            }
        }
    }
    /// <summary>Appends one line describing every declared probe and binding row's live state — the
    /// <c>probe.status</c> read-back.</summary>
    /// <param name="builder">The builder to append into.</param>
    /// <returns><see langword="false"/> when this world declares no <c>probes</c> rows, leaving
    /// <paramref name="builder"/> untouched.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public bool Describe(StringBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (0 == m_probes.Length) {
            return false;
        }

        var nowTimestamp = Stopwatch.GetTimestamp();
        var wroteSegment = false;

        foreach (var probe in m_probes) {
            AppendSeparator(builder: builder, wroteSegment: ref wroteSegment);
            DescribeProbe(builder: builder, probe: probe, nowTimestamp: nowTimestamp);
        }
        foreach (var axis in m_axisBindings) {
            AppendSeparator(builder: builder, wroteSegment: ref wroteSegment);
            DescribeAxis(builder: builder, axis: axis, nowTimestamp: nowTimestamp);
        }
        foreach (var parameter in m_parameterBindings) {
            AppendSeparator(builder: builder, wroteSegment: ref wroteSegment);
            DescribeParameter(builder: builder, parameter: parameter);
        }
        foreach (var control in m_controlBindings) {
            AppendSeparator(builder: builder, wroteSegment: ref wroteSegment);
            DescribeControl(builder: builder, control: control);
        }

        return true;
    }
    /// <summary>Arms a live recording of one declared probe's fresh readings to a
    /// <c>puck.probe-track.v1</c> document — <c>probe.record</c>'s own seam. Serviced from <see cref="CaptureFrame"/>,
    /// so it only progresses while this instance is polled (the windowed launcher; see the class remarks). The
    /// document writes, and completion narrates on <see cref="Console.Error"/>, once <paramref name="seconds"/>
    /// elapses.</summary>
    /// <param name="probeId">The declared probe id to record.</param>
    /// <param name="path">The output path, resolved against the process's current directory.</param>
    /// <param name="seconds">The recording window. Must be finite and positive.</param>
    /// <param name="reason">A human-readable refusal reason when this returns <see langword="false"/>; otherwise
    /// <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the recording armed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probeId"/> or <paramref name="path"/> is
    /// <see langword="null"/>.</exception>
    public bool TryBeginRecording(string probeId, string path, float seconds, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: probeId);
        ArgumentNullException.ThrowIfNull(argument: path);

        if (!m_probeIndexById.TryGetValue(
            key: probeId,
            value: out var probeIndex
        )) {
            reason = $"no probe '{probeId}'";

            return false;
        }
        if (!(seconds > 0f) || float.IsNaN(seconds) || float.IsPositiveInfinity(seconds)) {
            reason = $"seconds '{seconds.ToString(provider: CultureInfo.InvariantCulture)}' must be a positive, finite number";

            return false;
        }
        if (m_recording is { } active) {
            reason = $"already recording '{active.ProbeId}' -> {active.Path}";

            return false;
        }

        string resolvedPath;

        try {
            resolvedPath = Path.GetFullPath(path: path);

            // A trial open (never truncating an existing file) proves the path is writable NOW rather than only
            // once the window elapses — an unwritable path refuses the arm instead of silently discarding the
            // recorded window later.
            using var trial = File.Open(path: resolvedPath, mode: FileMode.OpenOrCreate, access: FileAccess.Write);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) {
            reason = $"path '{path}' is not writable: {exception.Message}";

            return false;
        }

        var probe = m_probes[probeIndex];

        m_recording = new RecordingState {
            ProbeId = probeId,
            ProbeIndex = probeIndex,
            ChannelCount = probe.Manifest.Channels.Count,
            DurationTicks = (long)(seconds * Stopwatch.Frequency),
            Path = resolvedPath,
            RateHz = probe.Row.RateHz,
            StartTimestamp = Stopwatch.GetTimestamp(),
        };
        reason = null;

        return true;
    }

    private static void AppendSeparator(StringBuilder builder, ref bool wroteSegment) {
        if (wroteSegment) {
            builder.Append(value: " | ");
        }

        wroteSegment = true;
    }
    private void DescribeProbe(StringBuilder builder, ProbeState probe, long nowTimestamp) {
        builder.Append(value: probe.Row.Id);
        builder.Append(value: " kind=").Append(value: probe.Row.Kind);
        builder.Append(value: " input=");

        switch (probe.Row.Input) {
            case WorldProbeInput.Camera camera:
                builder.Append(value: "camera:").Append(value: camera.Sensor.ToString().ToLowerInvariant());

                break;
            case WorldProbeInput.Track:
                builder.Append(value: "track");

                break;
        }

        if (probe.Track is not null) {
            builder.Append(value: " state=running tier=track");
        } else if (probe.Run is { IsEnded: false }) {
            builder.Append(value: " state=running tier=gpu");
        } else {
            builder.Append(value: " state=idle");

            if (probe.Fault is { } fault) {
                builder.Append(value: " fault=").Append(value: fault);
            }
        }

        builder.Append(value: " rate=").Append(value: probe.Row.RateHz);

        if (probe.Run is { IsEnded: false } run) {
            builder.Append(value: " cycles=").Append(value: run.Cycles);
            builder.Append(value: " drops=").Append(value: run.Drops);
        }

        if (probe.Manifest.Output is { } output) {
            builder.Append(value: " output=").Append(value: output.Of.ToString().ToLowerInvariant());
        }

        if (probe.Ring.TryReadLatest(reading: out var reading)) {
            var age = Stopwatch.GetElapsedTime(startingTimestamp: reading.CaptureTimestamp, endingTimestamp: nowTimestamp);

            builder.Append(value: " capture-age=").Append(value: Math.Max(val1: 0L, val2: (long)age.TotalMilliseconds)).Append(value: "ms");

            for (var channel = 0; (channel < probe.Manifest.Channels.Count); channel++) {
                builder.Append(value: ' ').Append(value: probe.Manifest.Channels[channel].Name).Append(value: '=').Append(value: reading[channel].ToString());
            }

            builder.Append(value: " confidence=").Append(value: reading.Confidence.ToString());

            if (reading.OutputSlot >= 0) {
                builder.Append(value: " slot=").Append(value: reading.OutputSlot);
            }
        } else {
            builder.Append(value: " no-reading");
        }
    }
    private void DescribeAxis(StringBuilder builder, AxisState axis, long nowTimestamp) {
        builder.Append(value: "axis ").Append(value: axis.Row.Source);
        builder.Append(value: " source=").Append(value: axis.Source);
        builder.Append(value: " seat=").Append(value: axis.Row.Seat);

        if (!m_probes[axis.ProbeIndex].Ring.TryReadLatest(reading: out var reading)) {
            builder.Append(value: " no-reading");

            return;
        }

        // A value copy of the mutable conditioner: stepping the COPY reads the conditioned value the real
        // conditioner would emit right now without disturbing its deadband/hysteresis/EMA history — the real
        // conditioner (axis.Conditioner) only ever advances from ServiceAxes, once per host frame.
        var probe = axis.Conditioner;
        var sample = probe.Step(reading: in reading, channel: axis.Channel, nowTimestamp: nowTimestamp);

        builder.Append(value: " value=").Append(value: sample.Value.ToString());
        builder.Append(value: " confidence=").Append(value: sample.Confidence.ToString());
        builder.Append(value: " captured=").Append(value: sample.Value.ToString());
        builder.Append(value: " expired=").Append(value: (sample.Expired ? "true" : "false"));
    }
    private void DescribeControl(StringBuilder builder, ControlState control) {
        var channelName = m_probes[control.ProbeIndex].Manifest.Channels[control.Channel].Name;

        builder.Append(value: "control ").Append(value: m_probes[control.ProbeIndex].Row.Id).Append(value: '.').Append(value: channelName);
        builder.Append(value: " -> ").Append(value: control.Row.ControlName);

        if (!control.HasWritten) {
            builder.Append(value: " value=none writes=0");

            return;
        }

        builder.Append(value: " value=").Append(value: control.LastValue);
        builder.Append(value: " writes=").Append(value: control.Writes);
    }
    private void DescribeParameter(StringBuilder builder, ParameterState parameter) {
        var channelName = m_probes[parameter.ProbeIndex].Manifest.Channels[parameter.Channel].Name;

        builder.Append(value: "parameter ").Append(value: m_probes[parameter.ProbeIndex].Row.Id).Append(value: '.').Append(value: channelName);

        if (parameter.TargetProbeIndex >= 0) {
            builder.Append(value: " -> probe ").Append(value: m_probes[parameter.TargetProbeIndex].Row.Id).Append(value: '.').Append(value: ((WorldProbeParameterTarget.Probe)parameter.Row.Target).Field);
        } else {
            builder.Append(value: " -> extension ").Append(value: parameter.ExtensionId).Append(value: '.').Append(value: parameter.ExtensionField);
        }

        if (parameter.Writes == 0L) {
            builder.Append(value: " value=none writes=0");

            return;
        }

        builder.Append(value: " value=").Append(value: parameter.LastValue.ToString(format: "0.0000", provider: CultureInfo.InvariantCulture));
        builder.Append(value: " writes=").Append(value: parameter.Writes);
    }
    // Writes the finished recording's puck.probe-track.v1 document and narrates completion — or, honestly, a
    // failure — on stderr. Static: it never touches instance state, only the finished snapshot handed to it.
    private static void FinishRecording(RecordingState recording) {
        if (recording.Samples.Count == 0) {
            Console.Error.WriteLine(value: $"[probe.record: {recording.ProbeId} -> {recording.Path} failed: no readings were published during the window]");

            return;
        }

        var document = new ProbeTrackDocument(
            Schema: ProbeTrackDocument.SchemaVersion,
            RateHz: recording.RateHz,
            Channels: recording.ChannelCount,
            Samples: recording.Samples
        );

        try {
            File.WriteAllText(
                path: recording.Path,
                contents: JsonSerializer.Serialize(value: document, jsonTypeInfo: ProbeTrackJsonContext.Default.ProbeTrackDocument)
            );
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine(value: $"[probe.record: {recording.ProbeId} -> {recording.Path} failed: {exception.Message}]");

            return;
        }

        Console.Error.WriteLine(value: $"[probe.record: {recording.ProbeId} -> {recording.Path} ({recording.Samples.Count} samples)]");
    }
    // Advances the one live recording, if any: appends a fresh reading (a changed Sequence, so a stalled
    // probe never records a repeated sample) and, once the declared window elapses, hands the finished
    // recording to FinishRecording.
    private void ServiceRecording() {
        if (m_recording is not { } recording) {
            return;
        }

        var nowTimestamp = Stopwatch.GetTimestamp();
        var probe = m_probes[recording.ProbeIndex];

        if (
            probe.Ring.TryReadLatest(reading: out var reading) &&
            (reading.Sequence != recording.LastSequence)
        ) {
            recording.LastSequence = reading.Sequence;

            var channels = new double[recording.ChannelCount];

            for (var channel = 0; (channel < recording.ChannelCount); channel++) {
                channels[channel] = (double)reading[channel];
            }

            recording.Samples.Add(item: new ProbeTrackSample(
                T: (Math.Max(val1: 0L, val2: (reading.CaptureTimestamp - recording.StartTimestamp)) / (double)Stopwatch.Frequency),
                C: channels,
                K: (double)reading.Confidence
            ));
        }

        if ((nowTimestamp - recording.StartTimestamp) < recording.DurationTicks) {
            return;
        }

        m_recording = null;
        FinishRecording(recording: recording);
    }

    // Resolves an (probe, channel) reference pair a binding row carries, deep-validating both against the
    // resolved probe's loaded manifest (the document's own load-time validator only proved the ANALYZER id
    // resolves; it cannot see channel names, which live behind the kind vocabulary hook).
    private int ResolveChannel(int probeIndex, string channel, string path) {
        var manifest = m_probes[probeIndex].Manifest;

        for (var index = 0; (index < manifest.Channels.Count); index++) {
            if (string.Equals(
                a: manifest.Channels[index].Name,
                b: channel,
                comparisonType: StringComparison.Ordinal
            )) {
                return index;
            }
        }

        throw new InvalidOperationException(message: $"{path}.channel '{channel}' names no channel of probe kind '{manifest.Name}'.");
    }
    // Maps a reading channel's raw value onto [-1, 1] about the channel's declared neutral — the same
    // asymmetric-about-neutral rule ProbeAxisConditioner.Normalize applies, over presentation doubles rather than
    // Puck.Maths fixed point (this feeds a shader-extension config float or a device control int, never simulation
    // state). A degenerate side (equal to neutral) maps its whole side to zero.
    private static double NormalizeChannel(double raw, double min, double max, double neutral) {
        if (raw >= neutral) {
            var span = (max - neutral);

            return ((span > 0.0) ? Math.Clamp(value: ((raw - neutral) / span), min: 0.0, max: 1.0) : 0.0);
        }

        var lowerSpan = (neutral - min);

        return ((lowerSpan > 0.0) ? -Math.Clamp(value: ((neutral - raw) / lowerSpan), min: 0.0, max: 1.0) : 0.0);
    }

    // One declared probe's live state: its loaded kind manifest, its packed constants (the bound config, patched in
    // place by a parameter binding that steers this probe), the reading ring every binding against it reads, and —
    // for a camera-input probe — the attachment and output-ring generations its kernel run was started against and
    // the run itself; for a track-input probe, the player advancing the same ring.
    private sealed class ProbeState {
        public required byte[] Constants { get; init; }
        public string? Fault { get; set; }
        public required ProbeKindManifest Manifest { get; init; }
        public object? OutputSet { get; set; }
        public required ProbeReadingRing Ring { get; init; }
        public required WorldProbe Row { get; init; }
        public IProbeKernelRun? Run { get; set; }
        public WorldCameraSensor? Sensor { get; init; }
        public object? TargetSet { get; set; }
        public ProbeTrackPlayer? Track { get; init; }
    }
    // One declared axis binding's live state. Conditioner is a plain field (never a property): ProbeAxisConditioner
    // is a mutable struct whose Step mutates hysteresis/smoothing history in place, and reading it back through a
    // property getter would operate on a discarded copy — see ProbeAxisConditioner's own remarks. Held records that
    // the router carries a live (non-expired) sample from this axis; Suppressed that the device lost terminal focus
    // since the last capture, so the next focused frame re-captures even an unchanged sample.
    private sealed class AxisState {
        public required int ProbeIndex { get; init; }
        public required int Channel { get; init; }
        public ProbeAxisConditioner Conditioner;
        public required InputDeviceId Device { get; init; }
        public bool Held;
        public required WorldProbeBinding.Axis Row { get; init; }
        public required int Slot { get; init; }
        public required string Source { get; init; }
        public bool Suppressed;
    }
    // One declared control binding's live state — the last written device value, so a re-authored/repeated write is
    // skipped, and the count probe.status reads back.
    private sealed class ControlState {
        public required int ProbeIndex { get; init; }
        public required int Channel { get; init; }
        public required CameraControl ControlEnum { get; init; }
        public bool HasWritten;
        public int LastValue;
        public required long MaxAgeTicks { get; init; }
        public required WorldProbeBinding.Control Row { get; init; }
        public long Writes;
    }
    // One declared parameter binding's live state — the last written value (NaN until the first write), so an
    // unchanged conditioned value never re-touches its target. Exactly one target is set: an extension pass's config
    // field, or another probe's constant at a byte offset of its packed block.
    private sealed class ParameterState {
        public required int ProbeIndex { get; init; }
        public required int Channel { get; init; }
        public int ConstantOffset { get; init; }
        public string? ExtensionField { get; init; }
        public string? ExtensionId { get; init; }
        public float LastValue = float.NaN;
        public required long MaxAgeTicks { get; init; }
        public required WorldProbeBinding.Parameter Row { get; init; }
        public int TargetProbeIndex { get; init; } = -1;
        public long Writes;
    }
    // One armed probe.record recording's live state — at most one at a time. LastSequence starts at -1 so the
    // first-ever reading (sequence 0) is never mistaken for a repeat.
    private sealed class RecordingState {
        public required string ProbeId { get; init; }
        public required int ProbeIndex { get; init; }
        public required int ChannelCount { get; init; }
        public required long DurationTicks { get; init; }
        public long LastSequence = -1L;
        public required string Path { get; init; }
        public required double RateHz { get; init; }
        public List<ProbeTrackSample> Samples { get; } = [];
        public required long StartTimestamp { get; init; }
    }
}
