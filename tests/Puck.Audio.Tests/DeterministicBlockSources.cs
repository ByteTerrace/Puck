using Puck.Audio.Mixing;
using Puck.Maths;

namespace Puck.Audio.Tests;

/// <summary>A constant-valued stereo source: pan/ramp/clip cases read gain arithmetic directly off it.</summary>
/// <param name="value">The sample value every pulled frame carries on both channels.</param>
internal sealed class ConstSource(short value) : IAudioBlockSource {
    /// <inheritdoc/>
    public int Pull(Span<short> interleavedStereo, int frames) {
        interleavedStereo[..(frames * 2)].Fill(value: value);

        return frames;
    }
}
/// <summary>A pull-counting constant source: the single-pull and never-pulled contracts read its counter.</summary>
/// <param name="value">The sample value every pulled frame carries on both channels.</param>
internal sealed class CountingSource(short value) : IAudioBlockSource {
    /// <summary>Gets the number of <see cref="Pull"/> calls observed.</summary>
    public int Pulls { get; private set; }

    /// <inheritdoc/>
    public int Pull(Span<short> interleavedStereo, int frames) {
        Pulls++;
        interleavedStereo[..(frames * 2)].Fill(value: value);

        return frames;
    }
}
/// <summary>A deterministic seeded-noise stereo source standing in for a tune-backed feed — a fixed
/// <see cref="Pcg32XshRr"/> stream reproduces the SAME bytes for the same seed across fresh instances, which is
/// all the mixer-core proofs below need of an external source.</summary>
/// <param name="seed">The PCG32 seed.</param>
internal sealed class SeededPatternSource(ulong seed) : IAudioBlockSource {
    private Pcg32XshRr m_stream = Pcg32XshRr.Create(state: seed, stream: 1UL);

    /// <inheritdoc/>
    public int Pull(Span<short> interleavedStereo, int frames) {
        var span = interleavedStereo[..(frames * 2)];

        for (var i = 0; (i < span.Length); i++) {
            span[i] = ((short)(m_stream.NextUInt32() >> 17));
        }

        return frames;
    }
}
