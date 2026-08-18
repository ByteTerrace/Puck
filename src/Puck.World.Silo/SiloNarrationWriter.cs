using System.Text;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>
/// Wraps <see cref="Console.Out"/>/<see cref="Console.Error"/> with a line prefix read from
/// <see cref="WorldNarrationScope.Current"/> at write time — <c>[&lt;scope&gt;] </c> while a row's own step,
/// transfer, or connection work has set one, <c>[silo] </c> otherwise. Installed once at silo startup with
/// <see cref="Console.SetOut(TextWriter)"/>/<see cref="Console.SetError(TextWriter)"/>; the desktop installs no such
/// writer, so engine narration there is written unprefixed exactly as before this type existed.
/// </summary>
/// <remarks>Only the whole-line write path (<see cref="WriteLine(string?)"/>/<see cref="WriteLine()"/>) is tagged —
/// every engine narration call in this codebase writes one complete line per call.</remarks>
internal sealed class SiloNarrationWriter(TextWriter inner) : TextWriter {
    /// <inheritdoc/>
    public override Encoding Encoding => inner.Encoding;

    private static string Prefix => ((WorldNarrationScope.Current is { Length: > 0 } scope)
        ? $"[{scope}] "
        : "[silo] "
    );

    /// <inheritdoc/>
    public override void Flush() => inner.Flush();
    /// <inheritdoc/>
    public override void Write(char value) => inner.Write(value: value);
    /// <inheritdoc/>
    public override void WriteLine() => inner.WriteLine(value: Prefix);
    /// <inheritdoc/>
    public override void WriteLine(string? value) => inner.WriteLine(value: (Prefix + (value ?? string.Empty)));
}
