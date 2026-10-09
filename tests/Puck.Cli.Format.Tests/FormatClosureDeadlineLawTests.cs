using System.Xml.Linq;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Format.Tests;

/// <summary>A stalled reference query has one deadline in both pooled and individual evaluation. A batch timeout
/// refuses the batch rather than returning an empty result that would trigger an individual retry for every project.</summary>
public sealed class FormatClosureDeadlineLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task AStalledClosureQueryRefusesAtItsDeadline(bool batched) {
        using var directory = FormatNamedArgsClosureTests.PinnedScratch(prefix: "puck-format-closure-deadline-");
        var project = Path.Combine(path1: directory.RootPath, path2: "Waiting.proj");
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(path: directory.RootPath, filter: "entered.txt");

        watcher.Created += (_, _) => entered.TrySetResult();
        watcher.EnableRaisingEvents = true;
        // The target has no SDK or restore. Its marker is the handshake; the finite child is only a cleanup backstop
        // for a broken deadline, and the virtual clock determines the verdict without waiting out the product bound.
        var wait = (OperatingSystem.IsWindows()
            ? "powershell.exe -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 10\""
            : "sleep 10");

        new XDocument(new XElement(name: "Project",
            new XElement(name: "Target", new XAttribute(name: "Name", value: "FindReferenceAssembliesForReferences"),
                new XElement(name: "WriteLinesToFile",
                    new XAttribute(name: "File", value: "$(MSBuildProjectDirectory)/entered.txt"),
                    new XAttribute(name: "Lines", value: "entered")),
                new XElement(name: "Exec", new XAttribute(name: "Command", value: wait))),
            new XElement(name: "Import", new XAttribute(name: "Project", value: "$(CustomAfterMicrosoftCommonTargets)"),
                new XAttribute(name: "Condition", value: "'$(CustomAfterMicrosoftCommonTargets)' != ''"))))
            .Save(fileName: project);
        var clock = new VirtualClock();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);
        var run = Task.Run(action: () => {
            if (batched) {
                _ = CompileClosure.EvaluateAll(projects: [project], configuration: "Release", clock: clock, cancellationToken: cancelled.Token);
            } else {
                _ = CompileClosure.Evaluate(project: project, configuration: "Release", clock: clock, cancellationToken: cancelled.Token);
            }
        }, cancellationToken: cancelled.Token);

        try {
            if (await Task.WhenAny(task1: entered.Task, task2: run).WaitAsync(cancellationToken: cancelled.Token) == run) {
                await run;
                Assert.Fail(message: "MSBuild returned without entering the held reference query.");
            }
            await entered.Task.WaitAsync(cancellationToken: cancelled.Token);
            await clock.ExpireAsync(dueTime: CompileClosure.EvaluationTimeout, pending: run, ct: cancelled.Token);
            var refusal = await Assert.ThrowsAsync<TimeoutException>(testCode: () => run);

            Assert.Contains(expectedSubstring: "MSBuild compile-closure evaluation", actualString: refusal.Message);
            Assert.Contains(expectedSubstring: "its process tree was stopped", actualString: refusal.Message);
            Assert.Equal(expected: CompileClosure.EvaluationTimeout, actual: clock.Elapsed);
        } finally {
            await cancelled.CancelAsync();
            try { await run; } catch (Exception error) when ((error is TimeoutException or OperationCanceledException)) { }
            watcher.EnableRaisingEvents = false;
        }
    }
}
