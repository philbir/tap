import { ActionIcon, Box, Group, Menu, Paper, Text, Tooltip } from '@mantine/core'
import { IconAlertCircle, IconCheck, IconChevronDown, IconRefresh, IconSchema, IconSparkles } from '@tabler/icons-react'
import CodeMirror from '@uiw/react-codemirror'
import { vscodeDark, vscodeLight } from '@uiw/codemirror-theme-vscode'
import { json } from '@codemirror/lang-json'
import { graphql as graphqlLang } from 'cm6-graphql'
import { autocompletion, closeBrackets } from '@codemirror/autocomplete'
import { bracketMatching } from '@codemirror/language'
import { history } from '@codemirror/commands'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { buildClientSchema, buildSchema, type GraphQLSchema, type IntrospectionQuery } from 'graphql'
import { api } from '../api/client'
import type { GraphQLSchemaMode } from '../api/types'
import { parseGraphQLBody, serializeGraphQLBody, tryPrettyJson } from './body-mode'

interface Props {
  /** Path of the currently-edited request file — needed to render through the pipeline
   *  when the user clicks "Fetch schema". */
  requestPath: string
  env: string | null
  stage: string | null
  /** Raw wire-format body string: usually `{ "query": "...", "variables": {...} }`. */
  body: string
  /** Whether the parent request has unsaved changes. We disable the schema-fetch button
   *  when dirty since the render uses the persisted spec. */
  dirty: boolean
  onChange: (body: string | undefined) => void
}

type SchemaStatus = 'idle' | 'loading' | 'loaded' | 'error'

const SCHEMA_MODE_LABEL: Record<GraphQLSchemaMode, string> = {
  introspection: 'Introspection',
  sdl: 'SDL (?sdl)',
}

export function GraphQLEditor({ requestPath, env, stage, body, dirty, onChange }: Props) {
  const initial = useMemo(() => parseGraphQLBody(body), [body])
  const [query, setQuery] = useState(initial.query)
  const [variables, setVariables] = useState(initial.variables)

  // Re-sync local editor state when the parent's body string changes for non-edit reasons
  // (e.g., user navigated to a different request, or hit Discard). We compare the parsed
  // body to our last serialization to avoid clobbering in-flight typing.
  const lastSerialized = useRef(body)
  useEffect(() => {
    if (body === lastSerialized.current) return
    const parsed = parseGraphQLBody(body)
    setQuery(parsed.query)
    setVariables(parsed.variables)
    lastSerialized.current = body
  }, [body])

  function emit(nextQuery: string, nextVars: string) {
    const serialized = serializeGraphQLBody({ query: nextQuery, variables: nextVars })
    lastSerialized.current = serialized
    onChange(nextQuery.length === 0 && nextVars.trim().length === 0 ? undefined : serialized)
  }

  const handleQueryChange = useCallback((next: string) => {
    setQuery(next)
    emit(next, variables)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [variables])

  const handleVariablesChange = useCallback((next: string) => {
    setVariables(next)
    emit(query, next)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query])

  // Schema state — fetched on demand; lives only in component memory (deliberate, the
  // user can re-fetch any time and it's typically small enough that caching is moot).
  const [schema, setSchema] = useState<GraphQLSchema | null>(null)
  const [status, setStatus] = useState<SchemaStatus>('idle')
  const [schemaError, setSchemaError] = useState<string | null>(null)
  const [mode, setMode] = useState<GraphQLSchemaMode>('introspection')

  async function fetchSchema(nextMode: GraphQLSchemaMode = mode) {
    setStatus('loading')
    setSchemaError(null)
    try {
      const result = await api.graphqlSchema(requestPath, env, stage, nextMode)
      if (result.error || !result.schema) {
        setStatus('error')
        setSchemaError(result.error ?? 'Empty response from upstream')
        return
      }
      let built: GraphQLSchema
      if (nextMode === 'sdl') {
        built = buildSchema(result.schema)
      } else {
        const parsed = JSON.parse(result.schema) as { data?: IntrospectionQuery; __schema?: IntrospectionQuery['__schema'] }
        const introspection = (parsed.data ?? (parsed.__schema ? parsed : null)) as IntrospectionQuery | null
        if (!introspection || !introspection.__schema)
          throw new Error('Response did not contain a `data.__schema` (introspection) payload')
        built = buildClientSchema(introspection)
      }
      setSchema(built)
      setStatus('loaded')
    } catch (e) {
      setStatus('error')
      setSchemaError(e instanceof Error ? e.message : String(e))
    }
  }

  const schemaColor =
    status === 'loaded' ? 'green' :
    status === 'error' ? 'red' :
    status === 'loading' ? 'blue' :
    'orange'

  const schemaTitle =
    status === 'loaded' ? `Schema loaded (${SCHEMA_MODE_LABEL[mode]})` :
    status === 'error' ? `Schema error — ${schemaError ?? 'unknown'}` :
    status === 'loading' ? 'Fetching schema…' :
    'Fetch schema'

  // Theme tracking — Mantine swaps a data-attribute on <html>; CodeMirror needs an
  // explicit theme prop. Listen once and flip on changes.
  const [dark, setDark] = useState(() =>
    document.documentElement.getAttribute('data-mantine-color-scheme') === 'dark')
  useEffect(() => {
    const obs = new MutationObserver(() => {
      setDark(document.documentElement.getAttribute('data-mantine-color-scheme') === 'dark')
    })
    obs.observe(document.documentElement, { attributes: true, attributeFilter: ['data-mantine-color-scheme'] })
    return () => obs.disconnect()
  }, [])
  const theme = dark ? vscodeDark : vscodeLight

  const queryExtensions = useMemo(
    () => [graphqlLang(schema ?? undefined), bracketMatching(), closeBrackets(), autocompletion(), history()],
    [schema],
  )

  return (
    <Box>
      <Group justify="space-between" mb={6} wrap="nowrap">
        <Group gap={6} wrap="nowrap">
          <Text size="xs" fw={600} c="dimmed" tt="uppercase">Query</Text>
          <Tooltip label={schemaTitle} withArrow openDelay={300}>
            <ActionIcon
              size="sm"
              variant="light"
              color={schemaColor}
              onClick={() => fetchSchema()}
              loading={status === 'loading'}
              disabled={dirty}
              aria-label="Fetch GraphQL schema"
            >
              <IconRefresh size={13} />
            </ActionIcon>
          </Tooltip>
          <Menu shadow="md" position="bottom-start" withinPortal>
            <Menu.Target>
              <ActionIcon size="sm" variant="subtle" color="gray" aria-label="Schema mode" disabled={dirty}>
                <IconChevronDown size={13} />
              </ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>Schema source</Menu.Label>
              <Menu.Item
                leftSection={<IconSchema size={13} />}
                rightSection={mode === 'introspection' ? <IconCheck size={12} /> : null}
                onClick={() => { setMode('introspection'); fetchSchema('introspection') }}
              >
                Introspection query (POST)
              </Menu.Item>
              <Menu.Item
                leftSection={<IconSchema size={13} />}
                rightSection={mode === 'sdl' ? <IconCheck size={12} /> : null}
                onClick={() => { setMode('sdl'); fetchSchema('sdl') }}
              >
                SDL (<Text component="span" ff="var(--mono)">?sdl</Text> GET)
              </Menu.Item>
            </Menu.Dropdown>
          </Menu>
          {status === 'error' && (
            <Tooltip
              label={<Box maw={420} style={{ whiteSpace: 'pre-wrap' }}>{schemaError}</Box>}
              withArrow multiline w={440}
            >
              <ActionIcon size="sm" variant="light" color="red" aria-label="Schema error details">
                <IconAlertCircle size={13} />
              </ActionIcon>
            </Tooltip>
          )}
          {dirty && <Text size="xs" c="dimmed">Save the request to fetch the schema.</Text>}
        </Group>

        <Group gap={6} wrap="nowrap">
          <Text size="xs" fw={600} c="dimmed" tt="uppercase">Variables</Text>
          <Tooltip label="Format variables" withArrow openDelay={300}>
            <ActionIcon
              size="sm"
              variant="subtle"
              color="gray"
              onClick={() => handleVariablesChange(tryPrettyJson(variables || '{}'))}
              aria-label="Format variables JSON"
            >
              <IconSparkles size={13} />
            </ActionIcon>
          </Tooltip>
        </Group>
      </Group>

      <Group grow align="stretch" gap="sm">
        <Paper withBorder p={0} style={{ overflow: 'hidden' }}>
          <CodeMirror
            value={query}
            onChange={handleQueryChange}
            theme={theme}
            extensions={queryExtensions}
            basicSetup={{
              lineNumbers: true,
              highlightActiveLine: true,
              bracketMatching: true,
              closeBrackets: true,
              autocompletion: true,
              indentOnInput: true,
              foldGutter: true,
            }}
            minHeight="320px"
            style={{ fontSize: 12 }}
          />
        </Paper>
        <Paper withBorder p={0} style={{ overflow: 'hidden' }}>
          <CodeMirror
            value={variables}
            onChange={handleVariablesChange}
            theme={theme}
            extensions={[json(), bracketMatching(), closeBrackets(), history()]}
            basicSetup={{
              lineNumbers: true,
              bracketMatching: true,
              closeBrackets: true,
              indentOnInput: true,
            }}
            placeholder={'{\n  "id": 1\n}'}
            minHeight="320px"
            style={{ fontSize: 12 }}
          />
        </Paper>
      </Group>
    </Box>
  )
}
