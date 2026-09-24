import type { MantineColorsTuple } from "@mantine/core";

// Ten-step ramps for Puck's three hue families (branding/tokens.css): a violet-neutral ground (H~275), the
// rose-coral lead (H~350), and its jade complement (H~168). Each ramp runs light to dark and places the brand
// token values at the shades the theme reads, so Mantine's generated variants and the shared tokens agree.

/** Rose-coral: shade 7 is the light-scheme `--accent`, shade 4 the dark-scheme one. */
export const coral: MantineColorsTuple = [
  "#fdf0f3",
  "#fce1e6",
  "#f9c1cc",
  "#ffa3b3",
  "#f2879a",
  "#e56784",
  "#d15d78",
  "#ad3a53",
  "#8e2d42",
  "#6b1f31",
];

/** Jade: shade 7 is the light-scheme `--accent-2`, shade 4 the dark-scheme one. */
export const jade: MantineColorsTuple = [
  "#e6fbf5",
  "#cbf7ec",
  "#9bf0dc",
  "#62e4c8",
  "#4fd0b4",
  "#1bc09f",
  "#0fa185",
  "#0e7d6b",
  "#0d6457",
  "#083b34",
];

/**
 * The ground's light end, standing in for Mantine's `gray`: shade 2 is the light `--paper`, 4 the light
 * `--rule`, 6 the light `--ink-soft` (Mantine's dimmed text), and 8 the light `--ink`.
 */
export const ground: MantineColorsTuple = [
  "#f6f5f9",
  "#efedf3",
  "#e7e4ed",
  "#dbd7e4",
  "#d0cbdc",
  "#877f99",
  "#5a5470",
  "#433d55",
  "#2b2736",
  "#1b1924",
];

/**
 * The ground's dark end, standing in for Mantine's `dark`: shade 0 is the dark `--ink`, 2 the dark `--ink-soft`,
 * 5 the dark `--rule`, 6 the dark `--paper-2`, 7 the dark `--paper`, and 8 the dark `--code-bg`.
 */
export const night: MantineColorsTuple = [
  "#ebe7f2",
  "#cdc7d8",
  "#b5aec3",
  "#847d93",
  "#4a4459",
  "#363145",
  "#262231",
  "#1b1924",
  "#15131c",
  "#0f0d14",
];
