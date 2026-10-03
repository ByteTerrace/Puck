using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Owns a native Vulkan instance (<c>VkInstance</c>) through its command table and destroys it when disposed.
/// </summary>
public sealed class VulkanInstance : IDisposable {
    /// <summary>The line an instance created with validation writes when its debug messenger reports the layer's
    /// messages.</summary>
    public const string ValidationLiveLine = "[vulkan] validation layer live: the instance reports through its debug messenger";
    /// <summary>The line an instance created with validation writes when it has no debug messenger, so the layer's
    /// messages reach no one.</summary>
    public const string ValidationNotLiveLine = "[vulkan] validation layer requested but not live: the instance has no debug messenger, so nothing is reported";

    private readonly nint m_debugMessengerHandle;
    private readonly VulkanDebugOutput? m_debugOutput;
    private readonly IVulkanInstanceApi m_instanceApi;

    private bool m_disposed;

    /// <summary>Gets the instance's command table, which carries the native <c>VkInstance</c> handle. Calling through it
    /// after disposal is a use-after-free.</summary>
    public VulkanInstanceCommands Commands { get; }
    /// <summary>Gets the native display kind the instance was created for.</summary>
    public NativeDisplayKind DisplayKind { get; }
    /// <summary>Gets the names of the instance extensions that were enabled.</summary>
    public IReadOnlyList<string> EnabledExtensions { get; }
    /// <summary>Gets the names of the instance layers that were enabled.</summary>
    public IReadOnlyList<string> EnabledLayers { get; }
    /// <summary>Gets whether the instance runs under the validation layer and its debug messenger reports the layer's
    /// messages.</summary>
    public bool ReportsValidation => ((0 != m_debugMessengerHandle) && EnabledLayers.Contains(value: VulkanInstanceCreateChain.ValidationLayer));

    /// <summary>Initializes a new instance of the <see cref="VulkanInstance"/> class, taking ownership of an existing native instance.</summary>
    /// <param name="instance">The command table of the native instance to own.</param>
    /// <param name="displayKind">The native display kind the instance was created for.</param>
    /// <param name="enabledExtensions">The names of the enabled instance extensions.</param>
    /// <param name="enabledLayers">The names of the enabled instance layers.</param>
    /// <param name="instanceApi">The API used to destroy the instance on disposal.</param>
    /// <param name="debugMessengerHandle">The native <c>VkDebugUtilsMessengerEXT</c> owned alongside the instance (destroyed first on disposal), or zero when none was created.</param>
    /// <param name="debugOutput">The writer the instance's messengers report to, released after the instance is destroyed,
    /// or <see langword="null"/> when they report to the process's standard error.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/>, <paramref name="enabledExtensions"/>, <paramref name="enabledLayers"/>, or <paramref name="instanceApi"/> is <see langword="null"/>.</exception>
    public VulkanInstance(
        VulkanInstanceCommands instance,
        NativeDisplayKind displayKind,
        IReadOnlyList<string> enabledExtensions,
        IReadOnlyList<string> enabledLayers,
        IVulkanInstanceApi instanceApi,
        nint debugMessengerHandle = 0,
        VulkanDebugOutput? debugOutput = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: instance);
        ArgumentNullException.ThrowIfNull(argument: enabledExtensions);
        ArgumentNullException.ThrowIfNull(argument: enabledLayers);
        ArgumentNullException.ThrowIfNull(argument: instanceApi);

        Commands = instance;
        DisplayKind = displayKind;
        EnabledExtensions = enabledExtensions.ToArray();
        EnabledLayers = enabledLayers.ToArray();
        m_debugMessengerHandle = debugMessengerHandle;
        m_debugOutput = debugOutput;
        m_instanceApi = instanceApi;
    }

    /// <summary>Destroys the owned debug messenger and instance. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        // The messenger must go before the instance it reports for.
        m_instanceApi.DestroyDebugMessenger(
            instance: Commands,
            messengerHandle: m_debugMessengerHandle
        );
        m_instanceApi.DestroyInstance(instance: Commands);
        // The messenger chained into creation reports vkDestroyInstance's messages, so the writer outlives the instance.
        m_debugOutput?.Dispose();

        m_disposed = true;
    }
}
