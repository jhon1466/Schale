import { For, Show, createMemo, createSignal, onMount } from 'solid-js';
import { api, get } from '../lib/api';
import { currentLang } from '../lib/i18n';

/* Same admin API the server's /control panel (old Control Center) uses: /api/admin/*.
   Commands go through /api/admin/command, which really executes them (the /dev/api one is a stub). */

const L = {
  es: {
    quick: 'Acciones rápidas', quickHint: 'Se aplican a la cuenta seleccionada. El Sensei debe reiniciar el juego para verlas.',
    giveall: 'Todos los estudiantes', maxall: 'Maximizar estudiantes', lobbies: 'Desbloquear Live2D (memorias)',
    maps: 'Desbloquear campaña e historia', dungeons: 'Desbloquear mazmorras', missions: 'Completar misiones',
    bp: 'Desbloquear Battle Pass', equip: 'Todo el equipamiento', inv: 'Todo el inventario', furniture: 'Todos los muebles',
    maxcur: 'Monedas al máximo', everything: 'Desbloquear TODO', done: 'Hecho', confirmAll: '¿Desbloquear todo en la cuenta {uid}? Puede tardar un poco.',
    students: 'Estudiantes', owned: 'Tiene', add: 'Añadir estudiante', addHint: 'Busca por nombre', preset: 'Preset',
    maxOne: 'Maximizar', added: 'Añadido', maxed: 'Maximizado', starSet: 'Estrellas cambiadas', search: 'Buscar…', reload: 'Recargar',
    needUid: 'Selecciona una cuenta (UID) primero.', edit: 'Editar estudiante', bond: 'Afecto', star: 'Estrellas', passive: 'Pasiva', sub: 'Sub', save: 'Guardar', cancel: 'Cancelar', saved: 'Estudiante guardado. Reinicia el juego para verlo.', allMax: 'Todo al máximo', clickEdit: 'Haz clic en un estudiante para editarlo', sensei: 'Sensei a nivel 90', count: '{n} de {t}', noChars: 'Sin estudiantes.', lvl: 'Nv.', name: 'Nombre',
  },
  en: {
    quick: 'Quick actions', quickHint: 'Applied to the selected account. The Sensei must restart the game to see them.',
    giveall: 'All students', maxall: 'Max all students', lobbies: 'Unlock Live2D (memorial lobbies)',
    maps: 'Unlock campaign + story', dungeons: 'Unlock dungeons', missions: 'Complete missions',
    bp: 'Unlock Battle Pass', equip: 'All equipment', inv: 'Full inventory', furniture: 'All furniture',
    maxcur: 'Max currencies', everything: 'Unlock EVERYTHING', done: 'Done', confirmAll: 'Unlock everything on account {uid}? It may take a while.',
    students: 'Students', owned: 'Owned', add: 'Add student', addHint: 'Search by name', preset: 'Preset',
    maxOne: 'Max', added: 'Added', maxed: 'Maxed', starSet: 'Stars changed', search: 'Search…', reload: 'Reload',
    needUid: 'Select an account (UID) first.', edit: 'Edit student', bond: 'Bond', star: 'Stars', passive: 'Passive', sub: 'Sub', save: 'Save', cancel: 'Cancel', saved: 'Student saved. Restart the game to see it.', allMax: 'All max', clickEdit: 'Click a student to edit it', sensei: 'Sensei to level 90', count: '{n} of {t}', noChars: 'No students.', lvl: 'Lv.', name: 'Name',
  },
};
const t = () => (currentLang() === 'es' ? L.es : L.en);

export async function runCommand(uid: number, command: string): Promise<string> {
  const r = await api<any>('POST', 'api/admin/command', { uid, command: command.replace(/^[!/]/, '') });
  return typeof r === 'string' ? r : String(get(r, 'output') ?? JSON.stringify(r, null, 2));
}

type Notify = (m: string, e?: boolean) => void;

/** Student icon from SchaleDB (by character id); falls back to the initial when missing. */
export function Avatar(props: { id: number | string; name: string }) {
  const [failed, setFailed] = createSignal(false);
  return <span class="avatar overflow-hidden !p-0">
    <Show when={!failed()} fallback={String(props.name).slice(0, 1)}>
      <img src={`https://schaledb.com/images/student/icon/${props.id}.webp`} alt="" loading="lazy" class="size-full object-cover" onError={() => setFailed(true)}/>
    </Show>
  </span>;
}
const toUid = (uid: string, notify: Notify) => {
  const n = Number(uid);
  if (!Number.isInteger(n) || n <= 0) { notify(t().needUid, true); return 0; }
  return n;
};

const MAX_CURRENCY = 999_999_999;
const CURRENCY_IDS = [1, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 24, 25, 26, 27];

export function QuickActions(props: { uid: string; notify: Notify }) {
  const [busy, setBusy] = createSignal<string>();
  const actions = (): [string, string, string[] | 'currency' | 'sensei'][] => [
    ['fa-solid fa-user-tie', t().sensei, 'sensei'],
    ['fa-solid fa-user-plus', t().giveall, ['giveall']],
    ['fa-solid fa-angles-up', t().maxall, ['max all']],
    ['fa-solid fa-panorama', t().lobbies, ['inventory add lobbies']],
    ['fa-solid fa-map', t().maps, ['unlockall campaign', 'unlockall story']],
    ['fa-solid fa-dungeon', t().dungeons, ['unlockall weekdungeon', 'unlockall schooldungeon']],
    ['fa-solid fa-list-check', t().missions, ['unlockall mission']],
    ['fa-solid fa-ticket', t().bp, ['unlockall battlepass']],
    ['fa-solid fa-shield-halved', t().equip, ['giveallequip']],
    ['fa-solid fa-box-open', t().inv, ['inventory add items', 'inventory add gears', 'inventory add emblems']],
    ['fa-solid fa-couch', t().furniture, ['inventory add furnitures']],
    ['fa-solid fa-coins', t().maxcur, 'currency'],
  ];

  async function exec(label: string, cmds: string[] | 'currency' | 'sensei') {
    const uid = toUid(props.uid, props.notify); if (!uid) return;
    setBusy(label);
    try {
      if (cmds === 'sensei') {
        await api('POST', 'api/admin/account/update', { serverId: uid, level: 90, exp: 0 });
      } else if (cmds === 'currency') {
        for (const c of CURRENCY_IDS) await api('POST', 'api/admin/currency/set', { accountServerId: uid, currencyType: c, amount: MAX_CURRENCY });
      } else {
        for (const c of cmds) await runCommand(uid, c);
      }
      props.notify(`${label}: ${t().done}`);
    } catch (e) { props.notify(`${label}: ${e}`, true); } finally { setBusy(undefined); }
  }

  async function everything() {
    const uid = toUid(props.uid, props.notify); if (!uid) return;
    if (!window.confirm(t().confirmAll.replace('{uid}', String(uid)))) return;
    for (const [, label, cmds] of actions()) await exec(label, cmds);
  }

  return <section class="surface-card p-5">
    <h3 class="card-title mb-1 flex items-center gap-2"><i class="fa-solid fa-wand-magic-sparkles text-ba-blue"/> {t().quick}</h3>
    <p class="card-caption mb-3">{t().quickHint}</p>
    <div class="grid grid-cols-2 gap-2">
      <For each={actions()}>{([icon, label, cmds]) =>
        <button class="quiet-button !justify-start" disabled={!!busy()} onClick={() => void exec(label, cmds)}>
          <span class="flex items-center gap-2">{busy() === label ? <i class="fa-solid fa-spinner fa-spin"/> : <i class={icon}/>} {label}</span>
        </button>}</For>
    </div>
    <button class="yellow-button mt-3 w-full" disabled={!!busy()} onClick={() => void everything()}>
      <span class="flex items-center gap-2"><i class="fa-solid fa-star"/> {t().everything}</span>
    </button>
  </section>;
}

export function StudentsPanel(props: { uid: string; notify: Notify }) {
  const [owned, setOwned] = createSignal<any[]>([]);
  const [catalog, setCatalog] = createSignal<any[]>([]);
  const [q, setQ] = createSignal('');
  const [addQ, setAddQ] = createSignal('');
  const [preset, setPreset] = createSignal('max');
  const [busy, setBusy] = createSignal(false);
  const [editing, setEditing] = createSignal<any>();
  const [form, setForm] = createSignal<Record<string, number>>({});
  const ownedIds = createMemo(() => new Set(owned().map((r) => Number(get(r, 'uniqueId')))));
  const shown = createMemo(() => {
    const s = q().trim().toLowerCase();
    return owned().filter((r) => !s || String(get(r, 'name')).toLowerCase().includes(s) || String(get(r, 'uniqueId')).includes(s));
  });
  const candidates = createMemo(() => {
    const s = addQ().trim().toLowerCase();
    return catalog().filter((c) => !ownedIds().has(Number(get(c, 'id'))) && (!s || String(get(c, 'name')).toLowerCase().includes(s) || String(get(c, 'id')).includes(s)));
  });

  async function load() {
    const uid = toUid(props.uid, props.notify); if (!uid) return;
    setBusy(true);
    try {
      const [o, c] = await Promise.all([
        api<any[]>('GET', `api/admin/account/${uid}/characters`),
        catalog().length ? Promise.resolve(catalog()) : api<any[]>('GET', 'api/admin/static/characters?limit=5000&search='),
      ]);
      setOwned(o ?? []); setCatalog((c ?? []).filter((x: any) => !/localizeerror/i.test(String(get(x, 'name'))) && Number(get(x, 'id')) < 19000));
    } catch (e) { props.notify(String(e), true); } finally { setBusy(false); }
  }
  async function act(command: string, msg: string) {
    const uid = toUid(props.uid, props.notify); if (!uid) return;
    setBusy(true);
    try { await runCommand(uid, command); props.notify(msg); await load(); }
    catch (e) { props.notify(String(e), true); } finally { setBusy(false); }
  }
  const FIELDS = (): [string, string, number][] => [
    ['level', t().lvl, 90], ['starGrade', t().star, 5], ['favorRank', t().bond, 100],
    ['exSkillLevel', 'EX', 5], ['publicSkillLevel', 'Normal', 10], ['passiveSkillLevel', t().passive, 10], ['extraPassiveSkillLevel', t().sub, 10],
  ];
  const openEdit = (r: any) => { setEditing(r); setForm(Object.fromEntries(FIELDS().map(([k]) => [k, Number(get(r, k) ?? 1)]))); };
  async function saveEdit() {
    const r = editing(); const uid = toUid(props.uid, props.notify); if (!r || !uid) return;
    setBusy(true);
    try { await api('POST', `api/admin/account/${uid}/characters/${get(r, 'uniqueId')}/update`, form()); props.notify(t().saved); setEditing(undefined); await load(); }
    catch (e) { props.notify(String(e), true); } finally { setBusy(false); }
  }
  onMount(() => { if (props.uid) void load(); });

  return <div class="grid grid-cols-[1.3fr_.7fr] gap-5">
    <section class="surface-card overflow-hidden">
      <div class="flex gap-3 border-b border-[#e3eef9] p-4">
        <div class="search-field"><i class="fa-solid fa-magnifying-glass"/><input placeholder={t().search} value={q()} onInput={(e) => setQ(e.currentTarget.value)}/></div>
        <button class="quiet-button" disabled={busy()} onClick={() => void load()}><span class="flex items-center gap-1.5"><i class="fa-solid fa-rotate"/> {t().reload}</span></button>
      </div>
      <div class="max-h-[600px] overflow-auto">
        <Show when={shown().length} fallback={<div class="empty-state"><img src="/game-ui/Image_Char_Arona2.png" alt=""/>{t().noChars}</div>}>
          <table class="data-table"><thead><tr><th>{t().name}</th><th>★</th><th>{t().lvl}</th><th>{t().bond}</th><th>EX / N / P / S</th><th/></tr></thead>
            <tbody><For each={shown()}>{(r) => <tr class="cursor-pointer" title={t().clickEdit} onClick={() => openEdit(r)}>
              <td><span class="flex items-center gap-2 font-bold"><Avatar id={get(r, 'uniqueId')} name={get(r, 'name')}/><span>{get(r, 'name')}<small class="block font-mono text-[10px] font-normal text-ink-500">#{get(r, 'uniqueId')}</small></span></span></td>
              <td class="whitespace-nowrap"><For each={[1, 2, 3, 4, 5]}>{(i) =>
                <button class="px-0.5 text-[14px] text-[#f5b800]" title={`${i}★`} disabled={busy()}
                  onClick={(e) => { e.stopPropagation(); void act(`character modify ${get(r, 'uniqueId')} star ${i}`, t().starSet); }}>{i <= Number(get(r, 'starGrade')) ? '★' : '☆'}</button>}</For></td>
              <td>{get(r, 'level')}</td>
              <td>{get(r, 'favorRank')}</td>
              <td class="font-mono text-[11px]">{get(r, 'exSkillLevel')} / {get(r, 'publicSkillLevel')} / {get(r, 'passiveSkillLevel')} / {get(r, 'extraPassiveSkillLevel')}</td>
              <td class="text-right"><button class="chip-btn" disabled={busy()} onClick={(e) => { e.stopPropagation(); void act(`max ${get(r, 'devName') || get(r, 'name')}`, `${get(r, 'name')}: ${t().maxed}`); }}>{t().maxOne}</button></td>
            </tr>}</For></tbody></table>
        </Show>
      </div>
    </section>

    <section class="surface-card flex flex-col p-5">
      <h3 class="card-title mb-3 flex items-center gap-2"><i class="fa-solid fa-user-plus text-ba-blue"/> {t().add}<span class="tiny-chip ml-auto">{t().count.replace('{n}', String(candidates().length)).replace('{t}', String(catalog().length))}</span></h3>
      <div class="mb-3 grid grid-cols-[1fr_110px] gap-2">
        <div class="search-field"><i class="fa-solid fa-magnifying-glass"/><input placeholder={t().addHint} value={addQ()} onInput={(e) => setAddQ(e.currentTarget.value)}/></div>
        <select class="input-field" value={preset()} onChange={(e) => setPreset(e.currentTarget.value)}>
          <For each={['max', 'ue50', 'ue30', 'basic', 'barebone']}>{(p) => <option>{p}</option>}</For>
        </select>
      </div>
      <div class="max-h-[520px] flex-1 overflow-auto">
        <For each={candidates()}>{(c) =>
          <button class="account-row" disabled={busy()} onClick={() => void act(`character add ${get(c, 'id')} ${preset()}`, `${t().added}: ${get(c, 'name')}`)}>
            <Avatar id={get(c, 'id')} name={get(c, 'name')}/>
            <span class="min-w-0 flex-1"><b class="block truncate text-[13px]">{get(c, 'name')}</b><small class="text-[11px] text-ink-500">#{get(c, 'id')} · ★{get(c, 'maxStar')}</small></span>
            <i class="fa-solid fa-plus text-ba-blue"/>
          </button>}</For>
      </div>
    </section>

    <Show when={editing()}>{(r) =>
      <div class="modal-backdrop" onClick={() => setEditing(undefined)}>
        <div class="surface-card w-[560px] overflow-hidden" onClick={(e) => e.stopPropagation()}>
          <div class="arona-hero flex items-center gap-4 !rounded-none p-5">
            <img src={`https://schaledb.com/images/student/collection/${get(r(), 'uniqueId')}.webp`} alt="" class="h-24 w-24 rounded-md border-2 border-white object-cover shadow" onError={(e) => { e.currentTarget.style.visibility = 'hidden'; }}/>
            <div><div class="label-sm text-ba-blue">{t().edit}</div><h3 class="section-title">{get(r(), 'name')}</h3><div class="font-mono text-[11px] text-ink-500">#{get(r(), 'uniqueId')}</div></div>
          </div>
          <div class="grid grid-cols-2 gap-x-3 p-5">
            <For each={FIELDS()}>{([k, label, max]) =>
              <label class="field-label">{label} <span class="normal-case tracking-normal text-ink-300">(1–{max})</span>
                <input class="input-field" type="number" min="1" max={max} value={form()[k]} onInput={(e) => setForm({ ...form(), [k]: Number(e.currentTarget.value) })}/></label>}</For>
          </div>
          <div class="flex justify-end gap-3 border-t border-[#e3eef9] p-4">
            <button class="yellow-button" onClick={() => setForm(Object.fromEntries(FIELDS().map(([k, , m]) => [k, m])))}><span>{t().allMax}</span></button>
            <button class="secondary-button" onClick={() => setEditing(undefined)}><span>{t().cancel}</span></button>
            <button class="primary-button" disabled={busy()} onClick={() => void saveEdit()}><span>{t().save}</span></button>
          </div>
        </div>
      </div>}</Show>
  </div>;
}
