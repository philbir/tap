import { RichTextEditor } from '@mantine/tiptap'
import { Box, Group, SegmentedControl, Text, Tooltip } from '@mantine/core'
import { IconArticle, IconEye, IconMarkdown } from '@tabler/icons-react'
import { useEditor, type Editor } from '@tiptap/react'
import StarterKit from '@tiptap/starter-kit'
import { Markdown, type MarkdownStorage } from 'tiptap-markdown'
import { useEffect, useRef, useState } from 'react'
import { CodeBlock } from './CodeBlock'

interface Props {
  /** Current markdown body (no frontmatter, no ```http fence — those are owned elsewhere). */
  value: string
  onChange: (value: string) => void
  /** Placeholder shown when the body is empty. */
  emptyHint?: string
}

type Mode = 'preview' | 'rich' | 'markdown'

/**
 * Shared Docs editor used by the request / collection / auth editors. Edits the markdown
 * `body` of the artifact — the human-facing documentation that lives in the file *around*
 * the structured frontmatter and (for requests) the fenced `http` block. Those parts are
 * stripped by the server before we ever see the body, so what's edited here is pure prose.
 *
 * Three synced views over the same markdown `value`:
 *   - "Preview" (default) — read-only rendered markdown.
 *   - "Rich" — Mantine's RichTextEditor (TipTap) WYSIWYG surface with a toolbar.
 *   - "Markdown" — a raw CodeMirror markdown source editor.
 * `tiptap-markdown` keeps the WYSIWYG storage in Markdown, so the persisted `body` stays
 * plain markdown regardless of which view produced the edit. Preview and Rich share one
 * TipTap instance toggled between read-only and editable, so they render identically.
 */
function getMarkdown(editor: Editor): string {
  return (editor.storage as unknown as { markdown: MarkdownStorage }).markdown.getMarkdown()
}

export function DocsEditor({ value, onChange, emptyHint }: Props) {
  const [mode, setMode] = useState<Mode>('preview')

  // TipTap's markdown serializer normalizes prose (spacing, escaping, list markers), so the
  // string it emits for *unchanged* content rarely matches the byte-for-byte `value` we were
  // given. Emitting that normalized form on load would flip the parent's dirty flag with no
  // real edit. We guard against it by remembering the serializer's rendering of the current
  // value (`baseline`) and only propagating updates that actually diverge from it.
  const baselineRef = useRef<string | null>(null)

  const editor = useEditor({
    immediatelyRender: false,
    extensions: [
      StarterKit.configure({ link: { openOnClick: false } }),
      Markdown.configure({ html: false, linkify: true, transformPastedText: true }),
    ],
    content: value,
    onCreate: ({ editor }) => {
      baselineRef.current = getMarkdown(editor)
    },
    onUpdate: ({ editor }) => {
      const md = getMarkdown(editor)
      if (md === baselineRef.current) return
      baselineRef.current = md
      onChange(md)
    },
  })

  // Sync external value changes (switching artifacts, AI proposals, edits made in the raw
  // markdown view) into the shared TipTap instance without clobbering the cursor while the
  // user is typing — only reset when the markdown actually differs. Re-baseline afterwards so
  // the new value's normalized form becomes the "unchanged" reference.
  useEffect(() => {
    if (!editor) return
    const current = getMarkdown(editor)
    if (value !== current && value !== baselineRef.current) {
      editor.commands.setContent(value, { emitUpdate: false })
    }
    baselineRef.current = getMarkdown(editor)
  }, [value, editor])

  // Preview is read-only; Rich is editable. Markdown uses CodeMirror, so the TipTap
  // editable flag is irrelevant there.
  useEffect(() => {
    editor?.setEditable(mode === 'rich')
  }, [editor, mode])

  const empty = value.trim().length === 0

  return (
    <>
      <Group justify="flex-end" mb="xs">
        <SegmentedControl
          size="xs"
          value={mode}
          onChange={(v) => setMode(v as Mode)}
          data={[
            {
              value: 'preview',
              label: (
                <Tooltip label="Preview" withArrow>
                  <IconEye size={16} style={{ display: 'block' }} />
                </Tooltip>
              ),
            },
            {
              value: 'rich',
              label: (
                <Tooltip label="Rich text" withArrow>
                  <IconArticle size={16} style={{ display: 'block' }} />
                </Tooltip>
              ),
            },
            {
              value: 'markdown',
              label: (
                <Tooltip label="Markdown source" withArrow>
                  <IconMarkdown size={16} style={{ display: 'block' }} />
                </Tooltip>
              ),
            },
          ]}
        />
      </Group>

      {mode === 'markdown' ? (
        <Box style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, overflow: 'hidden' }}>
          <CodeBlock
            value={value}
            language="markdown"
            readOnly={false}
            onChange={onChange}
            height={360}
          />
        </Box>
      ) : mode === 'preview' && empty ? (
        <Text size="sm" c="dimmed" py="lg" ta="center">
          {emptyHint ?? 'No docs yet. Switch to Rich text or Markdown to add some.'}
        </Text>
      ) : editor ? (
        <RichTextEditor editor={editor} variant="subtle">
          {mode === 'rich' && (
            <RichTextEditor.Toolbar sticky stickyOffset={0}>
              <RichTextEditor.ControlsGroup>
                <RichTextEditor.Bold />
                <RichTextEditor.Italic />
                <RichTextEditor.Strikethrough />
                <RichTextEditor.Code />
              </RichTextEditor.ControlsGroup>

              <RichTextEditor.ControlsGroup>
                <RichTextEditor.H1 />
                <RichTextEditor.H2 />
                <RichTextEditor.H3 />
              </RichTextEditor.ControlsGroup>

              <RichTextEditor.ControlsGroup>
                <RichTextEditor.BulletList />
                <RichTextEditor.OrderedList />
                <RichTextEditor.Blockquote />
                <RichTextEditor.CodeBlock />
              </RichTextEditor.ControlsGroup>

              <RichTextEditor.ControlsGroup>
                <RichTextEditor.Link />
                <RichTextEditor.Unlink />
                <RichTextEditor.Hr />
              </RichTextEditor.ControlsGroup>

              <RichTextEditor.ControlsGroup>
                <RichTextEditor.Undo />
                <RichTextEditor.Redo />
              </RichTextEditor.ControlsGroup>
            </RichTextEditor.Toolbar>
          )}

          <RichTextEditor.Content
            mih={mode === 'rich' ? 320 : undefined}
            data-placeholder={emptyHint ?? 'Document what this is, how to use it, and what to expect.'}
          />
        </RichTextEditor>
      ) : null}
    </>
  )
}
