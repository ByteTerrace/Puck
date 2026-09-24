import { Box, type BoxProps } from "@mantine/core";
import type { ReactNode } from "react";
import classes from "./Well.module.css";

/**
 * A sunken well inside a raised panel: the home of tables, logs, and consoles. A table inside it takes the well's
 * surface for its sticky header and the theme's rules for its lines.
 */
export function Well({ children, className, ...props }: BoxProps & { children: ReactNode }) {
  return (
    <Box className={className ? `${classes.well} ${className}` : classes.well} {...props}>
      {children}
    </Box>
  );
}
