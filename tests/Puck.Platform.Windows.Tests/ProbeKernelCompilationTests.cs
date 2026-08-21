using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class ProbeKernelCompilationTests {
    private static readonly string IrBlobKernelPath = ResolveIrBlobKernelPath();

    [Fact]
    [SupportedOSPlatform("windows10.0.19041")]
    public void Shipped_ir_blob_kernel_compiles_both_entry_points() {
        var source = File.ReadAllText(path: IrBlobKernelPath);

        Win32D3D11ProbeKernelRunner.Compile(entry: "accumulate", source: source);
        Win32D3D11ProbeKernelRunner.Compile(entry: "finalize", source: source);
    }
    [Fact]
    [SupportedOSPlatform("windows10.0.19041")]
    public void Broken_source_is_refused_with_the_compiler_message() {
        const string BrokenSource = """
            [numthreads(1, 1, 1)]
            void accumulate(uint3 dispatchId : SV_DispatchThreadID) {
                this is not hlsl;
            }
            """;

        var exception = Assert.Throws<COMException>(testCode: () => Win32D3D11ProbeKernelRunner.Compile(entry: "accumulate", source: BrokenSource));

        Assert.False(condition: string.IsNullOrWhiteSpace(value: exception.Message));
    }

    private static string ResolveIrBlobKernelPath([CallerFilePath] string callerFilePath = "") {
        var repositoryRoot = Path.GetFullPath(path: Path.Combine(Path.GetDirectoryName(path: callerFilePath)!, "..", ".."));

        return Path.Combine(repositoryRoot, "src", "Puck.Shaders", "Assets", "Probes", "ir-blob.hlsl");
    }
}
