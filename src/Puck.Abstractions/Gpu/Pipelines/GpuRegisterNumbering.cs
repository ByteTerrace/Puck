namespace Puck.Abstractions.Gpu;

/// <summary>
/// Specifies how a Direct3D 12 compute root signature numbers each binding's shader register, in space 0. Vulkan binds
/// by binding number and ignores it.
/// </summary>
public enum GpuRegisterNumbering {
    /// <summary>A binding's register number equals its binding number, in the class its kind takes: <c>t</c> for a
    /// read-only buffer or a sampled image, whose static sampler is <c>s</c> at the same number, and <c>u</c> for a
    /// storage image or a read-write buffer. An array binding takes its count of registers from there. Every pass
    /// interface and shader pipeline pass follows this rule.</summary>
    Binding,
    /// <summary>Registers are numbered per class in binding-list order, from zero: each <c>t</c> binding takes the next
    /// <c>t</c> register and each <c>u</c> binding the next <c>u</c>, and each sampled image's static sampler the next
    /// <c>s</c>. Only the SDF engine's kernels and the region copy they share declare their registers this way.</summary>
    PackedByClass,
}
