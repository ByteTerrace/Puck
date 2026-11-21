namespace Puck.Cli;

/// <summary>
/// The profile of each start a launch began, recorded machine-wide so every process's host admission judges the running
/// World by the same profile its launch did (<see cref="HostStartProfile"/>): the launch names the profile it expects when the
/// World starts, and marks its store cold when the World's own output says it is baking meshes
/// (<see cref="WorldReadiness.Observe"/>). A mark is a file under the shared lease directory (<see cref="ProcessMarks"/>)
/// holding the profile's <see cref="HostStartProfile.Name"/>. A process with no mark (a World started by hand) is judged by its
/// kind alone.
/// </summary>
public static class StartProfiles {
    private const string Folder = "start-profile";

    private static string Directory(string root) => Path.Combine(path1: root, path2: Folder);

    /// <summary>Records a start's profile, replacing an earlier mark of the same process. A failure to write leaves the start
    /// judged by its kind alone.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="processId">The process id.</param>
    /// <param name="startTicks">The process's UTC start ticks.</param>
    /// <param name="profile">The start's profile.</param>
    public static void Mark(string root, int processId, long startTicks, HostStartProfile profile) =>
        ProcessMarks.Write(directory: Directory(root: root), processId: processId, startTicks: startTicks, contents: (profile.Name + "\n"));
    /// <summary>Reads the profiles recorded for the running processes, and removes the marks of processes no longer running.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="running">The running processes' UTC start ticks, by process id.</param>
    /// <returns>The profile of each marked process still running, by process id and start ticks.</returns>
    public static IReadOnlyDictionary<(int ProcessId, long StartTicks), HostStartProfile> Among(string root, IReadOnlyDictionary<int, long> running) =>
        ProcessMarks.Read<HostStartProfile>(directory: Directory(root: root), prune: true, running: running, parse: static (_, lines) => ((lines is [var name, ..] && HostStartProfile.TryParse(name: name, profile: out var profile)) ? profile : null));
}
