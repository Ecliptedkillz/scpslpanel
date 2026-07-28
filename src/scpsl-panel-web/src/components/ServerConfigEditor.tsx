import { useEffect, useState } from 'react'
import { FolderOpen, Save } from 'lucide-react'
import { api } from '../api'

type ConfigSource = 'server-config' | 'files'
type ConfigIndex = { queryPort: number; root: string; files: string[] }
type ServerOption = { id: string; name: string }

export function ServerConfigEditor({
  serverId, canWrite, onError,
}: {
  serverId: string
  canWrite: boolean
  onError: (message: string) => void
}) {
  const [source, setSource] = useState<ConfigSource>('server-config')
  const [index, setIndex] = useState<ConfigIndex | null>(null)
  const [path, setPath] = useState('')
  const [loadedPath, setLoadedPath] = useState('')
  const [content, setContent] = useState('')
  const [originalContent, setOriginalContent] = useState('')
  const [servers,setServers]=useState<ServerOption[]>([])
  const [compareServer,setCompareServer]=useState('')
  const [comparison,setComparison]=useState('')
  const [busy, setBusy] = useState(false)
  const endpoint = (file: string) =>
    `/servers/${serverId}/${source}/${file.split(/[\\/]/).map(encodeURIComponent).join('/')}`
  useEffect(()=>{api<{servers:ServerOption[]}>('/overview').then(x=>setServers(x.servers.filter(s=>s.id!==serverId))).catch(()=>{})},[serverId])
  useEffect(()=>{const handler=(event:BeforeUnloadEvent)=>{if(content===originalContent)return;event.preventDefault();event.returnValue=''};window.addEventListener('beforeunload',handler);return()=>window.removeEventListener('beforeunload',handler)},[content,originalContent])

  const loadIndex = async () => {
    if (source !== 'server-config') return setIndex(null)
    try {
      const value = await api<ConfigIndex>(`/servers/${serverId}/server-config`)
      setIndex(value)
      if (!path && value.files[0]) setPath(value.files[0])
    } catch (error) {
      onError(error instanceof Error ? error.message : 'Unable to list SCP:SL configuration files')
    }
  }
  useEffect(() => { setPath(''); setLoadedPath(''); setContent(''); setOriginalContent(''); setComparison(''); void loadIndex() }, [source, serverId])

  const open = async (selectedPath = path) => {
    if (!selectedPath.trim()) return
    setBusy(true)
    try {
      const response = await fetch(`/api${endpoint(selectedPath)}`, { credentials: 'include' })
      if (!response.ok) throw new Error(response.status === 404
        ? 'Configuration file not found in the selected directory.'
        : `Unable to open file (${response.status}).`)
      const text=await response.text()
      setContent(text)
      setOriginalContent(text)
      setPath(selectedPath)
      setLoadedPath(selectedPath)
    } catch (error) {
      onError(error instanceof Error ? error.message : 'Unable to open configuration file')
    } finally { setBusy(false) }
  }
  const save = async () => {
    if (!loadedPath || !canWrite) return
    setBusy(true)
    try {
      await api(endpoint(loadedPath), { method: 'PUT', body: JSON.stringify({ content }) })
      setOriginalContent(content)
      window.dispatchEvent(new CustomEvent('panel-success',{detail:'Configuration saved.'}))
      if (source === 'server-config') await loadIndex()
    } catch (error) {
      onError(error instanceof Error ? error.message : 'Unable to save configuration file')
    } finally { setBusy(false) }
  }
  const compare=async()=>{if(!compareServer||!loadedPath)return;try{const file=loadedPath.split(/[\\/]/).map(encodeURIComponent).join('/');const response=await fetch(`/api/servers/${compareServer}/${source}/${file}`,{credentials:'include'});if(!response.ok)throw new Error('The same file was not found on that server.');setComparison(await response.text())}catch(error){onError(error instanceof Error?error.message:'Unable to compare configuration')}}

  return <section className="file-editor">
    <div className="preset-row">
      <span>CONFIGURATION SOURCE</span>
      <button className={source === 'server-config' ? 'active' : ''} onClick={() => setSource('server-config')}>SCP:SL PORT CONFIG</button>
      <button className={source === 'files' ? 'active' : ''} onClick={() => setSource('files')}>SERVER DIRECTORY</button>
    </div>
    <div className="file-context">{source === 'server-config'
      ? index ? `Live config for port ${index.queryPort}: ${index.root}` : 'Loading the per-port SCP:SL configuration directory…'
      : 'Files below the registered server working directory.'}</div>
    {source === 'server-config' && !!index?.files.length &&
      <div className="file-toolbar"><label>AVAILABLE CONFIG FILES<select value={path} onChange={event => { setPath(event.target.value); void open(event.target.value) }}><option value="">Select a file</option>{index.files.map(file => <option key={file} value={file}>{file}</option>)}</select></label></div>}
    <div className="file-toolbar">
      <div><label>RELATIVE FILE PATH<input value={path} onChange={event => setPath(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') void open() }}/></label></div>
      <button onClick={() => open()} disabled={!path || busy}><FolderOpen size={15}/> OPEN</button>
      {canWrite && <button className="primary" onClick={save} disabled={!loadedPath || busy}><Save size={15}/> SAVE</button>}
    </div>
    <div className="file-context">{loadedPath
      ? `Editing ${loadedPath}${canWrite ? '' : ' (read only)'}`
      : 'Select an existing file or enter a relative path.'}</div>
    {loadedPath&&<div className="file-toolbar compare-toolbar"><label>COMPARE WITH SERVER<select value={compareServer} onChange={e=>setCompareServer(e.target.value)}><option value="">Choose server</option>{servers.map(server=><option key={server.id} value={server.id}>{server.name}</option>)}</select></label><button disabled={!compareServer} onClick={compare}>COMPARE</button>{comparison&&<button onClick={()=>setComparison('')}>CLOSE COMPARISON</button>}</div>}
    <div className={comparison?'config-comparison':''}><textarea className="code-editor" value={content} onChange={event => setContent(event.target.value)}
      readOnly={!canWrite} spellCheck={false} placeholder="File contents will appear here…"/>
    {comparison&&<textarea className="code-editor comparison-editor" value={comparison} readOnly spellCheck={false}/>}</div>
  </section>
}
