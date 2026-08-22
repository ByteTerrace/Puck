using System.Diagnostics;
using Puck.Platform;

namespace Puck.World;

internal sealed partial class WorldProbes {
    // The document member name -> CameraControl mapping WorldDefinitionValidator.ProbeControlNames already proved
    // exhaustive at document load — this is the runtime half of that same vocabulary.
    private static readonly Dictionary<string, CameraControl> ProbeControlsByName = new(comparer: StringComparer.Ordinal) {
        ["backlightCompensation"] = CameraControl.BacklightCompensation,
        ["brightness"] = CameraControl.Brightness,
        ["contrast"] = CameraControl.Contrast,
        ["exposure"] = CameraControl.Exposure,
        ["fieldOfView"] = CameraControl.FieldOfView,
        ["focus"] = CameraControl.Focus,
        ["gain"] = CameraControl.Gain,
        ["pan"] = CameraControl.Pan,
        ["saturation"] = CameraControl.Saturation,
        ["sharpness"] = CameraControl.Sharpness,
        ["tilt"] = CameraControl.Tilt,
        ["whiteBalance"] = CameraControl.WhiteBalance,
        ["zoom"] = CameraControl.Zoom,
    };
    // Only every 4th host frame's controls are actually written (device writes are slow) — a fixed frame-key
    // divisor, not a document field: it paces HOW OFTEN the surface is touched, never what value it settles on.
    private const ulong ControlWriteFrameDivisor = 4UL;

    // Deep-validates and resolves one declared control binding row into its reusable template: its (probe, channel)
    // reference and its control name against the runtime vocabulary — WorldDefinitionValidator.ProbeControlNames
    // already proved the string itself is one of the thirteen recognized names, so a miss here can only mean the two
    // lists drifted.
    private static ControlBindingTemplate BuildControlTemplate(WorldProbeBinding.Control control, string path, ProbeRowInfo rowInfo) {
        var channel = ResolveChannel(
            channel: control.Channel,
            manifest: rowInfo.Manifest,
            path: path
        );

        if (!ProbeControlsByName.TryGetValue(
            key: control.ControlName,
            value: out var controlEnum
        )) {
            throw new InvalidOperationException(message: $"{path}.control '{control.ControlName}' names no WorldCameraControls member.");
        }

        return new ControlBindingTemplate {
            Channel = channel,
            ControlEnum = controlEnum,
            MaxAgeTicks = (long)(control.MaxAgeSeconds * Stopwatch.Frequency),
            Row = control,
        };
    }
    // Builds one instance's live control binding state from its row's already-validated template — cheap, never
    // re-validating.
    private static ControlState BuildControl(ProbeInstance instance, ControlBindingTemplate template) {
        return new ControlState {
            Channel = template.Channel,
            ControlEnum = template.ControlEnum,
            Instance = instance,
            MaxAgeTicks = template.MaxAgeTicks,
            Row = template.Row,
        };
    }
    // Writes every declared control binding's conditioned value onto its instance's own seat's device control
    // surface, at most once every ControlWriteFrameDivisor host frames and only on change — mirroring
    // ApplyCameraControls' own best-effort, device-authoritative contract. A reopened device reapplies the world's
    // AUTHORED control state first (WorldScreenBinder.ApplyCameraControls), so a sense-driven write here only ever
    // moves the device off that baseline; it never competes with it for which one "wins" on open.
    private void ServiceControls(ulong frameKey) {
        if (0UL != (frameKey % ControlWriteFrameDivisor)) {
            return;
        }

        var nowTimestamp = Stopwatch.GetTimestamp();

        foreach (var instance in m_liveInstances) {
            foreach (var control in instance.ControlBindings) {
                if (!instance.Ring.TryReadLatest(reading: out var reading)) {
                    continue;
                }
                if ((nowTimestamp - reading.CaptureTimestamp) > control.MaxAgeTicks) {
                    continue;
                }
                if (instance.RowInfo.TriggerSensor is not { } sensor) {
                    continue;
                }
                if (
                    !m_screens.TryGetCameraAttachment(seat: instance.Seat, sensor: sensor, attachment: out var attachment) ||
                    (attachment.Controls is not { } surface)
                ) {
                    continue;
                }

                var spec = instance.RowInfo.Manifest.Channels[control.Channel];
                var normalized = NormalizeChannel(
                    raw: (double)reading[control.Channel],
                    min: spec.Min,
                    max: spec.Max,
                    neutral: spec.Neutral
                );
                var unitInterval = ((normalized + 1.0) / 2.0);
                var value = (int)Math.Round(control.Row.Minimum + (unitInterval * (control.Row.Maximum - control.Row.Minimum)));

                if (control.HasWritten && (value == control.LastValue)) {
                    continue;
                }
                if (!surface.TrySet(control: control.ControlEnum, value: value)) {
                    continue;
                }

                control.HasWritten = true;
                control.LastValue = value;
                control.Writes++;
            }
        }
    }
}
