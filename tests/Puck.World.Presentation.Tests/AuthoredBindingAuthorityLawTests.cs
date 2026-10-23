using System.Diagnostics;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>A bindings-only author cannot put an authority verb on someone else's input page. The child process
/// owns the real process-wide registry, so installing it cannot change another law's validation environment.</summary>
public sealed class AuthoredBindingAuthorityLawTests {
    private const string AllowedCommand = "binding.safe";
    private const string ProtectedCommand = "world.grant";

    [Fact]
    public async Task ABindingsAuthorCannotExposeAnUnbindableAuthorityVerb() {
        var info = new ProcessStartInfo(fileName: "dotnet") {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(item: typeof(AuthoredBindingAuthorityLawTests).Assembly.Location);
        info.ArgumentList.Add(item: "--filter-method");
        info.ArgumentList.Add(item: $"{typeof(AuthoredBindingAuthorityLawTests).FullName}.{nameof(BindingMutationChild)}");
        info.ArgumentList.Add(item: "--explicit");
        info.ArgumentList.Add(item: "only");

        var installedBefore = WorldAffordances.Installed;
        using var child = (Process.Start(startInfo: info) ?? throw new InvalidOperationException(message: "The binding witness child test host did not start."));
        var output = child.StandardOutput.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        var errors = child.StandardError.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        await child.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken);
        var printed = $"{await output}{await errors}";

        Assert.True(condition: (child.ExitCode == 0), userMessage: $"The binding witness exited {child.ExitCode}:{Environment.NewLine}{printed}");
        Assert.Contains(actualString: printed, expectedSubstring: "total: 1");
        Assert.Contains(actualString: printed, expectedSubstring: "succeeded: 1");
        Assert.Equal(expected: installedBefore, actual: WorldAffordances.Installed);
    }

    // Only the wrapper above selects this fact. The process exits with its registry; no reset or replacement of
    // Schema's hooks is needed. VocabularyHooks.Client installs the same hooks as the real World composition.
    [Fact(Explicit = true)]
    public void BindingMutationChild() {
        Assert.False(condition: WorldAffordances.Installed);
        var registry = new CommandRegistry(modules: [new WorldGrantCommandModule(authority: new UnusedAuthority()), new SafeCommand()]);
        Assert.True(condition: registry.TryGetMetadata(name: ProtectedCommand, metadata: out var protectedMetadata));
        Assert.Equal(expected: CommandBindability.Unbindable, actual: protectedMetadata.Bindability);
        Assert.True(condition: registry.TryGetMetadata(name: AllowedCommand, metadata: out var allowedMetadata));
        Assert.Equal(expected: CommandBindability.Bindable, actual: allowedMetadata.Bindability);
        WorldAffordances.Install(registry: registry);

        using var fixture = Fixtures.FreshServer();
        // This host-stamped addon begins with no standing. Give it only the one mutation kind on bindings, with
        // the ordinary untrusted dispatch budget; it never acquires Console, Control or grant administration.
        var actor = Principal.Addon(name: "bindings-author");
        Assert.Empty(collection: fixture.Server.GrantRows(principal: actor));
        Assert.True(condition: MutationKindMask.TryParse(text: nameof(WorldMutation.UpsertBindingOverlay), mask: out var kinds, unknown: out var unknown), userMessage: unknown);
        var hold = new WorldGrant(Grantee: actor, Capability: WorldCapability.Mutate,
            Subject: GrantSubject.Section(section: WorldSection.Bindings), Exclusive: false, Budget: 2, KindMask: kinds);
        fixture.Server.Grant(actor: Principal.Console, grant: hold);
        Assert.Equal(expected: hold, actual: Assert.Single(collection: fixture.Server.GrantRows(principal: actor)));

        var before = fixture.DefinitionBytes();
        var version = fixture.Server.DocumentVersion;
        var refused = Submit(fixture: fixture, actor: actor, command: ProtectedCommand, sequence: 1);
        Assert.True(condition: refused.Refused, userMessage: refused.Detail);
        Assert.Equal(expected: actor, actual: refused.Actor);
        Assert.Contains(expectedSubstring: ProtectedCommand, actualString: refused.Detail);
        Assert.Contains(expectedSubstring: "not bindable", actualString: refused.Detail);
        Assert.Equal(expected: version, actual: fixture.Server.DocumentVersion);
        Assert.Equal(expected: before, actual: fixture.DefinitionBytes());

        // Same actor, grant, overlay id, source and pipeline; only the destination verb changes.
        var applied = Submit(fixture: fixture, actor: actor, command: AllowedCommand, sequence: 2);
        Assert.True(condition: applied.Applied, userMessage: applied.Detail);
        Assert.Equal(expected: actor, actual: applied.Actor);
        var overlay = Assert.Single(collection: fixture.Server.Definition.BindingOverlays);
        var chord = Assert.Single(collection: overlay.Document!.Chords);
        Assert.Equal(expected: AllowedCommand, actual: Assert.Single(collection: chord.Page!.Entries).Command);
        Assert.False(condition: before.AsSpan().SequenceEqual(other: fixture.DefinitionBytes()));
        Assert.Equal(expected: hold, actual: Assert.Single(collection: fixture.Server.GrantRows(principal: actor)));
    }

    private static WorldMutationOutcome Submit(WorldFixture fixture, Principal actor, string command, long sequence) {
        var overlay = new WorldBindingOverlay(Id: "authored", Document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion, Modifiers: [], Chords: [new BindingChordDefinition(
                Group: "play", Chord: [], Page: new BindingPageDefinition(Id: "rest",
                    Entries: [new BindingPageEntryDefinition(Sources: ["keyboard.f9"], Command: command)]))]));
        WorldSubmissionResult? result = null;
        fixture.Server.Submit(completion: answer => result = answer, envelope: new SubmissionEnvelope(
            ConnectionId: SubmissionEnvelope.LocalConnectionId, SessionGeneration: 0, Sequence: sequence,
            CorrelationId: sequence, Principal: actor, OperationId: Guid.NewGuid(),
            Payload: new WorldSubmissionPayload.Mutation(Value: new WorldMutation.UpsertBindingOverlay(Principal: actor, Overlay: overlay))));
        fixture.Step();
        return Assert.IsType<WorldSubmissionResult.Mutation>(@object: result).Outcome;
    }

    private sealed class SafeCommand : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(name: AllowedCommand, description: "A harmless input gesture.",
                bindability: CommandBindability.Bindable, handler: static (_, _) => CommandResult.None);
        }
    }

    private sealed class UnusedAuthority : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance instance, out string refusal) =>
            throw new InvalidOperationException(message: "Binding admission must inspect command metadata without executing the authority verb.");
    }
}
