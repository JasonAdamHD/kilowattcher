import { useEffect, useRef, useState } from 'react'
import type { ChatReply, Citation } from '../api'
import { describeTool } from '../conversation'
import { ToolIcon } from './Icons'

type Props = {
  messageId: number
  reply: ChatReply
  onOpenCitation: (citation: Citation, trigger: HTMLElement) => void
  onAsk: (question: string) => void
}

export function AssistantReply({ messageId, reply, onOpenCitation, onAsk }: Props) {
  const [openTool, setOpenTool] = useState<number | null>(null)
  const panelRef = useRef<HTMLPreElement>(null)
  const paragraphs = reply.answer.split(/\n{2,}/)

  // On a phone the panel can open below the fold; bring it into view.
  useEffect(() => {
    if (openTool !== null) panelRef.current?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' })
  }, [openTool])

  return (
    <article className="bubble bubble--assistant">
      {paragraphs.map((text, i) => (
        <p key={i}>
          {text}
          {i === paragraphs.length - 1 &&
            reply.citations.map((citation, n) => (
              <sup key={citation.id}>
                <button
                  type="button"
                  className="cite"
                  aria-label={`Source ${n + 1}: ${citation.title}, ${citation.heading}`}
                  onClick={(e) => onOpenCitation(citation, e.currentTarget)}
                >
                  {n + 1}
                </button>
              </sup>
            ))}
        </p>
      ))}

      {reply.citations.length > 0 && (
        <div className="sources">
          <span className="helper">Sources</span>
          <ol>
            {reply.citations.map((citation) => (
              <li key={citation.id}>
                <button type="button" className="link" onClick={(e) => onOpenCitation(citation, e.currentTarget)}>
                  {citation.title} — {citation.heading}
                </button>
              </li>
            ))}
          </ol>
        </div>
      )}

      {reply.toolCalls.length > 0 && (
        <div className="chips" aria-label="Data used for this answer">
          {reply.toolCalls.map((call, i) => {
            const tool = describeTool(call.name)
            const open = openTool === i
            return (
              <button
                key={i}
                type="button"
                className="chip"
                aria-expanded={open}
                aria-controls={`tool-${messageId}-${i}`}
                aria-label={`${tool.label}: ${open ? 'hide' : 'show'} the data used`}
                onClick={() => setOpenTool(open ? null : i)}
              >
                <ToolIcon kind={tool.icon} />
                {tool.label}
              </button>
            )
          })}
        </div>
      )}

      {openTool !== null && reply.toolCalls[openTool] && (
        <pre ref={panelRef} className="tool-result" id={`tool-${messageId}-${openTool}`} tabIndex={0}>
          {formatToolCall(reply.toolCalls[openTool])}
        </pre>
      )}

      {reply.suggestedQuestion && (
        <button type="button" className="chip chip--suggestion" onClick={() => onAsk(reply.suggestedQuestion!)}>
          Try: {reply.suggestedQuestion}
        </button>
      )}
    </article>
  )
}

function formatToolCall(call: ChatReply['toolCalls'][number]) {
  const args = Object.keys(call.arguments).length > 0 ? JSON.stringify(call.arguments) : ''
  return `${call.name}(${args})\n${JSON.stringify(call.result, null, 2)}`
}
