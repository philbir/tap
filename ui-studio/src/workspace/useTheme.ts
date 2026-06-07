import { useMantineColorScheme } from '@mantine/core'

/**
 * Thin wrapper over Mantine's color-scheme manager so the rest of the app reads
 * `{ theme, toggle }` unchanged after the Mantine migration. Mantine persists the choice
 * to localStorage automatically (and writes a `<script>` before mount that prevents the
 * dark→light flash on first paint — see `ColorSchemeScript` in main.tsx).
 */
export function useTheme() {
  const { colorScheme, setColorScheme } = useMantineColorScheme()

  // Mantine's scheme can be 'auto' until the user picks one; collapse to the effective
  // value so the rest of the code only deals with light/dark.
  const effective: 'light' | 'dark' = colorScheme === 'light' ? 'light' : 'dark'

  return {
    theme: effective,
    toggle: () => setColorScheme(effective === 'dark' ? 'light' : 'dark'),
  }
}
