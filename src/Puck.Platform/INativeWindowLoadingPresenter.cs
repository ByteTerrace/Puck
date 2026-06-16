namespace Puck.Platform;

public interface INativeWindowLoadingPresenter {
    void ClearLoadingFrame();
    void RenderLoadingFrame(string heading, string detail, string? imagePath);
}
