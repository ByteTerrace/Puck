using System.Diagnostics;
using System.Text.Json;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.Shaders;

namespace Puck.World;

internal sealed partial class WorldProbes {
    // The generation every left-unbound optional socket resolves to: a single fixed instance, so an unbound socket
    // never itself triggers a restart (it can never change) yet still counts as "ready" (non-null) in the readiness
    // gate ServiceProbes applies to every other socket.
    private static readonly object s_unboundSocketGeneration = new();

    // Loads and deep-validates one declared probe row: its kind manifest (must exist, must be kernel-class — a
    // model-class kind has no registered host yet), its bound config, and — for a track-input row — the recorded
    // document itself; for a socket-input row, every socket binding the document's own shallow validator could not
    // see behind the kind vocabulary (WorldDefinitionValidator.ValidateProbeStream's own remarks). Every reading
    // ring is created here, once, so a binding against this probe always has somewhere to read from regardless of
    // whether a live run ever starts.
    private static ProbeState BuildProbe(string documentDirectory, int index, IReadOnlyDictionary<string, WorldProbe> probesById, WorldProbe row) {
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

        var ring = new ProbeReadingRing();
        var constants = manifest.ConstantsBlock(values: config).ToArray();

        if (row.Inputs is { } inputs) {
            var triggerSensor = ValidateProbeSockets(
                inputs: inputs,
                manifest: manifest,
                path: path,
                probesById: probesById
            );

            return new ProbeState {
                Constants = constants,
                Manifest = manifest,
                Ring = ring,
                Row = row,
                TriggerSensor = triggerSensor,
            };
        }
        if (row.Track is { } trackPath) {
            return new ProbeState {
                Constants = constants,
                Manifest = manifest,
                Ring = ring,
                Row = row,
                Track = BuildTrackPlayer(
                    channelCount: manifest.Channels.Count,
                    documentDirectory: documentDirectory,
                    path: path,
                    ring: ring,
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
    // socket names a probe whose kind declares an output. Returns the trigger socket's bound sensor.
    private static WorldCameraSensor ValidateProbeSockets(IReadOnlyDictionary<string, WorldFrameSource> inputs, ProbeKindManifest manifest, string path, IReadOnlyDictionary<string, WorldProbe> probesById) {
        var sockets = manifest.Inputs;

        for (var socketIndex = 0; (socketIndex < sockets.Count); socketIndex++) {
            var socket = sockets[socketIndex];

            if (!socket.Optional && !inputs.ContainsKey(key: socket.Name)) {
                throw new InvalidOperationException(message: $"{path}.inputs is missing required socket '{socket.Name}'.");
            }
        }

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

        return triggerCamera.Sensor;
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
    // conditioned step rather than at boot.
    private static ProbeTrackPlayer BuildTrackPlayer(int channelCount, string documentDirectory, string path, ProbeReadingRing ring, string trackPath) {
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

        try {
            return new ProbeTrackPlayer(document: document, ring: ring);
        } catch (InvalidDataException exception) {
            throw new InvalidOperationException(message: $"{path} track '{trackPath}' is invalid: {exception.Message}", innerException: exception);
        }
    }
    private static CameraSensor ToCameraSensor(WorldCameraSensor sensor) => (sensor switch {
        WorldCameraSensor.Color => CameraSensor.Color,
        WorldCameraSensor.Infrared => CameraSensor.Infrared,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(sensor), actualValue: sensor, message: "The camera sensor is not defined."),
    });
    // Advances every track-input probe's player and services every socket-input probe's kernel run: resolves every
    // declared socket against the binder's live state, restarts the kernel when any socket's generation (or the
    // output ring's) changed since the last successful attach, and ends it when its own run has stopped (a
    // device-loss/end-of-stream condition the graph surfaces as IsEnded) so the next frame's readiness check starts
    // a fresh one against whatever the binder now reports.
    private void ServiceProbes() {
        var nowTimestamp = Stopwatch.GetTimestamp();

        foreach (var probe in m_probes) {
            if (probe.Track is { } track) {
                _ = track.Advance(nowTimestamp: nowTimestamp);

                continue;
            }

            var manifest = probe.Manifest;
            var sockets = manifest.Inputs;
            var inputs = new ProbeKernelInput[sockets.Count];
            var generations = new object?[sockets.Count];
            string? fault = null;

            for (var socketIndex = 0; (socketIndex < sockets.Count); socketIndex++) {
                var socket = sockets[socketIndex];

                probe.Row.Inputs!.TryGetValue(key: socket.Name, value: out var source);

                var (input, generation, extent, socketFault) = ResolveSocket(socket: socket, source: source);

                inputs[socketIndex] = input;
                generations[socketIndex] = generation;
                fault ??= socketFault;

                if ((manifest.Output is { } declaredOutput) && string.Equals(a: declaredOutput.Of, b: socket.Name, comparisonType: StringComparison.Ordinal)) {
                    probe.OutputExtent = extent;
                }
            }

            object? outputSet = null;

            if (manifest.Output is not null) {
                if (probe.OutputExtent is { Width: > 0, Height: > 0 } extent) {
                    _ = m_screens.TryGetProbeOutput(id: probe.Row.Id, width: extent.Width, height: extent.Height, output: out _, generation: out outputSet, fault: out var outputFault);
                    fault ??= ((outputSet is null) ? outputFault : null);
                } else {
                    fault ??= "probe output awaiting provisioning";
                }
            }

            if (fault is not null) {
                EndRun(probe: probe, fault: fault);
                probe.SocketGenerations = null;
                probe.OutputSet = null;

                continue;
            }

            var changed = ((probe.SocketGenerations is not { } previous) || !SocketGenerationsEqual(current: generations, previous: previous) || !ReferenceEquals(objA: probe.OutputSet, objB: outputSet));

            if (changed) {
                RestartRun(generations: generations, inputs: inputs, outputSet: outputSet, probe: probe);

                continue;
            }

            if (probe.Run is { IsEnded: true } endedRun) {
                probe.Fault = (endedRun.Fault ?? "the probe kernel run ended");
                endedRun.Dispose();
                probe.Run = null;
                // Force the next frame's readiness check to retry from scratch rather than waiting for a socket's
                // generation to change again — a transient GPU fault, not necessarily a reopen.
                probe.SocketGenerations = null;
            }
        }
    }
    // Resolves one socket against the binder's current live state: the ProbeKernelInput arm to bind, its
    // generation (null while not ready — the caller never attaches on a null generation), the extent its bound
    // source renders at (meaningful only for the socket the kind's output.of names), and a fault naming why it is
    // not ready (null once it is).
    private (ProbeKernelInput Input, object? Generation, (int Width, int Height)? Extent, string? Fault) ResolveSocket(ProbeKindInput socket, WorldFrameSource? source) {
        if (source is null) {
            return (new ProbeKernelInput.Unbound(), s_unboundSocketGeneration, null, null);
        }

        switch (source) {
            case WorldScreenSource.Camera camera:
                if (!m_screens.TryGetCameraAttachment(sensor: camera.Sensor, attachment: out var attachment)) {
                    return (new ProbeKernelInput.Unbound(), null, null, "no camera feed for this sensor");
                }
                if ((attachment.Shared is not { } shared) || (attachment.TargetSet is not { } targetSet)) {
                    return (new ProbeKernelInput.Unbound(), null, null, "probe needs the camera GPU tier");
                }

                var sensorInput = ((socket.Class == ProbeSocketClass.StrobePair)
                    ? ((ProbeKernelInput)new ProbeKernelInput.StrobePair(Kind: ToCameraSensor(sensor: camera.Sensor)))
                    : new ProbeKernelInput.Sensor(Kind: ToCameraSensor(sensor: camera.Sensor))
                );

                return (sensorInput, targetSet, (shared.Width, shared.Height), null);
            case WorldScreenSource.Probe probeSource:
                if (!m_probeIndexById.TryGetValue(key: probeSource.Id, value: out var targetIndex)) {
                    return (new ProbeKernelInput.Unbound(), null, null, $"probe '{probeSource.Id}' names no declared probe");
                }
                if (m_probes[targetIndex].OutputExtent is not { Width: > 0, Height: > 0 } wanted) {
                    return (new ProbeKernelInput.Unbound(), null, null, $"probe '{probeSource.Id}' output is not provisioned yet");
                }
                if (!m_screens.TryGetProbeOutput(id: probeSource.Id, width: wanted.Width, height: wanted.Height, output: out var ringOutput, generation: out var ringGeneration, fault: out var ringFault)) {
                    return (new ProbeKernelInput.Unbound(), null, null, ringFault);
                }

                return (new ProbeKernelInput.Ring(Width: ringOutput.Width, Height: ringOutput.Height, Format: ringOutput.TargetFormat, SharedTargetHandles: ringOutput.SharedTargetHandles, Slots: ringOutput.Slots), ringGeneration, (ringOutput.Width, ringOutput.Height), null);
            case WorldScreenSource.View view:
                if (!m_screens.TryGetViewExport(cameraName: view.CameraName, ring: out var viewRing, generation: out var viewGeneration, fault: out var viewFault)) {
                    return (new ProbeKernelInput.Unbound(), null, null, viewFault);
                }

                return (viewRing, viewGeneration, (viewRing.Width, viewRing.Height), null);
            case WorldScreenSource.Capture:
                return (new ProbeKernelInput.Unbound(), null, null, "capture sources are not hosted as kernel inputs yet");
            default:
                return (new ProbeKernelInput.Unbound(), null, null, "unrecognized frame source");
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
    // Ends and clears whatever run a probe currently holds, recording a fault when one is given. Used both when a
    // socket falls out of readiness and right before a restart attempt (so a failed restart never leaves a stale
    // run reference behind).
    private static void EndRun(ProbeState probe, string? fault) {
        probe.Run?.Dispose();
        probe.Run = null;

        if (fault is not null) {
            probe.Fault = fault;
        }
    }
    // The whole restart decision for one socket-input probe against a freshly resolved, fully ready socket set:
    // end whatever ran before, adopt the new generations unconditionally (so a failed attach never retries every
    // single frame against the same dead generation set — only a further generation change tries again), provision
    // the declared output ring at its resolved extent when the kind writes one, resolve the trigger sensor's
    // kernel host, and attach.
    private void RestartRun(ProbeState probe, ProbeKernelInput[] inputs, object?[] generations, object? outputSet) {
        EndRun(probe: probe, fault: null);
        probe.SocketGenerations = generations;
        probe.OutputSet = outputSet;

        ProbeKernelOutput? output = null;

        if (probe.Manifest.Output is not null) {
            var extent = probe.OutputExtent!.Value;

            if (!m_screens.TryGetProbeOutput(id: probe.Row.Id, width: extent.Width, height: extent.Height, output: out var provisioned, generation: out _, fault: out var outputFault)) {
                probe.Fault = outputFault;

                return;
            }

            output = provisioned;
        }
        if (!m_screens.TryGetCameraAttachment(sensor: probe.TriggerSensor!.Value, attachment: out var triggerAttachment) || (triggerAttachment.Kernels is not { } kernels)) {
            probe.Fault = "the open camera graph hosts no kernels";

            return;
        }

        var kernel = (probe.Manifest.Kernel ?? throw new InvalidOperationException(message: $"probe kind '{probe.Manifest.Name}' is kernel-class but declares no kernel block."));
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: Path.Combine(path1: probe.Manifest.Directory, path2: kernel.Source)),
            AccumulateEntry: kernel.Accumulate,
            FinalizeEntry: kernel.Finalize,
            Constants: probe.Constants,
            ChannelCount: probe.Manifest.Channels.Count,
            RateHz: probe.Row.RateHz,
            Inputs: inputs,
            Trigger: ToCameraSensor(sensor: probe.TriggerSensor.Value),
            Output: output
        );

        if (kernels.TryAttachKernel(
            request: in request,
            ring: probe.Ring,
            run: out var run,
            fault: out var fault
        )) {
            probe.Fault = null;
            probe.Run = run;
        } else {
            probe.Fault = fault;
        }
    }
}
