namespace Puck.GamingBricks;

/// <summary>Copies serialized snapshot state into a reusable byte buffer.</summary>
public static class SnapshotBuffer {
    /// <summary>Copies a writer's current contents into a buffer, growing the buffer when required.</summary>
    /// <param name="writer">The writer containing serialized state.</param>
    /// <param name="buffer">The reusable destination buffer.</param>
    /// <returns>The number of bytes copied.</returns>
    public static int CopyWrittenState(StateWriter writer, ref byte[] buffer) {
        var written = writer.WrittenSpan;

        if (buffer.Length < written.Length) {
            buffer = new byte[written.Length];
        }

        written.CopyTo(destination: buffer);

        return written.Length;
    }
}
