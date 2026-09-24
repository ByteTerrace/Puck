using Puck.Vulkan.Bindings;

namespace Puck.Vulkan;

/// <summary>
/// Extension methods on <see cref="VkResult"/> for interpreting and acting on Vulkan result codes.
/// </summary>
public static class VulkanResultExtensions {
    /// <summary>Determines whether a result code indicates success (a non-negative value).</summary>
    /// <param name="result">The result code to test.</param>
    /// <returns><see langword="true"/> if <paramref name="result"/> is a success code; otherwise, <see langword="false"/>. Note that swapchain status codes such as <see cref="VkResult.SuboptimalKhr"/> are successes.</returns>
    public static bool IsSuccess(this VkResult result) {
        return (result >= VkResult.Success);
    }
    /// <summary>Throws a <see cref="VulkanException"/> if a result code indicates failure.</summary>
    /// <param name="result">The result code to check.</param>
    /// <param name="operation">The name of the operation that produced the result, included in the exception message.</param>
    /// <exception cref="VulkanException"><paramref name="result"/> is not a success code.</exception>
    public static void ThrowIfFailed(this VkResult result, string operation) {
        if (result.IsSuccess()) {
            return;
        }

        // A lost device/surface is a RECOVERABLE signal, not an ordinary failure: surface it as the neutral
        // DeviceLostException (from any call site — wait-for-idle, a node's own submit, present) so the host pump's
        // device-loss recovery catches it uniformly rather than as a backend-specific VulkanException.
        if (
            (result == VkResult.ErrorDeviceLost) ||
            (result == VkResult.ErrorSurfaceLostKhr)
        ) {
            throw new DeviceLostException(
                message: $"{operation} failed: {result} (graphics device lost).",
                reasonCode: ((long)result)
            );
        }

        throw new VulkanException(
            operation: operation,
            result: result
        );
    }
    /// <summary>
    /// Throws a <see cref="GpuDeviceUnavailableException"/> if a device bring-up call's result code indicates failure:
    /// on the bring-up path (instance creation, device enumeration, device creation) a failing result means this host
    /// has no usable Vulkan device. A lost device or surface still raises <see cref="DeviceLostException"/>.
    /// </summary>
    /// <param name="result">The result code to check.</param>
    /// <param name="operation">The name of the bring-up operation that produced the result.</param>
    /// <exception cref="GpuDeviceUnavailableException"><paramref name="result"/> is a failure code other than a lost device or surface; the inner exception is the <see cref="VulkanException"/>.</exception>
    /// <exception cref="DeviceLostException"><paramref name="result"/> reports a lost device or surface.</exception>
    public static void ThrowIfUnavailable(this VkResult result, string operation) {
        try {
            result.ThrowIfFailed(operation: operation);
        } catch (VulkanException exception) {
            throw Unavailable(
                innerException: exception,
                reason: exception.Message
            );
        }
    }
    /// <summary>Creates the <see cref="GpuDeviceUnavailableException"/> the Vulkan bring-up path raises.</summary>
    /// <param name="reason">What the bring-up found missing or refused.</param>
    /// <param name="innerException">The failure being classified, if any.</param>
    /// <returns>The exception, naming the <c>vulkan</c> backend.</returns>
    public static GpuDeviceUnavailableException Unavailable(string reason, Exception? innerException = null) => new(
        backend: "vulkan",
        innerException: innerException,
        reason: reason
    );
}
