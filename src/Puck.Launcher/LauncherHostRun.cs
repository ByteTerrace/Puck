using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions;
using Puck.Abstractions.Gpu;

namespace Puck.Launcher;

/// <summary>
/// Runs a built launcher host to completion and maps how it ended onto the process exit code. A run that stopped
/// normally is 0. A host whose environment could not provide a resource the run requires — a
/// <see cref="HostResourceUnavailableException"/> such as a <see cref="GpuDeviceUnavailableException"/> or a
/// <see cref="ListenEndpointUnavailableException"/>, raised while the host started or by a pump, including as the inner
/// failure of a <see cref="PresenterActivationException"/> — is <see cref="UnsupportedExitCode"/> with one
/// <c>[&lt;label&gt;.host: unsupported: &lt;resource&gt; unavailable: &lt;reason&gt;]</c> line on the error writer.
/// Any other failure, from the start or from a pump, is rethrown unchanged, so a pump that crashed never exits 0.
/// </summary>
public static class LauncherHostRun {
    /// <summary>The process exit code of a host this environment cannot run: its backend has no usable device here,
    /// the operating system does not offer that backend, or a listener cannot bind its endpoint. A refused
    /// configuration exits 1 instead.</summary>
    public const int UnsupportedExitCode = 2;

    /// <summary>Finds the environment failure a run ended on, searching <paramref name="exception"/>, its inner
    /// exceptions, and every exception an <see cref="AggregateException"/> carries.</summary>
    /// <param name="exception">The failure the run ended on.</param>
    /// <returns>The first <see cref="HostResourceUnavailableException"/> found, or <see langword="null"/> when the
    /// failure is an ordinary one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static HostResourceUnavailableException? FindUnavailable(Exception exception) {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch {
            HostResourceUnavailableException unavailable => unavailable,
            AggregateException aggregate => aggregate.InnerExceptions.Select(selector: FindUnavailable).FirstOrDefault(predicate: static found => (found is not null)),
            { InnerException: { } inner } => FindUnavailable(exception: inner),
            _ => null,
        };
    }
    /// <summary>Formats the one line that announces a host this environment cannot run.</summary>
    /// <param name="label">The host label, such as <c>world</c>.</param>
    /// <param name="unavailable">The environment failure, naming its resource and reason.</param>
    /// <returns>The line, <c>[&lt;label&gt;.host: unsupported: &lt;resource&gt; unavailable: &lt;reason&gt;]</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="unavailable"/> is <see langword="null"/>.</exception>
    public static string FormatUnsupported(string label, HostResourceUnavailableException unavailable) {
        ArgumentNullException.ThrowIfNull(unavailable);

        return $"{UnsupportedLinePrefix(label: label)}{unavailable.Message}]";
    }
    /// <summary>Runs <paramref name="host"/> until it stops and returns the process exit code.</summary>
    /// <param name="host">The built, not yet started host; it is disposed when the run ends.</param>
    /// <param name="label">The host label the unsupported line carries, such as <c>world</c>.</param>
    /// <param name="error">The writer the unsupported line goes to (stderr in a real process).</param>
    /// <returns>0, or <see cref="UnsupportedExitCode"/> when the host environment cannot provide a resource the run requires.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> or <paramref name="error"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="label"/> is <see langword="null"/>, empty, or white space.</exception>
    public static async Task<int> RunAsync(IHost host, string label, TextWriter error) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(error);

        BackgroundService[] pumps = [];
        Exception? runFailure = null;
        var ran = false;

        try {
            // Resolved before the run, because RunAsync disposes the host and its service provider with it; inside the
            // try, because constructing a pump can bring a device up (the offscreen shape resolves its render root).
            pumps = [.. host.Services.GetServices<IHostedService>().OfType<BackgroundService>()];
            ran = true;
            await host.RunAsync();
        } catch (Exception exception) {
            runFailure = exception;
        }

        if (!ran) {
            try {
                host.Dispose();
            } catch (Exception exception) {
                WriteSecondary(
                    error: error,
                    failure: exception,
                    label: label
                );
            }
        }

        // .NET 10's RunAsync returns normally while a BackgroundService's ExecuteTask is faulted, so pump failures are
        // read from the tasks themselves. A pump's fault ended the run, so it precedes anything RunAsync raised while
        // stopping or disposing the host, and it is the failure the run reports.
        Exception? pumpFault = null;

        foreach (var pump in pumps) {
            if (pump.ExecuteTask is { IsFaulted: true } faulted) {
                pumpFault = faulted.Exception.InnerException!;

                break;
            }
        }

        if ((pumpFault ?? runFailure) is not { } first) {
            return 0;
        }

        if (
            (pumpFault is not null) &&
            (runFailure is not null)
        ) {
            WriteSecondary(
                error: error,
                failure: runFailure,
                label: label
            );
        }

        if (FindUnavailable(exception: first) is { } unavailable) {
            return ReportUnsupported(
                error: error,
                label: label,
                unavailable: unavailable
            );
        }

        ExceptionDispatchInfo.Throw(source: first);

        return 1;
    }
    /// <summary>
    /// Runs a host pump's teardown steps in order, every one of them even when an earlier one throws. A teardown failure
    /// never replaces the fault that ended the pump: with <paramref name="fault"/> set, every teardown failure is logged
    /// beside it and nothing is thrown, so the pump's own fault propagates unchanged — a device bring-up failure stays
    /// the pump's fault even when a render root that never allocated throws on disposal. With no fault, the first
    /// teardown failure is rethrown once every step has run, and later ones are logged.
    /// </summary>
    /// <param name="fault">The exception that ended the pump, or <see langword="null"/> when it ended normally.</param>
    /// <param name="logger">The logger that records a teardown failure the pump does not report.</param>
    /// <param name="steps">The named teardown steps, in order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public static void RunTeardown(Exception? fault, ILogger logger, params ReadOnlySpan<(string Step, Action Run)> steps) {
        ArgumentNullException.ThrowIfNull(logger);

        Exception? failure = null;

        foreach (var (step, run) in steps) {
            try {
                run();
            } catch (Exception exception) {
                if (
                    (fault is null) &&
                    (failure is null)
                ) {
                    failure = exception;
                } else {
                    logger.LogError(
                        exception: exception,
                        message: "Host teardown step '{Step}' failed after an earlier failure, which stands: {First}",
                        step,
                        (fault ?? failure)!.Message
                    );
                }
            }
        }

        if (failure is not null) {
            ExceptionDispatchInfo.Throw(source: failure);
        }
    }
    /// <summary>Returns the start of the unsupported line, which a supervising process matches to recognise the outcome.</summary>
    /// <param name="label">The host label, such as <c>world</c>.</param>
    /// <returns><c>[&lt;label&gt;.host: unsupported: </c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="label"/> is <see langword="null"/>, empty, or white space.</exception>
    public static string UnsupportedLinePrefix(string label) {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        return $"[{label}.host: unsupported: ";
    }

    // A failure that came after the one the run reports: kept on the error writer, never in place of the first.
    private static void WriteSecondary(TextWriter error, string label, Exception failure) =>
        error.WriteLine(value: $"[{label}.host: a later failure followed the first: {failure.GetType().Name}: {failure.Message.ReplaceLineEndings(replacementText: " ")}]");

    /// <summary>Writes the one line that announces a host this environment cannot run and returns its exit code.</summary>
    /// <param name="error">The writer the line goes to (stderr in a real process).</param>
    /// <param name="label">The host label, such as <c>world</c>.</param>
    /// <param name="unavailable">The environment failure, naming its resource and reason.</param>
    /// <returns><see cref="UnsupportedExitCode"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> or <paramref name="unavailable"/> is <see langword="null"/>.</exception>
    public static int ReportUnsupported(TextWriter error, string label, HostResourceUnavailableException unavailable) {
        ArgumentNullException.ThrowIfNull(error);
        error.WriteLine(value: FormatUnsupported(
            label: label,
            unavailable: unavailable
        ));

        return UnsupportedExitCode;
    }
}
