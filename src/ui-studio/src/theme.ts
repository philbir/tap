import { createTheme, type MantineColorsTuple } from '@mantine/core'

/**
 * Tap Studio's Mantine theme. The accent ("tap") tuple keeps the violet identity from
 * the existing CSS-var palette while giving Mantine its expected 0–9 shade scale.
 *
 * Why a custom tuple rather than `violet`: Mantine's stock violet has a slightly cooler
 * undertone; Tap's brand violet (#a483ff dark / #5d2fd9 light) sits a hair warmer.
 *
 * Generated with mantine.dev/colors-generator. Index 6 is the canonical brand fill;
 * index 8/9 cover hover/active. Index 0 is body/background tint.
 */
const tap: MantineColorsTuple = [
  '#f1ecff',
  '#ddd1ff',
  '#bba0ff',
  '#996cff',
  '#7c41ff',
  '#6925ff',
  '#5d2fd9', // light-mode brand
  '#4f1cd1',
  '#a483ff', // dark-mode brand
  '#3e15a8',
]

export const theme = createTheme({
  primaryColor: 'tap',
  colors: { tap },
  fontFamily: "'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif",
  fontFamilyMonospace: "'JetBrains Mono', 'SF Mono', Menlo, Consolas, monospace",
  defaultRadius: 'md',
  cursorType: 'pointer',
  // Tighter form rhythm. The default is 36px — too tall for the data-density we want.
  components: {
    Button: { defaultProps: { size: 'sm' } },
    TextInput: { defaultProps: { size: 'sm' } },
    Textarea: { defaultProps: { size: 'sm' } },
    Select: { defaultProps: { size: 'sm', checkIconPosition: 'right' } },
    MultiSelect: { defaultProps: { size: 'sm' } },
    PasswordInput: { defaultProps: { size: 'sm' } },
    NumberInput: { defaultProps: { size: 'sm' } },
    TagsInput: { defaultProps: { size: 'sm' } },
    Tabs: { defaultProps: { variant: 'default' } },
    Modal: { defaultProps: { centered: true, radius: 'md', overlayProps: { backgroundOpacity: 0.55, blur: 2 } } },
  },
})
