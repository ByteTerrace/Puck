namespace Puck.Platform;

public interface IClipboardService {
    void SetText(string text);
    bool TryGetText(out string text);
}
