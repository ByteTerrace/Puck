using Puck.Abstractions.Machines;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Audio;

/// <summary>
/// Adapts a SM83-family machine's <see cref="IAudioSink"/> to the neutral <see cref="IAudioMachine"/> capability, so
/// <see cref="CabinetAudioOutput"/> pumps every cabinet through one path regardless of core. Cache one instance per
/// machine (construction is the only allocation this adapter costs) — never build one per pump call.
/// </summary>
/// <param name="sink">The machine's audio sink, resolved once at assembly.</param>
internal sealed class HumbleAudioMachine(IAudioSink sink) : IAudioMachine {
    /// <inheritdoc/>
    public int SampleRate =>
        sink.SampleRate;

    /// <inheritdoc/>
    public int ReadSamples(Span<short> destination) =>
        sink.ReadSamples(destination: destination);
}
