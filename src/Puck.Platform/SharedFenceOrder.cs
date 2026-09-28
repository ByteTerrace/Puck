namespace Puck.Platform;

/// <summary>How a producer on another device orders its writes into shared targets before a consumer's reads.</summary>
/// <param name="SharedFence">Whether the producer signals the consumer's shared fence after each write, so the
/// consumer waits on the GPU; <see langword="false"/> when it completes each write on the CPU before publishing.</param>
/// <param name="Reason">Why the producer waits on the CPU (the device's refusal of the fence, or no fence offered);
/// empty when it signals the fence, and before it has opened the targets.</param>
public readonly record struct SharedFenceOrder(bool SharedFence, string Reason) {
    /// <summary>Gets the order of a producer that has not opened its targets yet.</summary>
    public static SharedFenceOrder Pending => new(
        Reason: "",
        SharedFence: false
    );

    /// <summary>Describes the order as <c>fence</c>, <c>cpu-wait (reason)</c>, or <c>pending</c>.</summary>
    /// <returns>The description.</returns>
    public override string ToString() => (SharedFence
        ? "fence"
        : ((Reason.Length == 0)
            ? "pending"
            : $"cpu-wait ({Reason})"));
}
