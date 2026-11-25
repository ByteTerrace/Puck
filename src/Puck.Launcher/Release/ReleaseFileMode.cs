namespace Puck.Launcher.Release;

/// <summary>
/// The one rule for a release file's Unix mode. Publication records whether each file of the built tree is executable in
/// <see cref="ReleasePayloadFile.Executable"/>; the stager creates every installed file with the mode the record names,
/// and the applier refuses a staged file whose mode disagrees with it. Copies made from an installed version (the stub
/// laid beside the release record) carry the installed file's mode. Windows has no execute permission and ignores the
/// record.
/// </summary>
public static class ReleaseFileMode {
    private const UnixFileMode AnyExecute = (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    private const UnixFileMode Readable = (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

    /// <summary>Reports whether a built file publishes as executable. On Unix the answer is the file's own mode (any
    /// execute bit). A Windows file system records no execute permission, so there the content decides: an ELF image or a
    /// <c>#!</c> script is executable, which keeps a non-Windows payload published from Windows runnable.</summary>
    /// <param name="path">The built file's path.</param>
    /// <param name="content">The file's complete bytes, as published.</param>
    /// <returns><see langword="true"/> when the file installs executable.</returns>
    public static bool IsExecutable(string path, ReadOnlySpan<byte> content) => (OperatingSystem.IsWindows()
        ? (content.StartsWith(value: "\u007FELF"u8) || content.StartsWith(value: "#!"u8))
        : ((File.GetUnixFileMode(path: path) & AnyExecute) != 0));
    /// <summary>Returns the mode an installed file is created with: read for everyone and write for its owner, plus
    /// execute for everyone when the record says it is executable. The process's umask still applies at creation.</summary>
    /// <param name="file">The payload file's record.</param>
    /// <returns>The file's creation mode.</returns>
    public static UnixFileMode Of(ReleasePayloadFile file) {
        ArgumentNullException.ThrowIfNull(argument: file);
        return (file.Executable ? (Readable | AnyExecute) : Readable);
    }
    /// <summary>Reports whether an installed file's mode agrees with its record: its owner may execute it exactly when the
    /// record says it is executable. Always <see langword="true"/> on Windows.</summary>
    /// <param name="path">The installed file's path.</param>
    /// <param name="file">The payload file's record.</param>
    /// <returns><see langword="true"/> when the mode agrees with the record.</returns>
    public static bool Matches(string path, ReleasePayloadFile file) {
        ArgumentNullException.ThrowIfNull(argument: file);
        return (OperatingSystem.IsWindows() || (((File.GetUnixFileMode(path: path) & UnixFileMode.UserExecute) != 0) == file.Executable));
    }
}
