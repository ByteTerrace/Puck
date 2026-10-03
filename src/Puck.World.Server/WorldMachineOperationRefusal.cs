namespace Puck.World.Server;

/// <summary>Names the machine-operation refusals a caller can tell apart by code. <c>world.refusals</c> lists every
/// member.</summary>
public enum WorldMachineOperationRefusal : byte {
    /// <summary>A tape is recording. The replay format captures no provider operation, so one run now would change a
    /// runtime the tape cannot reproduce; refused before preparation.</summary>
    [Refusal(door: "machine.operation", condition: "a tape is recording while a machine operation is submitted", kind: RefusalKind.Verdict, Unsupported = true)]
    WhileRecording,

    /// <summary>The machine's provider performs no operations at all.</summary>
    [Refusal(door: "machine.operation", condition: "the machine's provider implements no operations", kind: RefusalKind.Verdict, Unsupported = true)]
    ProviderWithoutOperations,
}
