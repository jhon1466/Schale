import { For, Show, createMemo, createSignal, onCleanup, onMount, type JSX } from 'solid-js';
import { listen } from '@tauri-apps/api/event';
import ServerPanel from './ServerPanel';
import { QuickActions, StudentsPanel, runCommand } from './AdminTools';
import { AronaSpine } from './AronaSpine';
import { api, call, get, short, uptime, type Progress, type Status, type UpdateInfo } from '../lib/api';
import { currentLang, fmt, setLang, tr, type Lang } from '../lib/i18n';

type Tab = 'server' | 'overview' | 'accounts' | 'students' | 'content' | 'console';
const ROUTES: { id: Tab; icon: string }[] = [
  { id: 'server', icon: 'fa-solid fa-server' },
  { id: 'overview', icon: 'fa-solid fa-gauge-high' },
  { id: 'accounts', icon: 'fa-solid fa-users' },
  { id: 'students', icon: 'fa-solid fa-user-graduate' },
  { id: 'content', icon: 'fa-solid fa-trophy' },
  { id: 'console', icon: 'fa-solid fa-terminal' },
];

// [CurrencyTypes id, enum name as the server serializes it, label, SchaleDB icon]
const CURRENCIES: [number, string, string, string][] = [
  [1, 'Gold', 'Créditos', 'gold'], [4, 'Gem', 'Pyroxenos', 'gem'], [5, 'ActionPoint', 'AP', 'ap'],
  [6, 'AcademyTicket', 'Ticket Academia', 'academyticket'], [7, 'ArenaTicket', 'Ticket Arena', 'arenaticket'],
  [8, 'RaidTicket', 'Ticket Raid', 'raidticket'], [17, 'TimeAttackDungeonTicket', 'Ticket J.F.D.', 'timeattackticket'],
  [24, 'EliminateTicketA', 'Ticket Grand Assault', 'eliminateraidticketa'], [18, 'MasterCoin', 'Master Coin', 'mastercoin'],
];
const curIcon = (slug: string) => `https://schaledb.com/images/item/icon/currency_icon_${slug}.webp`;
const COMMANDS = ['help', 'giveall', 'max all', 'giveallequip', 'inventory add lobbies', 'unlockall campaign', 'unlockall story', 'unlockall mission', 'unlockall battlepass', 'currency 4 99999', 'give <item> <cantidad>', 'character add <id> max', 'setseason', 'clearinventory', 'gacha'];

function Fa(props: { icon: string; class?: string }) {
  return <i class={`${props.icon} ${props.class ?? ''}`} aria-hidden="true" />;
}

export default function GMApp() {
  const [tab, setTab] = createSignal<Tab>('server');
  const [status, setStatus] = createSignal<Status>();
  const [progress, setProgress] = createSignal<Progress>();
  const [logs, setLogs] = createSignal<string[]>([]);
  const [notice, setNotice] = createSignal<{ text: string; error?: boolean }>();
  const [uid, setUid] = createSignal(localStorage.getItem('schale-uid') ?? '');
  let noticeTimer: number | undefined;
  let poll: number | undefined;
  const unlisteners: (() => void)[] = [];

  const online = () => !!status()?.running;
  const routeNav = () => tr().nav[tab()];
  const rememberUid = (v: string) => { setUid(v); localStorage.setItem('schale-uid', v); };

  function notify(text: string, error = false) {
    setNotice({ text, error });
    if (noticeTimer) clearTimeout(noticeTimer);
    noticeTimer = window.setTimeout(() => setNotice(undefined), 5000);
  }

  async function refresh() {
    try { setStatus(await call<Status>('manager_status')); } catch { /* ignore */ }
  }

  onMount(async () => {
    await refresh();
    setLogs(await call<string[]>('manager_logs').catch(() => []));
    poll = window.setInterval(refresh, 3000);
    unlisteners.push(await listen<string>('server-log', (e) => setLogs((l) => [...l.slice(-799), e.payload])));
    unlisteners.push(await listen<Progress>('manager-progress', (e) => {
      setProgress(e.payload);
      if (e.payload.stage !== 'build' && e.payload.stage !== 'sync') setLogs((l) => [...l.slice(-799), `[gm-tools] ${e.payload.message}`]);
      if (e.payload.stage === 'done') { notify(e.payload.message); void refresh(); }
      if (e.payload.stage === 'error') { notify(e.payload.message, true); void refresh(); }
    }));
    unlisteners.push(await listen<UpdateInfo>('update-info', () => void refresh()));
    unlisteners.push(await listen<string>('auto-update', (e) => notify(e.payload)));
  });
  onCleanup(() => { clearInterval(poll); unlisteners.forEach((u) => u()); });

  return (
    <div class="app-shell flex h-screen w-screen overflow-hidden">
      {/* ── sidebar ── */}
      <aside class="sidebar flex w-[236px] shrink-0 flex-col px-3 py-5">
        <div class="flex items-center gap-3 px-2 pb-7">
          <div class="brand-mark size-12 shrink-0"><img src="/game-ui/Image_Char_Arona.png" alt="Arona" class="size-full object-cover"/></div>
          <div>
            <div class="brand-title text-[17px] text-white">SCHALE</div>
            <div class="mt-0.5 text-[10px] font-bold tracking-[.26em] text-ba-cyan">GM TOOLS</div>
          </div>
        </div>
        <div class="mb-2 px-3 text-[10px] font-bold uppercase tracking-[.2em] text-[#7f9bbb]">{tr().menu}</div>
        <nav class="flex flex-col gap-1">
          <For each={ROUTES}>{(r) => (
            <button class={`nav-item flex h-11 items-center gap-3 rounded-l-md px-3 text-left text-[13px] font-bold ${tab() === r.id ? 'active-nav' : ''}`} onClick={() => setTab(r.id)}>
              <Fa icon={r.icon} class="w-4 text-center text-[15px]"/><span>{tr().nav[r.id][0]}</span>
              <Show when={r.id === 'server' && status()?.update.updateAvailable}><span class="ml-auto size-2 rounded-full bg-ba-yellow shadow-[0_0_8px_#ffe433]"/></Show>
            </button>)}</For>
        </nav>

        <div class="side-card mx-1 mt-7 p-3">
          <div class="flex items-center gap-2 text-[12px] font-bold text-white">
            <span class={`status-dot ${status()?.busy ? 'busy' : online() ? 'online' : ''}`}/>
            {status()?.busy ? tr().updating : online() ? tr().online : tr().stopped}
          </div>
          <div class="mt-2 pl-4 text-[11px] text-[#9fb6d0]">{status()?.settings.apiUrl.replace(/^https?:\/\//, '')}</div>
          <div class="mt-1 flex items-center gap-1.5 pl-4 text-[11px] text-[#9fb6d0]"><Fa icon="fa-solid fa-code-commit"/>{short(status()?.localCommit)}</div>
        </div>

        <div class="relative mt-auto">
          <img
            src="/game-ui/Image_Char_Arona2.png"
            alt="Arona"
            onClick={() => {
              const quotes = tr().aronaQuotes;
              notify(quotes[Math.floor(Math.random() * quotes.length)]);
            }}
            class="float mx-auto -mb-2 w-[150px] cursor-pointer drop-shadow-[0_8px_16px_#0006] transition hover:scale-105"
          />
          <button class="primary-button w-full" disabled={!status()?.gameFound} title={status()?.gameFound ? tr().openGame : tr().noGame}
            onClick={() => void call('manager_play').catch((e) => notify(String(e), true))}>
            <span class="flex items-center gap-2"><Fa icon="fa-solid fa-gamepad"/> {tr().play}</span>
          </button>
        </div>
        <div class="mt-3 flex justify-between px-2 text-[10px] text-[#7f9bbb]"><span>Schale GM Tools</span><span>v{status()?.appVersion ?? '…'}</span></div>
      </aside>

      {/* ── main ── */}
      <main class="main-canvas flex min-w-0 flex-1 flex-col overflow-hidden">
        <header class="topbar flex h-[84px] shrink-0 items-center justify-between px-8">
          <img src="/game-ui/Bg_Public_Halo_1.png" alt="" class="topbar-halo"/>
          <div>
            <div class="crumb">Schale / {routeNav()[0]}</div>
            <h1 class="page-title m-0 mt-1">{routeNav()[1]}</h1>
          </div>
          <div class="flex items-center gap-3">
            <label class="flex items-center gap-1.5 rounded-md bg-white/20 px-2.5 py-1.5 text-[11px] font-bold text-white backdrop-blur">
              <Fa icon="fa-solid fa-globe"/>
              <select class="cursor-pointer bg-transparent text-white outline-none" style={{ "color-scheme": "light" }} value={currentLang()} onChange={(e) => setLang(e.currentTarget.value as Lang)}>
                <option value="es" style={{ background: "#ffffff", color: "#1b2c40" }}>Español</option>
                <option value="en" style={{ background: "#ffffff", color: "#1b2c40" }}>English</option>
                <option value="zh" style={{ background: "#ffffff", color: "#1b2c40" }}>中文</option>
                <option value="ja" style={{ background: "#ffffff", color: "#1b2c40" }}>日本語</option>
              </select>
            </label>
            <label class="flex items-center gap-2 rounded-md bg-white/20 px-3 py-1.5 text-[11px] font-bold backdrop-blur">
              UID <input class="w-[90px] bg-transparent text-white outline-none placeholder:text-white/60" placeholder="ServerId" value={uid()} onInput={(e) => rememberUid(e.currentTarget.value.trim())}/>
            </label>
            <span class={`flex items-center gap-2 rounded-md px-3 py-2 text-[11px] font-extrabold ${online() ? 'bg-white text-ba-blue' : 'bg-white/20 text-white'}`}>
              <Fa icon="fa-solid fa-wifi"/>{online() ? 'ONLINE' : 'OFFLINE'}
            </span>
          </div>
        </header>

        <div class="flex-1 overflow-y-auto px-8 py-6">
          <Show when={tab() === 'server'}>
            <ServerPanel status={status()} progress={progress()} logs={logs()} notify={notify} refresh={refresh}
              clearLogs={() => { setLogs([]); void call('manager_clear_logs'); }}/>
          </Show>
          <Show when={tab() === 'overview'}><Overview status={status()} go={setTab}/></Show>
          <Show when={tab() === 'accounts'}><Accounts online={online()} notify={notify} uid={uid()} setUid={rememberUid}/></Show>
          <Show when={tab() === 'students'}><Students online={online()} notify={notify} uid={uid()}/></Show>
          <Show when={tab() === 'content'}><Content online={online()} notify={notify} uid={uid()}/></Show>
          <Show when={tab() === 'console'}><Console online={online()} notify={notify} uid={uid()}/></Show>
        </div>

        <footer class="flex h-8 shrink-0 items-center justify-between border-t border-[#c9e2f8] bg-white/70 px-8 text-[10px] font-semibold text-ink-500">
          <span class="flex items-center gap-2"><span class={`status-dot ${online() ? 'online' : ''}`}/>{online() ? tr().connectedTo : tr().noConn} · {status()?.settings.apiUrl}</span>
          <span>{status()?.settings.autoUpdate ? fmt(tr().autoUpdEvery, { n: status()?.settings.pollMinutes ?? 5 }) : tr().autoUpdOff}</span>
        </footer>
      </main>

      <Show when={notice()}>{(n) => (
        <div class={`toast ${n().error ? 'toast-error' : ''}`}>
          <img src="/game-ui/Image_Char_Arona.png" alt=""/>
          <span class="min-w-0 flex-1">{n().text}</span>
          <button class="text-ink-500 hover:text-ba-deep" onClick={() => setNotice(undefined)}><Fa icon="fa-solid fa-xmark"/></button>
        </div>)}</Show>
    </div>
  );
}

/* ───────────── shared bits ───────────── */

function Heading(props: { title: string; hint: string; children?: JSX.Element }) {
  return <div class="mb-5 flex items-end justify-between gap-4"><div><h2 class="section-title">{props.title}</h2><p class="section-subtitle">{props.hint}</p></div>{props.children}</div>;
}
function Offline(props: { online: boolean; children: JSX.Element }) {
  return <Show when={props.online} fallback={
    <div class="surface-card empty-state"><img src="/game-ui/Image_Char_Arona2.png" alt=""/>
      <b class="text-[14px] text-ba-deep">{tr().offline}</b><span>{tr().offlineHint}</span></div>}>{props.children}</Show>;
}
function Btn(props: { kind?: 'primary' | 'yellow' | 'secondary' | 'quiet' | 'danger'; disabled?: boolean; onClick?: () => void; type?: 'submit' | 'button'; children: JSX.Element }) {
  return <button type={props.type ?? 'button'} class={`${props.kind ?? 'primary'}-button`} disabled={props.disabled} onClick={props.onClick}><span class="flex items-center gap-2">{props.children}</span></button>;
}
const needUid = (uid: string, notify: (m: string, e?: boolean) => void) => {
  const n = Number(uid);
  if (!Number.isInteger(n) || n <= 0) { notify(tr().needUid, true); return 0; }
  return n;
};

/* ───────────── overview ───────────── */

function Overview(props: { status?: Status; go: (t: Tab) => void }) {
  const s = () => props.status;
  const [quoteIdx, setQuoteIdx] = createSignal<number | null>(null);
  const statusMessage = () => !s()?.installed ? tr().aronaNotInst
    : s()?.update.updateAvailable ? fmt(s()?.settings.autoUpdate ? tr().aronaUpdAuto : tr().aronaUpdMan, { sha: short(s()?.update.remoteSha) })
    : s()?.running && s()?.settings.offlineMode ? tr().aronaOffline
    : s()?.running ? tr().aronaOk
    : tr().aronaIdle;
  const message = () => {
    const idx = quoteIdx();
    if (idx === null) return statusMessage();
    const quotes = tr().aronaQuotes;
    return quotes[idx % quotes.length];
  };
  const nextQuote = () => {
    const quotes = tr().aronaQuotes;
    setQuoteIdx((prev) => (prev === null ? 0 : (prev + 1) % quotes.length));
  };
  onMount(() => {
    const id = setInterval(() => {
      if (s()?.running) nextQuote();
    }, 22000);
    onCleanup(() => clearInterval(id));
  });
  return <>
    <section class="arona-hero mb-6 flex items-center gap-6 px-6 py-4">
      <AronaSpine onInteract={nextQuote}/>
      <div class="max-w-xl">
        <div class="label-sm mb-2 text-ba-blue">{tr().aronaTitle}</div>
        <div class="arona-bubble cursor-pointer transition hover:shadow-md" onClick={nextQuote}>{message()}</div>
        <div class="mt-4 flex gap-3">
          <Btn kind="yellow" onClick={() => props.go('server')}><Fa icon="fa-solid fa-server"/> {tr().goServer}</Btn>
          <Btn kind="secondary" onClick={() => props.go('accounts')}><Fa icon="fa-solid fa-users"/> {tr().accounts}</Btn>
        </div>
      </div>
    </section>
    <div class="grid grid-cols-4 gap-4">
      <Stat icon="fa-solid fa-wifi" label={tr().status} value={s()?.running ? tr().enLinea : tr().detenido} hint={s()?.settings.apiUrl ?? ''}/>
      <Stat icon="fa-solid fa-clock" tone="yellow" label={tr().uptime} value={uptime(s()?.uptimeSeconds)} hint={tr().uptimeHint}/>
      <Stat icon="fa-solid fa-code-commit" tone="pink" label={tr().version} value={short(s()?.localCommit)} hint={s()?.update.updateAvailable ? tr().updAvail : tr().upToDate}/>
      <Stat icon="fa-solid fa-gamepad" tone="navy" label="Blue Archive" value={s()?.gameFound ? tr().detected : tr().notFound} hint={s()?.settings.gameDir ?? ''}/>
    </div>
    <div class="mt-5 grid grid-cols-3 gap-4">
      <Quick icon="fa-solid fa-user-graduate" title={tr().editStudents} hint={tr().editStudentsHint} onClick={() => props.go('students')}/>
      <Quick icon="fa-solid fa-trophy" title={tr().raidSeasons} hint={tr().raidSeasonsHint} onClick={() => props.go('content')}/>
      <Quick icon="fa-solid fa-terminal" title={tr().gmConsole} hint={tr().gmConsoleHint} onClick={() => props.go('console')}/>
    </div>
  </>;
}
function Stat(props: { icon: string; label: string; value: string; hint: string; tone?: string }) {
  return <div class="stat-card"><i class={`${props.icon} bg-icon text-[74px]`}/><span class={`stat-icon ${props.tone ?? ''}`}><i class={`${props.icon} text-[16px]`}/></span>
    <div class="label-sm mt-4">{props.label}</div><div class="mt-1 text-[22px] font-extrabold text-ba-deep">{props.value}</div><div class="mt-1 truncate text-[11px] text-ink-500">{props.hint}</div></div>;
}
function Quick(props: { icon: string; title: string; hint: string; onClick: () => void }) {
  return <button class="surface-card flex items-center gap-3 p-4 text-left transition hover:-translate-y-0.5" onClick={props.onClick}>
    <span class="stat-icon"><i class={`${props.icon} text-[15px]`}/></span><span class="flex-1"><b class="block text-[13px]">{props.title}</b><small class="text-[11px] text-ink-500">{props.hint}</small></span><i class="fa-solid fa-arrow-right text-ba-blue"/></button>;
}

/* ───────────── accounts ───────────── */

function Accounts(props: { online: boolean; notify: (m: string, e?: boolean) => void; uid: string; setUid: (v: string) => void }) {
  const [rows, setRows] = createSignal<any[]>([]);
  const [q, setQ] = createSignal('');
  const [currencies, setCurrencies] = createSignal<Record<string, number>>();
  const [cur, setCur] = createSignal('4');
  const [amount, setAmount] = createSignal('99999');
  const [mail, setMail] = createSignal({ comment: tr().mailDefault, type: 'Currency', id: '4', amount: '1200' });
  const [busy, setBusy] = createSignal(false);
  const shown = createMemo(() => {
    const query = q().trim().toLowerCase();
    return rows().filter((r) => !query || String(get(r, 'serverId')).includes(query) || String(get(r, 'nickname') ?? '').toLowerCase().includes(query));
  });

  const load = async () => { setBusy(true); try { setRows(await api('GET', 'api/admin/accounts')); } catch (e) { props.notify(String(e), true); } finally { setBusy(false); } };
  const loadCurrencies = async (id: string) => { try { setCurrencies(await api('GET', `api/admin/account/${id}/currencies`)); } catch (e) { setCurrencies(undefined); props.notify(String(e), true); } };
  const del = async (r: any) => {
    const id = Number(get(r, 'serverId'));
    const answer = window.prompt(`Escribe el UID para borrar la cuenta ${id} (${get(r, 'nickname') || ''})`);
    if (answer?.trim() !== String(id)) return;
    setBusy(true);
    try { await api('POST', 'api/admin/account/delete', { serverId: id }); props.notify(`UID ${id} ✓`); if (props.uid === String(id)) props.setUid(''); setCurrencies(undefined); await load(); }
    catch (e) { props.notify(String(e), true); } finally { setBusy(false); }
  };
  const select = (r: any) => { const id = String(get(r, 'serverId')); props.setUid(id); void loadCurrencies(id); };

  const setCurrency = async () => {
    const id = needUid(props.uid, props.notify); if (!id) return;
    setBusy(true);
    try { await api('POST', 'api/admin/currency/set', { accountServerId: id, currencyType: Number(cur()), amount: Number(amount()) }); props.notify(tr().curUpdated); await loadCurrencies(String(id)); }
    catch (e) { props.notify(String(e), true); } finally { setBusy(false); }
  };
  const sendMail = async () => {
    const id = needUid(props.uid, props.notify); if (!id) return;
    const m = mail();
    setBusy(true);
    try { await api('POST', 'api/admin/mail/send', { accountServerId: id, sender: 'Arona', comment: m.comment, parcels: [{ type: m.type, id: Number(m.id), amount: Number(m.amount) }] }); props.notify(tr().mailSent); }
    catch (e) { props.notify(String(e), true); } finally { setBusy(false); }
  };

  onMount(() => { if (props.online) void load(); if (props.uid) void loadCurrencies(props.uid); });

  return <Offline online={props.online}>
    <Heading title={tr().accounts} hint={tr().accountsHint}><Btn kind="quiet" disabled={busy()} onClick={() => void load()}><Fa icon="fa-solid fa-rotate"/> {tr().reload}</Btn></Heading>
    <div class="grid grid-cols-[1fr_1fr] gap-5">
      <section class="surface-card overflow-hidden">
        <div class="border-b border-[#e3eef9] p-4"><div class="search-field"><Fa icon="fa-solid fa-magnifying-glass"/><input placeholder={tr().searchAcc} value={q()} onInput={(e) => setQ(e.currentTarget.value)}/></div></div>
        <div class="max-h-[560px] overflow-y-auto">
          <For each={shown()} fallback={<div class="empty-state"><img src="/game-ui/Image_Icon_Schale_MailBox.png" alt=""/>{tr().noAccounts}</div>}>{(r) => (
            <button class={`account-row ${String(get(r, 'serverId')) === props.uid ? 'selected' : ''}`} onClick={() => select(r)}>
              <span class="avatar">{String(get(r, 'nickname') || '?').slice(0, 1).toUpperCase()}</span>
              <span class="min-w-0 flex-1"><b class="block truncate text-[13px]">{get(r, 'nickname') || tr().noName}</b><small class="text-[11px] text-ink-500">UID {get(r, 'serverId')} · Lv. {get(r, 'level')}</small></span>
              <small class="text-[10px] text-ink-500">{get(r, 'lastConnectTime') ? new Date(get(r, 'lastConnectTime')).toLocaleDateString() : '—'}</small>
              <span role="button" title="Borrar cuenta" class="ml-1 grid size-7 place-items-center rounded text-[#c2304b] hover:bg-[#ffe5ea]" onClick={(e) => { e.stopPropagation(); void del(r); }}><i class="fa-solid fa-trash"/></span>
            </button>)}</For>
        </div>
      </section>

      <div class="flex flex-col gap-5">
        <section class="surface-card p-5">
          <h3 class="card-title mb-3 flex items-center gap-2"><Fa icon="fa-solid fa-coins" class="text-ba-blue"/> {tr().currencies} {props.uid ? `· UID ${props.uid}` : ''}</h3>
          <Show when={currencies()} fallback={<p class="card-caption mb-3">{tr().selectAccCur}</p>}>
            <div class="mb-4 grid grid-cols-3 gap-2">
              <For each={CURRENCIES}>{([id, key, label, icon]) => {
                const v = () => { const d = currencies()!; const k = Object.keys(d).find((x) => x === String(id) || x.toLowerCase() === key.toLowerCase()); return k ? Number(d[k]).toLocaleString() : '0'; };
                return <div class="metric-tile"><span class="flex items-center gap-1.5"><img src={curIcon(icon)} alt="" class="size-5 object-contain"/>{label}</span><b>{v()}</b></div>;
              }}</For>
            </div>
          </Show>
          <div class="grid grid-cols-[1fr_1fr_auto] items-end gap-3">
            <label class="field-label !mb-0">{tr().currency}<select class="input-field" value={cur()} onChange={(e) => setCur(e.currentTarget.value)}><For each={CURRENCIES}>{([id, , label]) => <option value={id}>{label}</option>}</For></select></label>
            <label class="field-label !mb-0">{tr().amount}<input class="input-field" type="number" value={amount()} onInput={(e) => setAmount(e.currentTarget.value)}/></label>
            <Btn disabled={busy()} onClick={() => void setCurrency()}><Fa icon="fa-solid fa-floppy-disk"/> {tr().setBtn}</Btn>
          </div>
        </section>

        <section class="surface-card p-5">
          <h3 class="card-title mb-3 flex items-center gap-2"><Fa icon="fa-solid fa-envelope" class="text-ba-blue"/> {tr().sendMail}</h3>
          <label class="field-label">{tr().message}<input class="input-field" value={mail().comment} onInput={(e) => setMail({ ...mail(), comment: e.currentTarget.value })}/></label>
          <div class="grid grid-cols-3 gap-3">
            <label class="field-label">{tr().type}<select class="input-field" value={mail().type} onChange={(e) => setMail({ ...mail(), type: e.currentTarget.value })}>
              <For each={['Currency', 'Item', 'Character', 'Equipment', 'Furniture', 'Costume', 'Emblem']}>{(tp) => <option>{tp}</option>}</For></select></label>
            <label class="field-label">ID<input class="input-field" value={mail().id} onInput={(e) => setMail({ ...mail(), id: e.currentTarget.value })}/></label>
            <label class="field-label">{tr().amount}<input class="input-field" value={mail().amount} onInput={(e) => setMail({ ...mail(), amount: e.currentTarget.value })}/></label>
          </div>
          <Btn kind="yellow" disabled={busy()} onClick={() => void sendMail()}><Fa icon="fa-solid fa-paper-plane"/> {tr().sendToUid} {props.uid || '—'}</Btn>
        </section>

        <QuickActions uid={props.uid} notify={props.notify}/>
      </div>
    </div>
  </Offline>;
}

/* ───────────── students ───────────── */

function Students(props: { online: boolean; notify: (m: string, e?: boolean) => void; uid: string }) {
  const [list, setList] = createSignal<any[]>([]);
  const [q, setQ] = createSignal('');
  const [sel, setSel] = createSignal<any>();
  const [form, setForm] = createSignal<Record<string, number>>({});
  const [busy, setBusy] = createSignal(false);
  const fields = (): [string, string, number][] => [
    ['level', tr().lvl, 90], ['starGrade', tr().stars, 5], ['favorRank', tr().bond, 100],
    ['exSkillLevel', 'EX', 5], ['publicSkillLevel', 'Normal', 10], ['passiveSkillLevel', tr().pasiva, 10], ['extraPassiveSkillLevel', 'Sub', 10],
  ];
  const ch = (row: any) => get(row, 'character') ?? row;
  const shown = createMemo(() => list().filter((r) => !q() || String(get(ch(r), 'uniqueId')).includes(q().trim())));

  const load = async () => {
    const id = needUid(props.uid, props.notify); if (!id) return;
    setBusy(true);
    try { const d = await api<any[]>('POST', 'dev/api/get_character', { userID: id }); setList(Array.isArray(d) ? d : []); setSel(undefined); }
    catch (e) { props.notify(String(e), true); } finally { setBusy(false); }
  };
  const pick = (row: any) => { setSel(row); const c = ch(row); setForm(Object.fromEntries(fields().map(([k]) => [k, Number(get(c, k) ?? 0)]))); };
  const maxAll = () => setForm(Object.fromEntries(fields().map(([k, , m]) => [k, m])));
  const save = async () => {
    const row = sel(); if (!row) return;
    const id = needUid(props.uid, props.notify); if (!id) return;
    setBusy(true);
    try {
      await api('POST', 'dev/api/modify_character', {
        userID: id, character: { ...ch(row), ...form() }, weapon: get(row, 'weapon') ?? null,
        equippedEquipment: get(row, 'equippedEquipment') ?? [], gear: get(row, 'gear') ?? null,
      });
      props.notify(tr().studentSaved);
      await load();
    } catch (e) { props.notify(String(e), true); } finally { setBusy(false); }
  };

  void list; void sel; void busy; void shown; void pick; void maxAll; void save; void load;
  return <Offline online={props.online}>
    <Heading title={tr().students} hint={`${tr().selectedAcc}: ${props.uid || tr().none}`}/>
    <StudentsPanel uid={props.uid} notify={props.notify}/>
  </Offline>;
}

/* ───────────── content ───────────── */

function Content(props: { online: boolean; notify: (m: string, e?: boolean) => void; uid: string }) {
  const [info, setInfo] = createSignal<any>();
  const [kind, setKind] = createSignal<'totalAssault' | 'grandAssault' | 'timeAttackDungeon' | 'multiFloor'>('totalAssault');
  const [busy, setBusy] = createSignal(false);
  const TYPES = { totalAssault: ['Total Assault', 'raids'], grandAssault: ['Grand Assault', 'eliminateraids'], timeAttackDungeon: ['J.F.D.', 'timeattackdungeons'], multiFloor: ['Final Restriction', 'multifloreraids'] } as const;
  const rows = () => (get(info(), kind()) as any[] | undefined) ?? [];

  const load = async () => { setBusy(true); try { setInfo(await api('GET', 'dev/api/get_raidinfo')); } catch (e) { props.notify(String(e), true); } finally { setBusy(false); } };
  const apply = async (row: any) => {
    const id = needUid(props.uid, props.notify); if (!id) return;
    const seasonId = Number(get(row, 'seasonId', 'id'));
    setBusy(true);
    try { await api('POST', 'dev/api/set_raid', { userID: id, raidType: TYPES[kind()][1], seasonId }); props.notify(fmt(tr().seasonApplied, { id: seasonId, uid: id })); }
    catch (e) { props.notify(String(e), true); } finally { setBusy(false); }
  };
  onMount(() => { if (props.online) void load(); });

  return <Offline online={props.online}>
    <Heading title={tr().seasons} hint={tr().seasonsHint}><Btn kind="quiet" disabled={busy()} onClick={() => void load()}><Fa icon="fa-solid fa-rotate"/> {tr().reload}</Btn></Heading>
    <div class="mb-4 flex gap-2">
      <For each={Object.entries(TYPES)}>{([k, [label]]) =>
        <button class={kind() === k ? 'primary-button' : 'secondary-button'} onClick={() => setKind(k as any)}><span>{label}</span></button>}</For>
    </div>
    <section class="surface-card overflow-hidden">
      <div class="max-h-[580px] overflow-auto">
        <Show when={rows().length} fallback={<div class="empty-state">{tr().noData}</div>}>
          <table class="data-table"><thead><tr><th>{tr().seasonCol}</th><th>{tr().bossCol}</th><th>{tr().dateCol}</th><th>{tr().detailsCol}</th><th/></tr></thead>
            <tbody><For each={rows().slice().reverse()}>{(r) => <tr>
              <td class="font-mono font-bold">{get(r, 'seasonId', 'id')}</td>
              <td>{get(r, 'bossName') ?? get(r, 'dungeonType') ?? '—'}</td>
              <td>{get(r, 'date')}</td>
              <td class="text-ink-500">{[get(r, 'groundType'), get(r, 'armorType'), ...(get(r, 'armorTypes') ?? [])].filter(Boolean).join(' · ')}</td>
              <td class="text-right"><button class="chip-btn" disabled={busy()} onClick={() => void apply(r)}>{tr().apply}</button></td>
            </tr>}</For></tbody></table>
        </Show>
      </div>
    </section>
  </Offline>;
}

/* ───────────── console ───────────── */

function Console(props: { online: boolean; notify: (m: string, e?: boolean) => void; uid: string }) {
  const [cmd, setCmd] = createSignal('');
  const [history, setHistory] = createSignal<{ cmd: string; out: string; error?: boolean }[]>([]);
  const [busy, setBusy] = createSignal(false);
  const run = async (e: SubmitEvent) => {
    e.preventDefault();
    const id = needUid(props.uid, props.notify); if (!id || !cmd().trim()) return;
    const c = cmd().trim();
    setBusy(true);
    try { const out = await runCommand(id, c); setHistory((h) => [...h, { cmd: c, out }]); setCmd(''); }
    catch (err) { setHistory((h) => [...h, { cmd: c, out: String(err), error: true }]); } finally { setBusy(false); }
  };
  return <Offline online={props.online}>
    <Heading title={tr().gmConsole} hint={fmt(tr().cmdHint, { uid: props.uid || tr().noUid })}/>
    <div class="grid grid-cols-[.7fr_1.3fr] gap-5">
      <section class="surface-card p-5">
        <h3 class="card-title mb-3"><Fa icon="fa-solid fa-bolt" class="mr-1.5 text-ba-blue"/>{tr().quickCmds}</h3>
        <div class="flex flex-wrap gap-2"><For each={COMMANDS}>{(c) => <button class="chip-btn" onClick={() => setCmd(c)}>{c}</button>}</For></div>
      </section>
      <section class="surface-card p-5">
        <pre class="console mb-3 h-[380px]">
          <For each={history()} fallback={<span class="text-ink-300">{tr().writeCmd}</span>}>{(h) => <div>
            <span class="text-ba-cyan">sensei › </span>{h.cmd}{'\n'}<span class={h.error ? 'err' : 'ok'}>{h.out}</span>{'\n'}</div>}</For>
        </pre>
        <form class="flex gap-3" onSubmit={run}>
          <div class="search-field font-mono"><span class="font-bold text-ba-blue">›</span><input value={cmd()} onInput={(e) => setCmd(e.currentTarget.value)} placeholder="!giveall"/></div>
          <Btn type="submit" disabled={busy()}><Fa icon="fa-solid fa-paper-plane"/> {tr().run}</Btn>
        </form>
      </section>
    </div>
  </Offline>;
}
