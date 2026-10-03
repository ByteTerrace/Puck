using System.Runtime.InteropServices;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Where an instance's debug messengers write the validation layer's messages: a writer held by a handle the messengers
/// carry as their <c>pUserData</c>, so each instance reports to its own writer, the counterpart of the Direct3D 12
/// context's <c>DebugOutput</c>. The messenger callback runs on whichever thread made the Vulkan call, so the writer is
/// synchronized. The instance that owns it releases it after the instance is destroyed.
/// </summary>
public sealed class VulkanDebugOutput : IDisposable {
    private GCHandle m_handle;

    /// <summary>Initializes a new instance of the <see cref="VulkanDebugOutput"/> class over a writer.</summary>
    /// <param name="writer">The writer the messages go to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public VulkanDebugOutput(TextWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        m_handle = GCHandle.Alloc(value: TextWriter.Synchronized(writer: writer));
    }

    /// <summary>Gets the <c>pUserData</c> a messenger carries to name this writer; zero once released.</summary>
    public nint UserData => (m_handle.IsAllocated ? GCHandle.ToIntPtr(value: m_handle) : 0);

    /// <summary>Returns the writer a messenger's <c>pUserData</c> names, or the process's standard error for zero.</summary>
    /// <param name="userData">The callback's <c>pUserData</c>.</param>
    /// <returns>The writer.</returns>
    public static TextWriter Writer(nint userData) => (((0 != userData) && (GCHandle.FromIntPtr(value: userData).Target is TextWriter writer))
        ? writer
        : Console.Error);
    /// <summary>Releases the handle. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_handle.IsAllocated) {
            m_handle.Free();
        }
    }
}
