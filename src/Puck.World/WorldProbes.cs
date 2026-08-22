using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Commands;
using Puck.Hosting;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The <c>probes</c> document section's live counterpart: services every declared probe (starting/restarting a
/// camera-input probe's kernel run against the binder's live shared feed, advancing a track-input probe's
/// player) and every declared binding (conditioning an axis into a per-tick <c>probe.&lt;name&gt;</c> command source,
/// writing a parameter into its composed extension pass, writing a control onto the camera control surface). One
/// row per declared <c>probes[]</c> entry; a row whose camera sockets every one name a seat (or that has no camera
/// sockets, or plays back a recorded track) runs as a single instance for the whole boot, exactly like every other
/// document section. A row with at least one seat-less camera socket is seat-relative: it is instanced once per
/// occupied local seat, each instance carrying its own reading ring, kernel run, packed constants, and live bindings,
/// and resolving its seat-less camera sockets against its own seat — the same per-seat instancing the identity HUD
/// panel already applies. Instances follow the roster's occupancy every serviced frame: a seat joining creates its
/// row's instances on the next pass, a seat leaving retires them (ending the run, releasing the output ring, and
/// releasing every binding's held router state) with no reboot. Every deep check the document's shallow validator
/// deferred (channel names, extension config fields, control names, cross-probe references) runs once per row, in
/// the constructor, and fails the boot loudly by name — the same precedent an invalid <c>render.extensions</c>
/// config follows; only the per-seat live state (rings, runs, binding state) is built lazily, as seats come and go.
/// </summary>
/// <remarks>
/// Registered as an <see cref="ISnapshotInputCapture"/> contribution serviced once per host frame, ahead of due
/// fixed ticks, by both the windowed and the headless host loop — exactly like <c>GamepadSnapshotInputCapture</c>.
/// Headless, a camera-input probe faults by name (no camera feed) and a parameter binding finds no composed
/// pass, while a track-input probe and every axis binding run in full. A world declaring no <c>probes</c>
/// section constructs zero rows and services nothing — the empty-section boot is byte-identical to a boot that
/// never heard of probes.
/// </remarks>
internal sealed partial class WorldProbes : ISnapshotInputCapture, IDisposable {
    private readonly List<ProbeInstance> m_liveInstances = [];
    private readonly Dictionary<string, int> m_rowIndexById;
    private readonly ProbeRowInfo[] m_rows;
    private readonly IInputClock m_clock;
    private readonly IInputFocus m_focus;
    private readonly WorldPostRenderExtensionPasses m_passes;
    private readonly InputRouter m_router;
    private readonly PlayerRoster m_roster;
    private readonly WorldScreenBinder m_screens;

    private bool m_disposed;
    private RecordingState? m_recording;

    /// <summary>Builds every declared probe row's static shape, resolving and deep-validating everything the
    /// document's shallow validator left to boot: a kind's manifest and config, a track document, a binding's
    /// probe/channel reference, an extension's config field, and a control name. Live per-instance state (rings,
    /// kernel runs, binding state) is built afterward, by the first <see cref="CaptureFrame"/>'s instance
    /// reconciliation.</summary>
    /// <param name="clock">The shared input capture clock an axis binding's captured signal is stamped with.</param>
    /// <param name="definitionSource">The booted document and its source path, for the <c>probes</c> section and
    /// resolving a track binding's path against the document's own directory.</param>
    /// <param name="focus">The terminal input focus an axis binding's capture is gated through.</param>
    /// <param name="passes">The composed <c>render.extensions</c> passes a parameter binding writes into.</param>
    /// <param name="roster">The seat roster a seat-relative row's instances follow — occupancy drives creation and
    /// retirement.</param>
    /// <param name="router">The command router an axis binding's conditioned sample is captured into.</param>
    /// <param name="screens">The screen binder a camera-input probe reads its live attachment through and a
    /// texture-writing probe publishes its output through.</param>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A <c>probes</c> row fails a deep check: an unloadable or
    /// model-class kind, a camera sensor other than the kind's trigger, an invalid config, an unreadable or
    /// channel-count-mismatched track document, an unresolved channel name, an unresolved or range-incompatible
    /// extension or probe config field, an unresolved control name, or a screen showing a probe whose kind writes no
    /// texture.</exception>
    public WorldProbes(IInputClock clock, WorldDefinitionSource definitionSource, IInputFocus focus, WorldPostRenderExtensionPasses passes, PlayerRoster roster, InputRouter router, WorldScreenBinder screens) {
        ArgumentNullException.ThrowIfNull(argument: clock);
        ArgumentNullException.ThrowIfNull(argument: definitionSource);
        ArgumentNullException.ThrowIfNull(argument: focus);
        ArgumentNullException.ThrowIfNull(argument: passes);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: screens);

        m_clock = clock;
        m_focus = focus;
        m_passes = passes;
        m_roster = roster;
        m_router = router;
        m_screens = screens;

        var probes = definitionSource.Definition.Probes;
        var documentDirectory = (Path.GetDirectoryName(path: Path.GetFullPath(path: definitionSource.SourcePath)) ?? "");

        m_rowIndexById = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        m_rows = new ProbeRowInfo[probes.Count];

        var probesById = new Dictionary<string, WorldProbe>(comparer: StringComparer.Ordinal);

        foreach (var row in probes) {
            probesById[row.Id] = row;
        }

        // Every row exists before any binding template resolves: a parameter binding may steer a probe declared
        // after its own row, and a `probe` socket may name one declared after its own row too.
        for (var index = 0; (index < probes.Count); index++) {
            var row = probes[index];

            m_rows[index] = BuildRow(
                documentDirectory: documentDirectory,
                index: index,
                probesById: probesById,
                row: row
            );
            m_rowIndexById[row.Id] = index;
        }

        foreach (var screen in definitionSource.Definition.Screens) {
            if ((screen.Source is WorldScreenSource.Probe probeSource) && (m_rows[m_rowIndexById[probeSource.Id]].Manifest.Output is null)) {
                throw new InvalidOperationException(message: $"screens[{screen.Index}] shows probe '{probeSource.Id}', whose kind writes no texture output.");
            }
        }

        for (var index = 0; (index < probes.Count); index++) {
            BuildRowBindingTemplates(index: index, row: probes[index]);
        }

        // The initial instance set: a non-seat-relative row's single instance exists from here on; a seat-relative
        // row's instances follow whichever seats are already occupied at boot, so a first-frame `probe.status` never
        // lags a seat that was joined before this constructor ran.
        ReconcileInstances();
    }

    /// <inheritdoc/>
    public void CaptureFrame(ulong frameKey) {
        if (m_disposed || (0 == m_rows.Length)) {
            return;
        }

        ReconcileInstances();
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

        // RetireInstance removes from m_liveInstances, so walk a snapshot rather than the live list.
        foreach (var instance in m_liveInstances.ToArray()) {
            RetireInstance(instance: instance);
        }
    }
    /// <summary>Appends one line describing every live probe instance and every binding row's live state — the
    /// <c>probe.status</c> read-back. A seat-relative row's instance is labeled <c>&lt;id&gt;@&lt;seat&gt;</c>; a
    /// single-instance row is labeled by its bare <c>id</c>.</summary>
    /// <param name="builder">The builder to append into.</param>
    /// <returns><see langword="false"/> when this world declares no <c>probes</c> rows, leaving
    /// <paramref name="builder"/> untouched.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public bool Describe(StringBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (0 == m_rows.Length) {
            return false;
        }

        var nowTimestamp = Stopwatch.GetTimestamp();
        var wroteSegment = false;

        foreach (var instance in m_liveInstances) {
            AppendSeparator(builder: builder, wroteSegment: ref wroteSegment);
            DescribeProbe(builder: builder, instance: instance, nowTimestamp: nowTimestamp);
        }
        foreach (var instance in m_liveInstances) {
            foreach (var axis in instance.AxisBindings) {
                AppendSeparator(builder: builder, wroteSegment: ref wroteSegment);
                DescribeAxis(builder: builder, axis: axis, nowTimestamp: nowTimestamp);
            }
        }
        foreach (var instance in m_liveInstances) {
            foreach (var parameter in instance.ParameterBindings) {
                AppendSeparator(builder: builder, wroteSegment: ref wroteSegment);
                DescribeParameter(builder: builder, parameter: parameter);
            }
        }
        foreach (var instance in m_liveInstances) {
            foreach (var control in instance.ControlBindings) {
                AppendSeparator(builder: builder, wroteSegment: ref wroteSegment);
                DescribeControl(builder: builder, control: control);
            }
        }

        return true;
    }
    /// <summary>Arms a live recording of one declared probe instance's fresh readings to a
    /// <c>puck.probe-track.v1</c> document — <c>probe.record</c>'s own seam. Serviced from <see cref="CaptureFrame"/>,
    /// so it only progresses while this instance is polled (the windowed launcher; see the class remarks). The
    /// document writes, and completion narrates on <see cref="Console.Error"/>, once <paramref name="seconds"/>
    /// elapses.</summary>
    /// <param name="probeRef">The declared probe id, optionally suffixed <c>@&lt;seat&gt;</c> to name one instance
    /// of a seat-relative row. The suffix is required for a seat-relative row (ambiguous otherwise) and optional for
    /// a single-instance row.</param>
    /// <param name="path">The output path, resolved against the process's current directory.</param>
    /// <param name="seconds">The recording window. Must be finite and positive.</param>
    /// <param name="reason">A human-readable refusal reason when this returns <see langword="false"/>; otherwise
    /// <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the recording armed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probeRef"/> or <paramref name="path"/> is
    /// <see langword="null"/>.</exception>
    public bool TryBeginRecording(string probeRef, string path, float seconds, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: probeRef);
        ArgumentNullException.ThrowIfNull(argument: path);

        if (!TryResolveRecordableInstance(
            instance: out var instance,
            probeRef: probeRef,
            reason: out reason
        )) {
            return false;
        }
        if (!(seconds > 0f) || float.IsNaN(seconds) || float.IsPositiveInfinity(seconds)) {
            reason = $"seconds '{seconds.ToString(provider: CultureInfo.InvariantCulture)}' must be a positive, finite number";

            return false;
        }
        if (m_recording is { } active) {
            reason = $"already recording '{active.Instance.Label}' -> {active.Path}";

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

        m_recording = new RecordingState {
            Instance = instance,
            ChannelCount = instance.RowInfo.Manifest.Channels.Count,
            DurationTicks = (long)(seconds * Stopwatch.Frequency),
            Path = resolvedPath,
            RateHz = instance.RowInfo.Row.RateHz,
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
    // A socket's bound source, in the same "kind:detail" shorthand every screen/camera read-back already uses. A
    // camera socket also names the resolved device token (camera1, ...) or the seat's own unassigned/incompatible fault
    // — the pipe-assertable answer to "which physical camera is this probe actually reading". contextSeat is the
    // enclosing instance's own seat — what a seat-less camera socket resolves against.
    private void AppendFrameSource(StringBuilder builder, WorldFrameSource source, int contextSeat) {
        switch (source) {
            case WorldScreenSource.Camera camera:
                var seat = (camera.Seat ?? contextSeat);
                var token = (m_screens.ResolvedCameraToken(seat: seat) ?? $"seat{seat}-unassigned");

                builder.Append(value: "camera:").Append(value: camera.Sensor.ToString().ToLowerInvariant()).Append(value: '@').Append(value: token);

                break;
            case WorldScreenSource.View view:
                builder.Append(value: "view:").Append(value: view.CameraName);

                break;
            case WorldScreenSource.Probe probe:
                builder.Append(value: "probe:").Append(value: probe.Id);

                break;
            case WorldScreenSource.Capture capture:
                builder.Append(value: "capture:").Append(value: ((capture.MonitorIndex is { } monitorIndex) ? $"monitor{monitorIndex}" : capture.WindowTitle));

                break;
            default:
                builder.Append(value: "unknown");

                break;
        }
    }
    private void DescribeProbe(StringBuilder builder, ProbeInstance instance, long nowTimestamp) {
        var row = instance.RowInfo.Row;

        builder.Append(value: instance.Label);
        builder.Append(value: " kind=").Append(value: row.Kind);

        if (row.Track is { } trackPath) {
            builder.Append(value: " track=").Append(value: trackPath);
        } else {
            builder.Append(value: " input=");

            var sockets = instance.RowInfo.Manifest.Inputs;

            for (var index = 0; (index < sockets.Count); index++) {
                if (index > 0) {
                    builder.Append(value: ' ');
                }

                var socket = sockets[index];

                builder.Append(value: socket.Name).Append(value: '=');

                if ((row.Inputs is { } inputs) && inputs.TryGetValue(key: socket.Name, value: out var source)) {
                    AppendFrameSource(builder: builder, source: source, contextSeat: instance.Seat);
                } else {
                    builder.Append(value: "unbound");
                }
            }
        }

        if (instance.Track is not null) {
            builder.Append(value: " state=running tier=track");
        } else if (instance.Run is { IsEnded: false }) {
            builder.Append(value: " state=running tier=gpu");
        } else {
            builder.Append(value: " state=idle");

            if (instance.Fault is { } fault) {
                builder.Append(value: " fault=").Append(value: fault);
            }
        }

        builder.Append(value: " rate=").Append(value: row.RateHz);

        if (instance.Run is { IsEnded: false } run) {
            builder.Append(value: " cycles=").Append(value: run.Cycles);
            builder.Append(value: " drops=").Append(value: run.Drops);
        }

        if (instance.RowInfo.Manifest.Output is { } output) {
            builder.Append(value: " output=").Append(value: output.Of);
        }

        if (instance.Ring.TryReadLatest(reading: out var reading)) {
            var age = Stopwatch.GetElapsedTime(startingTimestamp: reading.CaptureTimestamp, endingTimestamp: nowTimestamp);

            builder.Append(value: " capture-age=").Append(value: Math.Max(val1: 0L, val2: (long)age.TotalMilliseconds)).Append(value: "ms");

            for (var channel = 0; (channel < instance.RowInfo.Manifest.Channels.Count); channel++) {
                builder.Append(value: ' ').Append(value: instance.RowInfo.Manifest.Channels[channel].Name).Append(value: '=').Append(value: reading[channel].ToString());
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
        builder.Append(value: " seat=").Append(value: axis.Instance.Seat);

        if (!axis.Instance.Ring.TryReadLatest(reading: out var reading)) {
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
        var channelName = control.Instance.RowInfo.Manifest.Channels[control.Channel].Name;

        builder.Append(value: "control ").Append(value: control.Instance.Label).Append(value: '.').Append(value: channelName);
        builder.Append(value: " -> ").Append(value: control.Row.ControlName);

        if (!control.HasWritten) {
            builder.Append(value: " value=none writes=0");

            return;
        }

        builder.Append(value: " value=").Append(value: control.LastValue);
        builder.Append(value: " writes=").Append(value: control.Writes);
    }
    private void DescribeParameter(StringBuilder builder, ParameterState parameter) {
        var channelName = parameter.Instance.RowInfo.Manifest.Channels[parameter.Channel].Name;

        builder.Append(value: "parameter ").Append(value: parameter.Instance.Label).Append(value: '.').Append(value: channelName);

        if (parameter.TargetRowInfo is { } targetRow) {
            builder.Append(value: " -> probe ").Append(value: targetRow.Row.Id).Append(value: '.').Append(value: ((WorldProbeParameterTarget.Probe)parameter.Row.Target).Field);
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
            Console.Error.WriteLine(value: $"[probe.record: {recording.Instance.Label} -> {recording.Path} failed: no readings were published during the window]");

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
            Console.Error.WriteLine(value: $"[probe.record: {recording.Instance.Label} -> {recording.Path} failed: {exception.Message}]");

            return;
        }

        Console.Error.WriteLine(value: $"[probe.record: {recording.Instance.Label} -> {recording.Path} ({recording.Samples.Count} samples)]");
    }
    // Advances the one live recording, if any: appends a fresh reading (a changed Sequence, so a stalled
    // probe never records a repeated sample) and, once the declared window elapses, hands the finished
    // recording to FinishRecording. A recorded instance that retires mid-window (its seat left) simply stops
    // producing fresh readings; the window still completes and writes whatever was captured.
    private void ServiceRecording() {
        if (m_recording is not { } recording) {
            return;
        }

        var nowTimestamp = Stopwatch.GetTimestamp();
        var ring = recording.Instance.Ring;

        if (
            ring.TryReadLatest(reading: out var reading) &&
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
    // resolved row's own loaded manifest (the document's own load-time validator only proved the ANALYZER id
    // resolves; it cannot see channel names, which live behind the kind vocabulary hook).
    private static int ResolveChannel(ProbeKindManifest manifest, string channel, string path) {
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
    // Parses a probe reference of the shape "<id>" or "<id>@<seat>" — the probe.set/probe.record addressing grammar.
    // A malformed "@" suffix (non-numeric, or less than 1) is left folded into baseId, so it resolves to "no such
    // probe" rather than silently falling back to the no-seat behavior.
    private static void ParseInstanceRef(string probeRef, out string baseId, out int? seat) {
        var at = probeRef.IndexOf(value: '@');

        if (
            (at < 0) ||
            !int.TryParse(s: probeRef.AsSpan(start: (at + 1)), result: out var parsedSeat) ||
            (parsedSeat < 1)
        ) {
            baseId = probeRef;
            seat = null;

            return;
        }

        baseId = probeRef[..at];
        seat = parsedSeat;
    }
    // The single-instance row's instance, or the seat-relative row's instance at contextSeat — the resolution every
    // cross-probe reference (a `probe` socket, a parameter's probe target) makes, fresh, every time it is needed
    // (never cached at binding-build time): the target may not exist yet, or may have retired and been recreated as
    // a seat leaves and rejoins.
    private static ProbeInstance? ResolveInstance(ProbeRowInfo target, int contextSeat) => (target.IsSeatRelative
        ? (target.InstancesBySeat!.TryGetValue(key: contextSeat, value: out var instance) ? instance : null)
        : target.SingleInstance
    );
    // Formats the known live instances of a row for a refusal message — "head@1, head@2", or "(no live instances)".
    private static string DescribeKnownInstances(ProbeRowInfo rowInfo) {
        if ((rowInfo.InstancesBySeat is not { Count: > 0 } bySeat)) {
            return " (no live instances)";
        }

        var seats = new List<int>(bySeat.Keys);

        seats.Sort();

        var builder = new StringBuilder(value: " ");

        for (var index = 0; (index < seats.Count); index++) {
            if (index > 0) {
                builder.Append(value: ", ");
            }

            builder.Append(value: rowInfo.Row.Id).Append(value: '@').Append(value: seats[index]);
        }

        return builder.ToString();
    }
    // probe.record's own resolution: a seat-relative row without an explicit @seat is ambiguous and refused (naming
    // the live instances); every other case matches probe.set's single-instance resolution.
    private bool TryResolveRecordableInstance(string probeRef, out ProbeInstance instance, out string? reason) {
        ParseInstanceRef(probeRef: probeRef, baseId: out var baseId, seat: out var seat);

        if (!m_rowIndexById.TryGetValue(key: baseId, value: out var rowIndex)) {
            instance = null!;
            reason = $"no probe '{baseId}'";

            return false;
        }

        var rowInfo = m_rows[rowIndex];

        if (seat is { } explicitSeat) {
            if (ResolveInstance(target: rowInfo, contextSeat: explicitSeat) is not { } resolved) {
                instance = null!;
                reason = $"no live instance '{baseId}@{explicitSeat}'{DescribeKnownInstances(rowInfo: rowInfo)}";

                return false;
            }

            instance = resolved;
            reason = null;

            return true;
        }

        if (rowInfo.IsSeatRelative) {
            instance = null!;
            reason = $"'{baseId}' is seat-relative; specify one of{DescribeKnownInstances(rowInfo: rowInfo)}";

            return false;
        }

        if (rowInfo.SingleInstance is not { } single) {
            instance = null!;
            reason = $"probe '{baseId}' has no live instance";

            return false;
        }

        instance = single;
        reason = null;

        return true;
    }
    // The one place a probe instance's packed constants are patched and handed to its running kernel — a parameter
    // binding's target write and probe.set's live write share this so the two paths can never drift.
    private static void WriteConstant(int offset, ProbeInstance target, float value) {
        BitConverter.TryWriteBytes(destination: target.Constants.AsSpan(start: offset), value: value);
        target.Run?.SetConstants(constants: target.Constants);
    }

    // One declared probes[] row's static, boot-validated shape: its loaded kind manifest, its constants template
    // (cloned per instance — a parameter binding or probe.set patches an instance's own copy, never this one), the
    // trigger socket's sensor, whether it is seat-relative (Manifest.Class is always Kernel; TrackDocument is
    // non-null only for a track-input row, which is never seat-relative), and its bindings' resolved, reusable
    // templates. A non-seat-relative row's SingleInstance is created once (by ReconcileInstances, driven from the
    // constructor) and never retired; a seat-relative row's InstancesBySeat follows the roster's occupancy.
    private sealed class ProbeRowInfo {
        public required byte[] ConstantsTemplate { get; init; }
        public required bool IsSeatRelative { get; init; }
        public Dictionary<int, ProbeInstance>? InstancesBySeat { get; init; }
        public required ProbeKindManifest Manifest { get; init; }
        public required WorldProbe Row { get; init; }
        public ProbeInstance? SingleInstance { get; set; }
        // The seat a non-seat-relative row's single instance resolves against — every camera socket named its own
        // seat, so this is the trigger socket's (equal to every other socket's, by construction); 1 for a track row.
        public int SingleInstanceSeat { get; init; } = 1;
        public ProbeTrackDocument? TrackDocument { get; init; }
        public WorldCameraSensor? TriggerSensor { get; init; }
        public List<AxisBindingTemplate> AxisTemplates { get; } = [];
        public List<ControlBindingTemplate> ControlTemplates { get; } = [];
        public List<ParameterBindingTemplate> ParameterTemplates { get; } = [];
    }
    // One live probe instance: a row's own state for one seat (the row's single seat, or one occupied seat of a
    // seat-relative row). Label is the probe.status/probe.record/probe.set address ("id" or "id@seat");
    // OutputRingKey is the WorldScreenBinder output-ring key a texture-writing kind's ring is provisioned under —
    // seat 1's instance of a seat-relative row shares the row's bare id (so an authored screen/HUD `probe` source,
    // which carries no seat of its own, keeps resolving the same ring it always has) while every other seat gets its
    // own "id@seat" ring. OutputSet null means the run is not currently attached (never started, or every socket is
    // being re-evaluated after an unready frame) — the next ready frame always attaches.
    private sealed class ProbeInstance {
        public required byte[] Constants { get; init; }
        public string? Fault { get; set; }
        public required string Label { get; init; }
        public required string OutputRingKey { get; init; }
        public (int Width, int Height)? OutputExtent { get; set; }
        public object? OutputSet { get; set; }
        public required ProbeReadingRing Ring { get; init; }
        public required ProbeRowInfo RowInfo { get; init; }
        public IProbeKernelRun? Run { get; set; }
        public required int Seat { get; init; }
        public object?[]? SocketGenerations { get; set; }
        public ProbeTrackPlayer? Track { get; init; }
        public List<AxisState> AxisBindings { get; } = [];
        public List<ControlState> ControlBindings { get; } = [];
        public List<ParameterState> ParameterBindings { get; } = [];
    }
    // One declared axis binding row's resolved, reusable shape — built once per row, instantiated (as an AxisState)
    // once per instance the row ever gets.
    private sealed class AxisBindingTemplate {
        public required int Channel { get; init; }
        public required ProbeAxisPolicy Policy { get; init; }
        public required WorldProbeBinding.Axis Row { get; init; }
        public required string Source { get; init; }
    }
    // One declared control binding row's resolved, reusable shape.
    private sealed class ControlBindingTemplate {
        public required int Channel { get; init; }
        public required CameraControl ControlEnum { get; init; }
        public required long MaxAgeTicks { get; init; }
        public required WorldProbeBinding.Control Row { get; init; }
    }
    // One declared parameter binding row's resolved, reusable shape. TargetRowInfo is set only for a probe target;
    // the concrete target INSTANCE is resolved fresh every ServiceParameters pass (ResolveInstance), never cached
    // here, since it may not exist yet (or may have retired and been recreated) by the time a write is due.
    private sealed class ParameterBindingTemplate {
        public required int Channel { get; init; }
        public int ConstantOffset { get; init; }
        public string? ExtensionField { get; init; }
        public string? ExtensionId { get; init; }
        public required long MaxAgeTicks { get; init; }
        public required WorldProbeBinding.Parameter Row { get; init; }
        public ProbeRowInfo? TargetRowInfo { get; init; }
    }
    // One declared axis binding's live, per-instance state. Conditioner is a plain field (never a property):
    // ProbeAxisConditioner is a mutable struct whose Step mutates hysteresis/smoothing history in place, and reading
    // it back through a property getter would operate on a discarded copy — see ProbeAxisConditioner's own remarks.
    // Held records that the router carries a live (non-expired) sample from this axis; Suppressed that the device
    // lost terminal focus since the last capture, so the next focused frame re-captures even an unchanged sample.
    private sealed class AxisState {
        public required int Channel { get; init; }
        public ProbeAxisConditioner Conditioner;
        public required InputDeviceId Device { get; init; }
        public bool Held;
        public required ProbeInstance Instance { get; init; }
        public required WorldProbeBinding.Axis Row { get; init; }
        public required int Slot { get; init; }
        public required string Source { get; init; }
        public bool Suppressed;
    }
    // One declared control binding's live, per-instance state — the last written device value, so a
    // re-authored/repeated write is skipped, and the count probe.status reads back.
    private sealed class ControlState {
        public required int Channel { get; init; }
        public required CameraControl ControlEnum { get; init; }
        public bool HasWritten;
        public required ProbeInstance Instance { get; init; }
        public int LastValue;
        public required long MaxAgeTicks { get; init; }
        public required WorldProbeBinding.Control Row { get; init; }
        public long Writes;
    }
    // One declared parameter binding's live, per-instance state — the last written value (NaN until the first
    // write), so an unchanged conditioned value never re-touches its target.
    private sealed class ParameterState {
        public required int Channel { get; init; }
        public required long MaxAgeTicks { get; init; }
        public required ProbeInstance Instance { get; init; }
        public float LastValue = float.NaN;
        public required WorldProbeBinding.Parameter Row { get; init; }
        public ProbeRowInfo? TargetRowInfo { get; init; }
        public string? ExtensionField { get; init; }
        public string? ExtensionId { get; init; }
        public int ConstantOffset { get; init; }
        public long Writes;
    }
    // One armed probe.record recording's live state — at most one at a time. LastSequence starts at -1 so the
    // first-ever reading (sequence 0) is never mistaken for a repeat.
    private sealed class RecordingState {
        public required int ChannelCount { get; init; }
        public required long DurationTicks { get; init; }
        public required ProbeInstance Instance { get; init; }
        public long LastSequence = -1L;
        public required string Path { get; init; }
        public required double RateHz { get; init; }
        public List<ProbeTrackSample> Samples { get; } = [];
        public required long StartTimestamp { get; init; }
    }
}
