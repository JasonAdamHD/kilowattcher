import type { ChatReply, HistoryMessage } from './api'

export type UserMessage = { id: number; role: 'user'; text: string }
export type AssistantMessage = { id: number; role: 'assistant'; reply: ChatReply }
export type Message = UserMessage | AssistantMessage

export const STARTER_QUESTIONS = [
  'Should I charge my battery from the grid tonight?',
  'How long will my battery last if the power goes out?',
  'What does reserve percentage mean?',
] as const

let nextId = 1
export const newId = () => nextId++

/** Turns the visible conversation into the history the API expects. */
export function toHistory(messages: Message[]): HistoryMessage[] {
  return messages.map((m) =>
    m.role === 'user'
      ? { role: 'user', content: m.text }
      : { role: 'assistant', content: m.reply.answer, signature: m.reply.signature },
  )
}

const toolLabels: Record<string, { label: string; icon: 'battery' | 'rates' | 'solar' | 'docs' }> = {
  get_battery_status: { label: 'Battery', icon: 'battery' },
  estimate_backup_time: { label: 'Backup estimate', icon: 'battery' },
  get_rate_schedule: { label: 'Rates', icon: 'rates' },
  get_solar_forecast: { label: 'Solar forecast', icon: 'solar' },
  search_docs: { label: 'Docs', icon: 'docs' },
}

export function describeTool(name: string) {
  return toolLabels[name] ?? { label: name, icon: 'docs' as const }
}
