import { invoke, isTauri } from '@tauri-apps/api/core'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
export interface Settings {
  drive: string; share: string; profile: string; intervalSecs: number; threshold: number;
  tcpTimeoutMs: number; overallTimeoutMs: number; removeOffline: boolean; reconnect: boolean;
  language: string; notifications: boolean
}
export interface ProbeResult { mapping: string | null; targetMatches: boolean | null; tcp: boolean | null; share: string | null; nativeCode: number | null; sessionId: number | null }
export interface Event { source?: string; id: number; atMs: number; code: string; severity: string; state: string; nativeCode: number | null }
export interface Snapshot {
  settings: Settings; configured: boolean; busy: boolean; paused: boolean; state: string;
  result: ProbeResult | null; events: Event[]; error: string | null; storageError: string | null;
  dataDirectory: string; startup: { state: string; needsUpdate: boolean; error: string | null }; credentialSaved: boolean;
  lastCheckAt: number | null; lastChangeAt: number | null; nextCheckAt: number | null;
  lastTcpAt: number | null; lastShareAt: number | null; consecutiveFailures: number
}
export const desktop = isTauri()
export const defaults = (): Settings => ({ drive: 'Z:', share: '', profile: 'standard', intervalSecs: 5, threshold: 3, tcpTimeoutMs: 800, overallTimeoutMs: 10000, removeOffline: false, reconnect: false, language: 'zh', notifications: true })
export const empty = (): Snapshot => ({ settings: defaults(), configured: false, busy: false, paused: false, state: 'not_configured', result: null, events: [], error: null, storageError: null, dataDirectory: '', startup: { state: 'unknown', needsUpdate: false, error: null }, credentialSaved: false, lastCheckAt: null, lastChangeAt: null, nextCheckAt: null, lastTcpAt: null, lastShareAt: null, consecutiveFailures: 0 })
async function call<T>(command: string, args?: Record<string, unknown>): Promise<T> { if (!desktop) throw new Error('preview_only'); return invoke(command, args) }
export async function snapshot(): Promise<Snapshot> { return desktop ? invoke('get_snapshot') : empty() }
export const save = (settings: Settings, directory: string) => call<Snapshot>('save_settings', { settings, directory })
export const check = () => call<Snapshot>('check_now')
export const pause = (paused: boolean) => call<Snapshot>('set_paused', { paused })
export const credential = (user: string, password: string, remove: boolean) => call<Snapshot>('set_credential', { user, password, remove })
export const startup = (enabled: boolean) => call<Snapshot>('set_startup', { enabled })
export const directory = () => call<string | null>('choose_directory')
export const diagnostics = () => call<boolean>('export_diagnostics')
export const openFolder = (target: 'data' | 'mapping') => call<void>('open_folder', { target })
export const hide = () => call<void>('hide_window')
export const quit = () => call<void>('quit_app')
export async function subscribe(callback: (state: Snapshot) => void): Promise<UnlistenFn> { return desktop ? listen<Snapshot>('state-changed', e => callback(e.payload)) : () => {} }

export async function navigation(callback:()=>void):Promise<UnlistenFn>{return desktop?listen('navigate',callback):()=>{}}

export const preferences=(language:string,notifications:boolean)=>call<Snapshot>('save_preferences',{language,notifications})

export const relocate=(directory:string)=>call<Snapshot>('relocate_data',{directory})

export const events=()=>desktop?call<Event[]>('get_events'):Promise.resolve([])
