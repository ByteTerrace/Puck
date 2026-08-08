namespace Puck.World;

/// <summary>
/// The HOST's own <c>.puckreplay</c> codec could not honestly encode or re-drive something the host itself produced:
/// an authority-entry kind <see cref="WorldReplaySnapshot.Drive"/>'s switch does not handle, an enum member with no
/// value in the pinned wire set, or a live receipt set the encoder cannot represent. Every one of those is a
/// determinism hole — a recorded input silently dropped from the re-drive, or a state the tape cannot round-trip.
/// </summary>
/// <remarks>
/// This is deliberately its OWN type rather than an <see cref="InvalidOperationException"/>, and deliberately NOT
/// derived from one. The tape's post-persist verify catches the routine refusals a moved live tree produces
/// (<see cref="InvalidDataException"/> from the mount pin) and reports them as a benign line; sharing an exception
/// type with those refusals is what let the loudest possible host bug print as "the live tree moved". A separate
/// root type means no existing broad catch can absorb it by accident.
/// <para>Untrusted tape BYTES never raise this. Every fault this codec detects while reading a file throws
/// <see cref="InvalidDataException"/> instead — see <see cref="WorldReplaySnapshot.Read"/>.</para>
/// </remarks>
public sealed class WorldReplayCodecException : Exception {
    /// <summary>Initializes a new instance of the <see cref="WorldReplayCodecException"/> class.</summary>
    /// <param name="message">What the codec could not represent, and why that is a host bug rather than tape data.</param>
    public WorldReplayCodecException(string message)
        : base(message: message) {
    }
}
