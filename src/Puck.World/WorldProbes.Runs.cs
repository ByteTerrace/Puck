using System.Diagnostics;
using System.Text.Json;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.Shaders;

namespace Puck.World;

internal sealed partial class WorldProbes {
    // Loads and deep-validates one declared probe row: its kind manifest (must exist, must be kernel-class — a
    // model-class kind has no registered host yet), its bound config, and — for a track-input row — the recorded
    // document itself. Every reading ring is created here, once, so a binding against this probe always has
    // somewhere to read from regardless of whether a live run ever starts.
    private static ProbeState BuildProbe(string documentDirectory, int index, WorldProbe row) {
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

        switch (row.Input) {
            case WorldProbeInput.Camera camera:
                if (ToProbeInputSensor(sensor: camera.Sensor) != manifest.TriggerSensor) {
                    throw new InvalidOperationException(message: $"{path}.input.sensor '{camera.Sensor}' is not the '{manifest.TriggerSensor}' sensor kind '{row.Kind}' runs on.");
                }

                return new ProbeState {
                    Constants = constants,
                    Manifest = manifest,
                    Ring = ring,
                    Row = row,
                    Sensor = camera.Sensor,
                };
            case WorldProbeInput.Track track:
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
                        trackPath: track.Path
                    ),
                };
            default:
                throw new InvalidOperationException(message: $"{path}.input is an unrecognized probe input kind.");
        }
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
    private static CameraSensor ToCameraSensor(ProbeInputSensor sensor) => (sensor switch {
        ProbeInputSensor.Color => CameraSensor.Color,
        ProbeInputSensor.Infrared => CameraSensor.Infrared,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(sensor), actualValue: sensor, message: "The probe input sensor is not defined."),
    });
    private static ProbeInputSensor ToProbeInputSensor(WorldCameraSensor sensor) => (sensor switch {
        WorldCameraSensor.Color => ProbeInputSensor.Color,
        WorldCameraSensor.Infrared => ProbeInputSensor.Infrared,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(sensor), actualValue: sensor, message: "The camera sensor is not defined."),
    });
    private static WorldCameraSensor ToWorldSensor(ProbeInputSensor sensor) => (sensor switch {
        ProbeInputSensor.Color => WorldCameraSensor.Color,
        ProbeInputSensor.Infrared => WorldCameraSensor.Infrared,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(sensor), actualValue: sensor, message: "The probe input sensor is not defined."),
    });
    // Advances every track-input probe's player and services every camera-input probe's kernel run: restarts it
    // when the binder's attachment names a new target-ring generation (or, for a texture-writing kind, a new
    // output-ring generation), and ends it when its own run has stopped (a device-loss/end-of-stream condition the
    // graph surfaces as IsEnded) so the next frame's generation check starts a fresh one against whatever the binder
    // now reports.
    private void ServiceProbes() {
        var nowTimestamp = Stopwatch.GetTimestamp();

        foreach (var probe in m_probes) {
            if (probe.Track is { } track) {
                _ = track.Advance(nowTimestamp: nowTimestamp);

                continue;
            }

            if (probe.Sensor is not { } sensor) {
                continue;
            }

            if (!m_screens.TryGetCameraAttachment(sensor: sensor, attachment: out var attachment)) {
                EndRun(probe: probe, fault: "no camera feed for this sensor");
                probe.TargetSet = null;

                continue;
            }

            object? outputSet = null;

            if ((probe.Manifest.Output is { } output) && (OutputExtent(output: output) is { Width: > 0, Height: > 0 } extent)) {
                _ = m_screens.TryGetProbeOutput(id: probe.Row.Id, width: extent.Width, height: extent.Height, output: out _, generation: out outputSet, fault: out _);
            }

            if (!ReferenceEquals(objA: probe.TargetSet, objB: attachment.TargetSet) || !ReferenceEquals(objA: probe.OutputSet, objB: outputSet)) {
                RestartRun(probe: probe, attachment: in attachment);

                continue;
            }

            if (probe.Run is { IsEnded: true } endedRun) {
                probe.Fault = (endedRun.Fault ?? "the probe kernel run ended");
                endedRun.Dispose();
                probe.Run = null;
                // Force the next frame's generation check to retry against the same attachment (a transient GPU
                // fault, not necessarily a reopen) rather than waiting for the binder to hand out a new generation.
                probe.TargetSet = null;
            }
        }
    }
    // The extent a texture-writing kind's output takes: the declared sensor's live stream extent, or (0, 0) while
    // that sensor has no started shared stream — which the binder never provisions.
    private (int Width, int Height) OutputExtent(ProbeKindOutput output) => (m_screens.TryGetCameraAttachment(sensor: ToWorldSensor(sensor: output.Of), attachment: out var attachment) && (attachment.Shared is { } shared)
        ? (shared.Width, shared.Height)
        : (0, 0)
    );
    // Ends and clears whatever run a probe currently holds, recording a fault when one is given. Used both when an
    // attachment disappears and right before a restart attempt (so a failed restart never leaves a stale run
    // reference behind).
    private static void EndRun(ProbeState probe, string? fault) {
        probe.Run?.Dispose();
        probe.Run = null;

        if (fault is not null) {
            probe.Fault = fault;
        }
    }
    // The whole restart decision for one camera-input probe against freshly observed generations: end whatever ran
    // before, adopt the new generation markers unconditionally (so a failed start does not retry every single frame
    // against the same dead generation), and attach a new kernel only when the attachment actually carries a live
    // shared stream on a graph that hosts kernels and — for a texture-writing kind — the binder has provisioned the
    // output ring at the declared sensor's extent.
    private void RestartRun(ProbeState probe, in WorldCameraAttachment attachment) {
        EndRun(probe: probe, fault: null);
        probe.TargetSet = attachment.TargetSet;
        probe.OutputSet = null;

        if ((attachment.Shared is null) || (attachment.TargetSet is null)) {
            probe.Fault = "probe needs the camera GPU tier";

            return;
        }
        if (attachment.Kernels is not { } kernels) {
            probe.Fault = "the open camera graph hosts no kernels";

            return;
        }

        ProbeKernelOutput? output = null;

        if (probe.Manifest.Output is { } declaredOutput) {
            var (width, height) = OutputExtent(output: declaredOutput);

            if ((width <= 0) || (height <= 0)) {
                probe.Fault = $"probe output needs the {declaredOutput.Of.ToString().ToLowerInvariant()} camera GPU tier";

                return;
            }
            if (!m_screens.TryGetProbeOutput(id: probe.Row.Id, width: width, height: height, output: out var provisioned, generation: out var generation, fault: out var outputFault)) {
                probe.Fault = outputFault;
                // The ring arrives at a later publish; a null generation re-enters here on the next frame.
                probe.TargetSet = null;

                return;
            }

            output = provisioned;
            probe.OutputSet = generation;
        }

        var kernel = (probe.Manifest.Kernel ?? throw new InvalidOperationException(message: $"probe kind '{probe.Manifest.Name}' is kernel-class but declares no kernel block."));
        var inputs = new ProbeKernelInput[probe.Manifest.Inputs.Count];

        for (var index = 0; (index < inputs.Length); index++) {
            var input = probe.Manifest.Inputs[index];

            inputs[index] = new ProbeKernelInput(Sensor: ToCameraSensor(sensor: input.Sensor), Previous: input.Previous);
        }

        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: Path.Combine(path1: probe.Manifest.Directory, path2: kernel.Source)),
            AccumulateEntry: kernel.Accumulate,
            FinalizeEntry: kernel.Finalize,
            Constants: probe.Constants,
            ChannelCount: probe.Manifest.Channels.Count,
            RateHz: probe.Row.RateHz,
            Inputs: inputs,
            Trigger: ToCameraSensor(sensor: probe.Manifest.TriggerSensor),
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
