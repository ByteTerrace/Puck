namespace Puck.World;

public sealed partial class WorldRenderProbe {
    /// <summary>The optional synchronous indirect submission diagnostic sink, enabled by the host's debug layers.</summary>
    public Action<string>? IndirectSubmissionLog { get; init; }
}
