import { Text, type TextProps, Tooltip } from "@mantine/core";
import { useState } from "react";

const UNITS: readonly [Intl.RelativeTimeFormatUnit, number][] = [
  ["year", 31_536_000],
  ["month", 2_592_000],
  ["week", 604_800],
  ["day", 86_400],
  ["hour", 3_600],
  ["minute", 60],
];

const relativeFormat = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });

/** "3 minutes ago", "yesterday": the largest whole unit between `time` and `now`. */
export function relativeTime(time: Date, now: number): string {
  const seconds = Math.round((time.getTime() - now) / 1000);

  for (const [unit, size] of UNITS) {
    if (Math.abs(seconds) >= size) {
      return relativeFormat.format(Math.round(seconds / size), unit);
    }
  }

  return relativeFormat.format(0, "minute");
}

interface TimestampProps extends TextProps {
  /** Show the time relative to the page's opening ("2 hours ago") instead of as a clock time. */
  relative?: boolean;
  time: Date;
}

/** A point in time in monospace, with the full local date and time on hover. */
export function Timestamp({ relative = false, time, ...props }: TimestampProps) {
  // Relative times are read against the moment the view opened, so a render never reads the clock.
  const [openedAt] = useState(Date.now);
  const label = relative
    ? relativeTime(time, openedAt)
    : time.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });

  return (
    <Tooltip label={time.toLocaleString(undefined, { dateStyle: "full", timeStyle: "long" })}>
      <Text component="time" dateTime={time.toISOString()} ff="monospace" size="xs" {...props}>
        {label}
      </Text>
    </Tooltip>
  );
}
