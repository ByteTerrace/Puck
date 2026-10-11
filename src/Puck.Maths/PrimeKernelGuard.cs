using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>Holds the checks every public prime kernel shell makes before it enters its unchecked core.</summary>
/// <remarks>Each check is constant-time: it reads a length, a bound or an instruction-set flag, never an element.</remarks>
internal static class PrimeKernelGuard {
    /// <summary>Throws when an instruction set the calling kernel executes is missing.</summary>
    /// <param name="supported">Whether the host supports the instruction set.</param>
    /// <param name="instructionSet">The instruction set's name, as the exception message states it.</param>
    /// <param name="kernel">The calling kernel, filled in by the compiler.</param>
    /// <exception cref="PlatformNotSupportedException"><paramref name="supported"/> is <see langword="false"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RequireInstructionSet(bool supported, string instructionSet, [CallerMemberName] string kernel = "") {
        if (!supported) { ThrowUnsupported(instructionSet: instructionSet, kernel: kernel); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnsupported(string instructionSet, string kernel) =>
        throw new PlatformNotSupportedException(message: $"{kernel} requires {instructionSet}, which this host does not support.");
}
