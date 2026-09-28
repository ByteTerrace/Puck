using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>A bounded medium's motion at a presented tick, which the volume table carries so no pass reads a clock:
/// its advection in noise cells, each reduced by the noise's lattice period (<see cref="SdfVolume.NoisePeriodCells"/>,
/// the period the medium's noise wraps at, so the reduction shows no seam), and its pulse's density gain.</summary>
/// <param name="Advection">The drift, in noise cells, along the column for a flow and along local X for a cloud.</param>
/// <param name="AdvectionZ">A cloud's drift along local Z, in noise cells, at <see cref="SdfVolume.CloudDriftZ"/> of its
/// speed; a flow never reads it.</param>
/// <param name="Pulse">The density gain, <c>1 + amplitude · sin(2π · phase)</c>.</param>
public readonly record struct SdfVolumeMotion(float Advection, float AdvectionZ, float Pulse) {
    /// <summary>Returns a medium's motion at a presented tick: its speed and pulse frequency integrated from engine tick
    /// zero (<see cref="PresentedTick.Integrate"/>).</summary>
    /// <param name="volume">The medium; its width is positive, as <see cref="SdfVolume.Validate"/> requires.</param>
    /// <param name="clock">The presented tick.</param>
    /// <returns>The motion.</returns>
    public static SdfVolumeMotion At(in SdfVolume volume, PresentedTick clock) {
        var cellsPerSecond = (volume.Speed / ((double)volume.Width));
        var pulsePhase = clock.Integrate(
            modulus: 1d,
            ratePerSecond: volume.PulseFrequency
        );

        return new SdfVolumeMotion(
            Advection: ((float)clock.Integrate(
                modulus: SdfVolume.NoisePeriodCells,
                ratePerSecond: cellsPerSecond
            )),
            AdvectionZ: ((float)clock.Integrate(
                modulus: SdfVolume.NoisePeriodCells,
                ratePerSecond: (cellsPerSecond * SdfVolume.CloudDriftZ)
            )),
            Pulse: ((float)(1d + (volume.PulseAmplitude * Math.Sin(a: (Math.Tau * pulsePhase)))))
        );
    }
}
