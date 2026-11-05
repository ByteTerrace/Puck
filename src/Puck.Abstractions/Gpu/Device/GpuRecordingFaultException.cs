namespace Puck.Abstractions.Gpu;

/// <summary>
/// Raised partway through a render node's frame recording that an operator armed <see cref="GpuCreationFaults"/> to fail
/// (<see cref="GpuCreationFaults.ArmRecording"/>). Its message starts with
/// <c>[<see cref="GpuCreationFaults.RecordingRefusalCode"/>]</c> and names the recording's number.
/// </summary>
public sealed class GpuRecordingFaultException : Exception {
    /// <summary>Initializes a new instance of the <see cref="GpuRecordingFaultException"/> class.</summary>
    /// <param name="recording">The failed recording's one-based number among the recordings counted since the faults were
    /// last disarmed.</param>
    public GpuRecordingFaultException(long recording)
        : base(message: $"[{GpuCreationFaults.RecordingRefusalCode}] Frame recording {recording} was failed by gpu.faults partway through its command list.") => Recording = recording;

    /// <summary>Gets the failed recording's one-based number among the recordings counted since the faults were last
    /// disarmed.</summary>
    public long Recording { get; }
}
