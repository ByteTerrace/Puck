namespace Puck.Input.Hid;

internal static class HidFeatureWriteRetry {
    private const int Attempts = 50;
    private const int DelayMilliseconds = 2;

    public static async ValueTask<bool> TryWriteAsync(IHidDevice device, byte[] buffer, CancellationToken cancellationToken) {
        for (var attempt = 0; (attempt < Attempts); ++attempt) {
            if (device.TrySetFeatureReport(buffer: buffer)) {
                return true;
            }

            await Task.Delay(
                cancellationToken: cancellationToken,
                millisecondsDelay: DelayMilliseconds
            );
        }

        return false;
    }
}
