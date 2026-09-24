import {
  ActionIcon,
  AppShell,
  Badge,
  Card,
  Code,
  createTheme,
  type CSSVariablesResolver,
  Loader,
  localStorageColorSchemeManager,
  Menu,
  Modal,
  NavLink,
  Paper,
  Table,
  Tabs,
  Tooltip,
} from "@mantine/core";
import { coral, ground, jade, night } from "./palette";
import classes from "./theme.module.css";

/**
 * Puck's Mantine theme. Palettes carry the brand; the resolver maps the brand's surfaces onto Mantine's scheme
 * variables; component defaults and class names hold the few deliberate departures from Mantine's own look.
 * Type roles: Lora (the brand's `--font-body`) for display and headings, the system sans for interface text,
 * and JetBrains Mono (`--font-code`) for labels, data, and code.
 */
export const puckTheme = createTheme({
  autoContrast: true,
  black: ground[8],
  colors: { coral, dark: night, gray: ground, jade },
  components: {
    ActionIcon: ActionIcon.extend({ defaultProps: { variant: "subtle" } }),
    AppShell: AppShell.extend({
      classNames: { header: classes.shellBar, main: classes.shellMain, navbar: classes.shellBar },
    }),
    Badge: Badge.extend({
      classNames: { root: classes.badge },
      defaultProps: { radius: "sm", variant: "light" },
    }),
    Card: Card.extend({ classNames: { root: classes.surface }, defaultProps: { withBorder: true } }),
    Code: Code.extend({ classNames: { root: classes.code } }),
    Loader: Loader.extend({ defaultProps: { type: "dots" } }),
    Menu: Menu.extend({ defaultProps: { shadow: "md", withinPortal: true } }),
    Modal: Modal.extend({
      classNames: { content: classes.surface, header: classes.surface },
      defaultProps: { centered: true, overlayProps: { backgroundOpacity: 0.5, blur: 3 } },
    }),
    NavLink: NavLink.extend({ classNames: { label: classes.navLabel, root: classes.navLink } }),
    Paper: Paper.extend({ classNames: { root: classes.surface } }),
    Table: Table.extend({
      classNames: { table: classes.table, th: classes.tableHead },
      defaultProps: { highlightOnHover: true, verticalSpacing: "xs" },
    }),
    Tabs: Tabs.extend({ classNames: { list: classes.tabsList, tab: classes.tab } }),
    Tooltip: Tooltip.extend({ defaultProps: { openDelay: 250, withArrow: true } }),
  },
  cursorType: "pointer",
  defaultRadius: "md",
  focusRing: "auto",
  fontFamily: 'system-ui, -apple-system, "Segoe UI Variable Text", "Segoe UI", Roboto, sans-serif',
  fontFamilyMonospace: "var(--font-code)",
  headings: {
    fontFamily: "var(--font-body)",
    fontWeight: "600",
    sizes: {
      h1: { fontSize: "1.75rem", lineHeight: "1.2" },
      h2: { fontSize: "1.375rem", lineHeight: "1.25" },
      h3: { fontSize: "1.125rem", lineHeight: "1.3" },
      h4: { fontSize: "1rem", lineHeight: "1.35" },
    },
  },
  luminanceThreshold: 0.35,
  primaryColor: "coral",
  primaryShade: { dark: 4, light: 7 },
  radius: { lg: "10px", md: "6px", sm: "4px", xl: "16px", xs: "2px" },
  shadows: {
    lg: "0 12px 32px -12px rgb(27 25 36 / 0.35)",
    md: "0 6px 18px -8px rgb(27 25 36 / 0.3)",
    sm: "0 1px 3px rgb(27 25 36 / 0.12)",
    xl: "0 20px 48px -16px rgb(27 25 36 / 0.4)",
    xs: "0 1px 2px rgb(27 25 36 / 0.08)",
  },
  white: ground[0],
});

/**
 * The brand's elevation model: a canvas, raised surfaces (bars, cards, dialogs, inputs), sunken wells (code,
 * viewports), and a hairline rule. In light the canvas is the brand `--paper` and surfaces lift toward white; in
 * dark the canvas is the dark `--paper` and surfaces lift to `--paper-2`.
 */
export const puckCssVariablesResolver: CSSVariablesResolver = () => ({
  dark: {
    "--puck-line": "var(--mantine-color-dark-5)",
    "--puck-surface-canvas": "var(--mantine-color-dark-7)",
    "--puck-surface-raised": "var(--mantine-color-dark-6)",
    "--puck-surface-sunken": "var(--mantine-color-dark-8)",
  },
  light: {
    "--mantine-color-body": "var(--mantine-color-gray-2)",
    "--mantine-color-default": "var(--mantine-color-gray-0)",
    "--mantine-color-default-hover": "var(--mantine-color-gray-1)",
    "--puck-line": "var(--mantine-color-gray-4)",
    "--puck-surface-canvas": "var(--mantine-color-gray-2)",
    "--puck-surface-raised": "var(--mantine-color-gray-0)",
    "--puck-surface-sunken": "var(--mantine-color-gray-3)",
  },
  variables: {},
});

export const puckColorSchemeManager = localStorageColorSchemeManager({
  key: "byteterrace.mantine.theme.colorScheme",
});
