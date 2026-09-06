namespace Puck.World.Server;

/// <summary>One line of engine narration, delivered through a <see cref="WorldOutputHub"/> in place of writing
/// straight to <see cref="Console.Error"/> from inside the deterministic core.</summary>
/// <param name="Channel">The narration's tag — the bracketed word (or words) the formatted line itself opens with,
/// e.g. <c>world.mutation</c>.</param>
/// <param name="Text">The fully formatted line, computed once, at delivery — the same text a direct
/// <see cref="Console.Error"/> write would have produced.</param>
/// <param name="Stream">The console stream the line belongs on: standard error for narration, standard output
/// for the few lines a read-back script reads as answers.</param>
public readonly record struct WorldNarration(string Channel, string Text, WorldNarrationStream Stream = WorldNarrationStream.Error) {
    /// <summary>Returns <see cref="Text"/>.</summary>
    public override string ToString() => Text;
}

/// <summary>A sink that receives every <see cref="WorldNarration"/> a <see cref="WorldOutputHub"/> delivers while
/// attached (<see cref="WorldOutputHub.AttachNarrationSink"/>). A sink that throws is detached — see
/// <see cref="WorldOutputHub"/>'s own remarks on delivery isolation.</summary>
public enum WorldNarrationStream {
    /// <summary>Standard error.</summary>
    Error,
    /// <summary>Standard output.</summary>
    Output,
}

public interface IWorldNarrationSink {
    /// <summary>Receives one narration line.</summary>
    /// <param name="narration">The narration.</param>
    void Narrate(in WorldNarration narration);
}

/// <summary>The narration sink every composition root binds so a headless script or canary keeps reading the exact
/// lines it read when the deterministic core wrote them straight to <see cref="Console.Error"/>.</summary>
public sealed class WorldConsoleNarrationSink : IWorldNarrationSink {
    /// <inheritdoc/>
    public void Narrate(in WorldNarration narration) =>
        ((narration.Stream == WorldNarrationStream.Output) ? Console.Out : Console.Error).WriteLine(value: narration.Text);
}
