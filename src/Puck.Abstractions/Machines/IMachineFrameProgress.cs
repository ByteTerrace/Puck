namespace Puck.Abstractions.Machines;

/// <summary>Optional authoritative native-frame progress, distinct from host submissions or display uploads.
/// A coherent memory read or checkpoint barrier first completes accepted work before this counter is sampled.</summary>
public interface IMachineFrameProgress {
    /// <summary>Gets the last completed native frame index. Restore may move it backwards.</summary>
    long NativeFrameIndex { get; }
}
