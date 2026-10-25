import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { marked } from 'marked'
import DOMPurify from 'dompurify'
import { api, chat, type Conversation, type Dataset, type EnvironmentProfile, type Row, type Source, type Status } from './api'

// ---------- types for the chat transcript ----------

interface ToolItem {
  id: string; name: string; input?: unknown; summary?: string; rowCount?: number; datasetId?: string
  warnings?: string[]; sources?: Source[]; error?: string; ms?: number; running: boolean
}

type Entry =
  | { kind: 'user'; text: string }
  | { kind: 'assistant'; text: string }
  | { kind: 'tools'; items: ToolItem[] }
  | { kind: 'error'; text: string }
  | { kind: 'meta'; text: string }

const QUICK_ACTIONS: { label: string; tool: string; needsCompare?: boolean; input?: Record<string, unknown> }[] = [
  { label: 'Environment profile', tool: 'get_environment_profile' },
  { label: 'Health check', tool: 'run_health_checks' },
  { label: 'Running jobs', tool: 'get_running_jobs' },
  { label: 'Async backlog', tool: 'get_async_backlog' },
  { label: 'Failed jobs (24h)', tool: 'get_job_failures' },
  { label: 'Plug-in performance', tool: 'get_plugin_performance' },
  { label: 'Recent changes', tool: 'get_recent_changes' },
  { label: 'Compare roles', tool: 'compare_roles', needsCompare: true },
  { label: 'Compare solutions', tool: 'compare_solutions', needsCompare: true },
]

const SUGGESTIONS = [
  'What system jobs are running right now, and is the async queue healthy?',
  'Run a health check and explain the most important findings.',
  'What runs when an account is updated, and is anything likely to slow saves down?',
  'What changed in this environment in the last 3 days?',
]

function md(text: string) {
  return { __html: DOMPurify.sanitize(marked.parse(text, { async: false }) as string) }
}

function fmt(v: unknown): string {
  if (v === null || v === undefined) return ''
  if (typeof v === 'object') return JSON.stringify(v)
  return String(v)
}

// ---------- evidence table ----------

function DataTable({ rows }: { rows: Row[] }) {
  const [sort, setSort] = useState<{ col: string; dir: 1 | -1 } | null>(null)
  const [filter, setFilter] = useState('')
  const cols = useMemo(() => {
    const set = new Set<string>()
    rows.slice(0, 200).forEach(r => Object.keys(r).forEach(k => set.add(k)))
    return [...set]
  }, [rows])
  const view = useMemo(() => {
    const f = filter.trim().toLowerCase()
    let out = f ? rows.filter(r => Object.values(r).some(v => fmt(v).toLowerCase().includes(f))) : rows
    if (sort) {
      out = [...out].sort((a, b) => {
        const x = a[sort.col], y = b[sort.col]
        if (typeof x === 'number' && typeof y === 'number') return (x - y) * sort.dir
        return fmt(x).localeCompare(fmt(y), undefined, { numeric: true }) * sort.dir
      })
    }
    return out
  }, [rows, filter, sort])

  return (
    <div className="table-wrap">
      <input className="filter" placeholder={`Filter ${rows.length} rows…`} value={filter} onChange={e => setFilter(e.target.value)} />
      <div className="table-scroll">
        <table>
          <thead>
            <tr>{cols.map(c => (
              <th key={c} onClick={() => setSort(s => ({ col: c, dir: s?.col === c && s.dir === 1 ? -1 : 1 }))}>
                {c}{sort?.col === c ? (sort.dir === 1 ? ' ▲' : ' ▼') : ''}
              </th>))}
            </tr>
          </thead>
          <tbody>
            {view.slice(0, 1000).map((r, i) => (
              <tr key={i}>{cols.map(c => <td key={c} title={fmt(r[c])}>{fmt(r[c])}</td>)}</tr>
            ))}
          </tbody>
        </table>
      </div>
      {view.length > 1000 && <p className="muted">Showing first 1,000 of {view.length} rows. Export to see all.</p>}
    </div>
  )
}

function toCsv(rows: Row[]) {
  const cols = [...new Set(rows.flatMap(r => Object.keys(r)))]
  const esc = (v: unknown) => `"${fmt(v).replace(/"/g, '""')}"`
  return [cols.map(esc).join(','), ...rows.map(r => cols.map(c => esc(r[c])).join(','))].join('\r\n')
}

function EvidencePanel({ dataset, tool, onClose }: { dataset: Dataset | null; tool: ToolItem | null; onClose: () => void }) {
  if (!tool) {
    return (
      <aside className="evidence empty">
        <h2>Evidence</h2>
        <p className="muted">Select a tool result to see the full data, the query that produced it and any warnings.</p>
      </aside>
    )
  }
  const download = () => {
    if (!dataset) return
    const blob = new Blob([toCsv(dataset.rows)], { type: 'text/csv' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `${dataset.info.tool}-${dataset.info.env}-${dataset.info.id}.csv`
    a.click()
    URL.revokeObjectURL(a.href)
  }
  return (
    <aside className="evidence">
      <div className="evidence-head">
        <h2>{tool.name}</h2>
        <div className="row-gap">
          {dataset && <button className="ghost" onClick={download}>Export CSV</button>}
          <button className="ghost" onClick={onClose} aria-label="Close evidence">✕</button>
        </div>
      </div>
      {tool.summary && <p className="summary">{tool.summary}</p>}
      {tool.error && <p className="error-text">{tool.error}</p>}
      {tool.warnings && tool.warnings.length > 0 && (
        <ul className="warnings">{tool.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
      )}
      {dataset ? <DataTable rows={dataset.rows} /> : tool.datasetId ? <p className="muted">Loading…</p> : null}
      {tool.sources && tool.sources.length > 0 && (
        <details className="sources">
          <summary>Sources and queries</summary>
          {tool.sources.map((s, i) => (
            <div key={i} className="source">
              <div><b>{s.env}</b> · {s.kind}{s.window ? ` · ${s.window}` : ''}</div>
              {s.query && <pre>{decodeURIComponent(s.query)}</pre>}
            </div>
          ))}
          {tool.input !== undefined && <><div className="muted">Tool input</div><pre>{JSON.stringify(tool.input, null, 2)}</pre></>}
        </details>
      )}
    </aside>
  )
}

// ---------- environments ----------

function AddEnvironment({ onAdded }: { onAdded: () => void }) {
  const [open, setOpen] = useState(false)
  const [name, setName] = useState('')
  const [url, setUrl] = useState('')
  const [tags, setTags] = useState('')
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState('')
  if (!open) return <button className="ghost small" onClick={() => setOpen(true)}>+ Add environment</button>
  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true); setErr('')
    try {
      await api.addEnvironment({ name, url, tags: tags.split(',').map(t => t.trim()).filter(Boolean) })
      setOpen(false); setName(''); setUrl(''); setTags('')
      onAdded()
    } catch (ex) { setErr((ex as Error).message) } finally { setBusy(false) }
  }
  return (
    <form className="add-env" onSubmit={submit}>
      <input placeholder="Name, e.g. PROD" value={name} onChange={e => setName(e.target.value)} required />
      <input placeholder="https://org.crm.dynamics.com" value={url} onChange={e => setUrl(e.target.value)} required />
      <input placeholder="Tags, e.g. prod, contoso" value={tags} onChange={e => setTags(e.target.value)} />
      {err && <p className="error-text">{err}</p>}
      <div className="row-gap">
        <button type="submit" disabled={busy}>{busy ? 'Adding…' : 'Add'}</button>
        <button type="button" className="ghost" onClick={() => setOpen(false)}>Cancel</button>
      </div>
    </form>
  )
}

// ---------- app ----------

export default function App() {
  const [status, setStatus] = useState<Status | null>(null)
  const [envs, setEnvs] = useState<EnvironmentProfile[]>([])
  const [env, setEnv] = useState<string>('')
  const [compareEnv, setCompareEnv] = useState<string>('')
  const [conversations, setConversations] = useState<Conversation[]>([])
  const [conversationId, setConversationId] = useState<string | undefined>()
  const [entries, setEntries] = useState<Entry[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [deep, setDeep] = useState(false)
  const [selected, setSelected] = useState<ToolItem | null>(null)
  const [dataset, setDataset] = useState<Dataset | null>(null)
  const [fatal, setFatal] = useState('')
  const abortRef = useRef<AbortController | null>(null)
  const bottomRef = useRef<HTMLDivElement>(null)

  const refresh = useCallback(async () => {
    try {
      const [s, e, c] = await Promise.all([api.status(), api.environments(), api.conversations()])
      setStatus(s); setEnvs(e); setConversations(c)
      setEnv(cur => cur || e[0]?.name || '')
    } catch (ex) { setFatal((ex as Error).message) }
  }, [])

  useEffect(() => { if (api.hasToken()) refresh(); else setFatal('Missing session token. Open the link printed by dvqna in the console.') }, [refresh])
  useEffect(() => { bottomRef.current?.scrollIntoView({ behavior: 'smooth' }) }, [entries])

  const select = async (t: ToolItem) => {
    setSelected(t); setDataset(null)
    if (t.datasetId) {
      try { setDataset(await api.dataset(t.datasetId)) } catch { /* ignore */ }
    }
  }

  const patchTool = (id: string, patch: Partial<ToolItem>) => {
    setEntries(prev => prev.map(e => e.kind === 'tools'
      ? { ...e, items: e.items.map(i => (i.id === id ? { ...i, ...patch } : i)) } : e))
  }

  const send = async (text: string) => {
    if (!text.trim() || busy) return
    setInput('')
    setEntries(prev => [...prev, { kind: 'user', text }])
    setBusy(true)
    const controller = new AbortController()
    abortRef.current = controller
    try {
      for await (const evt of chat({ conversationId, message: text, env: env || undefined, compareEnv: compareEnv || undefined, deep }, controller.signal)) {
        const d = evt.data
        switch (evt.type) {
          case 'conversation': setConversationId(d.id as string); break
          case 'text': setEntries(prev => [...prev, { kind: 'assistant', text: d.text as string }]); break
          case 'tool_start':
            setEntries(prev => {
              const item: ToolItem = { id: d.id as string, name: d.name as string, input: d.input, running: true }
              const last = prev[prev.length - 1]
              if (last?.kind === 'tools') return [...prev.slice(0, -1), { kind: 'tools', items: [...last.items, item] }]
              return [...prev, { kind: 'tools', items: [item] }]
            })
            break
          case 'tool_end':
            patchTool(d.id as string, {
              running: false, summary: d.summary as string, rowCount: d.row_count as number, datasetId: d.dataset_id as string,
              warnings: d.warnings as string[], sources: d.sources as Source[], error: d.error as string, ms: d.ms as number,
            })
            break
          case 'error': setEntries(prev => [...prev, { kind: 'error', text: d.message as string }]); break
          case 'done': {
            const u = d.usage as Record<string, number>
            setEntries(prev => [...prev, { kind: 'meta', text: `${d.tool_calls} tool call(s) · tokens in ${u.input + u.cache_read} (cached ${u.cache_read}) · out ${u.output}` }])
            break
          }
        }
      }
    } catch (ex) {
      if ((ex as Error).name !== 'AbortError') setEntries(prev => [...prev, { kind: 'error', text: (ex as Error).message }])
    } finally {
      setBusy(false)
      abortRef.current = null
      api.conversations().then(setConversations).catch(() => {})
    }
  }

  const runQuick = async (q: typeof QUICK_ACTIONS[number]) => {
    if (!env) return
    const inputArgs: Record<string, unknown> = q.needsCompare ? { env_a: env, env_b: compareEnv } : { env, ...(q.input ?? {}) }
    if (q.needsCompare && !compareEnv) {
      setEntries(prev => [...prev, { kind: 'error', text: 'Pick a comparison environment first.' }])
      return
    }
    const id = `quick-${Date.now()}`
    const item: ToolItem = { id, name: q.tool, input: inputArgs, running: true }
    setEntries(prev => [...prev, { kind: 'tools', items: [item] }])
    try {
      const r = await api.runTool(q.tool, inputArgs)
      const done: ToolItem = { ...item, running: false, summary: r.summary, rowCount: r.rowCount, datasetId: r.datasetId, warnings: r.warnings, sources: r.sources }
      patchTool(id, done)
      select(done)
    } catch (ex) {
      patchTool(id, { running: false, error: (ex as Error).message })
    }
  }

  const openConversation = async (c: Conversation) => {
    setConversationId(c.id); setSelected(null); setDataset(null)
    const msgs = await api.conversation(c.id)
    setEntries([
      { kind: 'meta', text: 'Earlier conversation (tool details are not replayed). The assistant will not remember it after an app restart.' },
      ...msgs.map(m => (m.role === 'user' ? { kind: 'user', text: m.content } : { kind: 'assistant', text: m.content }) as Entry),
    ])
  }

  const newChat = () => { setConversationId(undefined); setEntries([]); setSelected(null); setDataset(null) }

  if (fatal) return <div className="fatal"><h1>Dataverse QnA</h1><p>{fatal}</p></div>

  return (
    <div className="layout">
      <nav className="sidebar">
        <div className="brand">Dataverse QnA <span className="badge">read-only</span></div>
        <section>
          <h3>Environments</h3>
          {envs.length === 0 && <p className="muted small">No environments yet.</p>}
          <ul className="env-list">
            {envs.map(e => (
              <li key={e.name} className={e.name === env ? 'active' : ''} onClick={() => setEnv(e.name)} title={e.url}>
                <span>{e.name}</span>
                {e.tags.map(t => <span key={t} className={`tag ${t === 'prod' ? 'prod' : ''}`}>{t}</span>)}
              </li>
            ))}
          </ul>
          <AddEnvironment onAdded={refresh} />
        </section>
        <section>
          <h3>Quick checks</h3>
          <div className="quick">
            {QUICK_ACTIONS.map(q => <button key={q.tool} className="ghost small" disabled={!env} onClick={() => runQuick(q)}>{q.label}</button>)}
          </div>
        </section>
        <section className="grow">
          <h3>Conversations <button className="link" onClick={newChat}>New</button></h3>
          <ul className="conv-list">
            {conversations.map(c => (
              <li key={c.id} className={c.id === conversationId ? 'active' : ''} onClick={() => openConversation(c)}>{c.title}</li>
            ))}
          </ul>
        </section>
      </nav>

      <main className="chat">
        <header className="chat-head">
          <label>Environment
            <select value={env} onChange={e => setEnv(e.target.value)}>
              <option value="">(none)</option>
              {envs.map(e => <option key={e.name}>{e.name}</option>)}
            </select>
          </label>
          <label>Compare with
            <select value={compareEnv} onChange={e => setCompareEnv(e.target.value)}>
              <option value="">(none)</option>
              {envs.filter(e => e.name !== env).map(e => <option key={e.name}>{e.name}</option>)}
            </select>
          </label>
          <label className="check"><input type="checkbox" checked={deep} onChange={e => setDeep(e.target.checked)} /> Deep investigation</label>
        </header>

        {status && !status.llmReady && (
          <div className="banner">AI assistant unavailable: {status.llmStatus} Quick checks still work.</div>
        )}

        <div className="transcript">
          {entries.length === 0 && (
            <div className="welcome">
              <h1>Ask about your Dataverse environments</h1>
              <p className="muted">Performance, plug-ins, system jobs, security roles, configuration drift. Everything is read-only.</p>
              <div className="suggestions">
                {SUGGESTIONS.map(s => <button key={s} className="ghost" onClick={() => send(s)} disabled={!status?.llmReady}>{s}</button>)}
              </div>
            </div>
          )}
          {entries.map((e, i) => {
            switch (e.kind) {
              case 'user': return <div key={i} className="msg user">{e.text}</div>
              case 'assistant': return <div key={i} className="msg assistant" dangerouslySetInnerHTML={md(e.text)} />
              case 'error': return <div key={i} className="msg error">{e.text}</div>
              case 'meta': return <div key={i} className="meta">{e.text}</div>
              case 'tools': return (
                <div key={i} className="tools">
                  {e.items.map(t => (
                    <button key={t.id} className={`tool ${selected?.id === t.id ? 'selected' : ''} ${t.error ? 'failed' : ''}`} onClick={() => select(t)}>
                      <span className="tool-name">{t.running ? '⏳' : t.error ? '⚠' : '✓'} {t.name}</span>
                      <span className="tool-summary">{t.running ? 'Running…' : t.error ?? t.summary}</span>
                      {!t.running && t.ms !== undefined && <span className="tool-ms">{(t.ms / 1000).toFixed(1)}s</span>}
                    </button>
                  ))}
                </div>
              )
            }
          })}
          {busy && <div className="meta">Working…</div>}
          <div ref={bottomRef} />
        </div>

        <form className="composer" onSubmit={e => { e.preventDefault(); send(input) }}>
          <textarea
            value={input}
            placeholder={status?.llmReady ? 'Ask a question, e.g. "Why is saving a case slow in PROD?"' : 'AI assistant not configured. Use the quick checks on the left.'}
            onChange={e => setInput(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(input) } }}
            disabled={!status?.llmReady}
            rows={2}
          />
          {busy
            ? <button type="button" onClick={() => abortRef.current?.abort()}>Stop</button>
            : <button type="submit" disabled={!status?.llmReady || !input.trim()}>Send</button>}
        </form>
      </main>

      <EvidencePanel dataset={dataset} tool={selected} onClose={() => { setSelected(null); setDataset(null) }} />
    </div>
  )
}
