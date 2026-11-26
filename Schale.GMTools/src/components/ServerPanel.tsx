import { For, Show, createSignal } from 'solid-js';
import { call, short, ago, type Progress, type Settings, type Status } from '../lib/api';
import { tr } from '../lib/i18n';

type Props = {
  status?: Status; progress?: Progress; logs: string[];
  notify: (m: string, error?: boolean) => void; refresh: () => Promise<void>; clearLogs: () => void;
};

export default function ServerPanel(props: Props) {
  const [working, setWorking] = createSignal(false);
  const [draft, setDraft] = createSignal<Settings>();
  const s = () => props.status;
  const settings = () => draft() ?? s()?.settings;
  const edit = <K extends keyof Settings>(k: K, v: Settings[K]) => setDraft({ ...(settings() as Settings), [k]: v });
  const busy = () => working() || !!s()?.busy;
  let logBox: HTMLPreElement | undefined;

  async function run(cmd: string, args?: Record<string, unknown>) {
    setWorking(true);
    try {
      const r = await call<unknown>(cmd, args);
      if (typeof r === 'string') props.notify(r);
    } catch (e) { props.notify(String(e), true); }
    finally { setWorking(false); void props.refresh(); }
  }

  async function save(next?: Settings) {
    const value = next ?? settings();
    if (!value) return;
    await run('manager_save_settings', { settings: value });
    setDraft(undefined);
    props.notify(tr().settingsSaved);
  }

  const upd = () => s()?.update;
  const local = () => s()?.localCommit ?? '';
  const localIndex = () => upd()?.commits.findIndex((c) => c.sha === local()) ?? -1;
  const pending = () => { const i = localIndex(); return i < 0 ? (upd()?.commits.length ?? 0) : i; };

  const copyDiag = () => {
    const st = s();
    const text = [
      `GM Tools ${st?.appVersion} · local ${short(st?.localCommit)} · remoto ${short(upd()?.remoteSha)}`,
      `installed=${st?.installed} online=${st?.running} git=${st?.tools.git} dotnet=${st?.tools.dotnet}`,
      `server=${st?.serverDir}`, `source=${st?.sourceDir}`, '', ...props.logs.slice(-80),
    ].join('\n');
    void navigator.clipboard.writeText(text).then(() => props.notify(tr().diagCopied));
  };

  return (
    <div class="flex flex-col gap-5">
      {/* status + main actions */}
      <section class="surface-card p-5">
        <div class="flex items-start justify-between gap-6">
          <div class="grid flex-1 grid-cols-4 gap-5">
            <Info label={tr().status} value={s()?.busy ? tr().updating : s()?.running ? tr().enLinea : s()?.installed ? tr().detenido : tr().sinInstalar}
              dot={s()?.busy ? 'busy' : s()?.running ? 'online' : ''} />
            <Info label={tr().installedCommit} value={short(local())} mono />
            <Info label={tr().latestCommit} value={short(upd()?.remoteSha)} mono />
            <Info label={tr().tools} value={`${s()?.tools.git ? '✔' : '✖'} git  ${s()?.tools.dotnet ? '✔' : '✖'} .NET`} />
          </div>
          <div class="flex shrink-0 flex-wrap items-center justify-end gap-3">
            <Show when={s()?.running} fallback={
              <button class="primary-button" disabled={busy() || !s()?.installed} onClick={() => void run('manager_start')}><span class="flex items-center gap-2"><i class="fa-solid fa-play"/> {tr().start}</span></button>}>
              <button class="secondary-button" disabled={busy()} onClick={() => void run('manager_restart')}><span class="flex items-center gap-2"><i class="fa-solid fa-rotate-right"/> {tr().restart}</span></button>
              <button class="danger-button" disabled={busy()} onClick={() => void run('manager_stop')}><span class="flex items-center gap-2"><i class="fa-solid fa-stop"/> {tr().stop}</span></button>
            </Show>
            <button class="yellow-button" disabled={busy() || !s()?.tools.git || !s()?.tools.dotnet} onClick={() => void run('manager_update')}>
              <span class="flex items-center gap-2"><i class="fa-solid fa-hammer"/> {s()?.installed ? tr().updateBuild : tr().installServer}</span>
            </button>
          </div>
        </div>
        <Show when={upd()?.error}>
          <div class="mt-4 flex items-center gap-3 rounded-md bg-[#fff6d1] px-4 py-3 text-[12px] text-[#7a5600]">
            <img src="/game-ui/Common_Icon_Notice.png" class="size-6" alt="" />
            <span><b>{tr().repoErr}</b> {upd()!.error}</span>
          </div>
        </Show>
        <Show when={props.progress && (busy() || props.progress.stage === 'error' || props.progress.stage === 'done')}>
          <div class="mt-4">
            <div class="mb-2 flex items-center justify-between text-[12px]">
              <span class={`truncate ${props.progress!.stage === 'error' ? 'text-[#c2304b] font-bold' : 'text-ink-500'}`}>{props.progress!.message}</span>
              <span class="label-sm">{props.progress!.stage}</span>
            </div>
            <div class="progress-track"><div class="progress-bar" style={{ width: `${Math.max(4, props.progress!.pct)}%` }} /></div>
          </div>
        </Show>
      </section>

      <div class="grid grid-cols-[1.1fr_.9fr] gap-5">
        {/* commits */}
        <section class="surface-card p-5">
          <div class="mb-3 flex items-center justify-between">
            <div>
              <h3 class="card-title flex items-center gap-2"><i class="fa-solid fa-code-branch text-ba-blue"/> {tr().commitsOf} {settings()?.branch ?? 'main'}</h3>
              <p class="card-caption">{settings()?.repoUrl}{upd()?.checkedAt ? ` · ${tr().checked} ${ago(new Date(upd()!.checkedAt * 1000).toISOString())}` : ''}</p>
            </div>
            <div class="flex items-center gap-2">
              <Show when={upd()?.updateAvailable} fallback={<span class="tiny-chip ok">{tr().upToDateBadge}</span>}>
                <span class="tiny-chip warn">{pending() > 0 ? `${pending()} ${tr().newBadge}` : tr().newVerBadge}</span>
              </Show>
              <button class="quiet-button" disabled={working()} onClick={() => void run('manager_check')}><span class="flex items-center gap-1.5"><i class="fa-solid fa-rotate"/> {tr().check}</span></button>
            </div>
          </div>
          <Show when={(upd()?.commits.length ?? 0) > 0} fallback={
            <div class="empty-state"><img src="/game-ui/Image_Icon_Schale_MailBox.png" alt=""/>
              <span>{upd()?.error ? tr().noCommitsYet : tr().loadingHist}</span></div>}>
            <div class="max-h-[330px] overflow-y-auto pr-1">
              <For each={upd()!.commits}>{(c, i) => (
                <div class={`commit-row ${c.sha === local() ? 'is-local' : ''} ${localIndex() < 0 || i() < localIndex() ? 'is-new' : ''}`}>
                  <span class="commit-dot"/>
                  <div class="min-w-0 flex-1">
                    <div class="truncate text-[13px] font-semibold">{c.message}</div>
                    <div class="mt-0.5 text-[11px] text-ink-500">{c.author} · {ago(c.date)}</div>
                  </div>
                  <Show when={c.sha === local()}><span class="tiny-chip">{tr().installedBadge}</span></Show>
                  <span class="sha">{short(c.sha)}</span>
                </div>
              )}</For>
            </div>
          </Show>
        </section>

        {/* settings */}
        <section class="surface-card p-5">
          <h3 class="card-title mb-4 flex items-center gap-2"><i class="fa-solid fa-sliders text-ba-blue"/> {tr().autoUpdate}</h3>
          <label class="mb-4 flex items-center justify-between gap-3 text-[13px] font-semibold">
            {tr().autoInstallCommits}
            <input type="checkbox" class="toggle" checked={settings()?.autoUpdate ?? true}
              onChange={(e) => void save({ ...(settings() as Settings), autoUpdate: e.currentTarget.checked })}/>
          </label>
          <label class="mb-4 flex items-center justify-between gap-3 text-[13px] font-semibold">
            {tr().restartAfterUpd}
            <input type="checkbox" class="toggle" checked={settings()?.restartAfterUpdate ?? true}
              onChange={(e) => void save({ ...(settings() as Settings), restartAfterUpdate: e.currentTarget.checked })}/>
          </label>
          <label class="mb-4 flex items-center justify-between gap-3 text-[13px] font-semibold">
            <span>{tr().offlineMode} <small class="block text-[11px] font-normal text-ink-500">{tr().offlineModeHint}</small></span>
            <input type="checkbox" class="toggle" checked={settings()?.offlineMode ?? false}
              onChange={(e) => void save({ ...(settings() as Settings), offlineMode: e.currentTarget.checked })}/>
          </label>
          <div class="grid grid-cols-[1fr_110px_110px] gap-3">
            <label class="field-label">{tr().repo}<input class="input-field" value={settings()?.repoUrl ?? ''} onInput={(e) => edit('repoUrl', e.currentTarget.value)}/></label>
            <label class="field-label">{tr().branch}<input class="input-field" value={settings()?.branch ?? ''} onInput={(e) => edit('branch', e.currentTarget.value)}/></label>
            <label class="field-label">{tr().everyMin}<input class="input-field" type="number" min="1" max="240" value={settings()?.pollMinutes ?? 5} onInput={(e) => edit('pollMinutes', Number(e.currentTarget.value) || 5)}/></label>
          </div>
          <label class="field-label">{tr().sourceCode}
            <input class="input-field" placeholder={s()?.sourceDir} value={settings()?.sourceDir ?? ''} onInput={(e) => edit('sourceDir', e.currentTarget.value)}/></label>
          <div class="grid grid-cols-2 gap-3">
            <label class="field-label">{tr().serverApi}<input class="input-field" value={settings()?.apiUrl ?? ''} onInput={(e) => edit('apiUrl', e.currentTarget.value)}/></label>
            <label class="field-label">{tr().baFolder}
              <div class="flex gap-1.5">
                <input class="input-field min-w-0 flex-1" value={settings()?.gameDir ?? ''} onInput={(e) => edit('gameDir', e.currentTarget.value)}/>
                <button type="button" class="quiet-button shrink-0 px-2.5" disabled={working()} onClick={async () => {
                  const picked = await call<string | null>('manager_pick_folder', { initial: settings()?.gameDir ?? '' });
                  if (picked) edit('gameDir', picked);
                }}><i class="fa-solid fa-folder-open"/></button>
              </div>
            </label>
          </div>
          <div class="flex flex-wrap gap-2">
            <button class="primary-button" disabled={!draft() || working()} onClick={() => void save()}><span class="flex items-center gap-2"><i class="fa-solid fa-floppy-disk"/> {tr().save}</span></button>
            <button class="quiet-button" onClick={() => void call('manager_open', { which: 'server' })}><span class="flex items-center gap-1.5"><i class="fa-solid fa-folder-open"/> {tr().serverBtn}</span></button>
            <button class="quiet-button" onClick={() => void call('manager_open', { which: 'source' })}><span class="flex items-center gap-1.5"><i class="fa-solid fa-cloud-arrow-down"/> {tr().codeBtn}</span></button>
          </div>
        </section>
      </div>

      <section class="surface-card p-5">
        <div class="mb-3 flex items-center justify-between">
          <div><h3 class="card-title"><i class="fa-solid fa-rectangle-list mr-2 text-ba-blue"/>{tr().logTitle}</h3><p class="card-caption">{tr().logHint}</p></div>
          <div class="flex gap-2">
            <button class="quiet-button" onClick={copyDiag}><span class="flex items-center gap-1.5"><i class="fa-solid fa-copy"/> {tr().diag}</span></button>
            <button class="quiet-button" onClick={props.clearLogs}><span class="flex items-center gap-1.5"><i class="fa-solid fa-trash-can"/> {tr().clear}</span></button>
          </div>
        </div>
        <pre ref={logBox} class="console h-[280px]">
          <For each={props.logs.slice(-400)} fallback={<span class="text-ink-300">{tr().noLogs}</span>}>{(l) =>
            <div class={/\[ERR\]|error|fail|falló/i.test(l) ? 'err' : /\[gm-tools\]|succeeded|Build succeeded/i.test(l) ? 'ok' : ''}>{l}</div>}</For>
        </pre>
      </section>
    </div>
  );
}

function Info(props: { label: string; value: string; mono?: boolean; dot?: string }) {
  return <div>
    <div class="label-sm">{props.label}</div>
    <div class={`mt-1.5 flex items-center gap-2 text-[16px] font-extrabold ${props.mono ? 'font-mono' : ''}`}>
      <Show when={props.dot !== undefined}><span class={`status-dot ${props.dot}`}/></Show>{props.value}
    </div>
  </div>;
}
