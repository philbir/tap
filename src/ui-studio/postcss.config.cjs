// PostCSS preset required by Mantine v9 — enables its color-scheme mixins, breakpoint
// helpers, and CSS variables. Keep this file CommonJS so older toolchains (any future
// SSR or test runner) don't choke on ESM-only configs.
module.exports = {
  plugins: {
    'postcss-preset-mantine': {},
    'postcss-simple-vars': {
      variables: {
        'mantine-breakpoint-xs': '36em',
        'mantine-breakpoint-sm': '48em',
        'mantine-breakpoint-md': '62em',
        'mantine-breakpoint-lg': '75em',
        'mantine-breakpoint-xl': '88em',
      },
    },
  },
}
