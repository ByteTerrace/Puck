using Puck.Cli.Architecture;
using Xunit;

namespace Puck.Cli.Core.Tests;

public sealed class ToolingBoundaryLawTests {
    [Fact]
    public void ProviderProjectsAndSdkReferencesAreRejectedByTheSharedPolicyExceptInTheirProviderOrTests() {
        var model = new ArchitectureModel();
        model.Kinds["Tool"] = false;
        model.Kinds["Test"] = false;
        model.Kinds["Production"] = true;
        model.Ranks["Optional extensions"] = -1;
        model.Providers["Provider"] = ["Provider.Sdk"];
        var provider = new ArchitectureProject { Name = "Puck.Provider", Kind = "Production", Layer = "Optional extensions", Provider = "Provider" };
        var tool = new ArchitectureProject { Name = "Puck.Tool", Kind = "Tool" };
        var failures = model.Check(tool, [provider], [], ["Provider.Sdk.Storage"]);
        Assert.Equal(2, failures.Count);
        Assert.All(failures, failure => Assert.Equal("PUCKARCH009", failure.Code));
        tool.Provider = "Provider";
        Assert.Empty(model.Check(tool, [provider], [], ["Provider.Sdk.Storage"]));
        tool.Provider = "";
        tool.Kind = "Test";
        Assert.Empty(model.Check(tool, [provider], [], ["Provider.Sdk.Storage"]));
    }

}
