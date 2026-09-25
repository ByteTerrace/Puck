namespace Puck.Abstractions.Gpu;

/// <summary>
/// Names a device's native objects for its debug output: the one naming service of a device
/// (<see cref="GpuDeviceServices.Naming"/>). Each backend's creating members hand it every object they create, with the
/// <see cref="GpuObjectName"/> the creator passed; a backend applies the text with <c>vkSetDebugUtilsObjectNameEXT</c> on
/// Vulkan and <c>ID3D12Object::SetName</c> on Direct3D 12, so validation and debug-layer messages, and the Direct3D 12
/// teardown's live-object report, show the name beside the handle.
/// <para>
/// Naming is on only while the device reports through its debug layers. Off, <see cref="Name"/> returns before
/// formatting anything: no string is built, nothing is allocated, and the backend is never called.
/// <see cref="GpuWorkCounting"/> and <see cref="GpuCreationFaults"/> pass every name through to the creating member they
/// wrap and carry this service over unchanged.
/// </para>
/// </summary>
public abstract class GpuObjectNaming {
    /// <summary>Gets the naming of a device that names nothing.</summary>
    public static GpuObjectNaming Off { get; } = new OffNaming();
    /// <summary>Gets whether objects are named: the device reports through its debug layers, and its backend can name
    /// objects there.</summary>
    public abstract bool IsEnabled { get; }

    /// <summary>Applies a formatted name to one native object. Called only while <see cref="IsEnabled"/>, with a
    /// non-empty name and a nonzero handle.</summary>
    /// <param name="kind">How to read <paramref name="handle"/>.</param>
    /// <param name="handle">The native object.</param>
    /// <param name="name">The formatted name (<see cref="GpuObjectName.ToString"/>).</param>
    protected abstract void Apply(GpuObjectKind kind, nint handle, string name);

    /// <summary>Names one native object, when naming is on. Off, or for the default name or a zero handle, it returns
    /// at once and formats nothing.</summary>
    /// <param name="kind">How the backend reads <paramref name="handle"/>.</param>
    /// <param name="handle">The native object: its <c>Vk*</c> handle on Vulkan, its <c>ID3D12Object</c> pointer on
    /// Direct3D 12.</param>
    /// <param name="name">The creator's name for it.</param>
    public void Name(GpuObjectKind kind, nint handle, in GpuObjectName name) {
        if (
            (!IsEnabled) ||
            name.IsEmpty ||
            (handle == 0)
        ) {
            return;
        }

        Apply(
            handle: handle,
            kind: kind,
            name: name.ToString()
        );
    }

    private sealed class OffNaming : GpuObjectNaming {
        public override bool IsEnabled => false;

        protected override void Apply(GpuObjectKind kind, nint handle, string name) { }
    }
}
