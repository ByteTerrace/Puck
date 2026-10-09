using Puck.Cli.Automation;
using Xunit;

namespace Puck.Cli.Release.Tests.Automation;

/// <summary>CONTRACT UNDER TEST: <c>puck wasm build</c> reads each crate's committed builds from its
/// <c>[package.metadata.puck]</c> as <c>cargo metadata</c> reports it, builds a variant with its own features in place of
/// the crate's defaults, and refuses a declared build that writes nowhere.</summary>
public sealed class WasmBuildCommandLawTests {
    private const string Metadata = """
        {"packages": [
            {"name": "puck-stdlib", "metadata": null},
            {"name": "puck-addon-walker", "metadata": {"puck": {"builds": [
                {"outputs": ["wasm/puck-addon-walker/dist/walker.wasm", "src/Puck.World/Assets/addons/walker.wasm"]},
                {"features": ["bound64"], "outputs": ["wasm/puck-addon-walker/dist/walker-bound64.wasm"]}
            ]}}},
            {"name": "puck-addon-quiet", "metadata": {"other": {}}}
        ]}
        """;

    [Fact]
    public void EachDeclaredBuildIsReadWithItsFeaturesAndOutputs() {
        var builds = WasmBuildCommand.ReadBuilds(metadata: Metadata);

        Assert.Equal(expected: 2, actual: builds.Count);
        Assert.Equal(expected: ["build", "--release", "--package", "puck-addon-walker"], actual: builds[1].CargoArguments);
        Assert.Equal(expected: ["src/Puck.World/Assets/addons/walker.wasm", "wasm/puck-addon-walker/dist/walker.wasm"], actual: builds[1].Outputs.Order(comparer: StringComparer.Ordinal));
        Assert.Equal(expected: ["build", "--release", "--package", "puck-addon-walker", "--no-default-features", "--features", "bound64"], actual: builds[0].CargoArguments);
        Assert.Equal(expected: "target/wasm32-unknown-unknown/release/puck_addon_walker.wasm", actual: builds[0].Module);
    }
    [Fact]
    public void ADeclaredBuildWithNoOutputIsRefused() => Assert.Throws<InvalidDataException>(testCode: () => WasmBuildCommand.ReadBuilds(metadata: """{"packages": [{"name": "puck-addon-lost", "metadata": {"puck": {"builds": [{"outputs": []}]}}}]}"""));
}
