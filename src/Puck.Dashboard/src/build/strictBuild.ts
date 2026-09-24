import { createLogger, type Logger, type Plugin, type Rolldown } from "vite";

/** A build warning the dashboard accepts, with the reason it is not a defect. */
export interface AcceptedWarning {
  readonly code: string;
  readonly reason: string;
  matches(warning: Rolldown.RolldownLog): boolean;
}

/**
 * Production builds treat warnings as errors, the same contract the repository's .NET build enforces: every
 * warning from Rolldown or Vite fails the build unless it is named in `accepted` with its reason. Every warning,
 * Rolldown's and Vite's own (chunk budgets, config notices), ends at the logger, which prints and records it;
 * `onwarn` only removes the accepted ones on the way. The build fails once the bundle is written rather than
 * at the warning itself, because a throw from inside a log callback is swallowed on Rolldown's native reporter
 * path. Development serving keeps the stock logger, because a dev-server warning should never take the server
 * down mid-edit.
 */
export function strictBuild(accepted: readonly AcceptedWarning[] = []): {
  checks: Rolldown.ChecksOptions;
  customLogger: Logger;
  onwarn: Rolldown.WarningHandlerWithDefault;
  plugin: Plugin;
} {
  const logger = createLogger();
  const warnings: string[] = [];
  const record = (message: string, options?: Parameters<Logger["warn"]>[1]) => {
    warnings.push(message);
    logger.warn(message, options);
  };

  return {
    // PLUGIN_TIMINGS fires only once Rolldown's own build time passes three seconds, so whether it appears
    // depends on the machine and its load: a wall-clock advisory cannot be a deterministic gate. Profile plugin
    // cost with `node --cpu-prof` instead.
    checks: { pluginTimings: false },
    customLogger: { ...logger, warn: record, warnOnce: record },
    onwarn: (warning, forward) => {
      if (!accepted.some((candidate) => candidate.code === warning.code && candidate.matches(warning))) {
        forward(warning);
      }
    },
    plugin: {
      apply: "build",
      closeBundle: {
        order: "post",
        handler() {
          if (warnings.length > 0) {
            throw new Error(`warnings are errors in this build (${warnings.length}); see the warnings above.`);
          }
        },
      },
      name: "puck:strict-build",
    },
  };
}
