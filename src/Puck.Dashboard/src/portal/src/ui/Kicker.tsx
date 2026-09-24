import { Text, type TextProps } from "@mantine/core";
import type { ReactNode } from "react";
import classes from "./Kicker.module.css";

interface KickerProps extends TextProps {
  children: ReactNode;
  /** A paragraph by default; a span where only phrasing content is allowed, such as inside a legend. */
  component?: "p" | "span";
}

/** The brand's small monospaced overline: a section label set above a title. */
export function Kicker({ children, component = "p", ...props }: KickerProps) {
  return (
    <Text className={classes.kicker} component={component} {...props}>
      {children}
    </Text>
  );
}
