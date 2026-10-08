using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// Exercises <see cref="AnyAddressBindAnalyzer"/> over small compilations: every way of binding all network interfaces
/// is refused, a loopback listener passes, and an allowlisted deployment site passes only in the assembly, file and
/// member its entry names.
/// </summary>
public sealed class AnyAddressBindAnalyzerTests {
    private const string Id = "NET001";
    // The Kestrel surface the analyzer recognises by name; the harness references only the shared framework.
    private const string KestrelStub = """
        namespace Microsoft.AspNetCore.Server.Kestrel.Core {
            public sealed class KestrelServerOptions {
                public void ListenAnyIP(int port) { }
                public void Listen(System.Net.IPEndPoint endPoint) { }
            }
        }

        """;

    private static AnalysisResult Run(string body, string assemblyName = Harness.DefaultAssemblyName, string fileName = "Subject.cs") {
        var result = Harness.Analyze(
            analyzer: new AnyAddressBindAnalyzer(),
            compilation: Harness.Compile(
                assemblyName: assemblyName,
                sources: [
                    new SourceFile(
                        Name: "Kestrel.cs",
                        Text: KestrelStub
                    ),
                    new SourceFile(
                        Name: fileName,
                        Text: ("namespace Subject.Assembly;\n\n" + body)
                    ),
                ]
            )
        );

        Assert.True(
            condition: result.CompilesCleanly,
            userMessage: result.CompilerErrorText
        );

        return result;
    }
    private static string[] Messages(AnalysisResult result) => result.WithId(id: Id).Select(selector: static diagnostic => diagnostic.GetMessage()).ToArray();

    [Fact]
    public void KestrelListenAnyIpIsRefused() {
        var result = Run(body: """
            public static class Subject {
                public static void Health(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options) => options.ListenAnyIP(port: 8081);
            }

            """);

        var diagnostic = result.Single(id: Id);

        Assert.Equal(
            actual: diagnostic.Severity,
            expected: Microsoft.CodeAnalysis.DiagnosticSeverity.Error
        );
        Assert.StartsWith(
            actualString: diagnostic.GetMessage(),
            expectedStartString: "ListenAnyIP binds every network interface"
        );
    }
    [Fact]
    public void EveryAnyAddressSpellingIsRefused() {
        var result = Run(body: """
            using System.Net;
            using System.Net.Sockets;

            public static class Subject {
                public const string Wildcard = "http://*:5000";

                public static object[] Binds(int port) => [
                    IPAddress.Any,
                    IPAddress.IPv6Any,
                    IPAddress.IPv6None,
                    TcpListener.Create(port),
                    new UdpClient(port),
                    new UdpClient(port, AddressFamily.InterNetworkV6),
                    new IPEndPoint(0, port),
                    new IPAddress(0L),
                    IPEndPoint.Parse("0.0.0.0:7825"),
                    IPEndPoint.Parse($"0.0.0.0:{port}"),
                    IPEndPoint.Parse("[::]:7825"),
                    IPAddress.Parse("::"),
                    "http://+:80/",
                ];
            }

            """);

        var messages = Messages(result: result);

        Assert.Equal(
            actual: messages.Length,
            expected: 14
        );
        foreach (var expected in new[] {
            "IPAddress.Any ", "IPAddress.IPv6Any ", "IPAddress.IPv6None ", "TcpListener.Create ", "new UdpClient(port) ",
            "new IPEndPoint(0) ", "new IPAddress(0) ", "the literal \"0.0.0.0:7825\"", "the literal \"0.0.0.0:\"",
            "the literal \"[::]:7825\"", "the literal \"::\"", "the literal \"http://+:80/\"", "the literal \"http://*:5000\"",
        }) {
            Assert.Contains(
                collection: messages,
                filter: message => message.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: expected
                )
            );
        }
    }
    [Fact]
    public void AnAnyAddressInAnAttributeArgumentIsRefused() {
        var result = Run(body: """
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class EndpointAttribute(string endpoint) : System.Attribute {
                public string Endpoint { get; } = endpoint;
            }
            public static class Subject {
                [Endpoint("0.0.0.0:4433")]
                public static void Case() { }
            }

            """);

        Assert.Contains(
            actualString: result.Single(id: Id).GetMessage(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "the literal \"0.0.0.0:4433\""
        );
    }
    [Fact]
    public void OrleansListenOnAnyHostAddressIsRefused() {
        var result = Run(body: """
            public static class Subject {
                public static void ConfigureEndpoints(int siloPort, int gatewayPort, bool listenOnAnyHostAddress = false) { }
                public static void Configure() {
                    ConfigureEndpoints(siloPort: 11111, gatewayPort: 30000, listenOnAnyHostAddress: true);
                    ConfigureEndpoints(siloPort: 11111, gatewayPort: 30000);
                }
            }

            """);

        Assert.StartsWith(
            actualString: result.Single(id: Id).GetMessage(),
            expectedStartString: "listenOnAnyHostAddress: true binds"
        );
    }
    [Fact]
    public void LoopbackListenersAndLookalikeAddressesPass() {
        var result = Run(body: """
            using System.Net;
            using System.Net.Sockets;

            public static class Subject {
                public static object[] Binds(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options, string configured, int port) {
                    options.Listen(endPoint: new IPEndPoint(IPAddress.Loopback, port));
                    return [
                        new TcpListener(IPAddress.Loopback, port),
                        new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, port)),
                        IPEndPoint.Parse(configured),
                        IPEndPoint.Parse("127.0.0.1:0"),
                        IPAddress.Parse("::1"),
                        "http://localhost:5000/",
                        "10.0.0.0/8",
                        "Version=10.0.0.0, Culture=neutral",
                        "Puck::Engine",
                        $"{configured}::{port}",
                    ];
                }
            }

            """);

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void AnAllowlistedSitePassesOnlyInItsAssemblyFileAndMember() {
        const string Body = """
            public static class AzureCommand {
                public const string DeploymentListenAddress = "0.0.0.0";
                public const string Elsewhere = "0.0.0.0";
            }

            """;
        const string Site = "C:/repo/src/Puck.Cli/Azure/AzureWorld.cs";

        Assert.Contains(
            actualString: Assert.Single(collection: Messages(result: Run(
                assemblyName: "Puck.Cli",
                body: Body,
                fileName: Site
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "the literal \"0.0.0.0\""
        );
        Assert.Equal(
            actual: Messages(result: Run(
                assemblyName: "Puck.Cli",
                body: Body,
                fileName: "C:/repo/src/Puck.Cli/Azure/AzureVerification.cs"
            )).Length,
            expected: 2
        );
        Assert.Equal(
            actual: Messages(result: Run(
                body: Body,
                fileName: Site
            )).Length,
            expected: 2
        );
    }
    [Fact]
    public void AnAllowlistedMemberCoversTheLambdasInsideIt() {
        var result = Run(
            assemblyName: "Puck.Cli.Tests",
            body: """
                public static class RemoteMcpTests {
                    public static System.Func<string> UnsafeDeploymentConfigurationFailsBeforeListening() => () => "http://0.0.0.0:8080";
                }

                """,
            fileName: "tests/Puck.Cli.Tests/RemoteMcpTests.cs"
        );

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void EveryAllowlistEntryStatesItsReasonAndNamesAFileThatExists() {
        var root = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((root is not null) && !File.Exists(path: Path.Combine(
            path1: root.FullName,
            path2: "Puck.slnx"
        ))) {
            root = root.Parent;
        }
        Assert.NotNull(@object: root);
        Assert.All(
            action: entry => {
                Assert.False(
                    condition: string.IsNullOrWhiteSpace(value: entry.Reason),
                    userMessage: $"{entry.Path} {entry.Member} has no reason"
                );
                Assert.True(
                    condition: File.Exists(path: Path.Combine(
                        path1: root!.FullName,
                        path2: entry.Path
                    )),
                    userMessage: $"{entry.Path} does not exist"
                );
            },
            collection: AnyAddressBindAllowlist.Entries
        );
    }
}
