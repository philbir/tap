import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import classes from './Markdown.module.css'

/**
 * Read-only markdown renderer for assistant replies. Uses GitHub-flavoured markdown
 * (tables, strikethrough, autolinks) and styles output via a scoped CSS module so it
 * stays consistent with the Mantine theme. Links open in a new tab.
 */
export function Markdown({ children }: { children: string }) {
  return (
    <div className={classes.md}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          a: ({ node, ...props }) => <a {...props} target="_blank" rel="noreferrer noopener" />,
        }}
      >
        {children}
      </ReactMarkdown>
    </div>
  )
}
