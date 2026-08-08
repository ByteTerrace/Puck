using Microsoft.Extensions.Options;

namespace Puck.Platform.WindowProbe;

public sealed class WindowProbeOptionsValidator : IValidateOptions<WindowProbeOptions> {
    public ValidateOptionsResult Validate(string? name, WindowProbeOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>(capacity: 7);

        WindowOptionsValidation.AddFailures(failures: failures, title: options.Title, width: options.Width, height: options.Height);

        if (options.MaxPumpIterations < 0) {
            failures.Add(item: $"{nameof(WindowProbeOptions.MaxPumpIterations)} must be zero or greater.");
        }

        if (options.PollDelayMilliseconds < 0) {
            failures.Add(item: $"{nameof(WindowProbeOptions.PollDelayMilliseconds)} must be zero or greater.");
        }

        return ((failures.Count == 0)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures: failures));
    }
}
