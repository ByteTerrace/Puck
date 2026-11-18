using Microsoft.CodeAnalysis;
using Puck.Testing;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class DisclosureSurfaceLawTests {
    private static readonly SourceFile Control = new("Control.cs", """
        using Puck.Commands;
        using Puck.State;
        using Puck.State.Rules;
        using Puck.World.Server;

        public static class Control {
            public static CellKind Kind(RuleCompileContext context, string name) => context.FindRow(name)!.Kind;
            public static bool Failed(StateReadAccess access) => access.Failed;
            public static int Count(StateReadAccess access, StateReadRow row) => access.CellCount(row);
            public static StateReadRow Acquire(StateReadAccess access, string name) => access.ResolveRow(name);
            public static CommandContext Invocation(OperatorContext context) => context.Invocation;
            public static Type AuthorityType() => typeof(WorldDocument);
        }
        """);

    [Fact]
    public void ExternalCodeCanUseTheAdmittedSurface() =>
        Assert.Empty(Errors(FixtureCompiler.Compile("DisclosureControl", Control)));

    [Theory]
    [InlineData("object Read(RuleCompileContext c) => c.FindRowAt(0)!.Cells;", "CS1061")]
    [InlineData("object Read(StateRowDeclaration r) => r.Draw!.Secret;", "CS1061")]
    [InlineData("StateRow Seed(StateRowDeclaration r) => (StateRow)r;", "CS0030")]
    [InlineData("Func<StateReadAccess, StateArena> Store() => r => r.Arena;", "CS1061")]
    [InlineData("int Ordinal(StateReadRow row) => row.Ordinal;", "CS1061")]
    [InlineData("void Clear(StateReadAccess access) => access.Failed = false;", "CS0200")]
    [InlineData("OperatorContext Mint(CommandContext context) => new OperatorContext(context);", "CS1729")]
    [InlineData("Type Prepared() => typeof(WorldDocument.WorldPreparedMutation);", "CS0122")]
    public void ExternalCodeCannotAcquireOrMintThroughTheseMembers(string member, string code) {
        var probe = new SourceFile("Probe.cs", $$"""
            using Puck.Commands;
            using Puck.State;
            using Puck.State.Rules;
            using Puck.World.Server;
            public static class Probe { public static {{member}} }
            """);
        var error = Assert.Single(Errors(FixtureCompiler.Compile("DisclosureConsumer", Control, probe)));
        Assert.Equal("Probe.cs", error.Location.SourceTree?.FilePath);
        Assert.Equal(code, error.Id);
    }

    private static IEnumerable<Diagnostic> Errors(Compilation compilation) =>
        compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
