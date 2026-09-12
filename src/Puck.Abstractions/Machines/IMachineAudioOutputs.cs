namespace Puck.Abstractions.Machines;

/// <summary>Optional named audio streams. The host drains each stream once and fans it out to its consumers.</summary>
public interface IMachineAudioOutputs {
    /// <summary>Gets the available streams by provider-owned name, stable for the runtime's lifetime.</summary>
    IReadOnlyDictionary<string, IAudioMachine> AudioOutputs { get; }
}
