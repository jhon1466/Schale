import { invoke } from '@tauri-apps/api/core';

export type CommitInfo = { sha: string; message: string; author: string; date: string };
export type UpdateInfo = { checkedAt: number; remoteSha: string | null; updateAvailable: boolean; commits: CommitInfo[]; error: string | null };
export type Settings = {
  apiUrl: string; repoUrl: string; branch: string; sourceDir: string; autoUpdate: boolean;
  pollMinutes: number; restartAfterUpdate: boolean; gameDir: string;
};
export type Status = {
  appVersion: string; installed: boolean; running: boolean; busy: boolean; uptimeSeconds: number | null;
  serverDir: string; sourceDir: string; localCommit: string; gameFound: boolean;
  tools: { git: boolean; dotnet: boolean }; settings: Settings; update: UpdateInfo;
};
export type Progress = { stage: string; pct: number; message: string };

export const call = <T,>(cmd: string, args?: Record<string, unknown>) => invoke<T>(cmd, args);

/** Calls the Schale HTTP API through the Tauri backend (no CORS). Unwraps the /dev/api envelope. */
export async function api<T = any>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
  const res = await invoke<any>('api_request', { method, path, body: body ?? null });
  if (res && typeof res === 'object' && 'status' in res && ('data' in res || 'message' in res)) {
    const status = String(res.status).toLowerCase();
    if (status !== 'success') throw new Error(res.message || (typeof res.data === 'string' ? res.data : 'Error del servidor'));
    return (res.data ?? res.message) as T;
  }
  if (res && typeof res === 'object' && 'error' in res) throw new Error(String(res.error));
  return res as T;
}

/** Case-insensitive property access (the server mixes PascalCase and camelCase). */
export function get(o: any, ...keys: string[]): any {
  if (!o || typeof o !== 'object') return undefined;
  for (const k of keys) {
    if (k in o) return o[k];
    const found = Object.keys(o).find((x) => x.toLowerCase() === k.toLowerCase());
    if (found) return o[found];
  }
  return undefined;
}

export const short = (sha?: string | null) => (sha ? sha.slice(0, 7) : '—');
export function ago(iso: string) {
  const d = new Date(iso).getTime();
  if (!d) return '';
  const s = Math.max(0, (Date.now() - d) / 1000);
  if (s < 60) return 'hace un momento';
  if (s < 3600) return `hace ${Math.floor(s / 60)} min`;
  if (s < 86400) return `hace ${Math.floor(s / 3600)} h`;
  return `hace ${Math.floor(s / 86400)} d`;
}
export const uptime = (s?: number | null) => (s == null ? '—' : `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`);
