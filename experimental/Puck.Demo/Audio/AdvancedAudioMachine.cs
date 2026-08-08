using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;

namespace Puck.Demo.Audio;

/// <summary>
/// Adapts the native ARM7TDMI core's <see cref="IAgbApu"/> to the neutral <see cref="IAudioMachine"/> capability, so
/// <see cref="CabinetAudioOutput"/> pumps every cabinet through one path regardless of core. Cache one instance per
/// machine (construction is the only allocation this adapter costs) — never build one per pump call.
/// </summary>
/// <param name="apu">The machine's audio-processing unit, resolved once at assembly.</param>
internal sealed class AdvancedAudioMachine(IAgbApu apu) : IAudioMachine {
    /// <inheritdoc/>
    public int SampleRate =>
        apu.SampleRate;

    /// <inheritdoc/>
    public int ReadSamples(Span<short> destination) =>
        apu.DrainSamples(destination: destination);
}
