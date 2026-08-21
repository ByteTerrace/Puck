using System.Diagnostics;
using Puck.Commands;
using Puck.Input;
using Puck.Maths;
using Puck.Platform.Probes;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldProbes {
    // Compiles one declared axis binding row into its live conditioner and input identity. The channel's declared
    // range/neutral (from the resolved probe's own manifest, never authored on the row) is what the conditioner
    // maps into [-1, 1]; every other policy field comes straight from the row. A sense is not a device: the row's
    // seat addresses the lane directly (InputSignal.Slot), and the synthesized device id exists only as the router's
    // held-state key — one per seat, so every axis of one camera releases together like one stick.
    private AxisState BuildAxis(int probeIndex, WorldProbeBinding.Axis axis, string path) {
        var channel = ResolveChannel(
            channel: axis.Channel,
            path: path,
            probeIndex: probeIndex
        );
        var spec = m_probes[probeIndex].Manifest.Channels[channel];
        var policy = new ProbeAxisPolicy(
            Deadband: FixedQ4816.FromDouble(value: axis.Deadband),
            Hysteresis: FixedQ4816.FromDouble(value: axis.Hysteresis),
            Maximum: FixedQ4816.FromDouble(value: spec.Max),
            MaxAgeTicks: (long)(axis.MaxAgeSeconds * Stopwatch.Frequency),
            Minimum: FixedQ4816.FromDouble(value: spec.Min),
            Neutral: FixedQ4816.FromDouble(value: spec.Neutral),
            QuantizeBits: axis.QuantizeBits,
            Smoothing: FixedQ4816.FromDouble(value: axis.Smoothing)
        );

        return new AxisState {
            ProbeIndex = probeIndex,
            Channel = channel,
            Conditioner = new ProbeAxisConditioner(policy: policy),
            Device = InputDeviceId.FromConnectionKey(key: $"probe:{axis.Seat}"),
            Row = axis,
            Slot = PlayerRoster.SlotFromDisplay(number: axis.Seat),
            Source = InputSources.Probe.Axis(name: axis.Source),
        };
    }
    // Conditions every declared axis binding against its probe's latest reading and captures a fresh sample
    // into the router as the axis' own probe.<name> source — exactly like a stick axis, gated the same way the
    // gamepad capture gates a pad: a device without terminal focus captures nothing, and the one live sample the
    // router still carries for it is released once, so a deflected axis never keeps driving a lane while the
    // terminal owns the device. When focus returns the current sample is re-captured even if unchanged.
    private void ServiceAxes(ulong frameKey) {
        var nowTimestamp = Stopwatch.GetTimestamp();

        foreach (var axis in m_axisBindings) {
            if (!m_probes[axis.ProbeIndex].Ring.TryReadLatest(reading: out var reading)) {
                continue;
            }

            var sample = axis.Conditioner.Step(
                reading: in reading,
                channel: axis.Channel,
                nowTimestamp: nowTimestamp
            );

            if (!m_focus.IsActiveFor(deviceId: axis.Device)) {
                if (axis.Held) {
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
                Value: CommandValue.Axis(value: (float)(double)sample.Value)
            ));
            axis.Held = !sample.Expired;
            axis.Suppressed = false;
        }
    }
}
