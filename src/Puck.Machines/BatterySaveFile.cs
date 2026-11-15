using Puck.Confinement;

namespace Puck.Machines;

/// <summary>
/// The persistence lifecycle every <see cref="IQueuedMachineCore"/> shares for its battery save: an optional path, a
/// reported (never propagated) read, an atomic replacement write, and the obligation to rewrite the file after the
/// machine's state moved backwards. A core decides what its save bytes are and when its emulated state is dirty; this
/// type owns where they go and when the disk may no longer match the machine.
/// <para>
/// The disk cannot rewind with a snapshot: a snapshot carries RAM but not the host's idea of what is already persisted,
/// so a core restored to RAM an earlier flush overtook looks clean while the file holds the abandoned future.
/// <see cref="MarkRewound"/> records that, and <see cref="IsStale"/> stays set until a write replaces the file, so a
/// failed write retries on the next flush. All members run on the core's owning thread.
/// </para>
/// </summary>
public sealed class BatterySaveFile {
    private readonly string? m_path;
    private readonly string m_reportTag;

    /// <summary>Initializes a new instance of the <see cref="BatterySaveFile"/> class.</summary>
    /// <param name="path">The save file's path, or <see langword="null"/> for a core that keeps its save in memory.</param>
    /// <param name="reportTag">The bracketed prefix on this file's stderr reports, such as <c>machine-host</c>.</param>
    public BatterySaveFile(string? path, string reportTag) {
        ArgumentNullException.ThrowIfNull(argument: reportTag);

        m_path = path;
        m_reportTag = reportTag;
    }

    /// <summary>Gets the save file's path, or <see langword="null"/> when the save lives in memory only.</summary>
    public string? Path =>
        m_path;
    /// <summary>Gets a value indicating whether the file may hold bytes the machine's state no longer has: the machine
    /// was restored since the last write, or the last write failed.</summary>
    public bool IsStale { get; private set; }

    /// <summary>Records that the machine's state was restored, so the file is rewritten at the next flush whether or not
    /// the emulated state reads as dirty.</summary>
    public void MarkRewound() {
        IsStale = true;
    }
    /// <summary>Reads the existing save, reporting a failure to stderr instead of propagating it.</summary>
    /// <returns>The file's bytes, or <see langword="null"/> when there is no path, no file, or the file is unreadable.</returns>
    public byte[]? Read() {
        if (
            (m_path is not { } path) ||
            !File.Exists(path: path)
        ) {
            return null;
        }

        try {
            return File.ReadAllBytes(path: path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Report(message: $"battery save '{path}' unreadable ({exception.Message}); booting with fresh battery memory.");

            return null;
        }
    }
    /// <summary>Reports a refusal to load an existing save to stderr, for a core that finds the file unusable.</summary>
    /// <param name="message">The refusal, naming the file and what the core does instead.</param>
    public void Report(string message) {
        Console.Error.WriteLine(value: $"[{m_reportTag}] {message}");
    }
    /// <summary>Replaces the file atomically with <paramref name="data"/>. A failure leaves the previous file intact, is
    /// reported to stderr, and leaves <see cref="IsStale"/> set so the next flush retries.</summary>
    /// <param name="data">The complete save image.</param>
    /// <returns>Whether the replacement succeeded; <see langword="false"/> also when there is no path.</returns>
    public bool TryWrite(ReadOnlySpan<byte> data) {
        if (m_path is not { } path) {
            return false;
        }

        try {
            AtomicFile.WriteAllBytes(
                bytes: data,
                path: path
            );
            IsStale = false;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            IsStale = true;
            Report(message: $"battery-save flush to '{path}' failed ({exception.Message}); retrying on the next flush.");

            return false;
        }
    }
}
