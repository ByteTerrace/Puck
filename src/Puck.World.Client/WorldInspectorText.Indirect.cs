namespace Puck.World.Client;

public sealed partial class WorldInspectorText {
    private readonly WorldIndirectPickText m_indirect = new();

    private void Indirect(in WorldInspectorSnapshot snapshot) {
        var text = m_indirect.Read(pick: snapshot.Pick?.Indirect, reference: snapshot.IndirectReference).AsSpan();

        foreach (var row in text.Split(separator: '\n')) { _ = Line(text[row]); }
    }
}
