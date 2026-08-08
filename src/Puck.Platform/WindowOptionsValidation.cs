namespace Puck.Platform;

internal static class WindowOptionsValidation {
    public static void AddFailures(List<string> failures, string title, uint width, uint height) {
        if (string.IsNullOrWhiteSpace(value: title)) {
            failures.Add(item: "Title must be provided.");
        }

        AddDimensionFailure(failures: failures, name: "Width", value: width);
        AddDimensionFailure(failures: failures, name: "Height", value: height);
    }

    private static void AddDimensionFailure(List<string> failures, string name, uint value) {
        if (value == 0) {
            failures.Add(item: $"{name} must be greater than zero.");
        } else if (value > int.MaxValue) {
            failures.Add(item: $"{name} must be less than or equal to {int.MaxValue}.");
        }
    }
}
