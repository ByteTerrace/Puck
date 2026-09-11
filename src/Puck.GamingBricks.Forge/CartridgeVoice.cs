namespace Puck.GamingBricks.Forge;

/// <summary>
/// A sound channel a document's music part or effect plays on. Each is an independent sequencer, and nothing
/// reserves one for either use: what separates a track from a one-shot is whether its voice carries a loop start.
/// </summary>
public enum CartridgeVoice {
    /// <summary>Pulse channel one, whose stream carries a sweep byte ahead of its four registers.</summary>
    Pulse1 = 0,
    /// <summary>Pulse channel two, four registers a step.</summary>
    Pulse2 = 1,
    /// <summary>The wave channel, which plays through a waveform its sound carries.</summary>
    Wave = 2,
    /// <summary>The noise channel, four registers a step.</summary>
    Noise = 3,
}

/// <summary>Reads the voice a document names.</summary>
public static class CartridgeVoices {
    /// <summary>Returns the voice a document's name selects.</summary>
    /// <param name="name">The authored voice name.</param>
    /// <returns>The voice; pulse one when the name is absent or unrecognized, which validation has already refused.</returns>
    public static CartridgeVoice Of(string? name) => name switch {
        Puck.Assets.Documents.AudioEffectDocument.VoiceNoise => CartridgeVoice.Noise,
        Puck.Assets.Documents.AudioEffectDocument.VoiceWave => CartridgeVoice.Wave,
        Puck.Assets.Documents.AudioEffectDocument.VoicePulse2 => CartridgeVoice.Pulse2,
        _ => CartridgeVoice.Pulse1,
    };
}
