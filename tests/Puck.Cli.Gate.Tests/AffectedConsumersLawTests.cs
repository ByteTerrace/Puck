using Puck.Cli.Affected;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Gate.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a changed file reaches the projects that link it into their own build, read from each project
/// file's items and resolved from the project's directory, a glob matched below its last literal directory: a shared
/// source under <c>tests/Shared</c>, or content copied from another project's directory, whose suite the project graph
/// alone never selects. A file the root build targets link into every test project reaches every suite and nothing
/// else, and a file no project owns still reaches the projects that name its directory.
/// </summary>
public sealed class AffectedConsumersLawTests {
    // Data.Tests references no project, so only the link can choose it for a change under src/Engine.
    private static readonly AffectedProject[] Projects = [
        new(Directory: "src/Engine", IsSuite: false, Name: "Engine", References: []),
        new(Directory: "tests/Server.Tests", IsSuite: true, Name: "Server.Tests", References: ["Engine"]),
        new(Directory: "tests/Client.Tests", IsSuite: true, Name: "Client.Tests", References: ["Engine"]),
        new(Directory: "tests/Data.Tests", IsSuite: true, Name: "Data.Tests", References: []),
    ];

    private static string Repository(TemporaryDirectory scratch) {
        _ = scratch.WriteText(name: "Directory.Build.targets", text: """
            <Project>
                <ItemGroup Condition="'$(PuckKind)' == 'Test'">
                    <Compile Include="$(MSBuildThisFileDirectory)tests/Shared/Everywhere.cs" Link="Properties/Everywhere.cs" />
                </ItemGroup>
            </Project>
            """);
        _ = scratch.WriteText(name: "src/Engine/Engine.csproj", text: "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        _ = scratch.WriteText(name: "tests/Server.Tests/Server.Tests.csproj", text: """
            <Project Sdk="Microsoft.NET.Sdk">
                <ItemGroup>
                    <Compile Include="../Shared/World/ServerFixtures.cs" Link="ServerFixtures.cs" />
                    <Compile Include="../Shared/World/DocumentFixtures.cs" Link="DocumentFixtures.cs" />
                </ItemGroup>
            </Project>
            """);
        _ = scratch.WriteText(name: "tests/Client.Tests/Client.Tests.csproj", text: """
            <Project Sdk="Microsoft.NET.Sdk">
                <ItemGroup>
                    <Compile Include="../Shared/World/DocumentFixtures.cs" Link="DocumentFixtures.cs" />
                </ItemGroup>
            </Project>
            """);
        _ = scratch.WriteText(name: "tests/Data.Tests/Data.Tests.csproj", text: """
            <Project Sdk="Microsoft.NET.Sdk">
                <ItemGroup>
                    <None Include="../../src/Engine/Assets/addons/*.wasm" LinkBase="Assets/addons" CopyToOutputDirectory="PreserveNewest" />
                </ItemGroup>
            </Project>
            """);
        _ = scratch.WriteText(name: "tests/Data.Tests/ReadsFixtures.cs", text: "const string Root = \"tests/Puck.Fixtures/worlds\";");

        return scratch.RootPath;
    }

    [Fact]
    public void ALinkedSourceReachesExactlyTheProjectsThatCompileIt() {
        using var scratch = new TemporaryDirectory(prefix: "puck-affected-consumers-");
        var consumersOf = AffectedConsumers.Search(projects: Projects, repositoryRoot: Repository(scratch: scratch));

        Assert.Equal(expected: ["Server.Tests"], actual: consumersOf(arg: "tests/Shared/World/ServerFixtures.cs").Order(comparer: StringComparer.Ordinal));
        Assert.Equal(expected: ["Client.Tests", "Server.Tests"], actual: consumersOf(arg: "tests/Shared/World/DocumentFixtures.cs").Order(comparer: StringComparer.Ordinal));
    }
    [Fact]
    public void ASourceTheRootTargetsLinkReachesEverySuiteAndNoOtherProject() {
        using var scratch = new TemporaryDirectory(prefix: "puck-affected-consumers-");
        var consumersOf = AffectedConsumers.Search(projects: Projects, repositoryRoot: Repository(scratch: scratch));

        Assert.Equal(expected: ["Client.Tests", "Data.Tests", "Server.Tests"], actual: consumersOf(arg: "tests/Shared/Everywhere.cs").Order(comparer: StringComparer.Ordinal));
    }
    [Fact]
    public void ContentLinkedFromAnotherProjectChoosesTheLinkingSuite() {
        using var scratch = new TemporaryDirectory(prefix: "puck-affected-consumers-");
        var linkedBy = AffectedConsumers.Linking(projects: Projects, repositoryRoot: Repository(scratch: scratch));
        var plan = AffectedSelection.Select(
            canaries: [],
            catalogInputs: static (_, _) => false,
            canariesReaching: static _ => new HashSet<string>(),
            changed: ["src/Engine/Assets/addons/guest.wasm"],
            consumersOf: static _ => [],
            coverage: new Dictionary<string, IReadOnlySet<string>>(),
            declaresTests: static _ => false,
            linkedBy: linkedBy,
            projects: Projects,
            standInsFor: static _ => [],
            worldClosure: new HashSet<string>(),
            worldInput: static _ => false
        );

        Assert.Equal(expected: ["Data.Tests"], actual: linkedBy(arg: "src/Engine/Assets/addons/guest.wasm"));
        Assert.Empty(collection: linkedBy(arg: "src/Engine/Assets/addons/notes.txt"));
        Assert.Equal(expected: ["Client.Tests", "Data.Tests", "Server.Tests"], actual: plan.Suites);
    }
    [Fact]
    public void ADataDirectoryStillReachesTheProjectsThatNameIt() {
        using var scratch = new TemporaryDirectory(prefix: "puck-affected-consumers-");
        var consumersOf = AffectedConsumers.Search(projects: Projects, repositoryRoot: Repository(scratch: scratch));

        Assert.Equal(expected: ["Data.Tests"], actual: consumersOf(arg: "tests/Puck.Fixtures/worlds/minimal-host.puck"));
        Assert.Empty(collection: consumersOf(arg: "tests/Shared/World/Unlinked.cs"));
    }
}
