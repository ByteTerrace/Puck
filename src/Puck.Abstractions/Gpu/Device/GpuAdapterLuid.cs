using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.Abstractions.Gpu;

/// <summary>The one packing of a Windows adapter <c>LUID</c> into the 64-bit identity <see cref="IGpuDeviceContext.AdapterLuid"/>
/// reports: <c>(HighPart &lt;&lt; 32) | LowPart</c>. Each native binding generates its own <c>LUID</c> struct, so the
/// packing takes the two parts, and every DXGI, Direct3D and Vulkan reader compares adapters through this value.</summary>
public static class GpuAdapterLuid {
    /// <summary>Packs the two parts of a native <c>LUID</c> into one 64-bit value.</summary>
    /// <param name="highPart">The <c>LUID</c>'s signed <c>HighPart</c>, kept in bits 32 to 63.</param>
    /// <param name="lowPart">The <c>LUID</c>'s unsigned <c>LowPart</c>, kept in bits 0 to 31.</param>
    /// <returns>The identifier as <c>(HighPart &lt;&lt; 32) | LowPart</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Pack(int highPart, uint lowPart) => PackedIntPair.Pack(high: highPart, low: unchecked(((int)lowPart)));
}
