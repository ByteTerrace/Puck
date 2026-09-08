using System.Diagnostics;
using Puck.Commands;
using Puck.Input;
using Puck.Maths;
using Puck.Platform.Probes;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldProbes {
    // Deep-validates and resolves one declared axis binding row into its reusable template: the (probe, channel)
    // reference and the channel's declared range/neutral (from the resolved row's own manifest, never authored on
    // the row), mapped into the conditioner policy every instance's ProbeAxisConditioner shares. Every other policy
    // field comes straight from the row.
    private static AxisBindingTemplate BuildAxisTemplate(WorldProbeBinding.Axis axis, string path, ProbeRowInfo rowInfo) {
        var channel = ResolveChannel(
            channel: axis.Channel,
            manifest: rowInfo.Manifest,
            path: path
        );
        var spec = rowInfo.Manifest.Channels[channel];
        var policy = new ProbeAxisPolicy(
            Deadband: FixedQ4816.FromDouble(value: axis.Deadband),
            Hysteresis: FixedQ4816.FromDouble(value: axis.Hysteresis),
            Maximum: FixedQ4816.FromDouble(value: spec.Max),
            MaxAgeTicks: ((long)(axis.MaxAgeSeconds * Stopwatch.Frequency)),
            Minimum: FixedQ4816.FromDouble(value: spec.Min),
            Neutral: FixedQ4816.FromDouble(value: spec.Neutral),
            QuantizeBits: axis.QuantizeBits,
            Smoothing: FixedQ4816.FromDouble(value: axis.Smoothing)
        );

        return new AxisBindingTemplate {
            Channel = channel,
            Policy = policy,
            Row = axis,
            Source = InputSources.Probe.Axis(name: axis.Source),
        };
    }
    // Builds one instance's live axis binding state from its row's already-validated template. A sense is not a
    // device: the row's seat addresses the lane directly (InputSignal.Slot), and the synthesized device id exists
    // only as the router's held-state key — one per seat, so every axis of one camera releases together like one
    // stick. The instance's OWN seat supplies the lane (never axis.Row.Seat, which a seat-relative row's authored
    // binding is forbidden from carrying — WorldDefinitionValidator's own law).
    private static AxisState BuildAxis(ProbeInstance instance, AxisBindingTemplate template) {
        return new AxisState {
            Channel = template.Channel,
            Conditioner = new ProbeAxisConditioner(policy: template.Policy),
            Device = InputDeviceId.FromConnectionKey(key: $"probe:{instance.Seat}"),
            Instance = instance,
            Row = template.Row,
            Slot = PlayerRoster.SlotFromDisplay(number: instance.Seat),
            Source = template.Source,
        };
    }
    // Conditions every live axis binding against its instance's latest reading and captures a fresh sample
    // into the router as the axis' own probe.<name> source — exactly like a stick axis, gated the same way the
    // gamepad capture gates a pad: a device without terminal focus captures nothing, and the one live sample the
    // router still carries for it is released once, so a deflected axis never keeps driving a lane while the
    // terminal owns the device. When focus returns the current sample is re-captured even if unchanged.
    private void ServiceAxes(ulong frameKey) {
        var nowTimestamp = Stopwatch.GetTimestamp();

        foreach (var instance in m_liveInstances) {
            foreach (var axis in instance.AxisBindings) {
                if (!instance.Ring.TryReadLatest(reading: out var reading)) {
                    continue;
                }

                var sample = axis.Conditioner.Step(
                    reading: in reading,
                    channel: axis.Channel,
                    nowTimestamp: nowTimestamp
                );

                if (!m_focus.IsActiveFor(deviceId: axis.Device)) {
                    ReleaseAxisHeldState(axis: axis);
                    axis.Suppressed = true;

                    continue;
                }

                if (!sample.Changed && !axis.Suppressed) {
                    continue;
                }

                m_router.Capture(signal: new InputSignal(
                    CaptureTick: m_clock.NowTicks,
                    DeviceId: axis.Device,
                    Phase: (sample.Expired ? CommandPhase.Completed : CommandPhase.Active),
                    Slot: axis.Slot,
                    Source: axis.Source,
                    Value: CommandValue.Axis(value: ((float)((double)sample.Value)))
                ));
                axis.Held = !sample.Expired;
                axis.Suppressed = false;
            }
        }
    }
    // Releases an axis binding's held router capture, if any — a device losing terminal focus (ServiceAxes) and an
    // instance retiring (RetireInstance, a seat leaving or process Dispose) share this so a lane can never stay
    // latched past either.
    private void ReleaseAxisHeldState(AxisState axis) {
        if (!axis.Held) {
            return;
        }

        m_router.Capture(signal: new InputSignal(
            CaptureTick: m_clock.NowTicks,
            DeviceId: axis.Device,
            Phase: CommandPhase.Completed,
            Slot: axis.Slot,
            Source: axis.Source,
            Value: CommandValue.Axis(value: 0f)
        ));
        axis.Held = false;
    }
}
