using System.Globalization;
using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// One corner of the irradiance gather about a receiver, as <see cref="SdfMeshIrradiance.TryGather(Vector3, Vector3, Span{ValueTuple{int, int, int}}, Span{float}, Span{SdfMeshProbeReport})"/>
/// reports it: the probe's record and the terms of its weight at the receiver.
/// </summary>
/// <param name="Cell">The corner's lattice cell.</param>
/// <param name="Position">Where the corner's probe stands, in world units: its cell's centre plus <paramref name="Offset"/>;
/// the centre alone while no probe is present.</param>
/// <param name="Present">Whether the window holds a traced probe of the cell.</param>
/// <param name="Invalid">Whether the probe lies inside geometry and takes no part in any gather.</param>
/// <param name="Offset">The probe's offset from its cell's centre, in metres; zero unless the trace moved it out of
/// geometry or off a face its centre lay on.</param>
/// <param name="Inside">The share of the probe's rays that met back faces where it stands, from zero to one.</param>
/// <param name="Sweeps">The sweeps the probe holds.</param>
/// <param name="Neighbours">How many of the probe's 26 neighbouring cells its rays reach.</param>
/// <param name="Sky">The mean over the probe's six cube faces of the sky its rays reach, from zero to one; its own
/// measurement, before the volume's filter.</param>
/// <param name="Luminance">The mean over the probe's six cube faces of the luminance the static geometry sends it, in
/// the views pass's units; its own measurement, before the volume's filter.</param>
/// <param name="Trilinear">The receiver's trilinear share of the corner, from zero to one.</param>
/// <param name="Facing">How far the probe lies in front of the receiver's surface, from zero (behind) to one (along the
/// normal).</param>
/// <param name="Visibility">The four-plane visibility of the pushed receiver from the probe, from zero to one.</param>
/// <param name="Suspect">The share of that visibility the probe's rays did not see for themselves.</param>
/// <param name="Front">Whether the probe lies in front of the receiver's surface.</param>
/// <param name="Weight">The weight the corner answers with, after front eligibility, separation and mutual shares; zero
/// where it does not answer.</param>
public readonly record struct SdfMeshProbeReport((int X, int Y, int Z) Cell, Vector3 Position, bool Present = false, bool Invalid = false, Vector3 Offset = default, float Inside = 0f,
    int Sweeps = 0, int Neighbours = 0, float Sky = 0f, float Luminance = 0f, float Trilinear = 0f, float Facing = 0f, float Visibility = 0f, float Suspect = 0f, bool Front = false, float Weight = 0f) {
    /// <summary>Describes the corner on one line: its cell and position, then, for a present probe, its offset, inside
    /// share, validity, sweeps, neighbours, cube readings and the terms of its weight.</summary>
    /// <returns>The line.</returns>
    public string Describe() {
        var head = string.Create(provider: CultureInfo.InvariantCulture, $"[world.probe: cell=({Cell.X}, {Cell.Y}, {Cell.Z}) at=({Position.X:0.###}, {Position.Y:0.###}, {Position.Z:0.###})");

        if (!Present) { return (head + " absent]"); }

        var state = (Invalid ? "invalid" : ((Offset == Vector3.Zero) ? "valid" : "moved"));

        return string.Create(provider: CultureInfo.InvariantCulture,
            $"{head} offset=({Offset.X:0.###}, {Offset.Y:0.###}, {Offset.Z:0.###}) inside={Inside:0.###} {state} sweeps={Sweeps} neighbours={Neighbours}/26 sky={Sky:0.###} luminance={Luminance:0.###} trilinear={Trilinear:0.###} facing={Facing:0.###} visibility={Visibility:0.###} suspect={Suspect:0.###} {(Front ? "front" : "behind")} weight={Weight:0.####}]");
    }
}
