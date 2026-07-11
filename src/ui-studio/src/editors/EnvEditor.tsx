import { Box, Code, Stack, Tabs, Text, TextInput } from '@mantine/core'
import { IconCode, IconVariable } from '@tabler/icons-react'
import { useState } from 'react'
import { api } from '../api/client'
import type { EnvDetail, EnvSpec } from '../api/types'
import { EditorShell, TabCount } from './EditorShell'
import { KvTable } from './KvTable'
import { SourceTab } from './SourceTab'
import { useSpecEditor } from './useSpecEditor'
import { flatVarsToRows, rowsToFlatVars } from './varRows'

interface Props {
  path: string
}

/** Environment editor — typed local state. */
export function EnvEditor({ path }: Props) {
  const editor = useSpecEditor<EnvDetail, EnvSpec>({
    key: path,
    fetchDetail: (p) => api.envDetail(p),
    specFromDetail: (d) => specFromDetail(d, path),
    saveSpec: (s) => api.saveEnvSpec(s),
  })
  const { detail, spec, setSpec, update, dirty, saving, errorMessage, save, discard } = editor
  const [tab, setTab] = useState<string | null>('variables')

  if (!detail || !spec) {
    return (
      <EditorShell
        title={detail?.name ?? basename(path)}
        kindLabel="Environment"
        dirty={false} saving={saving} errorMessage={errorMessage}
        onSave={save}
      >
        <Text c="dimmed">Loading…</Text>
      </EditorShell>
    )
  }

  const rows = flatVarsToRows(spec.vars, spec.secrets)

  return (
    <EditorShell
      title={spec.name || basename(path)}
      kindLabel="Environment"
      dirty={dirty} saving={saving} errorMessage={errorMessage}
      onSave={save}
      onDiscard={discard}
      onTitleChange={(n) => update('name', n)}
    >
      <Tabs value={tab} onChange={setTab}>
        <Tabs.List mb="md">
          <Tabs.Tab value="variables" leftSection={<IconVariable size={14} />}>
            Variables <TabCount count={rows.length} />
          </Tabs.Tab>
          <Tabs.Tab value="source" leftSection={<IconCode size={14} />}>Source</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="variables">
          <Stack gap="md" maw={880}>
            <TextInput
              label="Name"
              value={spec.name}
              onChange={(e) => update('name', e.currentTarget.value)}
            />
            <Box>
              <Text size="sm" mb={4}>Variables</Text>
              <Text size="xs" c="dimmed" mb="xs">
                Resolve at execute time. Values can be literals or secret references like
                {' '}<Code>${'{{azkv:vault/key}}'}</Code>.
              </Text>
              <KvTable
                rows={rows}
                onChange={(next) => {
                  const { vars, secrets } = rowsToFlatVars(next)
                  setSpec((cur) => cur ? { ...cur, vars, secrets } : cur)
                }}
                keyPlaceholder="var.name"
                valuePlaceholder="value"
                allowSecretToggle
                emptyHint="No variables defined for this environment yet."
              />
            </Box>
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="source">
          <SourceTab path={path} source={detail.source} />
        </Tabs.Panel>
      </Tabs>
    </EditorShell>
  )
}

function specFromDetail(d: EnvDetail, path: string): EnvSpec {
  // Flatten the VarSpec map: keep the default value in vars, and collect the names of
  // secret vars in a separate `secrets` array (the wire shape the emitter expects).
  const vars: Record<string, string> = {}
  const secrets: string[] = []
  for (const [k, spec] of Object.entries(d.vars ?? {})) {
    if (spec?.default != null) vars[k] = spec.default
    if (spec?.secret) secrets.push(k)
  }
  return {
    path,
    id: d.id,
    name: d.name,
    vars: Object.keys(vars).length > 0 ? vars : undefined,
    secrets: secrets.length > 0 ? secrets : undefined,
    tags: d.tags && d.tags.length > 0 ? d.tags : undefined,
    body: d.body && d.body.trim().length > 0 ? d.body : undefined,
  }
}

function basename(p: string): string {
  return p.split('/').pop() ?? p
}
