using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Puck.Maths;

/// <summary>
/// Decides whether the host's BMI2 <c>PDEP</c> and <c>PEXT</c> instructions are fast enough to use.
/// <para>
/// <see cref="Bmi2.IsSupported"/> says only that the instructions exist. AMD processors before Zen 3 — family
/// <c>0x17</c> (Zen, Zen+ and Zen 2, the Steam Deck's core) and the earlier BMI2 families — execute <c>PDEP</c> and
/// <c>PEXT</c> in microcode, with a latency that grows with the mask's set bits and reaches hundreds of cycles, so the
/// portable loop or SWAR ladder is faster there. Hygon's Dhyana (<c>HygonGenuine</c>, family <c>0x18</c>) is a licensed
/// Zen 1 and shares that microcode. Zen 3 (family <c>0x19</c>) and later, and Intel, execute them in a few
/// cycles. Every <c>PDEP</c>/<c>PEXT</c> path in the library tests <see cref="HasFastParallelBits"/> rather than
/// <see cref="Bmi2.IsSupported"/>; both paths return the same bits, so the gate chooses speed and never a value.
/// </para>
/// </summary>
public static class BitManipulation {
    private const string AmdVendor = "AuthenticAMD";
    private const string HygonVendor = "HygonGenuine";
    // Zen 3's family; Zen 4 shares it and Zen 5 is 0x1A. Hygon's Zen 1 derivative is 0x18.
    private const int FirstFastAmdFamily = 0x19;

    /// <summary>
    /// Gets whether this host has BMI2 and executes its <c>PDEP</c> and <c>PEXT</c> without microcode:
    /// <see cref="IsParallelBitsFast(string, int, bool)"/> applied to this processor's CPUID vendor and family, read
    /// once. <see langword="false"/> on a host that is not x86.
    /// <para>
    /// A <see langword="static"/> <see langword="readonly"/> field, so the tier-1 JIT reads it as a constant and a
    /// closed generic that tests it keeps only the chosen path.
    /// </para>
    /// </summary>
    public static readonly bool HasFastParallelBits = DetectFastParallelBits();

    /// <summary>Decides whether a processor executes BMI2 <c>PDEP</c> and <c>PEXT</c> fast enough to prefer them over a portable path.</summary>
    /// <param name="vendor">The twelve-character CPUID vendor identifier, such as <c>AuthenticAMD</c> or <c>GenuineIntel</c>.</param>
    /// <param name="family">The CPUID display family: the base family, plus the extended family when the base is <c>0xF</c>.</param>
    /// <param name="hasBmi2">The processor's BMI2 support, as CPUID reports it.</param>
    /// <returns>
    /// <see langword="false"/> without BMI2, or for an <c>AuthenticAMD</c> or <c>HygonGenuine</c> processor below family
    /// <c>0x19</c> (Zen 3), whose instructions are microcoded; otherwise <see langword="true"/>.
    /// </returns>
    public static bool IsParallelBitsFast(string vendor, int family, bool hasBmi2) =>
        (hasBmi2 && (((AmdVendor != vendor) && (HygonVendor != vendor)) || (FirstFastAmdFamily <= family)));

    private static bool DetectFastParallelBits() {
        if (!X86Base.IsSupported) { return false; }

        var (_, vendorLow, vendorHigh, vendorMiddle) = X86Base.CpuId(
            functionId: 0,
            subFunctionId: 0
        );
        var signature = X86Base.CpuId(
            functionId: 1,
            subFunctionId: 0
        ).Eax;
        // Leaf 0 spells the vendor across EBX, EDX and ECX, in that order.
        Span<byte> vendor = stackalloc byte[12];

        BinaryPrimitives.WriteInt32LittleEndian(
            destination: vendor,
            value: vendorLow
        );
        BinaryPrimitives.WriteInt32LittleEndian(
            destination: vendor[4..],
            value: vendorMiddle
        );
        BinaryPrimitives.WriteInt32LittleEndian(
            destination: vendor[8..],
            value: vendorHigh
        );

        var baseFamily = (signature >>> 8) & 0xF;
        var family = ((0xF == baseFamily)
            ? (baseFamily + ((signature >>> 20) & 0xFF))
            : baseFamily
        );

        return IsParallelBitsFast(
            family: family,
            hasBmi2: Bmi2.IsSupported,
            vendor: Encoding.ASCII.GetString(bytes: vendor)
        );
    }
}
