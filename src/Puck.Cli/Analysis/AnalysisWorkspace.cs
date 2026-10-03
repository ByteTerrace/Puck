using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Puck.Cli.Analysis;

/// <summary>The shared design-time workspace for semantic repository commands.</summary>
internal sealed class AnalysisWorkspace : IDisposable {
    private readonly MSBuildWorkspace m_workspace;
    private readonly IDisposable m_registration;

    private int m_failures;

    public AnalysisWorkspace(string configuration, string verb) {
        var properties = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["Configuration"] = configuration,
        };

        if (OperatingSystem.IsWindows()) {
            var sdk = Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.ProgramFilesX86), path2: "Windows Kits", path3: "10");

            if (Directory.Exists(path: sdk)) {
                // Use the installed machine SDK without probing unrelated per-user SDK registrations.
                properties["TargetPlatformSdkPath"] = (sdk.Replace(newChar: '/', oldChar: '\\') + "/");
                properties["TargetPlatformDisplayName"] = "Windows";
            }
        }
        m_workspace = MSBuildWorkspace.Create(properties: properties);
        m_registration = m_workspace.RegisterWorkspaceFailedHandler(handler: args => {
            Console.Error.WriteLine(value: $"{verb}: workspace: {args.Diagnostic.Message}");
            if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) {
                _ = Interlocked.Exchange(location1: ref m_failures, value: 1);
            }
        });
    }

    public bool Failed => (Volatile.Read(location: ref m_failures) != 0);

    public async Task<Solution> OpenAsync(string target, bool project) => (project
        ? (await m_workspace.OpenProjectAsync(projectFilePath: target)).Solution
        : await m_workspace.OpenSolutionAsync(solutionFilePath: target));
    public void Dispose() {
        m_registration.Dispose();
        m_workspace.Dispose();
    }
}
