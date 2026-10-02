using Puck.Overlays;

namespace Puck.World.Client;

public sealed partial class WorldInspectorText {
    /// <summary>Appends the followed owner's retained diagnostics without resolving any field again.</summary>
    /// <param name="diagnostics">The shared guard's current invalid-field readout.</param>
    public void Diagnostics(IReadOnlyList<WorldValueDomainDiagnostic> diagnostics) {
        for (var index = 0; (index < diagnostics.Count); index++) {
            var diagnostic = diagnostics[index];

            Diagnostic(diagnostic: in diagnostic);
        }
    }
    /// <summary>Appends an active invalid-field diagnostic using the console's shared format. Long rows wrap
    /// within the existing editor reservation; a complete record that cannot fit is refused by writer name.</summary>
    /// <param name="diagnostic">The actual followed view's active diagnostic.</param>
    public void Diagnostic(in WorldValueDomainDiagnostic diagnostic) {
        if (Refused) { return; }
        Span<char> formatted = stackalloc char[(InspectorWriter.MaxLines * (InspectorWriter.MaxLineChars + 1))];

        if (!diagnostic.TryFormat(charsWritten: out var length, destination: formatted, format: default, provider: null)) {
            Refuse();
            return;
        }
        for (var offset = 0; (offset < length);) {
            var count = Math.Min(val1: InspectorWriter.MaxLineChars, val2: (length - offset));

            if ((m_chars.Length - m_length) < (count + 1)) { Refuse(); return; }
            m_chars[m_length++] = '\n';
            formatted.Slice(length: count, start: offset).CopyTo(destination: m_chars.AsSpan(start: m_length));
            m_length += count;
            offset += count;
        }
        Validate();
    }
}
