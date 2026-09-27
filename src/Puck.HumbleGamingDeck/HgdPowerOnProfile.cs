namespace Puck.HumbleGamingDeck;

/// <summary>Explicit, reproducible choices for otherwise indeterminate power-up state.</summary>
public sealed record HgdPowerOnProfile {
    /// <summary>Initializes a new instance of the <see cref="HgdPowerOnProfile"/> class.</summary>
    /// <param name="name">The profile name included in machine identity.</param>
    /// <param name="alignmentPhase">The CPU/PPU alignment, from zero through three master ticks.</param>
    /// <param name="workRamFill">The byte used to fill every work-RAM location.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The phase is outside zero through three.</exception>
    public HgdPowerOnProfile(string name = "deterministic", int alignmentPhase = 0, byte workRamFill = 0) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentOutOfRangeException.ThrowIfNegative(value: alignmentPhase);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: alignmentPhase, other: 3);
        Name = name;
        AlignmentPhase = alignmentPhase;
        WorkRamFill = workRamFill;
    }

    /// <summary>Gets the profile's identity name.</summary>
    public string Name {
        get;
    }
    /// <summary>Gets the CPU's initial offset relative to the future PPU divider.</summary>
    public int AlignmentPhase {
        get;
    }
    /// <summary>Gets the work-RAM power-up byte.</summary>
    public byte WorkRamFill {
        get;
    }
}
