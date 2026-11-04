import { GameClock, paceName } from './clock';
import { mapLight } from './daylight';
import './style.css';
import { Connection, type Msg } from './net';
import { City, curveCells, depthNote, lCells, lineCells as diagonalCells, planPath, padSteps, PAD_COST_PER_STEP, SIDE_DX, SIDE_DY, type BuildingType } from './state';
import { Renderer, type OverlayView } from './render';
import { TalkBack } from './talkback';
import { Seats } from './seats';
import { Transport } from './transport';
import { Emergency } from './emergency';
import { TickClock } from './dispatch';
import { Region } from './region';
import { BuildMenuState, DISASTER_NAMES, TERRAFORM_NAMES, TREE_CHOICES, TREES_PER_CELL, icon, menuFor, type BuildCategory, type BuildItem } from './buildmenu';

// ---------------------------------------------------------------------------
// Tools
// ---------------------------------------------------------------------------

type Tool =
  | { kind: 'query' }
  | { kind: 'bulldoze' }
  | { kind: 'path' }
  | { kind: 'zone'; zone: number }
  | { kind: 'dezone' }
  | { kind: 'utility'; utility: number }
  | { kind: 'building' }
  | { kind: 'terraform' }
  | { kind: 'river' }
  | { kind: 'dam' }
  | { kind: 'trees' }
  | { kind: 'disaster' };

const DISASTER_SETTINGS = ['Off', 'Rare', 'Normal'];

const DENSITY_NAMES = ['Low', 'Medium', 'High'];
/** The cost line's grading part, or nothing on ground that needs none. */
const grading = (dollars: number) => (dollars > 0 ? ` (grading ${money(dollars)})` : '');
const ZONE_COST = 5;

const OVERLAYS: { kind: number; name: string; scheme: OverlayView['scheme'] }[] = [
  { kind: 1, name: 'Land value', scheme: 'good' },
  { kind: 2, name: 'Desirability R', scheme: 'diverging' },
  { kind: 3, name: 'Desirability C', scheme: 'diverging' },
  { kind: 4, name: 'Desirability I', scheme: 'diverging' },
  { kind: 5, name: 'Pollution', scheme: 'bad' },
  { kind: 6, name: 'Water pollution', scheme: 'bad' },
  { kind: 7, name: 'Power', scheme: 'binary' },
  { kind: 8, name: 'Water', scheme: 'binary' },
  { kind: 9, name: 'Traffic', scheme: 'bad' },
  { kind: 10, name: 'Noise', scheme: 'bad' },
  { kind: 11, name: 'Police', scheme: 'good' },
  { kind: 12, name: 'Fire', scheme: 'good' },
  { kind: 13, name: 'Health', scheme: 'good' },
  { kind: 14, name: 'Education', scheme: 'good' },
  { kind: 15, name: 'Parks', scheme: 'good' },
  { kind: 16, name: 'Commute', scheme: 'bad' },
  { kind: 17, name: 'Elevation', scheme: 'good' },
  { kind: 18, name: 'Litter', scheme: 'bad' },
  { kind: 19, name: 'Run-down', scheme: 'bad' },
  { kind: 20, name: 'Transit riders', scheme: 'good' },
  { kind: 21, name: 'Water quality', scheme: 'bad' },
];

/** The sewage plants' treatment levels, as SetTreatment numbers them (Beta 4, part 3). */
const TREATMENTS = ['None', 'Primary', 'Secondary', 'Tertiary'];

const $ = <T extends HTMLElement = HTMLElement>(sel: string) => document.querySelector(sel) as T;
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
const money = (n: number) => (n < 0 ? '-$' : '$') + Math.abs(Math.round(n)).toLocaleString('en-US');

const city = new City();
const canvas = $<HTMLCanvasElement>('#map');
const renderer = new Renderer(canvas, city);

let tool: Tool = { kind: 'query' };
let density = 0;
let profileId = 'road';
/** The surface paths are built in; '' for the type's default (Beta 3.2). */
let surfaceId = '';
/** Paths tunnel under hills where they'd run 8 m or more deep, rather than cutting through. */
let tunnel = false;
let buildingTypeId = 'police-station';
let terraformMode = 0;
/** The tree planter's plant kind (Beta 4.1). */
let treeKind = 0;
/** The river brush's channel, cells wide and steps deep, and how many steps a dam holds the river up. */
let riverWidth = 2, riverDepth = 2, damRise = 3;
/** As the sim: the longest stroke one dig takes, its cost a cell a step, and the longest dam and its costs. */
const DIG_LONGEST = 64, DIG_COST = 60, DAM_LONGEST = 40, DAM_COST = 800, DAM_COST_STEP = 300;
let disasterKind = 0;
let pathMode: 'straight' | 'diagonal' | 'curve' = 'straight';
/** Curve tool: the ends are set, the mouse now bends it. */
let curveEnds: { x0: number; y0: number; x1: number; y1: number } | null = null;
let zoneMode: 'area' | 'block' = 'area';
let leftover = 0;
const LEFTOVER_NAMES = ['Leave empty', 'Pocket parks', 'Zone anyway'];
let drag: { x: number; y: number } | null = null;
let shiftHeld = false;
let saveNames: string[] = [];
let activeOverlay = 0;
let lastOverlayMonth = -1;
let queried: { x: number; y: number } | null = null;
let queriedBuilding: Msg | null = null;
let dirty = true;
let activeTab = 'why';
// Created after the connection; messages that arrive first are handled once it exists.
let talk: TalkBack | null = null;
let seats: Seats | null = null;
let transport: Transport | null = null;
let emergency: Emergency | null = null;
let region: Region | null = null;
/** The followed commuters and this machine's tiers, as the server last sent them. */
let roster: Msg | null = null;

const conn = new Connection({
  onStatus(ok) {
    $('#conn').classList.toggle('off', !ok);
    transport?.onCityTick();
    emergency?.onCityTick();
    if (!ok) toast('Lost connection to the server. Reconnecting…', 'error');
  },
  onSignIn(welcome, error) {
    showSignIn(welcome, error);
  },
  onMessage(kind, body, replyTo) {
    if (kind === 'civic') seats?.onCivic(body);
    if (talk?.onMessage(kind, body, replyTo)) return;
    switch (kind) {
      case 'signedIn':
        if (dlg.open && signInOpen) dlg.close('signed-in');
        toast(`Signed in as ${body.name}.`, 'good');
        updateAdmin();
        break;
      case 'welcome':
        updateAdmin();
        break;
      case 'presence':
        renderPresence(body);
        break;
      case 'delinkCopy': {
        // The copy this player took when they de-linked: offered as a file, and kept in the server's saves too.
        const url = URL.createObjectURL(new Blob([body.save], { type: 'application/octet-stream' }));
        const a = document.createElement('a');
        a.href = url;
        a.download = `${body.name}.toycity`;
        a.click();
        setTimeout(() => URL.revokeObjectURL(url), 10_000);
        toast(`Your copy of the city is saved as "${body.name}". Load it on your own server to play it.`, 'good');
        break;
      }
      case 'full': {
        // A different city (moved within the region, or loaded): centre on it like the first time.
        const first = !city.loaded || city.width !== body.width || city.stats.cityName !== body.stats?.cityName;
        if (busyKind === 'new' || busyKind === 'load') busy(null);
        region?.onFull();
        city.applyFull(body);
        if (first) {
          renderer.scale = Math.max(4, Math.min(12, Math.floor(Math.min(canvas.clientWidth, canvas.clientHeight) / city.width) * 1.6));
          renderer.centerOn(city.width / 2, city.height / 2);
        }
        buildToolbar();
        updateTopBar();
        renderBudget();
        if (activeOverlay) requestOverlay();
        dirty = true;
        break;
      }
      case 'delta':
        city.applyDelta(body);
        for (const n of body.notices as Msg[]) toast(n.text, n.severity, n.x, n.y);
        updateTopBar();
        transport?.onCityTick();
    emergency?.onCityTick();
        if (city.stats.day === 1) {
          renderBudget();
          if (queried) query(queried.x, queried.y, false);
          talk?.onMonth(activeTab);
        }
        if (activeOverlay && city.stats.month !== lastOverlayMonth) requestOverlay();
        dirty = true;
        break;
      case 'why':
        renderWhy(body);
        break;
      case 'inspect':
        queriedBuilding = body.building ?? null;
        renderInspect(body);
        break;
      case 'overlay': {
        const o = OVERLAYS.find((x) => x.kind === body.kind);
        if (o && body.kind === activeOverlay) renderer.overlay = { kind: body.kind, values: new Uint8Array(body.values), scheme: o.scheme };
        renderLayers(body.legendLow, body.legendHigh);
        dirty = true;
        break;
      }
      case 'saves':
        saveNames = body.names;
        if (busyKind === 'save' || busyKind === 'list') busy(null);
        break;
      case 'agents':
        roster = body;
        renderer.agents = body.trips;
        if (activeTab === 'layers') renderLayers();
        dirty = true;
        break;
      case 'error':
        toast(body.message, 'error');
        busy(null);
        emergency?.refused();
        transport?.refused();
        region?.refused();
        break;
      case 'region':
        region?.onRegion(body);
        break;
      case 'redirect': {
        // That city runs on another host of the region: open it there, signing in with the same account.
        toast('That city is on another host. Moving there…', 'info');
        const q = new URLSearchParams(location.search);
        q.set('server', body.address);
        q.set('tile', `${body.col},${body.row}`);
        location.search = q.toString();
        break;
      }
      case 'sideGame':
        transport?.onView(body);
        emergency?.onView(body);
        dirty = true;
        break;
    }
  },
});

// ---------------------------------------------------------------------------
// Top bar
// ---------------------------------------------------------------------------

// Both clocks, the hands running on between ticks (Beta 3.2).
const gameClock = new GameClock();
/** The minute units drive by: smooth across each day's step (Beta 4, direct dispatch). */
const unitClock = new TickClock();
const unitMinute = () => unitClock.now(Number(city.stats.clockMinute ?? 0), city.stats.clockMinutesPerTick ?? 0, city.stats.speed ?? 0);

function updateClock() {
  const s = city.stats;
  if (!s) return;
  const text = gameClock.text(s) + (s.weather ? ` · ${s.weather}` : '');
  const el = $('#date');
  if (el.textContent !== text) el.textContent = text;
  el.title = `Day ${s.day} of the month, ${s.year}. The calendar runs at ${paceName(s.pace || 30).toLowerCase()} of the clock.`;
  const pace = $<HTMLSelectElement>('#pace');
  const want = String(s.pace || 30);
  if (pace.value !== want && document.activeElement !== pace) pace.value = want;
}
setInterval(updateClock, 250);

function updateTopBar() {
  const s = city.stats;
  $('#city-name').textContent = s.cityName;
  updateClock();
  const f = $('#funds');
  f.textContent = money(s.funds);
  f.classList.toggle('neg', s.funds < 0);
  const net = (s.lastMonthIncome ?? 0) - (s.lastMonthExpenses ?? 0);
  const fc = $('#funds-change');
  fc.textContent = s.lastMonthIncome || s.lastMonthExpenses ? `${net >= 0 ? '+' : '−'}${money(Math.abs(net))}/mo` : '';
  fc.className = `lb-change ${net >= 0 ? 'up' : 'down'}`;
  $('#pop').textContent = s.population.toLocaleString('en-US');
  document.querySelectorAll<HTMLButtonElement>('#speed button').forEach((b) => b.classList.toggle('active', Number(b.dataset.speed) === s.speed));
  const d: number[] = s.demand ?? [];
  const raw: number[] = s.demandRaw?.length ? s.demandRaw : d;
  const bars = ['r', 'c', 'i'];
  bars.forEach((cls, z) => {
    const place = (el: HTMLElement, v: number) => {
      const h = Math.abs(v) / 2;
      el.style.height = `${h}%`;
      el.style.top = v >= 0 ? `${50 - h}%` : '50%';
    };
    const v = Math.max(d[z * 3], d[z * 3 + 1], d[z * 3 + 2]);
    const rv = Math.max(raw[z * 3], raw[z * 3 + 1], raw[z * 3 + 2]);
    const el = $(`#rci .${cls}`);
    place(el, v);
    el.style.opacity = v >= 0 ? '1' : '0.45';
    // The outline shows demand before limits such as power: the gap is demand the city can't use yet.
    place($(`#rci .ghost-${cls}`), rv > v ? rv : v);
  });
  const tags = ['R$', 'R$$', 'R$$$', 'C$', 'C$$', 'C$$$', 'I dirty', 'I manufacturing', 'I high-tech'];
  $('#rci').title = 'Demand (up means the city wants more). Click for where it comes from.\n' +
    tags.map((t, k) => `${t}: ${d[k] > 0 ? '+' : ''}${d[k]}${raw[k] > d[k] ? ` (limited from ${raw[k] > 0 ? '+' : ''}${raw[k]})` : ''}`).join('\n');
}

document.querySelectorAll<HTMLButtonElement>('#speed button').forEach((b) =>
  b.addEventListener('click', () => conn.send({ speed: { speed: Number(b.dataset.speed) } })),
);
$<HTMLSelectElement>('#pace').addEventListener('change', (e) => {
  const pace = Number((e.target as HTMLSelectElement).value);
  conn.send({ pace: { daysPerClockDay: pace } });
  toast(`Asked for the calendar at ${paceName(pace).toLowerCase()} of the clock.`, 'info');
});

// ---------------------------------------------------------------------------
// Toolbar
// ---------------------------------------------------------------------------

function setTool(t: Tool) {
  tool = t;
  drag = null;
  if (t.kind !== 'path') curveEnds = null;
  // Subway tunnels are drawn where they run, under the ground.
  if (t.kind === 'utility' && t.utility === 2) renderer.underground = true;
  renderer.preview = null;
  buildToolbar();
  updateHints();
  dirty = true;
}

/** The surfaces the chosen path type may be built in, each with the grade it climbs (Beta 3.2). */
function surfaceOptions(): string {
  const prof = city.profiles.find((p) => p.id === profileId);
  const list = prof?.surfaces ?? [];
  if (!prof || list.length === 0) return `<option value="">${prof ? `Climbs ${prof.maxGradePct ?? 0}%` : ''}</option>`;
  return list
    .map((id, k) => {
      const s = city.surfaces.find((q) => q.id === id);
      const pct = prof.stairs ? `ramp ${s?.maxGradePct ?? 0}%, stairs to ${prof.maxGradePct}%` : `${Math.min(prof.maxGradePct, s?.maxGradePct ?? 100)}%`;
      const value = k === 0 ? '' : id;
      return `<option value="${value}" ${value === surfaceId ? 'selected' : ''}>${esc(s?.name ?? id)} (${pct})</option>`;
    })
    .join('');
}

/** " in brick" when paths are built in a surface other than the type's default. */
function surfaceLabel(): string {
  if (!surfaceId) return '';
  const s = city.surfaces.find((q) => q.id === surfaceId);
  return s ? ` in ${s.name.toLowerCase()}` : '';
}

// The build rail and its flyouts (Beta 4, part 2, variation A): the same menu as the Godot client's.
const buildMenu = new BuildMenuState();
let menu: BuildCategory[] = [];

function itemCurrent(i: BuildItem): boolean {
  switch (i.kind) {
    case 'zone': return tool.kind === 'zone' && tool.zone === i.zone && density === i.density;
    case 'path': return tool.kind === 'path' && profileId === i.profile;
    case 'utility': return tool.kind === 'utility' && tool.utility === i.utility;
    case 'building': return tool.kind === 'building' && buildingTypeId === i.type;
    case 'terraform': return tool.kind === 'terraform' && terraformMode === i.terraform;
    case 'disaster': return tool.kind === 'disaster' && disasterKind === i.disaster;
    case 'trees': return tool.kind === 'trees' && treeKind === i.treeKind;
    default: return tool.kind === i.kind;
  }
}

/** A tile: sets the tool it stands for, and the flyout closes unless it's pinned. */
function pickItem(i: BuildItem) {
  if (i.locked) { toast(`${i.name}: ${i.locked === 'Built' ? 'there can only be one, and it’s built' : i.locked}`, 'warning'); return; }
  buildMenu.picked();
  switch (i.kind) {
    case 'zone': density = i.density!; setTool({ kind: 'zone', zone: i.zone! }); break;
    case 'path': if (profileId !== i.profile) surfaceId = ''; profileId = i.profile!; setTool({ kind: 'path' }); break;
    case 'utility': setTool({ kind: 'utility', utility: i.utility! }); break;
    case 'building': buildingTypeId = i.type!; setTool({ kind: 'building' }); break;
    case 'terraform': terraformMode = i.terraform!; setTool({ kind: 'terraform' }); break;
    case 'disaster': disasterKind = i.disaster!; setTool({ kind: 'disaster' }); break;
    case 'trees': treeKind = i.treeKind ?? 0; setTool({ kind: 'trees' }); break;
    default: setTool({ kind: i.kind } as Tool);
  }
}

function closeFlyout() {
  if (!buildMenu.open || buildMenu.pinned) return;
  buildMenu.open = null;
  buildToolbar();
}

function seg(attr: string, options: [string, string, string?][], current: string): string {
  return `<div class="seg">${options.map(([v, label, title]) => `<button data-${attr}="${v}" class="${v === current ? 'active' : ''}" ${title ? `title="${esc(title)}"` : ''}>${label}</button>`).join('')}</div>`;
}

/** The tool in hand, and its options, along the top of the map. */
function toolOptions(): string {
  const named = (ic: string, name: string, price = '') => `<span class="opt-name">${icon(ic, 18)}${esc(name)}${price ? ` <span class="tile-price">${esc(price)}</span>` : ''}</span>`;
  switch (tool.kind) {
    case 'path': {
      const p = city.profiles.find((q) => q.id === profileId);
      return named(p?.rail || p?.tram ? 'tram' : 'road', p?.name ?? 'Path', p ? `$${p.costPerCell} / cell` : '') +
        `<select id="sel-surface" title="Surface: sets how steep it may climb">${surfaceOptions()}</select>` +
        seg('tunnel', [['0', 'Cut through', 'Grade through hills, however deep the cut'], ['1', 'Tunnel', 'Go under a hill where the road would run 8 m or more deep']], tunnel ? '1' : '0') +
        seg('pathmode', [['straight', 'Straight', 'Straight or L-shaped runs'], ['diagonal', 'Diagonal', 'Smooth diagonals at any angle (A cycles)'], ['curve', 'Curve', 'Arcs snapped to the grid (A cycles)']], pathMode);
    }
    case 'zone':
      return named('zones', ['', 'Residential', 'Commercial', 'Industrial'][tool.zone]) +
        seg('density', DENSITY_NAMES.map((d, k) => [String(k), d, `${d} density (V cycles)`] as [string, string, string]), String(density)) +
        seg('zonemode', [['area', 'Area', 'Drag a rectangle'], ['block', 'Block', 'Click inside a block enclosed by roads to zone it lot-deep (G toggles)']], zoneMode) +
        (zoneMode === 'block' ? `<label title="Cells deeper in the block than any lot can reach">Leftover<select id="sel-leftover">${LEFTOVER_NAMES.map((n, k) => `<option value="${k}" ${k === leftover ? 'selected' : ''}>${n}</option>`).join('')}</select></label>` : '');
    case 'dezone': return named('bulldoze', 'Dezone');
    case 'bulldoze': return named('bulldoze', 'Bulldoze');
    case 'utility': return named(['power', 'water', 'subway'][tool.utility], ['Power line', 'Water and sewer pipe', 'Subway tunnel'][tool.utility], ['$5 / cell', '$3 / cell', '$150 / cell'][tool.utility]);
    case 'building': {
      const t = city.typeById.get(buildingTypeId);
      return named('civic', t?.name ?? 'Building', t?.cost ? `$${t.cost.toLocaleString('en-US')}` : '');
    }
    case 'terraform':
      return named('land', 'Terraform') + seg('terraform', TERRAFORM_NAMES.map((n, k) => [String(k), n] as [string, string]), String(terraformMode));
    case 'river':
      return named('water', 'River brush') +
        seg('rwidth', [1, 2, 3, 4, 5].map((k) => [String(k), `${16 * k} m`, `${k} cell${k > 1 ? 's' : ''} wide`] as [string, string, string]), String(riverWidth)) +
        seg('rdepth', [1, 2, 3, 4].map((k) => [String(k), `${2 * k} m`, `${k} step${k > 1 ? 's' : ''} deep`] as [string, string, string]), String(riverDepth));
    case 'dam':
      return named('water', 'Dam') + seg('rise', [1, 2, 3, 4, 5, 6, 7, 8].map((k) => [String(k), `${2 * k} m`, `Holds the river ${2 * k} m over its level`] as [string, string, string]), String(damRise));
    case 'trees':
      return named('parks', 'Tree planter', `$${TREES_PER_CELL} / cell`) + seg('treekind', TREE_CHOICES.map(([n, k]) => [String(k), n] as [string, string]), String(treeKind));
    case 'disaster':
      return named('disasters', 'Disaster') + seg('disaster', DISASTER_NAMES.map((n, k) => [String(k), n] as [string, string]), String(disasterKind)) +
        `<label title="How often disasters happen by themselves. The sandbox tool works either way.">By themselves<select id="sel-disaster-setting">${DISASTER_SETTINGS.map((n, k) => `<option value="${k}" ${k === (city.stats.disasterSetting ?? 1) ? 'selected' : ''}>${n}</option>`).join('')}</select></label>`;
    default: return '';
  }
}

function buildToolbar() {
  menu = menuFor(city);
  const lit = menu.find((c) => c.groups.some((g) => g.items.some(itemCurrent)))?.id;
  // The rail: query, then a button for each category.
  const rail = $('#rail');
  rail.innerHTML =
    `<button class="icon-btn ${tool.kind === 'query' ? 'active' : ''}" data-rail="query" title="Query (Q): click anything to see why it is the way it is">${icon('query', 22)}<kbd>Q</kbd></button><div class="rail-sep"></div>` +
    menu.map((c) => `<button class="icon-btn ${buildMenu.open === c.id ? 'active' : ''} ${lit === c.id ? 'lit' : ''}" data-rail="${c.id}" title="${esc(c.name)}${c.key ? ` (${c.key})` : ''}">${icon(c.icon, 22)}${c.key ? `<kbd>${c.key}</kbd>` : ''}</button>`).join('');
  rail.querySelectorAll<HTMLButtonElement>('button[data-rail]').forEach((b) =>
    b.addEventListener('click', () => {
      const id = b.dataset.rail!;
      if (id === 'query') { buildMenu.open = null; setTool({ kind: 'query' }); return; }
      if (id === 'bulldoze') { buildMenu.open = null; setTool({ kind: 'bulldoze' }); return; }
      buildMenu.toggle(id);
      buildToolbar();
    }),
  );

  // The flyout: title, what it's for, the pin, the group tabs and the tiles with their prices.
  const fly = $('#flyout');
  const shown = buildMenu.shown(menu);
  fly.classList.toggle('hidden', !shown);
  if (shown) {
    const { category: c, group } = shown;
    const items = c.groups[group].items;
    fly.innerHTML = `
      <div class="fly-head"><div class="fly-icon">${icon(c.icon, 20)}</div><h3>${esc(c.name)}</h3>
        <button class="icon-btn ${buildMenu.pinned ? 'active' : ''}" data-fly="pin" title="${buildMenu.pinned ? 'Pinned open: click to let it close after a pick' : 'Pin it open'}">${icon('pin', 16)}</button>
        <button class="icon-btn" data-fly="close" title="Close">${icon('close', 16)}</button></div>
      <p class="fly-desc">${esc(c.description)}</p>
      ${c.groups.length > 1 ? `<div class="fly-tabs">${c.groups.map((g, k) => `<button data-group="${k}" class="${k === group ? 'active' : ''}">${esc(g.name)}</button>`).join('')}</div>` : ''}
      <div class="fly-tiles">${items.map((i, k) => `<button class="tile ${itemCurrent(i) ? 'active' : ''} ${i.locked ? 'locked' : ''}" data-item="${k}" title="${esc(i.name)}${i.locked ? ` (${esc(i.locked)})` : ''}">
        <span class="tile-ic"${i.kind === 'zone' ? ` style="color:${['', 'var(--r)', 'var(--c)', 'var(--i)'][i.zone!]}"` : ''}>${icon(i.icon, 24)}</span>
        <span class="tile-name">${esc(i.name)}</span>
        <span class="tile-price">${i.locked ? `🔒 ${esc(i.locked)}` : esc(i.price)}</span></button>`).join('')}</div>`;
    fly.querySelector('[data-fly="pin"]')!.addEventListener('click', () => { buildMenu.pinned = !buildMenu.pinned; buildToolbar(); });
    fly.querySelector('[data-fly="close"]')!.addEventListener('click', () => { buildMenu.open = null; buildToolbar(); });
    fly.querySelectorAll<HTMLButtonElement>('button[data-group]').forEach((b) => b.addEventListener('click', () => { buildMenu.group = Number(b.dataset.group); buildToolbar(); }));
    fly.querySelectorAll<HTMLButtonElement>('button[data-item]').forEach((b) => b.addEventListener('click', () => pickItem(items[Number(b.dataset.item)])));
  }

  // The tool in hand and its options.
  const el = $('#toolopts');
  const opts = toolOptions();
  el.classList.toggle('hidden', !opts);
  el.innerHTML = opts;
  el.querySelectorAll<HTMLButtonElement>('button[data-density]').forEach((b) =>
    b.addEventListener('click', () => { density = Number(b.dataset.density); buildToolbar(); updateHints(); }),
  );
  el.querySelectorAll<HTMLButtonElement>('button[data-treekind]').forEach((b) =>
    b.addEventListener('click', () => { treeKind = Number(b.dataset.treekind); setTool({ kind: 'trees' }); }),
  );
  el.querySelectorAll<HTMLButtonElement>('button[data-terraform]').forEach((b) =>
    b.addEventListener('click', () => { terraformMode = Number(b.dataset.terraform); setTool({ kind: 'terraform' }); }),
  );
  el.querySelectorAll<HTMLButtonElement>('button[data-rwidth]').forEach((b) =>
    b.addEventListener('click', () => { riverWidth = Number(b.dataset.rwidth); setTool({ kind: 'river' }); }),
  );
  el.querySelectorAll<HTMLButtonElement>('button[data-rdepth]').forEach((b) =>
    b.addEventListener('click', () => { riverDepth = Number(b.dataset.rdepth); setTool({ kind: 'river' }); }),
  );
  el.querySelectorAll<HTMLButtonElement>('button[data-rise]').forEach((b) =>
    b.addEventListener('click', () => { damRise = Number(b.dataset.rise); setTool({ kind: 'dam' }); }),
  );
  el.querySelectorAll<HTMLButtonElement>('button[data-disaster]').forEach((b) =>
    b.addEventListener('click', () => { disasterKind = Number(b.dataset.disaster); setTool({ kind: 'disaster' }); }),
  );
  el.querySelectorAll<HTMLButtonElement>('button[data-tunnel]').forEach((b) =>
    b.addEventListener('click', () => { tunnel = b.dataset.tunnel === '1'; setTool({ kind: 'path' }); }),
  );
  el.querySelector<HTMLSelectElement>('#sel-surface')?.addEventListener('change', (e) => { surfaceId = (e.target as HTMLSelectElement).value; setTool({ kind: 'path' }); });
  el.querySelectorAll<HTMLButtonElement>('button[data-pathmode]').forEach((b) =>
    b.addEventListener('click', () => { pathMode = b.dataset.pathmode as typeof pathMode; curveEnds = null; setTool({ kind: 'path' }); }),
  );
  el.querySelectorAll<HTMLButtonElement>('button[data-zonemode]').forEach((b) =>
    b.addEventListener('click', () => { zoneMode = b.dataset.zonemode as typeof zoneMode; setTool(tool.kind === 'zone' ? tool : { kind: 'zone', zone: 1 }); }),
  );
  el.querySelector<HTMLSelectElement>('#sel-leftover')?.addEventListener('change', (e) => { leftover = Number((e.target as HTMLSelectElement).value); dirty = true; });
  el.querySelector<HTMLSelectElement>('#sel-disaster-setting')?.addEventListener('change', (e) => {
    const setting = Number((e.target as HTMLSelectElement).value);
    act({ disasters: { setting } }, `Disasters: ${DISASTER_SETTINGS[setting].toLowerCase()}`);
  });
}

/** Lower-left key hints for the current tool (Fortnite style). */
function updateHints() {
  const common: [string, string][] = [['Wheel', 'zoom'], ['Right-drag', 'pan'], ['U', renderer.underground ? 'leave underground view' : 'underground view'], ['M', 'commuters'], ['Space', 'pause']];
  const per: Record<Tool['kind'], [string, string][]> = {
    query: [['Click', 'ask why']],
    bulldoze: [['Drag', renderer.underground ? 'remove pipes in area' : 'clear area'], ['Esc', 'cancel']],
    path: pathMode === 'curve'
      ? (curveEnds ? [['Move', 'bend the curve'], ['Click', 'build it'], ['Esc', 'start again']] : [['Drag', 'set the two ends'], ['A', 'straight paths'], ['Esc', 'cancel']])
      : pathMode === 'diagonal'
        ? [['Drag', 'draw a road at any angle'], ['A', 'curved paths'], ['Esc', 'cancel']]
        : [['Drag', 'draw path'], ['Shift', 'vertical leg first'], ['A', 'diagonal paths'], ['Esc', 'cancel']],
    zone: zoneMode === 'block'
      ? [['Click', `zone a whole block, ${DENSITY_NAMES[density].toLowerCase()} density`], ['G', 'paint areas'], ['V', 'cycle density'], ['Esc', 'cancel']]
      : [['Drag', `paint ${DENSITY_NAMES[density].toLowerCase()} density zone`], ['G', 'zone whole blocks'], ['V', 'cycle density'], ['Esc', 'cancel']],
    dezone: [['Drag', 'remove zoning'], ['Esc', 'cancel']],
    utility: [['Drag', 'draw line'], ['Shift', 'vertical leg first'], ['Esc', 'cancel']],
    building: [['Click', 'place'], ['Esc', 'cancel']],
    terraform: [['Drag', `${TERRAFORM_NAMES[terraformMode].toLowerCase()} area`], ['Esc', 'cancel']],
    river: [['Drag', 'dig a river from where its water starts'], ['Esc', 'cancel']],
    dam: [['Drag', 'wall a river, bank to bank'], ['Esc', 'cancel']],
    trees: [['Drag', 'plant trees on open land'], ['Esc', 'cancel']],
    disaster: [['Click', `start a ${DISASTER_NAMES[disasterKind].toLowerCase()} here`], ['Esc', 'cancel']],
  };
  $('#hints').innerHTML = [...per[tool.kind], ...common].map(([k, v]) => `<div><kbd>${k}</kbd>${v}</div>`).join('');
}

// ---------------------------------------------------------------------------
// Map input
// ---------------------------------------------------------------------------

let panning: { sx: number; sy: number; ox: number; oy: number } | null = null;
/** Direct dispatch: a left press on the map (a click, or a box once dragged) and where a right press began. */
let unitPress: { sx: number; sy: number; moved: boolean } | null = null;
let rightFrom: { sx: number; sy: number } | null = null;

/** The unit under a screen point, as drawn: within its marker, or a cell and a half. */
function unitUnder(sx: number, sy: number): number | null {
  if (!emergency) return null;
  const reach = Math.max(1.5, 12 / renderer.scale);
  return emergency.map.unitAt((sx - renderer.offX) / renderer.scale, (sy - renderer.offY) / renderer.scale, unitMinute(), reach);
}

function localXY(e: MouseEvent) {
  const r = canvas.getBoundingClientRect();
  return { sx: e.clientX - r.left, sy: e.clientY - r.top };
}

function footprintAt(x: number, y: number) {
  const t = city.typeById.get(buildingTypeId);
  const w = t?.width ?? 1;
  const h = t?.height ?? 1;
  return { x: x - Math.floor((w - 1) / 2), y: y - Math.floor((h - 1) / 2), w, h };
}

function lineCells(x0: number, y0: number, x1: number, y1: number, verticalFirst: boolean) {
  return verticalFirst ? [...lCells(x0, y0, x0, y1), ...lCells(x0, y1, x1, y1).slice(1)] : lCells(x0, y0, x1, y1);
}

/**
 * Where a roadside stop goes for the cursor at screen (sx, sy) over a cell: on the kerb of the road under it nearest
 * the cursor, or on the kerb of a road beside clicked land, facing it. Mirrors World.CheckKerb.
 */
function kerbSpot(t: BuildingType, cell: { x: number; y: number }, sx: number, sy: number): { x: number; y: number; side: number; why: string } {
  const carries = (x: number, y: number) => {
    if (!city.inBounds(x, y)) return false;
    const p = city.profileAt(y * city.width + x);
    return !!p && (t.transit === 'bus' ? p.cars : t.transit === 'tram' ? !!p.tram : t.transit === 'rail' ? p.rail : true);
  };
  const kerb = (x: number, y: number, k: number) => {
    const nx = x + SIDE_DX[k], ny = y + SIDE_DY[k];
    return !city.inBounds(nx, ny) || !city.paths[ny * city.width + nx];
  };
  const { x: fx, y: fy } = renderer.cellFrac(sx, sy);
  const edge = [fy, 1 - fx, 1 - fy, fx];  // the cursor's distance to the north, east, south and west edges
  const order = [0, 1, 2, 3].sort((a, b) => edge[a] - edge[b]);
  const what = t.transit === 'bus' ? 'a road buses can use' : t.transit === 'tram' ? 'a tram line' : t.transit === 'rail' ? 'a rail line' : 'a road';
  let { x, y } = cell;
  let side = -1;
  if (carries(x, y)) side = order.find((k) => kerb(x, y, k)) ?? -1;
  else if (city.inBounds(x, y) && !city.paths[y * city.width + x]) {
    // Beside a road: the stop faces the land, on the road toward the cursor's nearest edge.
    const k = order.find((k) => carries(x + SIDE_DX[k], y + SIDE_DY[k]));
    if (k !== undefined) { x += SIDE_DX[k]; y += SIDE_DY[k]; side = (k + 2) % 4; }
  }
  if (side < 0) return { x, y, side: 0, why: carries(x, y) ? 'no kerb here: roads join on every side' : `must go beside ${what}` };
  if (city.stopsAt(x, y).some((b) => b.side === side)) return { x, y, side, why: 'a stop is already on that side' };
  return { x, y, side, why: '' };
}

/** Updates the tool preview and the cost tag under the cursor. */
/**
 * A road drawn to the map's edge near a neighbouring city's road snaps onto it, so the two meet cell for cell: within
 * two cells of the edge and three along it.
 */
function borderSnap(cell: { x: number; y: number }): { x: number; y: number } {
  if (tool.kind !== 'path' || !city.loaded) return cell;
  let best: { x: number; y: number; d: number } | null = null;
  for (const b of renderer.borders) {
    const along = b.side === 0 || b.side === 2;
    const out = b.side === 0 ? cell.y : b.side === 2 ? city.height - 1 - cell.y : b.side === 1 ? city.width - 1 - cell.x : cell.x;
    if (out > 2) continue;
    const pos = along ? cell.x : cell.y;
    for (let j = 0; j + 1 < b.paths.length; j += 2) {
      const k = b.paths[j], d = Math.abs(k - pos);
      if (d > 3 || (best && best.d <= d)) continue;
      const x = along ? k : b.side === 1 ? city.width - 1 : 0;
      const y = along ? (b.side === 0 ? 0 : city.height - 1) : k;
      best = { x, y, d };
    }
  }
  return best ? { x: best.x, y: best.y } : cell;
}

function updatePreview(cell: { x: number; y: number }, sx: number, sy: number) {
  cell = borderSnap(cell);
  renderer.hover = city.inBounds(cell.x, cell.y) ? cell : null;
  const costEl = $('#cost');
  let text = '';
  let ok = true;
  renderer.preview = null;
  if (tool.kind === 'building' && city.typeById.get(buildingTypeId)?.roadside) {
    const t = city.typeById.get(buildingTypeId)!;
    const k = kerbSpot(t, cell, sx, sy);
    renderer.preview = { kind: 'cell', x0: k.x, y0: k.y, x1: k.x, y1: k.y, ok: !k.why, color: t.color + 'dd', catchment: t.catchment || undefined, side: k.side };
    text = `${t.name}: ${money(t.cost)} · ${k.why || `${['north', 'east', 'south', 'west'][k.side]} side of the road, serves ${t.catchment} cells around`}`;
    ok = !k.why;
  } else if (tool.kind === 'building') {
    const f = footprintAt(cell.x, cell.y);
    const t = city.typeById.get(buildingTypeId);
    for (let y = f.y; y < f.y + f.h; y++)
      for (let x = f.x; x < f.x + f.w; x++) {
        const i = y * city.width + x;
        if (!city.inBounds(x, y) || city.isWater(i) || city.buildingAt[i] || city.paths[i]) ok = false;
      }
    // The building levels its own pad, up to a few steps of rise across the lot.
    const pad = padSteps(city, f.x, f.y, f.w, f.h);
    if (pad === null) ok = false;
    renderer.preview = { kind: 'footprint', x0: f.x, y0: f.y, x1: f.x + f.w - 1, y1: f.y + f.h - 1, ok, color: (t?.color ?? '#fff') + 'aa', catchment: t?.catchment || undefined };
    text = pad === null ? `${t?.name ?? ''}: the ground is too uneven for a pad`
      : pad > 0 ? `${t?.name ?? ''}: ${money((t?.cost ?? 0) + pad * PAD_COST_PER_STEP)} (pad ${money(pad * PAD_COST_PER_STEP)})`
      : `${t?.name ?? ''}: ${money(t?.cost ?? 0)}`;
    if (t?.transit) {
      // A stop must touch a path its vehicles use.
      // A subway station stands over its tunnel, or beside it.
      const subway = t.transit === 'subway';
      const carries = (j: number) => {
        if (subway) return !!city.subways[j];
        const p = city.profileAt(j);
        return !!p && (t.transit === 'bus' ? p.cars : t.transit === 'tram' ? !!p.tram : p.rail);
      };
      let touches = false;
      for (let y = f.y - 1; y <= f.y + f.h; y++)
        for (let x = f.x - 1; x <= f.x + f.w; x++) {
          const edge = (x === f.x - 1 || x === f.x + f.w) !== (y === f.y - 1 || y === f.y + f.h);
          const inside = x >= f.x && x < f.x + f.w && y >= f.y && y < f.y + f.h;
          if ((edge || (subway && inside)) && city.inBounds(x, y) && carries(y * city.width + x)) touches = true;
        }
      text += touches ? ` · serves ${t.catchment} cells around` : ` · must touch ${t.transit === 'bus' ? 'a road' : t.transit === 'tram' ? 'a tram line' : subway ? 'a subway tunnel (over it or beside it)' : 'a rail line'}`;
    }
  } else if (tool.kind === 'path' && curveEnds) {
    const cells = curveCells(curveEnds.x0, curveEnds.y0, cell.x, cell.y, curveEnds.x1, curveEnds.y1);
    const prof = city.profiles.find((p) => p.id === profileId);
    for (const [x, y] of cells) {
      const i = y * city.width + x;
      if (!city.inBounds(x, y) || (city.isWater(i) && !city.isRiver(i)) || city.buildingAt[i]) ok = false;
    }
    const plan = planPath(city, profileId, surfaceId, cells, tunnel);
    if (plan.problem) ok = false;
    renderer.preview = { kind: 'cells', x0: 0, y0: 0, x1: 0, y1: 0, ok, color: (prof?.color ?? '#888') + 'cc', cells };
    text = plan.problem ?? `Curve${surfaceLabel()}, ${cells.length} cells: ${money(plan.cost)}${grading(plan.grading)}${depthNote(plan, tunnel)}`;
  } else if (tool.kind === 'zone' && zoneMode === 'block' && !drag) {
    const block = city.blockAt(cell.x, cell.y);
    if (block) {
      const depth = city.frontageDepth(block);
      const reach = city.lotDepth[density] ?? 2;
      const zoneColor = ['', 'rgba(76,175,80,0.5)', 'rgba(74,127,224,0.5)', 'rgba(224,176,48,0.5)'][tool.zone];
      const restColor = ['rgba(255,255,255,0.12)', 'rgba(40,140,60,0.6)', zoneColor.replace('0.5)', '0.25)')][leftover];
      const cells: [number, number][] = [];
      const colors: string[] = [];
      let lots = 0;
      for (const c of block) {
        const d = depth.get(c) ?? 0;
        const isLot = d > 0 && d <= reach;
        if (isLot) lots++;
        cells.push([c % city.width, (c / city.width) | 0]);
        colors.push(isLot ? zoneColor : restColor);
      }
      const rest = block.length - lots;
      const cost = (leftover === 2 ? block.length : lots) * ZONE_COST + (leftover === 1 ? rest * (city.typeById.get('pocket-park')?.cost ?? 0) : 0);
      renderer.preview = { kind: 'cells', x0: 0, y0: 0, x1: 0, y1: 0, ok: true, color: zoneColor, cells, colors };
      text = `Block: ${lots} cells in reach of a road${rest ? `, ${rest} leftover (${LEFTOVER_NAMES[leftover].toLowerCase()})` : ''}: about ${money(cost)}`;
    } else if (city.inBounds(cell.x, cell.y)) {
      text = 'Not a block: no roads close off this land';
    }
  } else if (drag && tool.kind === 'path' && pathMode === 'diagonal') {
    const end = borderSnap(cell);
    const cells = diagonalCells(drag.x, drag.y, end.x, end.y);
    const prof = city.profiles.find((p) => p.id === profileId);
    for (const [x, y] of cells) {
      const i = y * city.width + x;
      // A river is bridged, at five times the cost; the sea can't be.
      if (!city.inBounds(x, y) || (city.isWater(i) && !city.isRiver(i)) || city.buildingAt[i]) ok = false;
    }
    const plan = planPath(city, profileId, surfaceId, cells, tunnel);
    if (plan.problem) ok = false;
    renderer.preview = { kind: 'cells', x0: 0, y0: 0, x1: 0, y1: 0, ok, color: (prof?.color ?? '#888') + 'cc', cells };
    text = plan.problem ?? `Diagonal${surfaceLabel()}, ${cells.length} cells: ${money(plan.cost)}${grading(plan.grading)}${depthNote(plan, tunnel)}`;
  } else if (drag && tool.kind === 'river') {
    const line = diagonalCells(drag.x, drag.y, cell.x, cell.y);
    const reach = riverWidth / 2, r = Math.ceil(reach);
    const seen = new Set<number>(), cells: [number, number][] = [];
    let cost = 0;
    for (const [cx, cy] of line)
      for (let dy = -r; dy <= r; dy++)
        for (let dx = -r; dx <= r; dx++) {
          const x = cx + dx, y = cy + dy, i = y * city.width + x;
          if (!city.inBounds(x, y) || dx * dx + dy * dy > reach * reach || seen.has(i)) continue;
          seen.add(i);
          cells.push([x, y]);
          if (city.buildingAt[i] || city.paths[i] || city.dams[i]) ok = false;
          if (!city.isWater(i)) cost += DIG_COST * riverDepth;
        }
    renderer.preview = { kind: 'cells', x0: 0, y0: 0, x1: 0, y1: 0, ok, color: '#3f86c8cc', cells };
    const what = `River ${riverWidth * 16} m wide, ${riverDepth * 2} m deep, ${line.length * 16} m long`;
    text = ok ? `${what}: about ${money(cost)}` : 'A river can’t be dug through buildings, roads or dams';
  } else if (drag && tool.kind === 'dam') {
    const cells = diagonalCells(drag.x, drag.y, cell.x, cell.y);
    let top = 0, crossing = 0, problem = cells.length > DAM_LONGEST ? `A dam is at most ${DAM_LONGEST} cells long` : '';
    for (const [x, y] of cells) {
      const i = y * city.width + x;
      if (!city.inBounds(x, y)) { ok = false; continue; }
      if (city.isRiver(i)) { crossing++; top = Math.max(top, city.rivers[i]); }
      if (city.buildingAt[i] || city.paths[i] || (city.isWater(i) && !city.isRiver(i))) problem ||= 'A dam can’t go through buildings, roads or the sea';
    }
    if (!crossing) problem ||= 'Draw the dam across a river, bank to bank';
    if (cells.length < 3) problem ||= 'Draw the dam across a river, bank to bank';
    if (problem) ok = false;
    const crest = top + damRise + 1;
    let cost = 0;
    for (const [x, y] of cells) if (city.inBounds(x, y)) cost += DAM_COST + DAM_COST_STEP * Math.max(0, crest - city.heights[y * city.width + x]);
    renderer.preview = { kind: 'cells', x0: 0, y0: 0, x1: 0, y1: 0, ok, color: '#b8b4a8dd', cells };
    text = problem || `Dam holding the river ${damRise * 2} m up, ${cells.length * 16} m long: about ${money(cost)}`;
  } else if (drag && (tool.kind === 'path' || tool.kind === 'utility')) {
    const cells = lineCells(drag.x, drag.y, cell.x, cell.y, shiftHeld);
    let cost = 0;
    const prof = city.profiles.find((p) => p.id === profileId);
    for (const [x, y] of cells) {
      const i = y * city.width + x;
      if (!city.inBounds(x, y) || (city.isWater(i) && !city.isRiver(i))) { ok = false; continue; }
      if (tool.kind === 'path') {
        if (city.buildingAt[i]) ok = false;
      } else if (tool.utility === 0) {
        if (!city.buildingAt[i] && !city.powerLines[i]) cost += 5;
      } else if (tool.utility === 2) {
        if (!city.subways[i]) cost += 150;
      } else if (!city.waterPipes[i]) cost += 3;
    }
    let graded = 0, deepest = 0, inTunnel = 0;
    let problem: string | null = null;
    if (tool.kind === 'path') {
      // Each leg is graded as the server grades it.
      const legs = shiftHeld ? [[drag.x, drag.y, drag.x, cell.y], [drag.x, cell.y, cell.x, cell.y]] : [[drag.x, drag.y, cell.x, cell.y]];
      for (const [x0, y0, x1, y1] of legs) {
        const plan = planPath(city, profileId, surfaceId, lCells(x0, y0, x1, y1), tunnel);
        cost += plan.cost;
        graded += plan.grading;
        problem ??= plan.problem;
        deepest = Math.max(deepest, plan.deepestCutDm);
        inTunnel += plan.tunnelCells;
      }
      if (problem) ok = false;
    }
    const color = tool.kind === 'path' ? (prof?.color ?? '#888') + 'cc' : tool.utility === 0 ? '#ffd84acc' : tool.utility === 2 ? '#d0605acc' : '#5ec8ffcc';
    renderer.preview = { kind: 'line', x0: drag.x, y0: drag.y, x1: cell.x, y1: cell.y, ok, color, verticalFirst: shiftHeld };
    text = problem ?? `${tool.kind === 'path' ? `${prof?.name ?? ''}${surfaceLabel()}, ` : ''}${cells.length} cells: ${money(cost)}${grading(graded)}${tool.kind === 'path' ? depthNote({ cost, grading: graded, problem, pct: 0, deepestCutDm: deepest, tunnelCells: inTunnel }, tunnel) : ''}`;
  } else if (drag) {
    const w = Math.abs(cell.x - drag.x) + 1;
    const h = Math.abs(cell.y - drag.y) + 1;
    const colors: Record<string, string> = { bulldoze: 'rgba(255,90,60,0.35)', dezone: 'rgba(200,200,200,0.3)', terraform: 'rgba(200,160,90,0.4)', trees: 'rgba(60,140,60,0.4)' };
    const color = tool.kind === 'zone' ? ['', 'rgba(76,175,80,0.45)', 'rgba(74,127,224,0.45)', 'rgba(224,176,48,0.45)'][tool.zone] : colors[tool.kind] ?? 'rgba(255,255,255,0.2)';
    renderer.preview = { kind: 'rect', x0: drag.x, y0: drag.y, x1: cell.x, y1: cell.y, ok: true, color };
    text = `${w} × ${h}`;
    if (tool.kind === 'zone') text += `: about ${money(w * h * ZONE_COST)}`;
    if (tool.kind === 'terraform') text += `: up to ${money(w * h * 25)}`;
    if (tool.kind === 'trees') {
      // The planter skips what's built on, under water or already wooded.
      let n = 0;
      for (let y = Math.min(drag.y, cell.y); y <= Math.max(drag.y, cell.y); y++)
        for (let x = Math.min(drag.x, cell.x); x <= Math.max(drag.x, cell.x); x++) {
          if (x < 0 || y < 0 || x >= city.width || y >= city.height) continue;
          const i = y * city.width + x;
          if (!city.isWater(i) && !city.paths[i] && !city.buildingAt[i] && (city.plants[i] & 15) < 4) n++;
        }
      text += n ? `: ${n} cells, ${money(n * TREES_PER_CELL)}` : ': nowhere here to plant';
    }
  }
  if (text) {
    costEl.textContent = text;
    costEl.classList.remove('hidden');
    costEl.classList.toggle('bad', !ok || (city.stats.funds ?? 0) < 0);
    costEl.style.left = `${sx + 16}px`;
    costEl.style.top = `${sy + 16}px`;
  } else costEl.classList.add('hidden');
  dirty = true;
}

function rect(a: { x: number; y: number }, b: { x: number; y: number }) {
  return { x0: a.x, y0: a.y, x1: b.x, y1: b.y };
}

/** Sends an action, or adds it to the what-if plan when planning is on. */
function act(action: Record<string, any>, label: string) {
  if (talk?.capture(action, label)) { toast(`Planned: ${label}`, 'info'); return; }
  conn.action(action);
}

function commit(start: { x: number; y: number }, end: { x: number; y: number }) {
  const size = `${Math.abs(end.x - start.x) + 1}×${Math.abs(end.y - start.y) + 1}`;
  switch (tool.kind) {
    case 'path':
    case 'utility': {
      if (tool.kind === 'path' && pathMode === 'diagonal') {
        act({ path: { x0: start.x, y0: start.y, x1: end.x, y1: end.y, profileId, surface: surfaceId, tunnel, diagonal: true } }, `Diagonal ${city.profiles.find((p) => p.id === profileId)?.name ?? profileId} from ${start.x},${start.y} to ${end.x},${end.y}`);
        break;
      }
      const legs = shiftHeld
        ? [[start.x, start.y, start.x, end.y], [start.x, end.y, end.x, end.y]]
        : [[start.x, start.y, end.x, end.y]];
      for (const [x0, y0, x1, y1] of legs) {
        if (tool.kind === 'path') act({ path: { x0, y0, x1, y1, profileId, surface: surfaceId, tunnel } }, `${city.profiles.find((p) => p.id === profileId)?.name ?? profileId} from ${x0},${y0} to ${x1},${y1}`);
        else act({ utility: { x0, y0, x1, y1, kind: tool.utility } }, `${tool.utility === 2 ? 'Subway tunnel' : tool.utility ? 'Water and sewer pipe' : 'Power line'} from ${x0},${y0} to ${x1},${y1}`);
      }
      break;
    }
    case 'zone':
      act({ zone: { area: rect(start, end), zone: tool.zone, density } }, `Zone ${size} ${['', 'residential', 'commercial', 'industrial'][tool.zone]} (${DENSITY_NAMES[density].toLowerCase()})`);
      break;
    case 'dezone':
      act({ zone: { area: rect(start, end), zone: 0, density: 0 } }, `De-zone ${size}`);
      break;
    case 'bulldoze':
      act({ bulldoze: { area: rect(start, end), underground: renderer.underground } }, `Bulldoze ${size} at ${start.x},${start.y}`);
      break;
    case 'terraform':
      act({ terraform: { area: rect(start, end), mode: terraformMode } }, `${TERRAFORM_NAMES[terraformMode]} ${size}`);
      break;
    case 'trees':
      act({ plantTrees: { area: rect(start, end), kind: treeKind } }, `Plant ${(TREE_CHOICES.find(([, k]) => k === treeKind)?.[0] ?? 'trees').toLowerCase()} over ${size}`);
      break;
    case 'river': {
      // A long stroke goes as several digs end to end, each within what one dig takes.
      const len = diagonalCells(start.x, start.y, end.x, end.y).length;
      const parts = Math.max(1, Math.ceil((len - 4) / (DIG_LONGEST - 4)));
      let px = start.x, py = start.y;
      for (let k = 1; k <= parts; k++) {
        const qx = start.x + Math.round(((end.x - start.x) * k) / parts), qy = start.y + Math.round(((end.y - start.y) * k) / parts);
        act({ digRiver: { x0: px, y0: py, x1: qx, y1: qy, width: riverWidth, depth: riverDepth } }, `River ${riverWidth * 16} m wide from ${px},${py} to ${qx},${qy}`);
        [px, py] = [qx, qy];
      }
      break;
    }
    case 'dam':
      act({ dam: { x0: start.x, y0: start.y, x1: end.x, y1: end.y, rise: damRise } }, `Dam from ${start.x},${start.y} to ${end.x},${end.y}`);
      break;
  }
}

function query(x: number, y: number, switchTab = true) {
  queried = { x, y };
  renderer.selected = { x, y };
  conn.send({ why: { x, y } });
  conn.send({ inspect: { x, y } });
  if (switchTab) showTab('why');
  dirty = true;
}

canvas.addEventListener('contextmenu', (e) => e.preventDefault());
canvas.addEventListener('mousedown', (e) => {
  closeFlyout();
  $('#game-menu').classList.add('hidden');
  const { sx, sy } = localXY(e);
  if (e.button === 2 || e.button === 1) {
    panning = { sx, sy, ox: renderer.offX, oy: renderer.offY };
    if (e.button === 2) rightFrom = { sx, sy };
    return;
  }
  if (e.button === 0 && emergency?.directing) { unitPress = { sx, sy, moved: false }; return; }
  if (e.button !== 0 || !city.loaded) return;
  const cell = renderer.cellAt(sx, sy);
  if (transport?.company) { if (city.inBounds(cell.x, cell.y)) transport.mapClick(cell.x, cell.y); return; }
  // The service's screen shows the city to look at, not to build on.
  if (emergency?.active) return;
  if (tool.kind === 'query') { if (city.inBounds(cell.x, cell.y)) query(cell.x, cell.y); return; }
  if (tool.kind === 'path' && curveEnds) {
    const e = curveEnds;
    act({ path: { x0: e.x0, y0: e.y0, x1: e.x1, y1: e.y1, profileId, surface: surfaceId, tunnel, curved: true, cx: cell.x, cy: cell.y } }, `Curved ${city.profiles.find((p) => p.id === profileId)?.name ?? profileId} from ${e.x0},${e.y0} to ${e.x1},${e.y1}`);
    curveEnds = null;
    updateHints();
    updatePreview(cell, sx, sy);
    return;
  }
  if (tool.kind === 'zone' && zoneMode === 'block') {
    if (city.inBounds(cell.x, cell.y))
      act({ block: { x: cell.x, y: cell.y, zone: tool.zone, density, leftover } }, `Zone the block at ${cell.x},${cell.y} ${['', 'residential', 'commercial', 'industrial'][tool.zone]} (${DENSITY_NAMES[density].toLowerCase()})`);
    return;
  }
  if (tool.kind === 'disaster') {
    if (city.inBounds(cell.x, cell.y)) act({ disaster: { kind: disasterKind, x: cell.x, y: cell.y } }, `${DISASTER_NAMES[disasterKind]} at ${cell.x},${cell.y}`);
    return;
  }
  if (tool.kind === 'building' && city.typeById.get(buildingTypeId)?.roadside) {
    const t = city.typeById.get(buildingTypeId)!;
    const k = kerbSpot(t, cell, sx, sy);
    if (k.why) { toast(`${t.name}: ${k.why}.`, 'warning'); return; }
    act({ building: { x: k.x, y: k.y, typeId: buildingTypeId, side: k.side + 1 } }, `${t.name} on the ${['north', 'east', 'south', 'west'][k.side]} side at ${k.x},${k.y}`);
    return;
  }
  if (tool.kind === 'building') {
    const f = footprintAt(cell.x, cell.y);
    act({ building: { x: f.x, y: f.y, typeId: buildingTypeId } }, `${city.typeById.get(buildingTypeId)?.name ?? buildingTypeId} at ${f.x},${f.y}`);
    return;
  }
  drag = cell;
  updatePreview(cell, sx, sy);
});
window.addEventListener('mousemove', (e) => {
  const { sx, sy } = localXY(e);
  if (unitPress) {
    unitPress.moved ||= Math.abs(sx - unitPress.sx) + Math.abs(sy - unitPress.sy) > 6;
    if (unitPress.moved) { renderer.unitBox = { x0: unitPress.sx, y0: unitPress.sy, x1: sx, y1: sy }; dirty = true; }
    return;
  }
  if (panning) {
    followCam = false;
    renderer.offX = panning.ox + (sx - panning.sx);
    renderer.offY = panning.oy + (sy - panning.sy);
    dirty = true;
    return;
  }
  if (transport?.company) return;
  if (e.target === canvas || drag) updatePreview(renderer.cellAt(sx, sy), sx, sy);
});
window.addEventListener('mouseup', (e) => {
  if (unitPress && e.button === 0 && emergency) {
    const { sx, sy } = localXY(e);
    if (unitPress.moved) {
      const x0 = Math.min(unitPress.sx, sx), x1 = Math.max(unitPress.sx, sx), y0 = Math.min(unitPress.sy, sy), y1 = Math.max(unitPress.sy, sy);
      emergency.map.box(renderer.shownUnits.filter((u) => { const p = renderer.unitScreen(u); return p.x >= x0 && p.x <= x1 && p.y >= y0 && p.y <= y1; }).map((u) => u.id), e.shiftKey);
    } else emergency.map.click(unitUnder(sx, sy), e.shiftKey);
    unitPress = null;
    renderer.unitBox = null;
    emergency.mapChanged();
    dirty = true;
    return;
  }
  if (e.button === 2 && rightFrom) {
    const { sx, sy } = localXY(e);
    // A right click without a drag sends the picked units; a drag only pans.
    if (emergency?.directing && Math.abs(sx - rightFrom.sx) + Math.abs(sy - rightFrom.sy) < 6) {
      const cell = renderer.cellAt(sx, sy);
      if (city.inBounds(cell.x, cell.y)) { emergency.map.rightClick(cell.x, cell.y, unitUnder(sx, sy), e.shiftKey); emergency.mapChanged(); dirty = true; }
    }
    rightFrom = null;
  }
  if (panning && (e.button === 2 || e.button === 1)) { panning = null; return; }
  if (e.button !== 0 || !drag) return;
  const { sx, sy } = localXY(e);
  if (tool.kind === 'path' && pathMode === 'curve') {
    // The ends are set; the curve bends with the mouse until the next click.
    const end = renderer.cellAt(sx, sy);
    if (end.x !== drag.x || end.y !== drag.y) curveEnds = { x0: drag.x, y0: drag.y, x1: end.x, y1: end.y };
    drag = null;
    updateHints();
    updatePreview(end, sx, sy);
    return;
  }
  commit(drag, borderSnap(renderer.cellAt(sx, sy)));
  drag = null;
  updatePreview(renderer.cellAt(sx, sy), sx, sy);
});
canvas.addEventListener('mouseleave', () => {
  if (!drag) { renderer.hover = null; renderer.preview = null; $('#cost').classList.add('hidden'); dirty = true; }
});
canvas.addEventListener('wheel', (e) => {
  e.preventDefault();
  const { sx, sy } = localXY(e);
  renderer.zoomAt(sx, sy, e.deltaY < 0 ? 1.15 : 1 / 1.15);
  dirty = true;
}, { passive: false });

const held = new Set<string>();
window.addEventListener('keydown', (e) => {
  if ((e.target as HTMLElement).closest('input, select, dialog')) return;
  if (e.key === 'Shift') { shiftHeld = true; return; }
  held.add(e.key);
  const k = e.key.toLowerCase();
  // The company's and the service's screens have their own tools; only the shared keys (pause, speed, panning) work there.
  if (transport?.company || emergency?.active) {
    if (emergency?.mapKey(e)) { dirty = true; return; }
    if (k === ' ') { e.preventDefault(); conn.send({ speed: { speed: city.stats.speed === 0 ? 1 : 0 } }); }
    else if (k >= '1' && k <= '3') conn.send({ speed: { speed: Number(k) } });
    return;
  }
  const keyTools: Record<string, Tool> = {
    q: { kind: 'query' }, b: { kind: 'bulldoze' }, r: { kind: 'path' }, z: { kind: 'zone', zone: 1 }, x: { kind: 'zone', zone: 2 },
    c: { kind: 'zone', zone: 3 }, d: { kind: 'dezone' }, p: { kind: 'utility', utility: 0 }, w: { kind: 'utility', utility: 1 }, j: { kind: 'utility', utility: 2 },
    e: { kind: 'building' }, t: { kind: 'terraform' }, k: { kind: 'disaster' },
  };
  if (keyTools[k]) setTool(keyTools[k]);
  else if (k === 'v') { density = (density + 1) % 3; buildToolbar(); updateHints(); }
  else if (k === 'm') { renderer.showAgents = !renderer.showAgents; if (activeTab === 'layers') renderLayers(); updateUpright(); dirty = true; }
  else if (k === 'u') { renderer.underground = !renderer.underground; updateHints(); updateUpright(); dirty = true; }
  else if (k === 'escape' && buildMenu.open) { buildMenu.open = null; buildToolbar(); }
  else if (k === 'escape' && !$('#panel').classList.contains('closed')) closePanel();
  else if (k === 'escape' && renderer.followed !== null) stopFollowing();
  else if (k === 'escape' && curveEnds) { curveEnds = null; renderer.preview = null; updateHints(); dirty = true; }
  else if (k === 'escape') { drag = null; renderer.preview = null; setTool({ kind: 'query' }); }
  else if (k === 'a') { pathMode = pathMode === 'straight' ? 'diagonal' : pathMode === 'diagonal' ? 'curve' : 'straight'; curveEnds = null; setTool({ kind: 'path' }); }
  else if (k === 'g') { zoneMode = zoneMode === 'block' ? 'area' : 'block'; setTool(tool.kind === 'zone' ? tool : { kind: 'zone', zone: 1 }); }
  else if (k === ' ') { e.preventDefault(); conn.send({ speed: { speed: city.stats.speed === 0 ? 1 : 0 } }); }
  else if (k >= '1' && k <= '3') conn.send({ speed: { speed: Number(k) } });
});
window.addEventListener('keyup', (e) => {
  if (e.key === 'Shift') shiftHeld = false;
  held.delete(e.key);
});

// Arrow keys pan smoothly.
setInterval(() => {
  const step = 12;
  let dx = 0;
  let dy = 0;
  if (held.has('ArrowLeft')) dx += step;
  if (held.has('ArrowRight')) dx -= step;
  if (held.has('ArrowUp')) dy += step;
  if (held.has('ArrowDown')) dy -= step;
  if (dx || dy) { followCam = false; renderer.offX += dx; renderer.offY += dy; dirty = true; }
}, 16);

// ---------------------------------------------------------------------------
// Side panel
// ---------------------------------------------------------------------------

const TAB_TITLES: Record<string, string> = {
  why: 'Why', inspect: 'Inspect', demand: 'Demand', alerts: 'Alerts', forecast: 'Forecast', laws: 'Laws', people: 'People', buses: 'Buses', budget: 'Budget', layers: 'Layers', guide: 'Guide',
};

/** Shows a tab of the floating panel, opening it. */
function showTab(name: string) {
  activeTab = name;
  $('#panel').classList.remove('closed');
  document.body.classList.add('panel-open');
  $('#panel-title').textContent = TAB_TITLES[name] ?? name;
  updateUpright();
  document.querySelectorAll<HTMLButtonElement>('#tabs button').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll<HTMLElement>('.tab').forEach((t) => t.classList.toggle('active', t.id === `tab-${name}`));
  if (name === 'budget') renderBudget();
  if (name === 'layers') renderLayers();
}
document.querySelectorAll<HTMLButtonElement>('#tabs button').forEach((b) =>
  b.addEventListener('click', () => (talk ? talk.open(b.dataset.tab!) : showTab(b.dataset.tab!))),
);

function closePanel() {
  $('#panel').classList.add('closed');
  document.body.classList.remove('panel-open');
  updateUpright();
}
$('#panel-close').innerHTML = icon('close', 16);
$('#panel-close').addEventListener('click', closePanel);

// ---------------------------------------------------------------------------
// The L (Beta 4, part 2, variation A): the minimap and the panels up the left, the city's numbers along the bottom
// ---------------------------------------------------------------------------

const UPRIGHT: [string, string, string][] = [
  ['layers', 'layers', 'Layers: what the map shows'], ['underground', 'underground', 'Underground view (U): pipes, tunnels and subways'], ['traffic', 'traffic', 'Commuters on the streets (M)'],
  ['why', 'why', 'Why: what you queried, and why it is the way it is'], ['inspect', 'inspect', 'Inspect: the raw values of a queried cell'],
  ['demand', 'demand', 'Demand: what the city wants more of, and why'], ['forecast', 'forecast', 'Forecast: where the city is heading'], ['people', 'people', 'People: what they think of you'],
  ['laws', 'laws', 'Laws: ordinances'], ['guide', 'guide', 'Guide: how to play'],
];

function updateUpright() {
  const open = !$('#panel').classList.contains('closed');
  document.querySelectorAll<HTMLButtonElement>('#lup-btns button').forEach((b) => {
    const p = b.dataset.panel!;
    b.classList.toggle('active', p === 'underground' ? renderer.underground : p === 'traffic' ? renderer.showAgents : open && activeTab === p);
  });
  $('#btn-budget').classList.toggle('active', open && activeTab === 'budget');
  $('#btn-alerts').classList.toggle('active', open && activeTab === 'alerts');
}

function openPanel(name: string) {
  if (!$('#panel').classList.contains('closed') && activeTab === name) { closePanel(); return; }
  if (talk) talk.open(name);
  else showTab(name);
}

$('#lup-btns').innerHTML = UPRIGHT.map(([p, ic, title]) => `<button class="icon-btn" data-panel="${p}" title="${esc(title)}">${icon(ic, 18)}</button>`).join('');
document.querySelectorAll<HTMLButtonElement>('#lup-btns button').forEach((b) =>
  b.addEventListener('click', () => {
    const p = b.dataset.panel!;
    if (p === 'underground') { renderer.underground = !renderer.underground; updateHints(); dirty = true; }
    else if (p === 'traffic') { renderer.showAgents = !renderer.showAgents; if (activeTab === 'layers') renderLayers(); dirty = true; }
    else openPanel(p);
    updateUpright();
  }),
);
$('#btn-budget').innerHTML = icon('budget', 20);
$('#btn-budget').addEventListener('click', () => openPanel('budget'));
$('#btn-alerts').insertAdjacentHTML('afterbegin', icon('alerts', 20));
$('#btn-menu').innerHTML = icon('menu', 18);
$('#btn-menu').addEventListener('click', () => $('#game-menu').classList.toggle('hidden'));
$('#game-menu').addEventListener('click', (e) => { if ((e.target as HTMLElement).closest('button')) $('#game-menu').classList.add('hidden'); });
document.querySelector<HTMLButtonElement>('#speed button[data-speed="0"]')!.innerHTML = icon('pause', 14);

/** The whole city, small: land and water, zones, roads and buildings, with the view's rectangle on it. */
const minimap = $<HTMLCanvasElement>('#minimap');
let minimapImage: ImageData | null = null;
let minimapAt = 0;

function drawMinimapImage() {
  const c = city;
  const w = c.width, h = c.height;
  if (!w || !h) return;
  const img = new ImageData(w, h);
  const zone = [[0, 0, 0], [95, 158, 74], [91, 127, 184], [192, 154, 69]];
  for (let i = 0; i < w * h; i++) {
    let r: number, g: number, b: number;
    const ht = c.heights[i];
    if (ht <= c.waterLevel || c.rivers[i]) { r = 38; g = 86; b = 140; }
    else { const k = Math.min(1, (ht - c.waterLevel) / 40); r = 70 + 50 * k; g = 104 + 30 * k; b = 62 + 30 * k; }
    const z = c.zones[i];
    if (z) { const col = zone[Math.floor(z / 3)] ?? zone[0]; r = (r + col[0]) / 2; g = (g + col[1]) / 2; b = (b + col[2]) / 2; }
    const bid = c.buildingAt[i];
    if (bid) {
      const bd = c.buildings.get(bid);
      const col = bd && bd.zone ? zone[bd.zone] : [200, 200, 210];
      r = col[0]; g = col[1]; b = col[2];
    }
    if (c.paths[i]) { r = 214; g = 214; b = 220; }
    img.data.set([r, g, b, 255], i * 4);
  }
  minimapImage = img;
}

function drawMinimap(now: number) {
  if (!city.loaded || document.body.classList.contains('company') || document.body.classList.contains('dispatch')) return;
  if (!minimapImage || now - minimapAt > 2000) { drawMinimapImage(); minimapAt = now; }
  const ctx = minimap.getContext('2d')!;
  const W = minimap.width, H = minimap.height;
  const s = Math.min(W / city.width, H / city.height);
  const ox = (W - city.width * s) / 2, oy = (H - city.height * s) / 2;
  ctx.fillStyle = '#0b0f17';
  ctx.fillRect(0, 0, W, H);
  if (minimapImage) {
    const tmp = new OffscreenCanvas(city.width, city.height);
    tmp.getContext('2d')!.putImageData(minimapImage, 0, 0);
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(tmp, ox, oy, city.width * s, city.height * s);
  }
  // What the main view shows.
  const r = canvas.getBoundingClientRect();
  const x0 = -renderer.offX / renderer.scale, y0 = -renderer.offY / renderer.scale;
  ctx.strokeStyle = '#ffffff';
  ctx.lineWidth = 1.5;
  ctx.strokeRect(ox + x0 * s, oy + y0 * s, (r.width / renderer.scale) * s, (r.height / renderer.scale) * s);
}

function minimapLook(e: MouseEvent) {
  const b = minimap.getBoundingClientRect();
  const s = Math.min(minimap.width / city.width, minimap.height / city.height);
  const ox = (minimap.width - city.width * s) / 2, oy = (minimap.height - city.height * s) / 2;
  const x = ((e.clientX - b.left) * (minimap.width / b.width) - ox) / s;
  const y = ((e.clientY - b.top) * (minimap.height / b.height) - oy) / s;
  followCam = false;
  renderer.centerOn(Math.max(0, Math.min(city.width - 1, x)) - 0.5, Math.max(0, Math.min(city.height - 1, y)) - 0.5);
  dirty = true;
}
let minimapDrag = false;
minimap.addEventListener('mousedown', (e) => { minimapDrag = true; minimapLook(e); });
window.addEventListener('mousemove', (e) => { if (minimapDrag) minimapLook(e); });
window.addEventListener('mouseup', () => (minimapDrag = false));
setInterval(() => drawMinimap(performance.now()), 200);

function renderWhy(w: Msg) {
  const reqs = (w.requirements as Msg[])
    .map((r) => `<div class="req ${r.met ? 'met' : 'unmet'}"><span class="mark">${r.met ? '✓' : '✗'}</span><span class="label">${esc(r.label)}${r.topic ? ` <a href="#" class="topic" data-topic="${esc(r.topic)}">?</a>` : ''}</span><span class="detail">${esc(r.detail)}</span></div>`)
    .join('');
  const factors = w.factors as Msg[];
  const max = Math.max(20, ...factors.map((f) => Math.abs(f.value)));
  const fac = factors
    .map((f) => {
      const pct = (Math.abs(f.value) / max) * 50;
      return `<div class="factor"><span>${esc(f.label)}</span><span class="track"><span class="fill ${f.value >= 0 ? 'pos' : 'neg'}" style="width:${pct}%"></span></span><span class="val">${f.value > 0 ? '+' : ''}${f.value}</span></div>`;
    })
    .join('');
  const b = queriedBuilding;
  const isCivic = b && city.typeById.get(b.typeId)?.placeable;
  const gauges = ((w.gauges ?? []) as Msg[]).map((g) => {
    const pct = g.max > 0 ? (g.value * 100) / g.max : 0;
    const level = g.fun ? 'fun' : pct > 100 ? 'over' : pct >= 85 ? 'full' : 'ok';
    const note = g.fun ? 'City statistic, this month'
      : pct > 100 ? `Over capacity by ${(g.value - g.max).toLocaleString()}: build another nearby`
      : pct >= 85 ? 'Nearly full: plan another one' : `Room for ${(g.max - g.value).toLocaleString()} more`;
    return `<div class="gauge ${level}">
      <div class="g-label">${g.fun ? '✨ ' : ''}${esc(g.label)}</div>
      <div class="g-num"><b>${Number(g.value).toLocaleString()}</b> / ${Number(g.max).toLocaleString()} <span>${esc(g.unit)}</span></div>
      <div class="g-track"><span style="width:${Math.min(100, pct)}%"></span></div>
      <div class="g-note">${note}</div>
    </div>`;
  }).join('');
  $('#tab-why').innerHTML = `
    <h3>${esc(w.subject)}</h3>
    ${gauges ? `<div class="gauges">${gauges}</div>` : ''}
    <div class="muted">Cell ${w.x}, ${w.y}</div>
    <p class="summary">${esc(w.summary)}</p>
    ${reqs ? `<h4>Needs</h4>${reqs}` : ''}
    ${fac ? `<h4>${(w.requirements as Msg[]).some((r) => r.key === 'desirability') ? 'Desirability factors' : 'Land value factors'}</h4>${fac}` : ''}
    <div class="upgrade-slot"></div>
    <div id="funding-slot"></div>
    <div id="lease-slot"></div>`;
  fillUpgrades();
  if (isCivic) renderFunding(b!);
  if (isCivic) seats?.renderLease(b!, $('#lease-slot'));
  document.querySelectorAll<HTMLAnchorElement>('#tab-why a.topic').forEach((a) =>
    a.addEventListener('click', (e) => { e.preventDefault(); talk?.article(a.dataset.topic!); }),
  );
}

function renderFunding(b: Msg) {
  const slot = document.getElementById('funding-slot');
  if (!slot) return;
  slot.innerHTML = `<h4>Funding</h4><div class="slider"><span>Budget</span><input type="range" min="0" max="200" step="10" value="${b.fundingPercent}" id="funding"><span id="funding-val">${b.fundingPercent}%</span></div>
    <p class="muted">Funding scales running cost, output and how many people it can serve. Coverage radius grows with the square root of funding.</p>`;
  const input = $<HTMLInputElement>('#funding');
  input.addEventListener('input', () => ($('#funding-val').textContent = `${input.value}%`));
  input.addEventListener('change', () => {
    conn.action({ funding: { buildingId: b.id, percent: Number(input.value) } });
    setTimeout(() => queried && query(queried.x, queried.y, false), 700);
  });
}

function renderInspect(r: Msg) {
  lastInspect = r;
  const rows = (r.fields as Msg[]).map((f) => `<tr><td>${esc(f.key)}</td><td>${esc(f.value)}</td></tr>`).join('');
  $('#tab-inspect').innerHTML = `<h3>Cell ${r.x}, ${r.y}</h3><div class="upgrade-slot"></div><table class="kv">${rows}</table><div class="residents-slot"></div>`;
  fillUpgrades();
  if (queriedBuilding && city.typeById.get(queriedBuilding.typeId)?.placeable) renderFunding(queriedBuilding);
  fillResidents();
}

/** The upgrades on offer for the inspected building, as buttons; the ones not yet on offer say what they need. */
function fillUpgrades() {
  const r = lastInspect;
  const b = r?.building;
  const ups = (r && queried && r.x === queried.x && r.y === queried.y ? r.upgrades ?? [] : []) as Msg[];
  const html = !ups.length || !b ? '' : `<h4>Upgrade</h4>
    ${ups.length > 1 ? '<p class="muted small">Pick one: the others are gone once it’s built.</p>' : ''}
    ${ups.map((u) => {
      const t = city.typeById.get(u.toTypeId);
      const newCost = t?.cost ?? 0;
      const facts = t ? `${t.capacity ? `${t.capacity.toLocaleString()} riders a day, ` : ''}upkeep ${money(t.maintenance)}/mo${t.leaseRent ? `, lease ${money(t.leaseRent)}/mo` : ''}` : '';
      return `<div class="upgrade ${u.available ? '' : 'locked'}">
        <button data-upgrade="${esc(u.toTypeId)}" ${u.available && city.stats.funds >= u.cost ? '' : 'disabled'}>${esc(u.name)}: ${money(u.cost)}</button>
        <div class="small">${esc(u.text)}${newCost ? ` <span class="muted">(${money(newCost)} to build new)</span>` : ''}</div>
        <div class="small muted">${facts}</div>
        ${u.available ? '' : `<div class="small warn">${esc(u.whyNot)}</div>`}
      </div>`;
    }).join('')}`;
  document.querySelectorAll<HTMLElement>('.upgrade-slot').forEach((slot) => {
    slot.innerHTML = html;
    slot.querySelectorAll<HTMLButtonElement>('button[data-upgrade]').forEach((btn) =>
      btn.addEventListener('click', () => {
        const name = city.typeById.get(btn.dataset.upgrade!)?.name ?? btn.dataset.upgrade;
        conn.action({ upgrade: { buildingId: b.id, toTypeId: btn.dataset.upgrade } });
        toast(`Upgrading to a ${name}…`, 'info');
        setTimeout(() => queried && query(queried.x, queried.y, false), 700);
      }),
    );
  });
}

/** The last inspect reply, so a Follow click can redraw its residents. */
let lastInspect: Msg | null = null;

function fillResidents() {
  const r = lastInspect;
  const html = r && queried && r.x === queried.x && r.y === queried.y ? renderResidents(r) : '';
  document.querySelectorAll<HTMLElement>('.residents-slot').forEach((slot) => {
    slot.innerHTML = html;
    slot.querySelectorAll<HTMLButtonElement>('button[data-follow]').forEach((b) =>
      b.addEventListener('click', () => {
        const id = Number(b.dataset.follow);
        if (renderer.followed === id) stopFollowing();
        else follow(id, b.dataset.name ?? 'Someone', b.dataset.job ?? '');
        fillResidents();
      }),
    );
    slot.querySelectorAll<HTMLAnchorElement>('a[data-jx]').forEach((a) =>
      a.addEventListener('click', (e) => { e.preventDefault(); jumpTo(Number(a.dataset.jx), Number(a.dataset.jy)); }),
    );
  });
}

const TIER_NAMES: Record<string, string> = {
  full: 'On the map: followed every day',
  light: 'Kept on file: checked monthly',
  household: 'Counted with the household',
};

/** The people who live in the inspected building, one row each, with their commute and how often they're late. */
function renderResidents(r: Msg): string {
  const people = (r.residents ?? []) as Msg[];
  if (!people.length) return '';
  const total = r.building?.residents ?? people.length;
  const items = people.map((c) => {
    const work = c.job
      ? `${c.byTransit ? '🚌' : '🚗'} <a href="#" data-jx="${c.jobX}" data-jy="${c.jobY}">${esc(c.job)}</a>, ${c.commuteMinutes} min`
      : c.role === 'job seeker' ? 'looking for work' : c.role;
    const late = c.tier === 'full' && c.daysTracked > 0
      ? ` · late ${c.daysLate} of ${c.daysTracked} days, about ${c.avgMinutes} min door to door`
      : '';
    const follow = c.tier === 'full'
      ? ` <button class="mini" data-follow="${c.id}" data-name="${esc(c.name)}" data-job="${esc(c.job ?? '')}">${renderer.followed === c.id ? 'Unfollow' : 'Follow'}</button>` : '';
    return `<li><b>${esc(c.name)}</b>, ${c.age} · ${work}${late}${follow}<br><span class="muted" title="${esc(TIER_NAMES[c.tier] ?? '')}">${esc(TIER_NAMES[c.tier] ?? c.tier)}</span></li>`;
  }).join('');
  const more = total > people.length ? `<p class="muted">…and ${total - people.length} more.</p>` : '';
  return `<h4>Residents (${total})</h4><ul class="residents">${items}</ul>${more}`;
}

function renderBudget() {
  const s = city.stats;
  if (!s || !s.taxTenths) return;
  const lines = ((s.budget ?? []) as Msg[])
    .map((l) => `<tr><td>${esc(l.label)}</td><td class="num ${l.amount >= 0 ? 'pos' : 'neg'}">${money(l.amount)}</td></tr>`)
    .join('');
  const net = (s.lastMonthIncome ?? 0) - (s.lastMonthExpenses ?? 0);
  const tags = ['Residential $', 'Residential $$', 'Residential $$$', 'Commercial $', 'Commercial $$', 'Commercial $$$', 'Industry: dirty', 'Industry: manufacturing', 'Industry: high-tech'];
  const taxes = tags
    .map((t, k) => `<div class="slider"><span>${t}</span><input type="range" min="0" max="200" step="5" value="${s.taxTenths[k]}" data-tax="${k}"><span>${(s.taxTenths[k] / 10).toFixed(1)}%</span></div>`)
    .join('');
  const util = (label: string, used: number, cap: number) =>
    `<tr><td>${label}</td><td class="num ${used > cap ? 'neg' : ''}">${used.toLocaleString('en-US')} / ${cap.toLocaleString('en-US')}</td></tr>`;
  $('#tab-budget').innerHTML = `
    <h3>${esc(s.cityName)} budget</h3>
    <div class="muted">Last month</div>
    <table class="kv">${lines || '<tr><td>No month has closed yet.</td><td></td></tr>'}
      <tr><td><b>Net</b></td><td class="num ${net >= 0 ? 'pos' : 'neg'}"><b>${money(net)}</b></td></tr>
      <tr><td>Treasury</td><td class="num">${money(s.funds)}</td></tr>
      <tr><td>Debt</td><td class="num">${money(s.debt)}</td></tr></table>
    <h4>Taxes</h4>${taxes}
    <p class="muted">Every point above 9% lowers that group's demand; every point below raises it.</p>
    <h4>Utilities</h4>
    <table class="kv">
      ${util('Power (kW)', s.powerDemandKw, s.powerCapacityKw)}
      ${util('Water', s.waterDemand, s.waterCapacity)}
      ${util('Garbage', s.garbageProduced, s.garbageCapacity)}
      <tr><td>Sewage</td><td class="num ${Number(s.sewageUntreated ?? 0) > 0 ? 'neg' : ''}">${Number(s.sewageProduced ?? 0).toLocaleString('en-US')} / ${Number(s.sewageCapacity ?? 0).toLocaleString('en-US')}${Number(s.sewageUntreated ?? 0) > 0 ? ` · ${Number(s.sewageUntreated).toLocaleString('en-US')} raw` : ''}</td></tr>
      <tr><td>Jobs filled</td><td class="num">${s.workers.toLocaleString('en-US')} / ${s.jobs.toLocaleString('en-US')}</td></tr>
    </table>
    <h4>Road crews</h4>
    <div class="slider"><span>Budget</span><input type="range" min="0" max="200" step="10" value="${s.roads?.crewsPct ?? 100}" id="road-crews"><span>${s.roads?.crewsPct ?? 100}%</span></div>
    ${s.roads?.text ? `<p>${esc(s.roads.text)}</p>` : ''}
    <p class="muted">Crews plough snow and salt ice on the highways first, then roads, then streets, and keep the drains on the bigger roads clear. Less budget, and the streets wait.</p>
    <h4>Sewage treatment</h4>
    <div class="row" style="display:flex;gap:6px" id="treatment">${TREATMENTS.map((t, k) => `<button data-treat="${k}" class="${k === (s.waterQuality?.treatment ?? 2) ? 'active' : ''}">${t}</button>`).join('')}</div>
    ${s.waterQuality?.text ? `<p class="${Number(s.waterQuality.worstTap ?? 0) > 60 || Number(s.waterQuality.foulPct ?? 0) > 10 ? 'warn' : ''}">${esc(s.waterQuality.text)} Plant upkeep ${s.waterQuality.upkeepPct ?? 100}%.</p>` : ''}
    <p class="muted">What the plants don't take out goes into the nearest water and down the river. None saves three quarters of their upkeep and fouls the river; tertiary is nearly clean and costs half as much again.</p>
    <h4>Loans</h4>
    <div class="row" style="display:flex;gap:6px"><button data-loan="10000">Borrow $10,000</button><button data-loan="50000">Borrow $50,000</button></div>
    <p class="muted">Loans are repaid over about 10 years with 6% yearly interest.</p>`;
  document.querySelectorAll<HTMLInputElement>('#tab-budget input[data-tax]').forEach((inp) => {
    inp.addEventListener('input', () => (inp.nextElementSibling!.textContent = `${(Number(inp.value) / 10).toFixed(1)}%`));
    inp.addEventListener('change', () => {
      const k = Number(inp.dataset.tax);
      conn.action({ tax: { zone: Math.floor(k / 3) + 1, wealth: k % 3, percentTenths: Number(inp.value) } });
    });
  });
  const crews = $<HTMLInputElement>('#road-crews');
  crews.addEventListener('input', () => (crews.nextElementSibling!.textContent = `${crews.value}%`));
  crews.addEventListener('change', () => conn.action({ roadCrews: { percent: Number(crews.value) } }));
  document.querySelectorAll<HTMLButtonElement>('#tab-budget button[data-treat]').forEach((b) =>
    b.addEventListener('click', () => conn.action({ treatment: { level: Number(b.dataset.treat) } })),
  );
  document.querySelectorAll<HTMLButtonElement>('#tab-budget button[data-loan]').forEach((b) =>
    b.addEventListener('click', () => conn.action({ loan: { amount: Number(b.dataset.loan) } })),
  );
}

function requestOverlay() {
  lastOverlayMonth = city.stats.month;
  conn.send({ overlay: { kind: activeOverlay } });
}

/** The underground view's key and the city's sewage (Beta 3.2), as the Godot client's panel says it. */
function sewageText(): string {
  const st = city.stats;
  const n = (v: unknown) => Number(v ?? 0);
  const fmt = (v: unknown) => n(v).toLocaleString('en-US');
  const made = n(st.sewageProduced), cap = n(st.sewageCapacity), raw = n(st.sewageUntreated);
  const sewage = made === 0 ? 'No sewage yet.'
    : raw > 0 ? `Sewage ${fmt(made)} a day, ${fmt(cap)} treated by plants, ${fmt(raw)} left raw.`
    : cap === 0 ? `Sewage ${fmt(made)} a day, on septic tanks.`
    : `Sewage ${fmt(made)} a day, all treated (plants take ${fmt(cap)}).`;
  return `Water mains blue, sewers brown beside them, road tunnels grey, subways red. ${sewage}`;
}

function renderLayers(low?: string, high?: string) {
  const o = OVERLAYS.find((x) => x.kind === activeOverlay);
  const ramp: Record<string, string> = {
    good: 'linear-gradient(90deg, rgba(40,120,200,0.2), rgb(40,255,80))',
    bad: 'linear-gradient(90deg, rgba(180,120,30,0.2), rgb(255,20,30))',
    diverging: 'linear-gradient(90deg, rgb(220,50,40), rgba(255,255,255,0.1), rgb(40,255,70))',
    binary: 'linear-gradient(90deg, rgb(200,40,40), rgb(240,170,30), rgb(60,200,90))',
  };
  $('#tab-layers').innerHTML = `
    <h3>Map layers</h3>
    <div class="layers">
      <button data-overlay="0" class="${activeOverlay === 0 ? 'active' : ''}">None</button>
      ${OVERLAYS.map((x) => `<button data-overlay="${x.kind}" class="${x.kind === activeOverlay ? 'active' : ''}">${x.name}</button>`).join('')}
    </div>
    ${o ? `<div class="legend"><span>${esc(low ?? 'low')}</span><span class="ramp" style="background:${ramp[o.scheme]}"></span><span>${esc(high ?? 'high')}</span></div>` : ''}
    ${activeOverlay === 9 && city.stats.roads?.text ? `<p class="warn">${esc(city.stats.roads.text)} · road crews ${city.stats.roads.crewsPct ?? 0}%${city.stats.roads.stayHomePct ? ` · ${city.stats.roads.stayHomePct}% stay home` : ''}</p>` : ''}
    <h4>Commuters</h4>
    <button id="btn-agents" class="${renderer.showAgents ? 'active' : ''}">${renderer.showAgents ? 'Showing commuters' : 'Show commuters'} (M)</button>
    ${roster ? `<p class="muted">${roster.full.toLocaleString('en-US')} people followed daily (${(roster.trips as Msg[]).length.toLocaleString('en-US')} drawn), ${roster.light.toLocaleString('en-US')} checked monthly, of ${roster.citizens.toLocaleString('en-US')}. This machine's limits: ${roster.fullTier.toLocaleString('en-US')} and ${roster.lightTier.toLocaleString('en-US')} (${esc(roster.machine)}). Yellow drive, blue ride transit; the map runs the morning and evening rush on a loop.</p>` : ''}
    <h4>Bus company</h4>
    <button id="btn-routes" class="${renderer.showRoutes ? 'active' : ''}">${renderer.showRoutes ? 'Showing bus routes' : 'Show bus routes'}</button>
    <h4>Light</h4>
    <button id="btn-daynight" class="${dayNight ? 'active' : ''}">${dayNight ? 'Day and night (follows the clock)' : 'Always daylight'}</button>
    <h4>Underground</h4>
    <button id="btn-under" class="${renderer.underground ? 'active' : ''}">${renderer.underground ? 'Back above ground' : 'Underground view'} (U)</button>
    ${renderer.underground ? `<p class="muted">${esc(sewageText())}</p>` : ''}`;
  document.querySelectorAll<HTMLButtonElement>('#tab-layers button[data-overlay]').forEach((b) =>
    b.addEventListener('click', () => {
      activeOverlay = Number(b.dataset.overlay);
      if (activeOverlay) requestOverlay();
      else renderer.overlay = null;
      renderLayers();
      dirty = true;
    }),
  );
  $('#btn-under').addEventListener('click', () => { renderer.underground = !renderer.underground; renderLayers(); updateHints(); dirty = true; });
  $('#btn-daynight').addEventListener('click', () => { dayNight = !dayNight; renderLayers(); dirty = true; });
  $('#btn-agents').addEventListener('click', () => { renderer.showAgents = !renderer.showAgents; renderLayers(); dirty = true; });
  $('#btn-routes').addEventListener('click', () => { renderer.showRoutes = !renderer.showRoutes; renderLayers(); dirty = true; });
}

// ---------------------------------------------------------------------------
// Toasts and dialogs
// ---------------------------------------------------------------------------

// A click outside an open dialog closes it, as Escape does (and a dialog that refuses Escape refuses this too).
document.addEventListener('click', (e) => {
  const dlg = e.target;
  if (!(dlg instanceof HTMLDialogElement) || !dlg.open) return;
  const r = dlg.getBoundingClientRect();
  if (e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) return;
  if (dlg.dispatchEvent(new Event('cancel', { cancelable: true }))) dlg.close();
});

function toast(text: string, severity = 'info', x = -1, y = -1) {
  const el = document.createElement('div');
  el.className = `toast ${severity}${x >= 0 ? ' located' : ''}`;
  el.textContent = text;
  if (x >= 0) {
    el.title = 'Click to go there';
    el.addEventListener('click', () => jumpTo(x, y));
  }
  const box = $('#toasts');
  box.appendChild(el);
  while (box.children.length > 5) box.firstElementChild!.remove();
  setTimeout(() => el.remove(), severity === 'info' ? 4000 : 9000);
}

function jumpTo(x: number, y: number) {
  renderer.centerOn(x, y);
  query(x, y);
}

// Following one commuter: the camera keeps them centred (until you pan away) and a banner says what they're doing.
let followCam = false;
let followName = '';
let followJob = '';
let bannerAt = 0;

function follow(id: number, name: string, job: string) {
  if (!renderer.agents.some((a) => a.id === id)) {
    toast(`${name} isn't drawn on the map right now. Only the first ${renderer.agents.length.toLocaleString('en-US')} followed commuters are.`, 'warning');
    return;
  }
  renderer.followed = id;
  followCam = true;
  followName = name;
  followJob = job;
  renderer.showAgents = true;
  renderer.scale = Math.max(renderer.scale, 12);
  $('#follow').classList.remove('hidden');
  dirty = true;
}

function stopFollowing() {
  renderer.followed = null;
  followCam = false;
  $('#follow').classList.add('hidden');
  fillResidents();
  dirty = true;
}

function updateFollow(now: number) {
  if (renderer.followed === null) return;
  const p = renderer.followedNow();
  if (!p) { stopFollowing(); return; }
  if (followCam) renderer.centerOn(p.x, p.y);
  if (now - bannerAt < 200) return;
  bannerAt = now;
  const hh = Math.floor(p.minute / 60), mm = Math.floor(p.minute % 60);
  const time = `${((hh + 11) % 12) + 1}:${String(mm).padStart(2, '0')} ${hh < 12 ? 'am' : 'pm'}`;
  $('#follow-text').innerHTML = `<b>${esc(followName)}</b> · ${esc(p.status)}${followJob ? ` (${esc(followJob)})` : ''} · ${time}`;
  $('#follow-centre').classList.toggle('hidden', followCam);
}

$('#follow-stop').addEventListener('click', stopFollowing);
$('#follow-centre').addEventListener('click', () => { followCam = true; dirty = true; });

/** Pulses an area of the map for a few seconds: what an alert or petition is about. */
function flash(area: { x0: number; y0: number; x1: number; y1: number }) {
  if (area.x0 < 0) return;
  renderer.flash = { ...area, until: performance.now() + 6000 };
  dirty = true;
}

/** Picks a building for the place tool, as if chosen from the list, and centres on where it's needed. */
function pickBuilding(typeId: string, x: number, y: number) {
  if (!city.typeById.has(typeId)) return;
  buildingTypeId = typeId;
  setTool({ kind: 'building' });
  if (x >= 0) renderer.centerOn(x, y);
  toast(`Placing ${city.typeById.get(typeId)!.name}: click the map to build it.`, 'info');
}

const dlg = $<HTMLDialogElement>('#dlg');
const form = $<HTMLFormElement>('#dlg-form');

let signInOpen = false;

/** Asks for a name and password on a server that wants them. It stays up until the server signs this player in. */
function showSignIn(welcome: Msg, error: string) {
  if (dlg.open) dlg.close('replaced');
  signInOpen = true;
  form.innerHTML = `
    <h3>Sign in to ${esc(welcome.serverName || 'this server')}</h3>
    ${error ? `<p class="error">${esc(error)}</p>` : ''}
    <label>Name</label><input type="text" name="name" maxlength="24" autocomplete="username" required>
    <label>Password</label><input type="password" name="password" autocomplete="current-password" required>
    ${welcome.inviteRequired ? '<label>Invite code (only for a new account)</label><input type="text" name="invite">' : ''}
    <div class="actions"><button value="create">Make an account</button><button value="ok" class="active">Sign in</button></div>`;
  dlg.onclose = () => {
    signInOpen = false;
    const v = dlg.returnValue;
    if (v !== 'ok' && v !== 'create') {
      // Escape or a click elsewhere doesn't get past the sign-in.
      if (v !== 'signed-in' && v !== 'replaced' && !conn.player) setTimeout(() => showSignIn(welcome, ''), 0);
      return;
    }
    const f = new FormData(form);
    conn.signIn(String(f.get('name') ?? '').trim(), String(f.get('password') ?? ''), v === 'create', String(f.get('invite') ?? '').trim());
  };
  dlg.showModal();
}

/** Who's here, in the top bar: each player online (dimmed while away), what they run, and who only watches. */
function renderPresence(p: Msg) {
  const players = p.players as Msg[];
  if (seats) seats.players = players.map((pl) => pl.name as string);
  const el = $('#players');
  el.classList.toggle('hidden', players.length < 2 && !conn.player.length);
  const seatName = (id: string) => (id === 'mayor' ? 'mayor' : seats?.seatName(id) ?? id);
  el.innerHTML = players.map((pl) => {
    const deputy = (pl.deputyFor as string[]).length ? [`deputy for ${(pl.deputyFor as string[]).join('/')}`] : [];
    const role = pl.spectator ? 'watching' : [...(pl.seats as string[]).map(seatName), ...deputy].join(', ');
    const me = pl.name === conn.player ? ' (you)' : '';
    return `<span class="player${pl.online ? '' : ' away'}" title="${pl.online ? 'Online' : 'Away: the computer has their seats'}${pl.admin ? ' · admin' : ''}">${esc(pl.name)}${me}<small>${esc(role)}</small></span>`;
  }).join('');
}

/** Only admins save, load, found cities and set the speed; everyone else doesn't see the buttons. */
function updateAdmin() {
  for (const id of ['#btn-new', '#btn-save', '#btn-load']) $(id).classList.toggle('hidden', !conn.admin);
  // Mayors may slow the region's one clock (or pause it for a couple of minutes); admins set it.
  $('#speed').classList.toggle('hidden', !conn.admin && !region?.isMayor(conn.player));
}

/**
 * A long job the server is doing for this player (a new map, a save, a load), shown the moment it's asked for and
 * until the answer comes: a click with no sign of anything happening reads as a miss.
 */
let busyKind: string | null = null;
let busyTimer = 0;
function busy(text: string | null, kind = '') {
  busyKind = text ? kind : null;
  const el = $('#busy');
  el.classList.toggle('hidden', !text);
  el.innerHTML = text ? `<span class="spinner"></span> ${esc(text)}` : '';
  window.clearTimeout(busyTimer);
  if (text) busyTimer = window.setTimeout(() => busy(null), 60_000);
}

$('#btn-new').addEventListener('click', () => {
  form.innerHTML = `
    <h3>New region or city</h3>
    <label>Start</label><select name="kind"><option value="region" selected>A region: tiles of open land, your city in the middle</option><option value="grow">A growing region: your city's tile to start, more added on any side</option><option value="city">A single city on its own</option></select>
    <label>Region size (tiles a side; a growing region starts with one)</label><select name="tiles"><option value="3" selected>Small, 3 × 3</option><option value="8">Medium, 8 × 8</option><option value="16">Large, 16 × 16</option><option value="32">Huge, 32 × 32</option></select>
    <p class="muted">Past 1,024 cells a side (a large region of medium maps, or a huge one), each tile's ground is made when it's first used.</p>
    <label>Charter: who adds land to a growing region</label><select name="chGrow"><option value="0">Admins only</option><option value="1">A vote of the mayors</option><option value="2" selected>Any mayor</option></select>
    <label>Charter: who lets a city annex the land beside it</label><select name="chAnnex"><option value="0">Admins only</option><option value="1" selected>A vote of the mayors</option><option value="2">Any mayor</option></select>
    <label>Name</label><input type="text" name="name" value="New City" maxlength="40">
    <label>Seed (blank for random)</label><input type="number" name="seed" min="1">
    <label>Map size</label><select name="size"><option value="64">Small (64 × 64, 1 km)</option><option value="128" selected>Medium (128 × 128, 2 km)</option><option value="256">Large (256 × 256, 4 km)</option></select>
    <label><input type="checkbox" name="starter"> Start with a small working town</label>
    <label><input type="checkbox" name="benchmark"> Benchmark city: 256 × 256, every block built (a single city)</label>
    <div class="actions"><button value="cancel">Cancel</button><button value="ok" class="active">Start</button></div>`;
  dlg.onclose = () => {
    if (dlg.returnValue !== 'ok') return;
    const fd = new FormData(form);
    if ((fd.get('kind') === 'region' || fd.get('kind') === 'grow') && fd.get('benchmark') !== 'on') {
      const n = Number(fd.get('tiles'));
      const grows = fd.get('kind') === 'grow';
      busy('Making the region…', 'new');
      conn.send({ newRegion: { name: `${String(fd.get('name') || 'New')} Region`, cityName: String(fd.get('name') || 'New City'), seed: Number(fd.get('seed') || 0), cols: n, rows: n, tileSize: Number(fd.get('size')), starterTown: fd.get('starter') === 'on', grows,
        charter: { grow: Number(fd.get('chGrow')), annex: Number(fd.get('chAnnex')), maxCitySide: 0 } } });
      return;
    }
    busy(fd.get('benchmark') === 'on' ? 'Building the benchmark city…' : 'Making the city…', 'new');
    conn.send({ newCity: { name: String(fd.get('name') || 'New City'), seed: Number(fd.get('seed') || 0), size: Number(fd.get('size')), starterTown: fd.get('starter') === 'on', benchmark: fd.get('benchmark') === 'on' } });
  };
  dlg.showModal();
});

$('#btn-save').addEventListener('click', () => {
  const safe = String(city.stats.cityName ?? 'city').replace(/[^A-Za-z0-9 _-]/g, '').slice(0, 40) || 'city';
  form.innerHTML = `
    <h3>Save city</h3>
    <label>Save name</label><input type="text" name="name" value="${esc(safe)}" maxlength="40" pattern="[A-Za-z0-9 _\\-]+">
    ${saveNames.length ? `<label>Existing saves</label><div class="muted">${saveNames.map(esc).join(', ')}</div>` : ''}
    <div class="actions"><button value="cancel">Cancel</button><button value="ok" class="active">Save</button></div>`;
  dlg.onclose = () => {
    if (dlg.returnValue !== 'ok') return;
    busy('Saving…', 'save');
    conn.send({ save: { name: String(new FormData(form).get('name')) } });
  };
  dlg.showModal();
});

$('#btn-load').addEventListener('click', () => {
  conn.send({ save: { name: '' } }); // refresh the list
  form.innerHTML = `
    <h3>Load city</h3>
    <div class="saves">${saveNames.length ? saveNames.map((n) => `<button value="load:${esc(n)}">${esc(n)}</button>`).join('') : '<p class="muted">No saves yet.</p>'}</div>
    <div class="actions"><button value="cancel">Cancel</button></div>`;
  dlg.onclose = () => {
    if (!dlg.returnValue.startsWith('load:')) return;
    busy(`Loading “${dlg.returnValue.slice(5)}”…`, 'load');
    conn.send({ load: { name: dlg.returnValue.slice(5) } });
  };
  dlg.showModal();
});

// ---------------------------------------------------------------------------
// Frame loop
// ---------------------------------------------------------------------------

/** Day and night on the map (Beta 3.2, Part 6); off shows the city in plain daylight. */
let dayNight = true;

function frame(now: number) {
  updateFollow(now);
  if (city.loaded) {
    const l = dayNight ? mapLight(city.stats, renderer.pinHour != null ? renderer.pinHour * 60 : gameClock.minuteNow(city.stats, now)) : null;
    const was = renderer.light;
    if (!l !== !was || (l && was && (Math.abs(l.night - was.night) > 0.01 || Math.abs(l.golden - was.golden) > 0.01 || l.overcast !== was.overcast))) {
      renderer.light = l;
      dirty = true;
    }
  }
  // Disasters animate: flames flicker, the tornado spins, the Colossus clanks.
  if (dirty || renderer.preview || renderer.animating) {
    renderer.draw();
    dirty = false;
  }
  requestAnimationFrame(frame);
}

window.addEventListener('resize', () => { renderer.resize(); dirty = true; });
setInterval(() => (dirty = true), 600); // blink power and water warnings
renderer.resize();
updateHints();
renderLayers();
talk = new TalkBack({ conn, city, jumpTo, showTab, onUnlocks: () => buildToolbar(), flash, pickBuilding });
seats = new Seats({ conn, jumpTo });
transport = new Transport({
  conn, city, jumpTo, toast,
  setLines: (lines) => { renderer.busLines = lines; dirty = true; },
  setCompanyMap: (m) => { renderer.company = m; dirty = true; },
  onModeChange: (company) => {
    if (company) emergency?.setMode(false);
    drag = null;
    renderer.preview = null;
    renderer.selected = null;
    canvas.style.cursor = company ? 'pointer' : '';
    renderer.resize();
    dirty = true;
  },
});
emergency = new Emergency({
  conn, jumpTo, toast, cityWidth: () => city.width,
  clock: () => ({ month: city.stats.month ?? 0, day: city.stats.day ?? 0, year: city.stats.year ?? 0, speed: city.stats.speed ?? 0, text: gameClock.text(city.stats) }),
  setCalls: (calls) => { renderer.calls = calls; dirty = true; },
  minute: unitMinute,
  setUnits: (units) => { renderer.units = units; if (!units) renderer.shownUnits = []; dirty = true; },
  onModeChange: (on) => {
    if (on && transport?.company) transport.setMode(false);
    drag = null;
    renderer.preview = null;
    renderer.selected = null;
    renderer.resize();
    dirty = true;
  },
});
region = new Region({ conn, toast, onRegion: (r) => { renderer.borders = r.borders ?? []; dirty = true; updateAdmin(); updateTopBar(); } });
region.admin = () => conn.admin;
requestAnimationFrame(frame);

// For debugging from the browser console and for automated checks.
(window as any).toycity = { city, renderer, conn, setTool, query, talk, seats, transport, emergency, region };
