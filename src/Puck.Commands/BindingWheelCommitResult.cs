namespace Puck.Commands;

/// <summary>Every distinguishable disposition of a radial commit release. Each is reported on its own so a refusal
/// is never narrated as one of the others.</summary>
public enum BindingWheelCommitStatus {
    /// <summary>No radial commit was armed.</summary>
    NotArmed,

    /// <summary>Another authored opener still holds the same radial.</summary>
    Deferred,

    /// <summary>The gesture ended without a selected activation.</summary>
    Cancelled,

    /// <summary>The compiled sector names a command absent from this input router's registry.</summary>
    Unregistered,

    /// <summary>The sector activation entered the seat's deterministic lane.</summary>
    Dispatched,
}
/// <summary>A radial commit result with enough identity for the command surface to report every failure honestly.</summary>
/// <param name="Status">The commit disposition.</param>
/// <param name="Command">The sector command when one was selected.</param>
/// <param name="Label">The selected sector's authored label.</param>
/// <param name="Ring">The zero-based selected ring.</param>
/// <param name="Sector">The zero-based selected sector.</param>
/// <param name="Reason">The stable cancellation/failure token.</param>
public readonly record struct BindingWheelCommitResult(
    BindingWheelCommitStatus Status,
    string? Command,
    string Label,
    int Ring,
    int Sector,
    string Reason
) {
    /// <summary>Creates a non-dispatch result for an explicit cancellation or an empty selection.</summary>
    public static BindingWheelCommitResult Cancelled(string reason, int ring, int sector) => new(
        Command: null,
        Label: string.Empty,
        Reason: reason,
        Ring: ring,
        Sector: sector,
        Status: BindingWheelCommitStatus.Cancelled
    );
    /// <summary>Creates a commit deferred by another opener holding the same radial.</summary>
    public static BindingWheelCommitResult Deferred(string label, int ring, int sector) => new(
        Command: null,
        Label: label,
        Reason: "still-held",
        Ring: ring,
        Sector: sector,
        Status: BindingWheelCommitStatus.Deferred
    );
    /// <summary>Queues a selected activation and preserves an unregistered-command refusal as its own status.</summary>
    public static BindingWheelCommitResult Dispatch(InputRouter router, int slot, BindingActivation activation, string label, int ring, int sector) {
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: activation);

        return (router.Activate(
            activation: activation,
            slot: slot
        )
            ? new BindingWheelCommitResult(
                Status: BindingWheelCommitStatus.Dispatched,
                Command: activation.Command,
                Label: label,
                Ring: ring,
                Sector: sector,
                Reason: string.Empty
            )
            : new BindingWheelCommitResult(
                Status: BindingWheelCommitStatus.Unregistered,
                Command: activation.Command,
                Label: label,
                Ring: ring,
                Sector: sector,
                Reason: "unregistered"
            )
        );
    }
    /// <summary>Creates the no-armed-gesture result.</summary>
    public static BindingWheelCommitResult NotArmed() => new(
        Command: null,
        Label: string.Empty,
        Reason: "not-armed",
        Ring: -1,
        Sector: -1,
        Status: BindingWheelCommitStatus.NotArmed
    );
}
