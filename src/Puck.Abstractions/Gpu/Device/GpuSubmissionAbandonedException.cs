namespace Puck.Abstractions.Gpu;

/// <summary>
/// The exception an <see cref="IGpuSubmissionFence"/> reports when the submission it was armed by never reached the queue:
/// the hand-off of the batch that deferred it failed (a refused <c>vkQueueSubmit</c>, a refused
/// <c>ID3D12CommandQueue::Wait</c> ahead of the execute). Nothing it recorded ran, so nothing its completion would have
/// proven may be assumed: an object it would have retired is still held, and a layout it would have left is not in
/// effect. The failure itself reached whoever handed the batch over; the wait that reports this drains the fence for
/// reuse.
/// </summary>
public sealed class GpuSubmissionAbandonedException : Exception {
    /// <summary>Initializes a new instance of the <see cref="GpuSubmissionAbandonedException"/> class.</summary>
    public GpuSubmissionAbandonedException() :
        this(innerException: null) { }
    /// <summary>Initializes a new instance of the <see cref="GpuSubmissionAbandonedException"/> class with the hand-off
    /// failure that abandoned the submission.</summary>
    /// <param name="innerException">The hand-off's failure, when the caller's wait was what handed the batch over; or
    /// <see langword="null"/>.</param>
    public GpuSubmissionAbandonedException(Exception? innerException) :
        base(message: "The submission never reached the queue: its batch's hand-off failed, so nothing it recorded ran.", innerException: innerException) { }
}
