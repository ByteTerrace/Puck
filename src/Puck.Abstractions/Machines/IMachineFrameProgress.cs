namespace Puck.Abstractions.Machines;

/// <summary>Optional authoritative native-frame progress, distinct from host submissions or display uploads.
/// A coherent memory read or checkpoint barrier first completes accepted work before this counter is sampled.</summary>
public interface IMachineFrameProgress {
    /// <summary>Gets the last completed native frame index. Restore may move it backwards.</summary>
    long NativeFrameIndex { get; }
    /// <summary>Gets the complete frames the machine has put in its video output since it was created, the frames a
    /// display could show. Host diagnostics, never machine state.</summary>
    long StagedFrames => 0L;
    /// <summary>Gets how many times a display took a frame other than the one taken before it, the frames actually
    /// shown: beside <see cref="StagedFrames"/> it tells a display that shows every frame from one that repeats or
    /// skips. Host diagnostics, never machine state.</summary>
    long ShownFrames => 0L;
}
