namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Gets the hub other Server-family components (a peer host, a replay tape, an instance host's row)
    /// narrate and attach client sinks through.</summary>
    internal WorldOutputHub Output => m_output;

    /// <summary>Attaches a sink that receives this server's narration — the same lines it would otherwise write
    /// straight to <see cref="Console.Error"/> — until the process ends or the returned lease is disposed.</summary>
    /// <param name="sink">The sink to add.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public IDisposable AttachNarrationSink(IWorldNarrationSink sink) => m_output.AttachNarrationSink(sink: sink);
}
