using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.SdfVm;
using Puck.Shaders;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the shader work sources a presentation shape registers — the compiler's
/// <c>shaders.compiler</c> counts and the kernel-set and shader-set manifest load counts — read through
/// <c>world.counters</c> like any other source, and <c>world.counters --json</c> publishes every kind they count in its
/// <c>kinds</c> legend with its unit and class.
/// </summary>
public sealed class WorldCountersShaderSourcesLawTests {
    [Fact]
    public void TheShaderSourcesListTheirCountsAndEveryKindInTheLegend() {
        var cache = Directory.CreateTempSubdirectory(prefix: "puck-test-").FullName;

        try {
            var services = new ServiceCollection();

            // Fresh sets under the process sources' names and kinds, so a sibling law loading shaders cannot move them.
            _ = services.AddWorldCounters();
            _ = services.AddSingleton<IWorkCounterSource>(implementationInstance: new ShaderCompiler(cacheDirectory: cache).Work);
            _ = services.AddSingleton<IWorkCounterSource>(implementationInstance: new WorkCounterSet(
                kinds: SdfWorldKernels.LoadWork.WorkKinds,
                name: SdfWorldKernels.LoadWorkSourceName
            ));
            _ = services.AddSingleton<IWorkCounterSource>(implementationInstance: new WorkCounterSet(
                kinds: ShaderSetManifest.LoadWork.WorkKinds,
                name: ShaderSetManifest.LoadWorkSourceName
            ));

            using var provider = services.BuildServiceProvider();
            var result = new CommandRegistry(modules: provider.GetServices<ICommandModule>()).Submit(line: "world.counters shaders --json");

            Assert.False(condition: result.IsError);
            Assert.Equal(
                expected: """[world.counters: {"sources":[{"name":"shaders.compiler","counts":{"shaders.compiler.requests":0,"shaders.compiler.cache-hits":0,"shaders.compiler.runs.dxc":0}},{"name":"shaders.sdf-kernels","counts":{"shaders.sdf-kernels.loads":0,"shaders.sdf-kernels.bytecode-bytes":0}},{"name":"shaders.set-manifest","counts":{"shaders.set-manifest.loads":0,"shaders.set-manifest.bytecode-bytes":0}}],"kinds":{"shaders.compiler.requests":{"unit":"count","class":"per-backend-deterministic"},"shaders.compiler.cache-hits":{"unit":"count","class":"pacing"},"shaders.compiler.runs.dxc":{"unit":"count","class":"per-backend-deterministic"},"shaders.sdf-kernels.loads":{"unit":"count","class":"per-backend-deterministic"},"shaders.sdf-kernels.bytecode-bytes":{"unit":"bytes","class":"per-backend-deterministic"},"shaders.set-manifest.loads":{"unit":"count","class":"per-backend-deterministic"},"shaders.set-manifest.bytecode-bytes":{"unit":"bytes","class":"per-backend-deterministic"}}}]""",
                actual: result.Output
            );
        } finally {
            Directory.Delete(
                path: cache,
                recursive: true
            );
        }
    }
}
