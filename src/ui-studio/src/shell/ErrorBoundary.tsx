import { Alert, Box, Button, Center, Code, Group, Stack, Text } from '@mantine/core'
import { IconAlertTriangle, IconChevronDown, IconChevronRight, IconRefresh, IconReload } from '@tabler/icons-react'
import { Component, useState, type ErrorInfo, type ReactNode } from 'react'

/**
 * Catches render/lifecycle errors from a subtree so one broken pane can't blank the whole
 * app. React has no hook equivalent — an error boundary must be a class component.
 *
 * Placement is deliberately layered:
 *   - `variant="page"` wraps <App/> in main.tsx, the last line of defence.
 *   - `variant="inline"` wraps each editor pane, so a crash in (say) the Response panel
 *     leaves the sidebar, tab bar and the rest of the editor usable.
 *
 * Recovery has two paths. "Try again" clears the captured error and re-renders the subtree,
 * which is enough when the fault was transient (a bad response body, a half-loaded module).
 * `resetKeys` clears it automatically when the user navigates elsewhere — otherwise a pane
 * that failed on one file would stay broken after switching to a different one.
 */
interface Props {
  children: ReactNode
  /** `page` fills the viewport and offers a reload; `inline` renders as a pane-sized alert. */
  variant?: 'page' | 'inline'
  /** Human label for the thing that broke, e.g. "Response panel". */
  label?: string
  /** When any value changes while an error is showing, the boundary resets itself. */
  resetKeys?: unknown[]
}

interface State {
  error: Error | null
  componentStack: string | null
}

export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null, componentStack: null }

  static getDerivedStateFromError(error: Error): Partial<State> {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    // Keep the console trace — the fallback UI shows the message, but the stack is what
    // you actually debug from, and swallowing it here would hide it entirely.
    console.error(`[ErrorBoundary${this.props.label ? `: ${this.props.label}` : ''}]`, error, info.componentStack)
    this.setState({ componentStack: info.componentStack ?? null })
  }

  componentDidUpdate(prev: Props) {
    if (!this.state.error) return
    const a = prev.resetKeys
    const b = this.props.resetKeys
    if (!a || !b) return
    if (a.length !== b.length || a.some((v, i) => !Object.is(v, b[i]))) this.reset()
  }

  reset = () => this.setState({ error: null, componentStack: null })

  render() {
    const { error, componentStack } = this.state
    if (!error) return this.props.children

    const fallback = (
      <ErrorFallback
        error={error}
        componentStack={componentStack}
        label={this.props.label}
        variant={this.props.variant ?? 'inline'}
        onRetry={this.reset}
      />
    )

    return this.props.variant === 'page'
      ? <Center h="100vh" p="xl">{fallback}</Center>
      : <Box p="md" h="100%" style={{ overflow: 'auto' }}>{fallback}</Box>
  }
}

function ErrorFallback({
  error, componentStack, label, variant, onRetry,
}: {
  error: Error
  componentStack: string | null
  label?: string
  variant: 'page' | 'inline'
  onRetry: () => void
}) {
  const [open, setOpen] = useState(false)

  return (
    <Alert
      color="red"
      variant="light"
      icon={<IconAlertTriangle size={18} />}
      title={label ? `${label} failed to render` : 'Something went wrong'}
      maw={variant === 'page' ? 640 : undefined}
    >
      <Stack gap="sm">
        <Text size="sm">
          {variant === 'page'
            ? 'The app hit an unexpected error. Your workspace files on disk are untouched.'
            : 'This panel hit an unexpected error. The rest of the app is still usable.'}
        </Text>

        <Code block fz="xs" style={{ whiteSpace: 'pre-wrap' }}>
          {error.message || String(error)}
        </Code>

        <Group gap="xs">
          <Button size="xs" leftSection={<IconRefresh size={14} />} onClick={onRetry}>
            Try again
          </Button>
          {variant === 'page' && (
            <Button
              size="xs"
              variant="default"
              leftSection={<IconReload size={14} />}
              onClick={() => window.location.reload()}
            >
              Reload app
            </Button>
          )}
          {componentStack && (
            <Button
              size="xs"
              variant="subtle"
              color="gray"
              leftSection={open ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
              onClick={() => setOpen(o => !o)}
            >
              Details
            </Button>
          )}
        </Group>

        {/*
          Plain conditional render, not <Collapse> — Collapse measures its child to animate,
          and inside these flex panes it measures 0 and never opens. An error UI has to be the
          one thing that cannot itself fail.
         */}
        {componentStack && open && (
          <Code block fz="10px" style={{ whiteSpace: 'pre-wrap', maxHeight: 240, overflow: 'auto' }}>
            {componentStack.trim()}
          </Code>
        )}
      </Stack>
    </Alert>
  )
}
