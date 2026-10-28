import './style.css';
import { Connection, type Msg } from './net';
import { City, curveCells, lCells } from './state';
import { Renderer, type OverlayView } from './render';
import { TalkBack } from './talkback';
import { Seats } from './seats';

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
  | { kind: 'disaster' };

const DISASTER_NAMES = ['Fire', 'Tornado', 'Earthquake', 'Clockwork Colossus'];
const DISASTER_SETTINGS = ['Off', 'Rare', 'Normal'];

const DENSITY_NAMES = ['Low', 'Medium', 'High'];
const TERRAFORM_NAMES = ['Raise', 'Lower', 'Level'];
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
];

const $ = <T extends HTMLElement = HTMLElement>(sel: string) => document.querySelector(sel) as T;
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
const money = (n: number) => (n < 0 ? '-$' : '$') + Math.abs(Math.round(n)).toLocaleString('en-US');

const city = new City();
const canvas = $<HTMLCanvasElement>('#map');
const renderer = new Renderer(canvas, city);

let tool: Tool = { kind: 'query' };
let density = 0;
let profileId = 'road';
let buildingTypeId = 'police-station';
let terraformMode = 0;
let disasterKind = 0;
let pathMode: 'straight' | 'curve' = 'straight';
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
/** The followed commuters and this machine's tiers, as the server last sent them. */
let roster: Msg | null = null;

const conn = new Connection({
  onStatus(ok) {
    $('#conn').classList.toggle('off', !ok);
    if (!ok) toast('Lost connection to the server. Reconnecting…', 'error');
  },
  onMessage(kind, body, replyTo) {
    if (kind === 'civic') seats?.onCivic(body);
    if (talk?.onMessage(kind, body, replyTo)) return;
    switch (kind) {
      case 'full': {
        const first = !city.loaded || city.width !== body.width;
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
        break;
      case 'agents':
        roster = body;
        renderer.agents = body.trips;
        if (activeTab === 'layers') renderLayers();
        dirty = true;
        break;
      case 'error':
        toast(body.message, 'error');
        break;
    }
  },
});

// ---------------------------------------------------------------------------
// Top bar
// ---------------------------------------------------------------------------

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function updateTopBar() {
  const s = city.stats;
  $('#city-name').textContent = s.cityName;
  $('#date').textContent = `${MONTHS[s.month - 1]} ${s.day}, ${s.year}`;
  const f = $('#funds');
  f.textContent = money(s.funds);
  f.classList.toggle('neg', s.funds < 0);
  $('#pop').textContent = `👥 ${s.population.toLocaleString('en-US')}`;
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

// ---------------------------------------------------------------------------
// Toolbar
// ---------------------------------------------------------------------------

function toolEquals(a: Tool, b: Tool) {
  return JSON.stringify(a) === JSON.stringify(b);
}

function setTool(t: Tool) {
  tool = t;
  drag = null;
  if (t.kind !== 'path') curveEnds = null;
  renderer.preview = null;
  buildToolbar();
  updateHints();
  dirty = true;
}

function buildToolbar() {
  const el = $('#tools');
  const btn = (label: string, key: string, t: Tool, swatch?: string) =>
    `<button data-tool='${JSON.stringify(t)}' class="${toolEquals(t, tool) ? 'active' : ''}">` +
    `<span>${swatch ? `<i class="swatch" style="background:${swatch}"></i>` : ''}${label}</span><kbd>${key}</kbd></button>`;
  const profiles = city.profiles.map((p) => `<option value="${p.id}" ${p.id === profileId ? 'selected' : ''}>${esc(p.name)} ($${p.costPerCell})</option>`).join('');
  const option = (t: (typeof city.types)[number]) => {
    const locked = !city.unlocked.has(t.id) && t.unlockText !== '';
    const built = t.unique && [...city.buildings.values()].some((b) => b.typeId === t.id);
    const price = t.cost ? `$${t.cost.toLocaleString('en-US')}` : 'free';
    const note = locked ? ` 🔒 ${t.unlockText}` : built ? ' (built)' : ` (${price})`;
    return `<option value="${t.id}" ${t.id === buildingTypeId ? 'selected' : ''} ${locked || built ? 'disabled' : ''}>${esc(t.name)}${esc(note)}</option>`;
  };
  const groups: [string, string[]][] = [
    ['Services', ['power', 'water', 'garbage', 'police', 'fire', 'health', 'education', 'park']],
    ['Transit', ['transit']],
    ['Landmarks', ['landmark']],
    ['Reward lots', ['reward']],
  ];
  const civic = groups
    .map(([label, cats]) => `<optgroup label="${label}">${city.types.filter((t) => t.placeable && cats.includes(t.category)).map(option).join('')}</optgroup>`)
    .join('');
  el.innerHTML = `
    <h4>Look</h4>
    ${btn('Query', 'Q', { kind: 'query' })}
    ${btn('Bulldoze', 'B', { kind: 'bulldoze' })}
    <h4>Paths</h4>
    ${btn('Build path', 'R', { kind: 'path' })}
    <select id="sel-profile" title="Path type">${profiles}</select>
    <div class="row"><button data-pathmode="straight" class="${pathMode === 'straight' ? 'active' : ''}" title="Straight or L-shaped runs">Straight</button><button data-pathmode="curve" class="${pathMode === 'curve' ? 'active' : ''}" title="Arcs snapped to the grid (A toggles)">Curve</button></div>
    <h4>Zones</h4>
    ${btn('Residential', 'Z', { kind: 'zone', zone: 1 }, 'var(--r)')}
    ${btn('Commercial', 'X', { kind: 'zone', zone: 2 }, 'var(--c)')}
    ${btn('Industrial', 'C', { kind: 'zone', zone: 3 }, 'var(--i)')}
    <div class="row">${DENSITY_NAMES.map((d, k) => `<button data-density="${k}" class="${k === density ? 'active' : ''}" title="${d} density (V cycles)">${d[0]}</button>`).join('')}</div>
    <div class="row"><button data-zonemode="area" class="${zoneMode === 'area' ? 'active' : ''}" title="Drag a rectangle">Area</button><button data-zonemode="block" class="${zoneMode === 'block' ? 'active' : ''}" title="Click inside a block enclosed by roads to zone it lot-deep (G toggles)">Block</button></div>
    ${zoneMode === 'block' ? `<label class="field" title="Cells deeper in the block than any lot can reach">Leftover<select id="sel-leftover">${LEFTOVER_NAMES.map((n, k) => `<option value="${k}" ${k === leftover ? 'selected' : ''}>${n}</option>`).join('')}</select></label>` : ''}
    ${btn('De-zone', 'D', { kind: 'dezone' })}
    <h4>Utilities</h4>
    ${btn('Power line', 'P', { kind: 'utility', utility: 0 }, '#ffd84a')}
    ${btn('Water pipe', 'W', { kind: 'utility', utility: 1 }, '#5ec8ff')}
    <h4>Civic</h4>
    ${btn('Place building', 'E', { kind: 'building' })}
    <select id="sel-building" title="Building">${civic}</select>
    <h4>Land</h4>
    ${btn('Terraform', 'T', { kind: 'terraform' })}
    <div class="row">${TERRAFORM_NAMES.map((n, k) => `<button data-terraform="${k}" class="${k === terraformMode ? 'active' : ''}">${n}</button>`).join('')}</div>
    <h4>Disasters</h4>
    ${btn('Start disaster', 'K', { kind: 'disaster' })}
    <select id="sel-disaster" title="Which disaster the sandbox tool starts">${DISASTER_NAMES.map((n, k) => `<option value="${k}" ${k === disasterKind ? 'selected' : ''}>${n}</option>`).join('')}</select>
    <label class="field" title="How often disasters happen by themselves. The sandbox tool works either way.">By themselves
      <select id="sel-disaster-setting">${DISASTER_SETTINGS.map((n, k) => `<option value="${k}" ${k === (city.stats.disasterSetting ?? 1) ? 'selected' : ''}>${n}</option>`).join('')}</select></label>
  `;
  el.querySelectorAll<HTMLButtonElement>('button[data-tool]').forEach((b) => b.addEventListener('click', () => setTool(JSON.parse(b.dataset.tool!))));
  el.querySelectorAll<HTMLButtonElement>('button[data-density]').forEach((b) =>
    b.addEventListener('click', () => { density = Number(b.dataset.density); buildToolbar(); }),
  );
  el.querySelectorAll<HTMLButtonElement>('button[data-terraform]').forEach((b) =>
    b.addEventListener('click', () => { terraformMode = Number(b.dataset.terraform); setTool({ kind: 'terraform' }); }),
  );
  $<HTMLSelectElement>('#sel-profile').addEventListener('change', (e) => { profileId = (e.target as HTMLSelectElement).value; setTool({ kind: 'path' }); });
  el.querySelectorAll<HTMLButtonElement>('button[data-pathmode]').forEach((b) =>
    b.addEventListener('click', () => { pathMode = b.dataset.pathmode as typeof pathMode; curveEnds = null; setTool({ kind: 'path' }); }),
  );
  el.querySelectorAll<HTMLButtonElement>('button[data-zonemode]').forEach((b) =>
    b.addEventListener('click', () => { zoneMode = b.dataset.zonemode as typeof zoneMode; setTool(tool.kind === 'zone' ? tool : { kind: 'zone', zone: 1 }); }),
  );
  document.querySelector<HTMLSelectElement>('#sel-leftover')?.addEventListener('change', (e) => { leftover = Number((e.target as HTMLSelectElement).value); dirty = true; });
  $<HTMLSelectElement>('#sel-disaster').addEventListener('change', (e) => { disasterKind = Number((e.target as HTMLSelectElement).value); setTool({ kind: 'disaster' }); });
  $<HTMLSelectElement>('#sel-disaster-setting').addEventListener('change', (e) => {
    const setting = Number((e.target as HTMLSelectElement).value);
    act({ disasters: { setting } }, `Disasters: ${DISASTER_SETTINGS[setting].toLowerCase()}`);
  });
  $<HTMLSelectElement>('#sel-building').addEventListener('change', (e) => { buildingTypeId = (e.target as HTMLSelectElement).value; setTool({ kind: 'building' }); });
}

/** Lower-left key hints for the current tool (Fortnite style). */
function updateHints() {
  const common: [string, string][] = [['Wheel', 'zoom'], ['Right-drag', 'pan'], ['U', renderer.underground ? 'leave underground view' : 'underground view'], ['M', 'commuters'], ['Space', 'pause']];
  const per: Record<Tool['kind'], [string, string][]> = {
    query: [['Click', 'ask why']],
    bulldoze: [['Drag', renderer.underground ? 'remove pipes in area' : 'clear area'], ['Esc', 'cancel']],
    path: pathMode === 'curve'
      ? (curveEnds ? [['Move', 'bend the curve'], ['Click', 'build it'], ['Esc', 'start again']] : [['Drag', 'set the two ends'], ['A', 'straight paths'], ['Esc', 'cancel']])
      : [['Drag', 'draw path'], ['Shift', 'vertical leg first'], ['A', 'curved paths'], ['Esc', 'cancel']],
    zone: zoneMode === 'block'
      ? [['Click', `zone a whole block, ${DENSITY_NAMES[density].toLowerCase()} density`], ['G', 'paint areas'], ['V', 'cycle density'], ['Esc', 'cancel']]
      : [['Drag', `paint ${DENSITY_NAMES[density].toLowerCase()} density zone`], ['G', 'zone whole blocks'], ['V', 'cycle density'], ['Esc', 'cancel']],
    dezone: [['Drag', 'remove zoning'], ['Esc', 'cancel']],
    utility: [['Drag', 'draw line'], ['Shift', 'vertical leg first'], ['Esc', 'cancel']],
    building: [['Click', 'place'], ['Esc', 'cancel']],
    terraform: [['Drag', `${TERRAFORM_NAMES[terraformMode].toLowerCase()} area`], ['Esc', 'cancel']],
    disaster: [['Click', `start a ${DISASTER_NAMES[disasterKind].toLowerCase()} here`], ['Esc', 'cancel']],
  };
  $('#hints').innerHTML = [...per[tool.kind], ...common].map(([k, v]) => `<div><kbd>${k}</kbd>${v}</div>`).join('');
}

// ---------------------------------------------------------------------------
// Map input
// ---------------------------------------------------------------------------

let panning: { sx: number; sy: number; ox: number; oy: number } | null = null;

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

/** Updates the tool preview and the cost tag under the cursor. */
function updatePreview(cell: { x: number; y: number }, sx: number, sy: number) {
  renderer.hover = city.inBounds(cell.x, cell.y) ? cell : null;
  const costEl = $('#cost');
  let text = '';
  let ok = true;
  renderer.preview = null;
  if (tool.kind === 'building') {
    const f = footprintAt(cell.x, cell.y);
    const t = city.typeById.get(buildingTypeId);
    for (let y = f.y; y < f.y + f.h; y++)
      for (let x = f.x; x < f.x + f.w; x++) {
        const i = y * city.width + x;
        if (!city.inBounds(x, y) || city.isWater(i) || city.buildingAt[i] || city.paths[i]) ok = false;
      }
    renderer.preview = { kind: 'footprint', x0: f.x, y0: f.y, x1: f.x + f.w - 1, y1: f.y + f.h - 1, ok, color: (t?.color ?? '#fff') + 'aa', catchment: t?.catchment || undefined };
    text = `${t?.name ?? ''}: ${money(t?.cost ?? 0)}`;
    if (t?.transit) {
      // A stop must touch a path its vehicles use.
      const carries = (j: number) => {
        const p = city.profileAt(j);
        return !!p && (t.transit === 'bus' ? p.cars : t.transit === 'tram' ? !!p.tram : p.rail);
      };
      let touches = false;
      for (let y = f.y - 1; y <= f.y + f.h; y++)
        for (let x = f.x - 1; x <= f.x + f.w; x++) {
          const edge = (x === f.x - 1 || x === f.x + f.w) !== (y === f.y - 1 || y === f.y + f.h);
          if (edge && city.inBounds(x, y) && carries(y * city.width + x)) touches = true;
        }
      text += touches ? ` · serves ${t.catchment} cells around` : ` · must touch ${t.transit === 'bus' ? 'a road' : t.transit === 'tram' ? 'a tram line' : 'a rail line'}`;
    }
  } else if (tool.kind === 'path' && curveEnds) {
    const cells = curveCells(curveEnds.x0, curveEnds.y0, cell.x, cell.y, curveEnds.x1, curveEnds.y1);
    const prof = city.profiles.find((p) => p.id === profileId);
    const pi = city.profiles.indexOf(prof!) + 1;
    let cost = 0;
    for (const [x, y] of cells) {
      const i = y * city.width + x;
      if (!city.inBounds(x, y) || city.isWater(i) || city.buildingAt[i]) ok = false;
      else if (city.paths[i] !== pi) cost += prof?.costPerCell ?? 0;
    }
    renderer.preview = { kind: 'cells', x0: 0, y0: 0, x1: 0, y1: 0, ok, color: (prof?.color ?? '#888') + 'cc', cells };
    text = `Curve, ${cells.length} cells: ${money(cost)}`;
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
  } else if (drag && (tool.kind === 'path' || tool.kind === 'utility')) {
    const cells = lineCells(drag.x, drag.y, cell.x, cell.y, shiftHeld);
    let cost = 0;
    const prof = city.profiles.find((p) => p.id === profileId);
    const pi = city.profiles.indexOf(prof!) + 1;
    for (const [x, y] of cells) {
      const i = y * city.width + x;
      if (!city.inBounds(x, y) || city.isWater(i)) { ok = false; continue; }
      if (tool.kind === 'path') {
        if (city.buildingAt[i]) ok = false;
        if (city.paths[i] !== pi) cost += prof?.costPerCell ?? 0;
      } else if (tool.utility === 0) {
        if (!city.buildingAt[i] && !city.powerLines[i]) cost += 5;
      } else if (!city.waterPipes[i]) cost += 3;
    }
    const color = tool.kind === 'path' ? (prof?.color ?? '#888') + 'cc' : tool.utility === 0 ? '#ffd84acc' : '#5ec8ffcc';
    renderer.preview = { kind: 'line', x0: drag.x, y0: drag.y, x1: cell.x, y1: cell.y, ok, color, verticalFirst: shiftHeld };
    text = `${cells.length} cells: ${money(cost)}`;
  } else if (drag) {
    const w = Math.abs(cell.x - drag.x) + 1;
    const h = Math.abs(cell.y - drag.y) + 1;
    const colors: Record<string, string> = { bulldoze: 'rgba(255,90,60,0.35)', dezone: 'rgba(200,200,200,0.3)', terraform: 'rgba(200,160,90,0.4)' };
    const color = tool.kind === 'zone' ? ['', 'rgba(76,175,80,0.45)', 'rgba(74,127,224,0.45)', 'rgba(224,176,48,0.45)'][tool.zone] : colors[tool.kind] ?? 'rgba(255,255,255,0.2)';
    renderer.preview = { kind: 'rect', x0: drag.x, y0: drag.y, x1: cell.x, y1: cell.y, ok: true, color };
    text = `${w} × ${h}`;
    if (tool.kind === 'zone') text += `: about ${money(w * h * ZONE_COST)}`;
    if (tool.kind === 'terraform') text += `: up to ${money(w * h * 25)}`;
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
      const legs = shiftHeld
        ? [[start.x, start.y, start.x, end.y], [start.x, end.y, end.x, end.y]]
        : [[start.x, start.y, end.x, end.y]];
      for (const [x0, y0, x1, y1] of legs) {
        if (tool.kind === 'path') act({ path: { x0, y0, x1, y1, profileId } }, `${city.profiles.find((p) => p.id === profileId)?.name ?? profileId} from ${x0},${y0} to ${x1},${y1}`);
        else act({ utility: { x0, y0, x1, y1, kind: tool.utility } }, `${tool.utility ? 'Water pipe' : 'Power line'} from ${x0},${y0} to ${x1},${y1}`);
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
  const { sx, sy } = localXY(e);
  if (e.button === 2 || e.button === 1) {
    panning = { sx, sy, ox: renderer.offX, oy: renderer.offY };
    return;
  }
  if (e.button !== 0 || !city.loaded) return;
  const cell = renderer.cellAt(sx, sy);
  if (tool.kind === 'query') { if (city.inBounds(cell.x, cell.y)) query(cell.x, cell.y); return; }
  if (tool.kind === 'path' && curveEnds) {
    const e = curveEnds;
    act({ path: { x0: e.x0, y0: e.y0, x1: e.x1, y1: e.y1, profileId, curved: true, cx: cell.x, cy: cell.y } }, `Curved ${city.profiles.find((p) => p.id === profileId)?.name ?? profileId} from ${e.x0},${e.y0} to ${e.x1},${e.y1}`);
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
  if (panning) {
    followCam = false;
    renderer.offX = panning.ox + (sx - panning.sx);
    renderer.offY = panning.oy + (sy - panning.sy);
    dirty = true;
    return;
  }
  if (e.target === canvas || drag) updatePreview(renderer.cellAt(sx, sy), sx, sy);
});
window.addEventListener('mouseup', (e) => {
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
  commit(drag, renderer.cellAt(sx, sy));
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
  const keyTools: Record<string, Tool> = {
    q: { kind: 'query' }, b: { kind: 'bulldoze' }, r: { kind: 'path' }, z: { kind: 'zone', zone: 1 }, x: { kind: 'zone', zone: 2 },
    c: { kind: 'zone', zone: 3 }, d: { kind: 'dezone' }, p: { kind: 'utility', utility: 0 }, w: { kind: 'utility', utility: 1 },
    e: { kind: 'building' }, t: { kind: 'terraform' }, k: { kind: 'disaster' },
  };
  if (keyTools[k]) setTool(keyTools[k]);
  else if (k === 'v') { density = (density + 1) % 3; buildToolbar(); updateHints(); }
  else if (k === 'm') { renderer.showAgents = !renderer.showAgents; if (activeTab === 'layers') renderLayers(); dirty = true; }
  else if (k === 'u') { renderer.underground = !renderer.underground; updateHints(); dirty = true; }
  else if (k === 'escape' && renderer.followed !== null) stopFollowing();
  else if (k === 'escape' && curveEnds) { curveEnds = null; renderer.preview = null; updateHints(); dirty = true; }
  else if (k === 'escape') { drag = null; renderer.preview = null; setTool({ kind: 'query' }); }
  else if (k === 'a') { pathMode = pathMode === 'curve' ? 'straight' : 'curve'; curveEnds = null; setTool({ kind: 'path' }); }
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

function showTab(name: string) {
  activeTab = name;
  document.querySelectorAll<HTMLButtonElement>('#tabs button').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll<HTMLElement>('.tab').forEach((t) => t.classList.toggle('active', t.id === `tab-${name}`));
  if (name === 'budget') renderBudget();
  if (name === 'layers') renderLayers();
}
document.querySelectorAll<HTMLButtonElement>('#tabs button').forEach((b) =>
  b.addEventListener('click', () => (talk ? talk.open(b.dataset.tab!) : showTab(b.dataset.tab!))),
);

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
  $('#tab-why').innerHTML = `
    <h3>${esc(w.subject)}</h3>
    <div class="muted">Cell ${w.x}, ${w.y}</div>
    <p class="summary">${esc(w.summary)}</p>
    ${reqs ? `<h4>Needs</h4>${reqs}` : ''}
    ${fac ? `<h4>${(w.requirements as Msg[]).some((r) => r.key === 'desirability') ? 'Desirability factors' : 'Land value factors'}</h4>${fac}` : ''}
    <div id="funding-slot"></div>
    <div id="lease-slot"></div>`;
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
    <p class="muted">Funding scales running cost and output. Coverage radius grows with the square root of funding.</p>`;
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
  $('#tab-inspect').innerHTML = `<h3>Cell ${r.x}, ${r.y}</h3><table class="kv">${rows}</table><div class="residents-slot"></div>`;
  if (queriedBuilding && city.typeById.get(queriedBuilding.typeId)?.placeable) renderFunding(queriedBuilding);
  fillResidents();
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
      <tr><td>Jobs filled</td><td class="num">${s.workers.toLocaleString('en-US')} / ${s.jobs.toLocaleString('en-US')}</td></tr>
    </table>
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
  document.querySelectorAll<HTMLButtonElement>('#tab-budget button[data-loan]').forEach((b) =>
    b.addEventListener('click', () => conn.action({ loan: { amount: Number(b.dataset.loan) } })),
  );
}

function requestOverlay() {
  lastOverlayMonth = city.stats.month;
  conn.send({ overlay: { kind: activeOverlay } });
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
    <h4>Commuters</h4>
    <button id="btn-agents" class="${renderer.showAgents ? 'active' : ''}">${renderer.showAgents ? 'Showing commuters' : 'Show commuters'} (M)</button>
    ${roster ? `<p class="muted">${roster.full.toLocaleString('en-US')} people followed daily (${(roster.trips as Msg[]).length.toLocaleString('en-US')} drawn), ${roster.light.toLocaleString('en-US')} checked monthly, of ${roster.citizens.toLocaleString('en-US')}. This machine's limits: ${roster.fullTier.toLocaleString('en-US')} and ${roster.lightTier.toLocaleString('en-US')} (${esc(roster.machine)}). Yellow drive, blue ride transit; the map runs the morning and evening rush on a loop.</p>` : ''}
    <h4>Underground</h4>
    <button id="btn-under" class="${renderer.underground ? 'active' : ''}">${renderer.underground ? 'Showing pipes' : 'Show water pipes'} (U)</button>`;
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
  $('#btn-agents').addEventListener('click', () => { renderer.showAgents = !renderer.showAgents; renderLayers(); dirty = true; });
}

// ---------------------------------------------------------------------------
// Toasts and dialogs
// ---------------------------------------------------------------------------

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

$('#btn-new').addEventListener('click', () => {
  form.innerHTML = `
    <h3>New city</h3>
    <label>Name</label><input type="text" name="name" value="New City" maxlength="40">
    <label>Seed (blank for random)</label><input type="number" name="seed" min="1">
    <label>Map size</label><select name="size"><option value="64">Small (64 × 64, 1 km)</option><option value="128" selected>Medium (128 × 128, 2 km)</option><option value="256">Large (256 × 256, 4 km)</option></select>
    <label><input type="checkbox" name="starter"> Start with a small working town</label>
    <div class="actions"><button value="cancel">Cancel</button><button value="ok" class="active">Found city</button></div>`;
  dlg.onclose = () => {
    if (dlg.returnValue !== 'ok') return;
    const fd = new FormData(form);
    conn.send({ newCity: { name: String(fd.get('name') || 'New City'), seed: Number(fd.get('seed') || 0), size: Number(fd.get('size')), starterTown: fd.get('starter') === 'on' } });
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
    conn.send({ load: { name: dlg.returnValue.slice(5) } });
  };
  dlg.showModal();
});

// ---------------------------------------------------------------------------
// Frame loop
// ---------------------------------------------------------------------------

function frame(now: number) {
  updateFollow(now);
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
requestAnimationFrame(frame);

// For debugging from the browser console and for automated checks.
(window as any).toycity = { city, renderer, conn, setTool, query, talk, seats };
