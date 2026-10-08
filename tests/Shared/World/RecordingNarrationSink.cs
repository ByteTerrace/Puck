
using Puck.World.Server;

namespace Puck.World.Testing;

/// <summary>A narration sink test double that records every delivered <see cref="WorldNarration"/> in order.</summary>
internal sealed class RecordingNarrationSink : IWorldNarrationSink {
    public List<WorldNarration> Narrations { get; } = [];

    public void Narrate(in WorldNarration narration) => Narrations.Add(item: narration);
}
