namespace Puck.Platform;

public sealed class NullClipboardService : IClipboardService {
    public void SetText(string text) {
        ArgumentNullException.ThrowIfNull(text);
    }
    public bool TryGetText(out string text) {
        text = string.Empty;
        return false;
    }
}
