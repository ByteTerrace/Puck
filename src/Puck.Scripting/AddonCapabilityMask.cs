namespace Puck.Scripting;

/// <summary>The addon ABI's <c>Ask</c> capability-mask bit values, defined independently of any consumer enum
/// (e.g. <c>WorldCapability</c> ordinals). An <c>Ask</c>'s mask must have exactly one bit set — one handle per
/// answer; the mask crosses the ABI as a <c>u64</c>. <see cref="All"/> exists for host-side attenuation arithmetic
/// (<c>requested &amp; granted</c>) and is never a valid value on an <c>Ask</c> itself.</summary>
public static class AddonCapabilityMask {
    /// <summary>The union of every defined capability bit (<c>0x1F</c>) — host-side attenuation arithmetic only,
    /// never a value an <c>Ask</c> itself may carry.</summary>
    public const ulong All = 0x1FUL;
    /// <summary>Authoritative control over a subject's movement.</summary>
    public const ulong Control = (1UL << 2);
    /// <summary>Driving a subject's movement intent.</summary>
    public const ulong Drive = (1UL << 0);
    /// <summary>Editing a subject outside normal play.</summary>
    public const ulong Edit = (1UL << 4);
    /// <summary>Mutating a subject's non-movement state.</summary>
    public const ulong Mutate = (1UL << 3);
    /// <summary>Reading a subject's disclosed state.</summary>
    public const ulong Observe = (1UL << 1);
}
