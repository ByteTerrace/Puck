namespace Puck.Vulkan.Bindings;

/// <summary>
/// Specifies the status code returned by a Vulkan command. Non-negative values indicate success (with
/// <see cref="Success"/> being the normal case); negative values indicate a runtime error.
/// </summary>
public enum VkResult {
    /// <summary>The command completed successfully.</summary>
    Success = 0,
    /// <summary>A fence or query has not yet completed.</summary>
    NotReady = 1,
    /// <summary>A wait operation did not complete within the specified time.</summary>
    Timeout = 2,
    /// <summary>An event is signaled.</summary>
    EventSet = 3,
    /// <summary>An event is unsignaled.</summary>
    EventReset = 4,
    /// <summary>A return array was too small for the result; the returned data is incomplete.</summary>
    Incomplete = 5,
    /// <summary>A swapchain no longer matches the surface properties exactly, but can still be used to present successfully.</summary>
    SuboptimalKhr = 1000001003,
    /// <summary>A host memory allocation failed.</summary>
    ErrorOutOfHostMemory = -1,
    /// <summary>A device memory allocation failed.</summary>
    ErrorOutOfDeviceMemory = -2,
    /// <summary>Initialization of an object could not be completed for implementation-specific reasons.</summary>
    ErrorInitializationFailed = -3,
    /// <summary>The logical or physical device has been lost.</summary>
    ErrorDeviceLost = -4,
    /// <summary>Mapping of a memory object failed.</summary>
    ErrorMemoryMapFailed = -5,
    /// <summary>A requested layer is not present or could not be loaded.</summary>
    ErrorLayerNotPresent = -6,
    /// <summary>A requested extension is not supported.</summary>
    ErrorExtensionNotPresent = -7,
    /// <summary>A requested feature is not supported.</summary>
    ErrorFeatureNotPresent = -8,
    /// <summary>The requested version of Vulkan is not supported by the driver, or is otherwise incompatible.</summary>
    ErrorIncompatibleDriver = -9,
    /// <summary>Too many objects of the requested type have already been created.</summary>
    ErrorTooManyObjects = -10,
    /// <summary>A requested format is not supported on this device.</summary>
    ErrorFormatNotSupported = -11,
    /// <summary>A pool allocation failed due to fragmentation of the pool's memory.</summary>
    ErrorFragmentedPool = -12,
    /// <summary>An unknown error occurred; the application may have provided invalid input, or an implementation failure occurred.</summary>
    ErrorUnknown = -13,
    /// <summary>A surface is no longer available.</summary>
    ErrorSurfaceLostKhr = -1000000000,
    /// <summary>A surface has changed such that it is no longer compatible with the swapchain; presentation requests using the swapchain will fail.</summary>
    ErrorOutOfDateKhr = -1000001004
}
