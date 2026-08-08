namespace Puck.Post;

/// <summary>The shared carrier the <see cref="PostBatteryNode"/> writes the battery's aggregate exit code into and the
/// entry point reads back after the host loop ends. It defaults to 2 (infra-fail) so a run that never reaches the
/// battery — the node never produced a frame — fails loudly rather than silently reporting success.</summary>
internal sealed class PostRunResult {
    /// <summary>The process exit code: 0 pass, 1 a check failed, 2 infra-fail / battery never ran.</summary>
    public int ExitCode { get; set; } = 2;
}
