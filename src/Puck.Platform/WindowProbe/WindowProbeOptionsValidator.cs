using Microsoft.Extensions.Options;

namespace Puck.Platform.WindowProbe;

public sealed class WindowProbeOptionsValidator : IValidateOptions<WindowProbeOptions> {
    public ValidateOptionsResult Validate(string? name, WindowProbeOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>(capacity: 7);

        if (string.IsNullOrWhiteSpace(value: options.Title)) {
            failures.Add(item: $"{nameof(WindowProbeOptions.Title)} must be provided.");
        }

        if (options.Width == 0) {
            failures.Add(item: $"{nameof(WindowProbeOptions.Width)} must be greater than zero.");
        } else if (options.Width > int.MaxValue) {
            failures.Add(item: $"{nameof(WindowProbeOptions.Width)} must be less than or equal to {int.MaxValue}.");
        }

        if (options.Height == 0) {
            failures.Add(item: $"{nameof(WindowProbeOptions.Height)} must be greater than zero.");
        } else if (options.Height > int.MaxValue) {
            failures.Add(item: $"{nameof(WindowProbeOptions.Height)} must be less than or equal to {int.MaxValue}.");
        }

        if (options.MaxPumpIterations < 0) {
            failures.Add(item: $"{nameof(WindowProbeOptions.MaxPumpIterations)} must be zero or greater.");
        }

        if (options.PollDelayMilliseconds < 0) {
            failures.Add(item: $"{nameof(WindowProbeOptions.PollDelayMilliseconds)} must be zero or greater.");
        }

        return ((failures.Count == 0)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures));
    }
}
