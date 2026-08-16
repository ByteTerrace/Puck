using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>Verifies a device's loaded shader bytecode reports the SDF ISA version this build expects, once per
/// device+shader-set.</summary>
public static class SdfShaderSetVerification {
    internal const uint ReportRequest = 0x53444656u;

    private static readonly ConditionalWeakTable<IGpuDeviceContext, VerificationState> VerifiedDevices = new();

    internal static void ValidateReport(ReadOnlySpan<byte> report, string viewsVariant) {
        if (
            (report.Length < 4) ||
            (report[0] != 0x53) ||
            (report[1] != 0x44)
        ) {
            throw new InvalidOperationException(message: $"SDF ISA handshake failed: the loaded beam/{viewsVariant} shader bytecode did not report the SDF version signature. Refusing to initialize the SDF world pipeline.");
        }

        if (report[2] != Puck.SignedDistance.SdfIsa.Version) {
            throw new InvalidOperationException(message: $"SDF ISA version mismatch: host expects v{Puck.SignedDistance.SdfIsa.Version}, but the loaded beam shader bytecode reports v{report[2]}. Refusing to initialize the SDF world pipeline.");
        }

        if (report[3] != Puck.SignedDistance.SdfIsa.Version) {
            throw new InvalidOperationException(message: $"SDF ISA version mismatch: host expects v{Puck.SignedDistance.SdfIsa.Version}, but the loaded {viewsVariant} shader bytecode reports v{report[3]}. Refusing to initialize the SDF world pipeline.");
        }
    }
    internal static void VerifyShaderSet(IGpuDeviceContext device, in SdfWorldKernels kernels, Action verify) {
        var state = VerifiedDevices.GetOrCreateValue(key: device);
        var shaderSetHash = ShaderSetHash(kernels: kernels);

        lock (state) {
            if (state.VerifiedShaderSets.Contains(item: shaderSetHash)) {
                return;
            }

            verify();
            _ = state.VerifiedShaderSets.Add(item: shaderSetHash);
        }
    }

    private static string ShaderSetHash(in SdfWorldKernels kernels) {
        using var hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

        hash.AppendData(data: kernels.Beam.Span);
        hash.AppendData(data: kernels.Views.Span);
        hash.AppendData(data: kernels.ViewsCore.Span);

        return Convert.ToHexString(inArray: hash.GetHashAndReset());
    }

    private sealed class VerificationState {
        public HashSet<string> VerifiedShaderSets { get; } = new(comparer: StringComparer.Ordinal);
    }
}
