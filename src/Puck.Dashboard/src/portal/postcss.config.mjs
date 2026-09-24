// Mantine's PostCSS preset: light-dark(), rem()/em(), and the @mixin hover/light/dark/smaller-than/larger-than
// helpers the theme's CSS modules are written with. Breakpoints mirror the Mantine theme's defaults.
export default {
  plugins: {
    "postcss-preset-mantine": {},
    "postcss-simple-vars": {
      variables: {
        "mantine-breakpoint-xs": "36em",
        "mantine-breakpoint-sm": "48em",
        "mantine-breakpoint-md": "62em",
        "mantine-breakpoint-lg": "75em",
        "mantine-breakpoint-xl": "88em",
      },
    },
  },
};
