using System.Diagnostics.CodeAnalysis;

namespace Puck.Confinement;

/// <summary>Best-effort removal of a file a caller owns and can leave behind: a file another process holds open, a
/// read-only one, or one Windows keeps pending deletion stays, and the caller learns it did.</summary>
public static class FileRemoval {
    /// <summary>Deletes a file, answering whether it is gone; a missing file is gone.</summary>
    /// <param name="path">The file to delete.</param>
    /// <returns><see langword="true"/> when the file no longer exists under its name; <see langword="false"/> when the
    /// operating system refused to remove it.</returns>
    public static bool TryDelete(string path) => TryDelete(failure: out _, path: path);
    /// <summary>Deletes a file, answering whether it is gone and, when it is not, why; a missing file is gone.</summary>
    /// <param name="path">The file to delete.</param>
    /// <param name="failure">The input/output or access failure that kept the file, or <see langword="null"/> when it is
    /// gone.</param>
    /// <returns><see langword="true"/> when the file no longer exists under its name; <see langword="false"/> when the
    /// operating system refused to remove it.</returns>
    public static bool TryDelete(string path, [NotNullWhen(returnValue: false)] out Exception? failure) {
        try {
            File.Delete(path: path);
            failure = null;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            failure = exception;

            return false;
        }
    }
}
