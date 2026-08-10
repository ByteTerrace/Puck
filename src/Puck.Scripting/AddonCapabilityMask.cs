namespace Puck.Scripting;

/// <summary>The addon ABI's <c>Ask</c> capability-mask bit values, frozen independently of any consumer enum
/// (e.g. <c>WorldCapability</c> ordinals). An <c>Ask</c>'s mask must have exactly one bit set — one handle per
/// answer; the <c>u64</c> shape stays wide so multi-capability asks can be admitted later under multi-part
/// framing without a break. <see cref="All"/> exists for host-side attenuation arithmetic
/// (<c>requested &amp; granted</c>) and is never a valid value on an <c>Ask</c> itself.</summary>
public static class AddonCapabilityMask {
    /// <summary>The union of every defined capability bit (<c>0x3F</c>) — includes the reserved hole
    /// (<see cref="Reserved"/>), because this constant is host-side attenuation arithmetic only and never a value an
    /// <c>Ask</c> itself may carry.</summary>
    public const ulong All = 0x3FUL;
    /// <summary>Authoritative control over a subject's movement.</summary>
    public const ulong Control = (1UL << 3);
    /// <summary>Driving a subject's movement intent.</summary>
    public const ulong Drive = (1UL << 0);
    /// <summary>Editing a subject outside normal play.</summary>
    public const ulong Edit = (1UL << 5);
    /// <summary>Mutating a subject's non-movement state.</summary>
    public const ulong Mutate = (1UL << 4);
    /// <summary>Reading a subject's disclosed state.</summary>
    public const ulong Observe = (1UL << 1);
    /// <summary>Permanently reserved — bit 2 formerly named <c>Present</c> (a declared-but-never-enforced
    /// capability, now removed). The bit is never compacted and never reused: retiring the hole rather than
    /// repacking the mask keeps every other bit's meaning fixed, so no guest rebuild is forced by the deletion
    /// itself. No engine capability maps to this bit any more — a guest naming it in an <c>Ask</c>'s mask resolves
    /// to no capability, the same as any other undefined bit.</summary>
    public const ulong Reserved = (1UL << 2);
}
