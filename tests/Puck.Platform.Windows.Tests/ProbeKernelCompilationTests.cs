using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class ProbeKernelCompilationTests {
    [Theory]
    [InlineData("ir-blob")]
    [InlineData("faerie")]
    [SupportedOSPlatform("windows10.0.10240")]
    public void Shipped_kernel_compiles_both_entry_points(string name) {
        var source = File.ReadAllText(path: KernelPath(name: name));

        Win32D3D11ProbeKernel.Compile(entry: "accumulate", source: source);
        Win32D3D11ProbeKernel.Compile(entry: "finalize", source: source);
    }
    [Fact]
    [SupportedOSPlatform("windows10.0.10240")]
    public void Broken_source_is_refused_with_the_compiler_message() {
        const string BrokenSource = """
            [numthreads(1, 1, 1)]
            void accumulate(uint3 dispatchId : SV_DispatchThreadID) {
                this is not hlsl;
            }
            """;

        var exception = Assert.Throws<COMException>(testCode: () => Win32D3D11ProbeKernel.Compile(entry: "accumulate", source: BrokenSource));

        Assert.False(condition: string.IsNullOrWhiteSpace(value: exception.Message));
    }

    private static string KernelPath(string name, [CallerFilePath] string callerFilePath = "") {
        var repositoryRoot = Path.GetFullPath(path: Path.Combine(Path.GetDirectoryName(path: callerFilePath)!, "..", ".."));

        return Path.Combine(repositoryRoot, "src", "Puck.Shaders", "Assets", "Probes", $"{name}.hlsl");
    }
}
