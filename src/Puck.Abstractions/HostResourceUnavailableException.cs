namespace Puck.Abstractions;

/// <summary>
/// Raised when this host's environment cannot provide a resource a run requires: a graphics device, a listen
/// endpoint. The code that acquires the resource classifies the failure where it happens and raises a subclass
/// INSTEAD of its native exception, which rides along as the inner exception, so a host tells an environment that
/// cannot run the boot apart from an ordinary failure without naming a platform's exception types. A host reports it
/// as an unsupported environment, one line and a distinct exit code, rather than a crash.
/// </summary>
public abstract class HostResourceUnavailableException : Exception {
    /// <summary>Initializes a new instance of the <see cref="HostResourceUnavailableException"/> class.</summary>
    /// <param name="resource">The resource this host could not provide, such as <c>vulkan device</c>.</param>
    /// <param name="reason">What the acquisition found missing or refused, in one line.</param>
    /// <param name="innerException">The native failure the acquisition classified, if any.</param>
    /// <exception cref="ArgumentException"><paramref name="resource"/> or <paramref name="reason"/> is <see langword="null"/>, empty, or white space.</exception>
    protected HostResourceUnavailableException(string resource, string reason, Exception? innerException)
        : base(
        innerException: innerException,
        message: $"{resource} unavailable: {reason}"
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Resource = resource;
        Reason = reason;
    }

    /// <summary>Gets what the acquisition found missing or refused.</summary>
    public string Reason { get; }
    /// <summary>Gets the resource this host could not provide.</summary>
    public string Resource { get; }
}
