using Puck.Vulkan.Bindings;

namespace Puck.Vulkan;

/// <summary>
/// The exception thrown when a Vulkan command returns a failure <see cref="VkResult"/>. It carries the name
/// of the failing operation and the result code.
/// </summary>
public sealed class VulkanException : Exception {
    private static string CreateMessage(string operation, VkResult result) {
        if (string.IsNullOrWhiteSpace(value: operation)) {
            throw new ArgumentException(
                message: "Operation name must be provided.",
                paramName: nameof(operation)
            );
        }

        return $"Vulkan operation '{operation}' failed with {result} ({(int)result}).";
    }

    /// <summary>Gets the name of the Vulkan operation that failed (for example, <c>vkCreateInstance</c>).</summary>
    public string Operation { get; }
    /// <summary>Gets the result code returned by the failing operation.</summary>
    public VkResult Result { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanException"/> class for a failed operation.</summary>
    /// <param name="operation">The name of the Vulkan operation that failed.</param>
    /// <param name="result">The result code returned by the operation.</param>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is <see langword="null"/>, empty, or white space.</exception>
    public VulkanException(string operation, VkResult result)
        : base(CreateMessage(
            operation: operation,
            result: result
        )) {
        Operation = operation;
        Result = result;
    }
}
