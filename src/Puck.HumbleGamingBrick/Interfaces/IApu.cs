namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The audio processing unit: four sound channels (two pulse, one wave, one noise) driven by a frame sequencer
/// divided from the system counter, mixed through the master volume and panning into a stereo sample stream.
/// </summary>
public interface IApu : IClockedComponent {
    /// <summary>Gets the CGB pulse-channel amplitude readback (PCM12, 0xFF76).</summary>
    byte PcmAmplitude12 { get; }
    /// <summary>Gets the CGB wave/noise amplitude readback (PCM34, 0xFF77).</summary>
    byte PcmAmplitude34 { get; }
    /// <summary>Reads an audio register or wave RAM byte (0xFF10-0xFF3F).</summary>
    byte Read(ushort address);
    /// <summary>Writes an audio register or wave RAM byte (0xFF10-0xFF3F).</summary>
    void Write(ushort address, byte value);
    /// <summary>Seeds the powered post-boot register state, as the boot ROM's startup chime leaves it.</summary>
    void InitializePostBoot();
    /// <summary>Brings the channel generators current to an access point, so a register read or write observes the
    /// correct sub-cycle phase.</summary>
    void AdvanceChannelsForAccess(int tCycles);
    /// <summary>Enables stereo sample capture at the given output rate (resampled from the master clock).</summary>
    void ConfigureAudioOutput(int sampleRate);
    /// <summary>Gets the number of captured stereo sample frames available to drain.</summary>
    int AvailableAudioSamples { get; }
    /// <summary>Drains captured stereo samples into the destination, returning the count written.</summary>
    int ReadAudioSamples(Span<short> destination);
}
