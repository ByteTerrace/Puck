using System.Diagnostics;
using System.Text.Json;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldProbes {
    // The generation every left-unbound optional socket resolves to: a single fixed instance, so an unbound socket
    // never itself triggers a restart (it can never change) yet still counts as "ready" (non-null) in the readiness
    // gate ServiceProbes applies to every other socket.
    private static readonly object s_unboundSocketGeneration = new();
    private static readonly ProbeKernelInput s_unboundKernelInput = new ProbeKernelInput.Unbound();
    private static readonly ProbeKernelInput s_colorKernelInput = new ProbeKernelInput.Sensor(Kind: CameraSensor.Color);
    private static readonly ProbeKernelInput s_infraredKernelInput = new ProbeKernelInput.Sensor(Kind: CameraSensor.Infrared);
    private static readonly ProbeKernelInput s_infraredStrobeKernelInput = new ProbeKernelInput.StrobePair(Kind: CameraSensor.Infrared);

    // Loads and deep-validates one declared probe row's static shape: its kind manifest (must exist, must be
    // kernel-class — a model-class kind has no registered host yet), its bound config, whether it is seat-relative,
    // and — for a track-input row — the recorded document itself; for a socket-input row, every socket binding the
    // document's own shallow validator could not see behind the kind vocabulary
    // (WorldDefinitionValidator.ValidateProbeStream's own remarks). No live instance is created here — the row's
    // instance(s) are built afterward by ReconcileInstances, once every row's id is registered.
    private static ProbeRowInfo BuildRow(string documentDirectory, int index, IReadOnlyDictionary<string, WorldProbe> probesById, WorldProbe row) {
        var path = $"probes[{index}] ('{row.Id}')";
        ProbeKindManifest manifest;

        try {
            manifest = WorldProbeKinds.Shipped.Load(id: row.Kind);
        } catch (Exception exception) {
            throw new InvalidOperationException(message: $"{path} kind '{row.Kind}' failed to load: {exception.Message}", innerException: exception);
        }

        if (manifest.Class != ProbeKindClass.Kernel) {
            throw new InvalidOperationException(message: $"{path} kind '{row.Kind}' is a {manifest.Class}-class kind; no host is registered for it in this build.");
        }
        if (!manifest.TryBindConfig(
            config: row.Config,
            values: out var config,
            reason: out var reason
        )) {
            throw new InvalidOperationException(message: $"{path} config is invalid: {reason}");
        }

        var constants = manifest.ConstantsBlock(values: config).ToArray();

        if (row.Inputs is { } inputs) {
            var (triggerSensor, singleInstanceSeat, isSeatRelative) = ValidateProbeSockets(
                inputs: inputs,
                manifest: manifest,
                path: path,
                probesById: probesById
            );

            return new ProbeRowInfo {
                ConstantsTemplate = constants,
                InstancesBySeat = (isSeatRelative ? [] : null),
                IsSeatRelative = isSeatRelative,
                Manifest = manifest,
                Row = row,
                SingleInstanceSeat = singleInstanceSeat,
                TriggerSensor = triggerSensor,
            };
        }
        if (row.Track is { } trackPath) {
            return new ProbeRowInfo {
                ConstantsTemplate = constants,
                IsSeatRelative = false,
                Manifest = manifest,
                Row = row,
                TrackDocument = LoadTrackDocument(
                    channelCount: manifest.Channels.Count,
                    documentDirectory: documentDirectory,
                    path: path,
                    trackPath: trackPath
                ),
            };
        }

        throw new InvalidOperationException(message: $"{path} declares neither 'inputs' nor 'track'.");
    }
    // Every deep check the document's own shallow validator left behind the kind vocabulary: a non-optional socket
    // is bound, a bound name is a declared socket, a strobePair socket binds only a camera Infrared source, the
    // trigger socket binds a camera source (kernels are hosted by a camera graph — this is what decides which
    // graph a kernel attaches to), the output.of socket is bound regardless of its own optionality, and a `probe`
    // socket names a probe whose kind declares an output. Returns the trigger socket's bound sensor, the seat a
    // NON-seat-relative row's single instance resolves against (every camera socket named its own seat, so the
    // trigger's is representative), and whether the row is seat-relative (WorldDefinitionValidator.IsSeatRelativeProbe's
    // runtime counterpart — at least one camera socket carries no seat).
    private static (WorldCameraSensor Sensor, int SingleInstanceSeat, bool IsSeatRelative) ValidateProbeSockets(IReadOnlyDictionary<string, WorldFrameSource> inputs, ProbeKindManifest manifest, string path, IReadOnlyDictionary<string, WorldProbe> probesById) {
        var sockets = manifest.Inputs;

        for (var socketIndex = 0; (socketIndex < sockets.Count); socketIndex++) {
            var socket = sockets[socketIndex];

            if (!socket.Optional && !inputs.ContainsKey(key: socket.Name)) {
                throw new InvalidOperationException(message: $"{path}.inputs is missing required socket '{socket.Name}'.");
            }
        }

        var isSeatRelative = false;

        foreach (var (socketName, source) in inputs) {
            if (!TryFindSocket(
                name: socketName,
                sockets: sockets,
                socket: out var socket
            )) {
                throw new InvalidOperationException(message: $"{path}.inputs['{socketName}'] names no socket of probe kind '{manifest.Name}'.");
            }
            if (
                (socket.Class == ProbeSocketClass.StrobePair) &&
                ((source is not WorldScreenSource.Camera camera) || (camera.Sensor != WorldCameraSensor.Infrared))
            ) {
                throw new InvalidOperationException(message: $"{path}.inputs['{socketName}'] is a strobePair socket; it must bind a camera source with sensor Infrared.");
            }
            if ((source is WorldScreenSource.Camera cameraSource) && (cameraSource.Seat is null)) {
                isSeatRelative = true;
            }
            if (source is WorldScreenSource.Probe probeSource) {
                if (!probesById.TryGetValue(key: probeSource.Id, value: out var targetRow)) {
                    throw new InvalidOperationException(message: $"{path}.inputs['{socketName}'].probe.id '{probeSource.Id}' names no declared probe.");
                }

                ProbeKindManifest targetManifest;

                try {
                    targetManifest = WorldProbeKinds.Shipped.Load(id: targetRow.Kind);
                } catch (Exception exception) {
                    throw new InvalidOperationException(message: $"{path}.inputs['{socketName}'].probe.id '{probeSource.Id}' kind '{targetRow.Kind}' failed to load: {exception.Message}", innerException: exception);
                }

                if (targetManifest.Output is null) {
                    throw new InvalidOperationException(message: $"{path}.inputs['{socketName}'].probe.id '{probeSource.Id}' names probe kind '{targetManifest.Name}', which writes no texture output.");
                }
            }
        }

        if (manifest.Output is { } output) {
            if (!inputs.ContainsKey(key: output.Of)) {
                throw new InvalidOperationException(message: $"{path}.inputs is missing socket '{output.Of}', named by the kind's output.of.");
            }
        }

        var triggerSocket = sockets[manifest.TriggerSocket];

        if (
            !inputs.TryGetValue(key: triggerSocket.Name, value: out var triggerSource) ||
            (triggerSource is not WorldScreenSource.Camera triggerCamera)
        ) {
            throw new InvalidOperationException(message: $"{path}.inputs['{triggerSocket.Name}'] is the trigger socket; it must bind a camera source (kernels are hosted by a camera graph).");
        }

        foreach (var (socketName, source) in inputs) {
            if ((source is WorldScreenSource.Camera camera) && (camera.Seat != triggerCamera.Seat)) {
                throw new InvalidOperationException(message: $"{path}.inputs['{socketName}'].camera.seat must match trigger socket '{triggerSocket.Name}' seat; one kernel run can bind only one camera graph.");
            }
        }

        return (triggerCamera.Sensor, (triggerCamera.Seat ?? 1), isSeatRelative);
    }
    private static bool TryFindSocket(string name, IReadOnlyList<ProbeKindInput> sockets, out ProbeKindInput socket) {
        for (var index = 0; (index < sockets.Count); index++) {
            if (string.Equals(a: sockets[index].Name, b: name, comparisonType: StringComparison.Ordinal)) {
                socket = sockets[index];

                return true;
            }
        }

        socket = null!;

        return false;
    }
    // Loads a track document and checks its channel count against the kind it stands in for: a binding's channel
    // ordinal resolves against the kind's manifest, so a track carrying fewer channels would fault at the first
    // conditioned step rather than at boot. A track-input row is never seat-relative, so this document is played
    // back by exactly one instance's ProbeTrackPlayer, built once ReconcileInstances creates it.
    private static ProbeTrackDocument LoadTrackDocument(int channelCount, string documentDirectory, string path, string trackPath) {
        var resolvedPath = Path.GetFullPath(path: Path.Combine(path1: documentDirectory, path2: trackPath));
        ProbeTrackDocument document;

        try {
            document = (JsonSerializer.Deserialize(json: File.ReadAllText(path: resolvedPath), jsonTypeInfo: ProbeTrackJsonContext.Default.ProbeTrackDocument)
                ?? throw new InvalidDataException(message: "the track document is empty or 'null'."));
        } catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException) {
            throw new InvalidOperationException(message: $"{path} track '{trackPath}' failed to load: {exception.Message}", innerException: exception);
        }

        if (document.Channels != channelCount) {
            throw new InvalidOperationException(message: $"{path} track '{trackPath}' carries {document.Channels} channel(s); the kind declares {channelCount}.");
        }

        // A trial construction against a throwaway ring proves the document's own sample-shape laws (ascending
        // time, matching channel counts) now, at boot, rather than at the first seat that ever instances this row.
        try {
            _ = new ProbeTrackPlayer(document: document, ring: new ProbeReadingRing());
        } catch (InvalidDataException exception) {
            throw new InvalidOperationException(message: $"{path} track '{trackPath}' is invalid: {exception.Message}", innerException: exception);
        }

        return document;
    }
    // Deep-validates and resolves every one of a row's declared bindings into a reusable template — the second half
    // of the original two-pass build (every row exists, by id, before any binding resolves a cross-probe reference).
    // Never builds live binding state; that happens once per instance, in BuildInstanceBindings.
    private void BuildRowBindingTemplates(int index, WorldProbe row) {
        if (row.Bindings is not { } bindings) {
            return;
        }

        var rowInfo = m_rows[index];

        for (var bindingIndex = 0; (bindingIndex < bindings.Count); bindingIndex++) {
            var path = $"probes[{index}].bindings[{bindingIndex}]";

            switch (bindings[bindingIndex]) {
                case WorldProbeBinding.Axis axis:
                    rowInfo.AxisTemplates.Add(item: BuildAxisTemplate(axis: axis, path: path, rowInfo: rowInfo));

                    break;
                case WorldProbeBinding.Parameter parameter:
                    rowInfo.ParameterTemplates.Add(item: BuildParameterTemplate(parameter: parameter, path: path, rowInfo: rowInfo));

                    break;
                case WorldProbeBinding.Control control:
                    rowInfo.ControlTemplates.Add(item: BuildControlTemplate(control: control, path: path, rowInfo: rowInfo));

                    break;
            }
        }
    }
    // Builds every one of an instance's binding rows from its row's already-validated templates — cheap, never
    // re-validating, since the templates already proved every channel/target/control name resolves.
    private void BuildInstanceBindings(ProbeInstance instance) {
        foreach (var template in instance.RowInfo.AxisTemplates) {
            instance.AxisBindings.Add(item: BuildAxis(instance: instance, template: template));
        }
        foreach (var template in instance.RowInfo.ParameterTemplates) {
            instance.ParameterBindings.Add(item: BuildParameter(instance: instance, template: template));
        }
        foreach (var template in instance.RowInfo.ControlTemplates) {
            instance.ControlBindings.Add(item: BuildControl(instance: instance, template: template));
        }
    }
    private static CameraSensor ToCameraSensor(WorldCameraSensor sensor) => (sensor switch {
        WorldCameraSensor.Color => CameraSensor.Color,
        WorldCameraSensor.Infrared => CameraSensor.Infrared,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(sensor), actualValue: sensor, message: "The camera sensor is not defined."),
    });
    // Follows the roster's occupancy: a non-seat-relative row's single instance exists once and forever (created
    // the first time this runs — from the constructor — and never retired here); a seat-relative row gains an
    // instance for every occupied local seat and loses one the moment its seat is no longer joined. Runs before
    // ServiceProbes every CaptureFrame, and once more from the constructor so a seat joined before boot has its
    // instances ready for the very first probe.status.
    private void ReconcileInstances() {
        foreach (var rowInfo in m_rows) {
            if (!rowInfo.IsSeatRelative) {
                if (rowInfo.SingleInstance is null) {
                    rowInfo.SingleInstance = CreateInstance(rowInfo: rowInfo, seat: rowInfo.SingleInstanceSeat);
                }

                continue;
            }

            var bySeat = rowInfo.InstancesBySeat!;

            for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
                var seat = PlayerRoster.DisplayNumber(slot: slot);
                var occupied = m_roster.IsJoined(slot: slot);
                var hasInstance = bySeat.TryGetValue(key: seat, value: out var existing);

                if (occupied && !hasInstance) {
                    bySeat[seat] = CreateInstance(rowInfo: rowInfo, seat: seat);
                } else if (!occupied && hasInstance) {
                    RetireInstance(instance: existing!);
                    bySeat.Remove(key: seat);
                }
            }
        }
    }
    // Builds one row's instance at one seat: a fresh reading ring, its own copy of the row's constants template, its
    // output ring key (seat 1 of a seat-relative row shares the row's bare id, so an authored screen/HUD `probe`
    // source — which carries no seat of its own — keeps resolving the same ring it always has; every other seat gets
    // its own "id@seat" ring), its track player when the row plays one back, and every one of its bindings' live
    // state (built from the row's already-validated templates — never re-validated per instance).
    private ProbeInstance CreateInstance(ProbeRowInfo rowInfo, int seat) {
        var ring = new ProbeReadingRing();
        var isSeatRelative = rowInfo.IsSeatRelative;
        var instance = new ProbeInstance {
            Constants = [.. rowInfo.ConstantsTemplate],
            Label = (isSeatRelative ? $"{rowInfo.Row.Id}@{seat}" : rowInfo.Row.Id),
            OutputRingKey = ((isSeatRelative && (seat != 1)) ? $"{rowInfo.Row.Id}@{seat}" : rowInfo.Row.Id),
            Inputs = new ProbeKernelInput[rowInfo.Manifest.Inputs.Count],
            ResolvedGenerations = new object?[rowInfo.Manifest.Inputs.Count],
            Ring = ring,
            RowInfo = rowInfo,
            Seat = seat,
            Track = ((rowInfo.TrackDocument is { } document) ? new ProbeTrackPlayer(document: document, ring: ring) : null),
        };

        if (rowInfo.Manifest.Output is not null) {
            m_screens.DeclareProbeOutput(id: instance.OutputRingKey);
        }
        if (rowInfo.Row.Inputs is { } inputs) {
            var retainedViews = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var source in inputs.Values) {
                if (source is WorldScreenSource.Camera camera) {
                    m_screens.RetainProbeCameraDemand(camera: camera, contextSeat: seat);
                    instance.CameraDemands.Add(item: camera);
                } else if ((source is WorldScreenSource.View view) && retainedViews.Add(item: view.CameraName)) {
                    m_screens.RetainViewExport(cameraName: view.CameraName);
                    instance.ViewExports.Add(item: view.CameraName);
                }
            }
        }

        BuildInstanceBindings(instance: instance);
        m_liveInstances.Add(item: instance);

        return instance;
    }
    // Ends the run, releases the output ring (if any), and drops every held router capture — the retirement path a
    // seat leaving and process Dispose share, so a mid-game leave can never leave a lane latched.
    private void RetireInstance(ProbeInstance instance) {
        instance.Run?.Dispose();
        instance.Run = null;

        foreach (var axis in instance.AxisBindings) {
            ReleaseAxisHeldState(axis: axis);
        }
        if (instance.RowInfo.Manifest.Output is not null) {
            m_screens.ReleaseProbeOutput(id: instance.OutputRingKey);
        }
        foreach (var cameraName in instance.ViewExports) {
            m_screens.ReleaseViewExport(cameraName: cameraName);
        }
        foreach (var camera in instance.CameraDemands) {
            m_screens.ReleaseProbeCameraDemand(camera: camera, contextSeat: instance.Seat);
        }

        _ = m_liveInstances.Remove(item: instance);
    }
    // Advances every track-input instance's player and services every socket-input instance's kernel run: resolves
    // every declared socket against the binder's live state, restarts the kernel when any socket's generation (or the
    // output ring's) changed since the last successful attach, and ends it when its own run has stopped (a
    // device-loss/end-of-stream condition the graph surfaces as IsEnded) so the next frame's readiness check starts
    // a fresh one against whatever the binder now reports.
    private void ServiceProbes() {
        var nowTimestamp = Stopwatch.GetTimestamp();

        foreach (var instance in m_liveInstances) {
            if (instance.Track is { } track) {
                _ = track.Advance(nowTimestamp: nowTimestamp);

                continue;
            }

            var manifest = instance.RowInfo.Manifest;
            var sockets = manifest.Inputs;
            var inputs = instance.Inputs;
            var generations = instance.ResolvedGenerations;
            string? fault = null;

            for (var socketIndex = 0; (socketIndex < sockets.Count); socketIndex++) {
                var socket = sockets[socketIndex];

                instance.RowInfo.Row.Inputs!.TryGetValue(key: socket.Name, value: out var source);

                var (input, generation, extent, socketFault) = ResolveSocket(
                    socket: socket,
                    source: source,
                    contextSeat: instance.Seat,
                    previousGeneration: generations[socketIndex],
                    previousInput: inputs[socketIndex]
                );

                inputs[socketIndex] = input;
                generations[socketIndex] = generation;
                fault ??= socketFault;

                if ((manifest.Output is { } declaredOutput) && string.Equals(a: declaredOutput.Of, b: socket.Name, comparisonType: StringComparison.Ordinal)) {
                    instance.OutputExtent = extent;
                }
            }

            object? outputSet = null;

            if (manifest.Output is not null) {
                if (instance.OutputExtent is { Width: > 0, Height: > 0 } extent) {
                    _ = m_screens.TryGetProbeOutput(id: instance.OutputRingKey, width: extent.Width, height: extent.Height, output: out _, generation: out outputSet, fault: out var outputFault);
                    fault ??= ((outputSet is null) ? outputFault : null);
                } else {
                    fault ??= "probe output awaiting provisioning";
                }
            }

            if (fault is not null) {
                EndRun(instance: instance, fault: fault);
                instance.SocketGenerations = null;
                instance.OutputSet = null;

                continue;
            }

            var changed = ((instance.SocketGenerations is not { } previous) || !SocketGenerationsEqual(current: generations, previous: previous) || !ReferenceEquals(objA: instance.OutputSet, objB: outputSet));

            if (changed) {
                RestartRun(generations: generations, inputs: inputs, outputSet: outputSet, instance: instance);

                continue;
            }

            if (instance.Run is { IsEnded: true } endedRun) {
                instance.Fault = (endedRun.Fault ?? "the probe kernel run ended");
                endedRun.Dispose();
                instance.Run = null;
                // Force the next frame's readiness check to retry from scratch rather than waiting for a socket's
                // generation to change again — a transient GPU fault, not necessarily a reopen.
                instance.SocketGenerations = null;
            }
        }
    }
    // Resolves one socket against the binder's current live state: the ProbeKernelInput arm to bind, its
    // generation (null while not ready — the caller never attaches on a null generation), the extent its bound
    // source renders at (meaningful only for the socket the kind's output.of names), and a fault naming why it is
    // not ready (null once it is). contextSeat is the enclosing instance's own seat — what a seat-less camera socket
    // (or a `probe` socket naming a seat-relative target) resolves against.
    private (ProbeKernelInput Input, object? Generation, (int Width, int Height)? Extent, string? Fault) ResolveSocket(ProbeKindInput socket, WorldFrameSource? source, int contextSeat, ProbeKernelInput? previousInput, object? previousGeneration) {
        if (source is null) {
            return (s_unboundKernelInput, s_unboundSocketGeneration, null, null);
        }

        switch (source) {
            case WorldScreenSource.Camera camera:
                var socketSeat = (camera.Seat ?? contextSeat);

                if (!m_screens.TryGetCameraAttachment(seat: socketSeat, sensor: camera.Sensor, attachment: out var attachment)) {
                    var cameraFault = ((m_screens.ResolvedCameraToken(seat: socketSeat) is null)
                        ? $"no camera assigned to seat {socketSeat}"
                        : $"no camera feed for seat {socketSeat}"
                    );

                    return (s_unboundKernelInput, null, null, cameraFault);
                }
                if ((attachment.Shared is not { } shared) || (attachment.TargetSet is not { } targetSet)) {
                    return (s_unboundKernelInput, null, null, "probe needs the camera GPU tier");
                }

                var sensor = ToCameraSensor(sensor: camera.Sensor);
                var sensorInput = ((socket.Class == ProbeSocketClass.StrobePair)
                    ? s_infraredStrobeKernelInput
                    : ((sensor == CameraSensor.Color) ? s_colorKernelInput : s_infraredKernelInput)
                );

                return (sensorInput, targetSet, (shared.Width, shared.Height), null);
            case WorldScreenSource.Probe probeSource:
                if (!m_rowIndexById.TryGetValue(key: probeSource.Id, value: out var targetRowIndex)) {
                    return (s_unboundKernelInput, null, null, $"probe '{probeSource.Id}' names no declared probe");
                }

                var targetRow = m_rows[targetRowIndex];
                var targetInstance = ResolveInstance(target: targetRow, contextSeat: contextSeat);

                if (targetInstance is null) {
                    return (s_unboundKernelInput, null, null, $"probe '{probeSource.Id}' has no live instance for seat {contextSeat}");
                }
                if (targetInstance.OutputExtent is not { Width: > 0, Height: > 0 } wanted) {
                    return (s_unboundKernelInput, null, null, $"probe '{probeSource.Id}' output is not provisioned yet");
                }
                if (!m_screens.TryGetProbeOutput(id: targetInstance.OutputRingKey, width: wanted.Width, height: wanted.Height, output: out var ringOutput, generation: out var ringGeneration, fault: out var ringFault)) {
                    return (s_unboundKernelInput, null, null, ringFault);
                }

                if (ReferenceEquals(objA: ringGeneration, objB: previousGeneration) && (previousInput is ProbeKernelInput.Ring previousRing)) {
                    return (previousRing, ringGeneration, (ringOutput.Width, ringOutput.Height), null);
                }

                return (new ProbeKernelInput.Ring(Width: ringOutput.Width, Height: ringOutput.Height, Format: ringOutput.TargetFormat, SharedTargetHandles: ringOutput.SharedTargetHandles, Slots: ringOutput.Slots), ringGeneration, (ringOutput.Width, ringOutput.Height), null);
            case WorldScreenSource.View view:
                if (!m_screens.TryGetViewExport(cameraName: view.CameraName, ring: out var viewRing, generation: out var viewGeneration, fault: out var viewFault)) {
                    return (s_unboundKernelInput, null, null, viewFault);
                }

                return (viewRing, viewGeneration, (viewRing.Width, viewRing.Height), null);
            case WorldScreenSource.Capture:
                return (s_unboundKernelInput, null, null, "capture probe inputs are rejected during world validation");
            default:
                return (s_unboundKernelInput, null, null, "unrecognized frame source");
        }
    }
    private static bool SocketGenerationsEqual(object?[] current, object?[] previous) {
        for (var index = 0; (index < previous.Length); index++) {
            if (!ReferenceEquals(objA: previous[index], objB: current[index])) {
                return false;
            }
        }

        return true;
    }
    // Ends and clears whatever run an instance currently holds, recording a fault when one is given. Used both when a
    // socket falls out of readiness and right before a restart attempt (so a failed restart never leaves a stale
    // run reference behind).
    private static void EndRun(ProbeInstance instance, string? fault) {
        instance.Run?.Dispose();
        instance.Run = null;

        if (fault is not null) {
            instance.Fault = fault;
        }
    }
    // The whole restart decision for one socket-input instance against a freshly resolved, fully ready socket set:
    // end whatever ran before, adopt the new generations unconditionally (so a failed attach never retries every
    // single frame against the same dead generation set — only a further generation change tries again), provision
    // the declared output ring at its resolved extent when the kind writes one, resolve the trigger sensor's
    // kernel host at this instance's own seat, and attach.
    private void RestartRun(ProbeInstance instance, ProbeKernelInput[] inputs, object?[] generations, object? outputSet) {
        EndRun(instance: instance, fault: null);
        instance.SocketGenerations = [.. generations];
        instance.OutputSet = outputSet;

        ProbeKernelOutput? output = null;

        if (instance.RowInfo.Manifest.Output is not null) {
            var extent = instance.OutputExtent!.Value;

            if (!m_screens.TryGetProbeOutput(id: instance.OutputRingKey, width: extent.Width, height: extent.Height, output: out var provisioned, generation: out _, fault: out var outputFault)) {
                instance.Fault = outputFault;

                return;
            }

            output = provisioned;
        }
        if (!m_screens.TryGetCameraAttachment(seat: instance.Seat, sensor: instance.RowInfo.TriggerSensor!.Value, attachment: out var triggerAttachment) || (triggerAttachment.Kernels is not { } kernels)) {
            instance.Fault = "the open camera graph hosts no kernels";

            return;
        }

        var kernel = (instance.RowInfo.Manifest.Kernel ?? throw new InvalidOperationException(message: $"probe kind '{instance.RowInfo.Manifest.Name}' is kernel-class but declares no kernel block."));
        // The camera graph keeps this request on its worker thread for the whole run. The per-instance Inputs array
        // above is render-thread scratch, rewritten on every service pass, so attach an immutable snapshot: a later
        // generation resolve must never substitute new ring handles beneath an older run's already-opened SRVs.
        var attachedInputs = inputs.ToArray();
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: Path.Combine(path1: instance.RowInfo.Manifest.Directory, path2: kernel.Source)),
            AccumulateEntry: kernel.Accumulate,
            FinalizeEntry: kernel.Finalize,
            Constants: instance.Constants,
            ChannelCount: instance.RowInfo.Manifest.Channels.Count,
            RateHz: instance.RowInfo.Row.RateHz,
            Inputs: attachedInputs,
            Trigger: ToCameraSensor(sensor: instance.RowInfo.TriggerSensor.Value),
            Output: output
        );

        if (kernels.TryAttachKernel(
            request: in request,
            ring: instance.Ring,
            run: out var run,
            fault: out var fault
        )) {
            instance.Fault = null;
            instance.Run = run;
        } else {
            instance.Fault = fault;
        }
    }
}
