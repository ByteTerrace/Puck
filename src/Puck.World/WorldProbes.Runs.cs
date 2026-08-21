using System.Diagnostics;
using System.Text.Json;
using Puck.Platform.Probes;
using Puck.Shaders;

namespace Puck.World;

internal sealed partial class WorldProbes {
    // Loads and deep-validates one declared probe row: its kind manifest (must exist, must be KERNEL-class — a
    // MODEL-class kind has no registered host yet, and its probe never resolves this platform's document
    // vocabulary either, so no shipped kind reaches this branch today), its bound config, and — for a track-input
    // row — the recorded document itself. Every reading ring is created here, once, so a binding against this
    // probe always has somewhere to read from regardless of whether a live run ever starts.
    private ProbeState BuildProbe(string documentDirectory, int index, WorldProbe row) {
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

        switch (row.Input) {
            case WorldProbeInput.Camera camera:
                if (ToProbeInputSensor(sensor: camera.Sensor) != manifest.Input.Sensor) {
                    throw new InvalidOperationException(message: $"{path}.input.sensor '{camera.Sensor}' is not the '{manifest.Input.Sensor}' sensor kind '{row.Kind}' declares as its input.");
                }

                return new ProbeState {
                    Config = config,
                    Manifest = manifest,
                    Ring = ring,
                    Row = row,
                    Sensor = camera.Sensor,
                };
            case WorldProbeInput.Track track:
                return new ProbeState {
                    Config = config,
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
    private static ProbeInputSensor ToProbeInputSensor(WorldCameraSensor sensor) => (sensor switch {
        WorldCameraSensor.Color => ProbeInputSensor.Color,
        WorldCameraSensor.Infrared => ProbeInputSensor.Infrared,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(sensor), actualValue: sensor, message: "The camera sensor is not defined."),
    });
    // Advances every track-input probe's player and services every camera-input probe's kernel run: restarts
    // it when the binder's attachment names a new target-ring generation, and ends it when its own run has stopped
    // (a device-loss/end-of-stream condition the runner surfaces as IsEnded) so the next frame's generation check
    // starts a fresh one against whatever the binder now reports.
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

            if (!ReferenceEquals(objA: probe.TargetSet, objB: attachment.TargetSet)) {
                RestartRun(probe: probe, attachment: in attachment);

                continue;
            }

            if (probe.Run is { IsEnded: true } endedRun) {
                probe.Fault = (endedRun.Fault ?? "the sense kernel run ended");
                endedRun.Dispose();
                probe.Run = null;
                // Force the next frame's generation check to retry against the SAME attachment (a transient GPU
                // fault, not necessarily a reopen) rather than waiting for the binder to hand out a new TargetSet.
                probe.TargetSet = null;
            }
        }
    }
    // Ends and clears whatever run an probe currently holds, recording a fault when one is given. Used both when
    // an attachment disappears and right before a restart attempt (so a failed restart never leaves a stale run
    // reference behind).
    private static void EndRun(ProbeState probe, string? fault) {
        probe.Run?.Dispose();
        probe.Run = null;

        if (fault is not null) {
            probe.Fault = fault;
        }
    }
    // The whole restart decision for one camera-input probe against a freshly observed attachment generation:
    // end whatever ran before, adopt the new generation marker unconditionally (so a failed start does not retry
    // every single frame against the same dead generation), and start a new run only when the attachment actually
    // carries a live shared stream on a platform whose kernel host is supported.
    private void RestartRun(ProbeState probe, in WorldCameraAttachment attachment) {
        EndRun(probe: probe, fault: null);
        probe.TargetSet = attachment.TargetSet;

        if ((attachment.Shared is not { } stream) || (attachment.TargetSet is null)) {
            probe.Fault = "probe needs the camera GPU tier";

            return;
        }
        if (!m_kernelHost.IsSupported) {
            probe.Fault = "no sense kernel host is registered on this platform";

            return;
        }
        if (attachment.AdapterLuid is not { } adapterLuid) {
            probe.Fault = "the render adapter reports no LUID";

            return;
        }

        var kernel = (probe.Manifest.Kernel ?? throw new InvalidOperationException(message: $"probe kind '{probe.Manifest.Name}' is kernel-class but declares no kernel block."));
        var request = new ProbeKernelRequest(
            AccumulateEntry: kernel.Accumulate,
            AdapterLuid: adapterLuid,
            ChannelCount: probe.Manifest.Channels.Count,
            FinalizeEntry: kernel.Finalize,
            Height: stream.Height,
            KernelSource: File.ReadAllText(path: Path.Combine(path1: probe.Manifest.Directory, path2: kernel.Source)),
            RateHz: probe.Row.RateHz,
            TargetFormat: stream.TargetFormat,
            Width: stream.Width,
            Constants: probe.Manifest.ConstantsBlock(values: probe.Config)
        );

        if (m_kernelHost.TryStart(
            request: in request,
            ring: probe.Ring,
            run: out var run,
            sharedTargetHandles: attachment.SharedHandles,
            stream: stream,
            fault: out var fault
        )) {
            probe.Fault = null;
            probe.Run = run;
        } else {
            probe.Fault = fault;
        }
    }
}
