// The emergency services' own view, a game screen of its own as the bus company has: its top bar, its tools down the
// left, its board on the right, and the city map in the middle with the calls marked on it. A call played at the
// scene fills the middle with a top-down picture of it, where the player moves crews, draws paths and gives orders;
// when it ends its report card comes up over it. Nothing here aims or drives carefully for the player: the switches
// in Settings decide how much the crews do alone.
import { decodeEmergencyView, encodeEmergencyCommand, type Connection, type Msg } from './net';

export interface EmergencyHost {
  conn: Connection;
  jumpTo(x: number, y: number): void;
  toast(text: string, severity?: string): void;
  /** The city's cells across, for naming its 32-cell districts. */
  cityWidth?(): number;
  /** The city's date and speed, for the service's top bar. */
  clock?(): { month: number; day: number; year: number; speed: number };
  /** The service's screen came up (true) or went (false): the city's tools and panel go or come back. */
  onModeChange?(on: boolean): void;
  /** Marks the service's calls on the city map while its screen is up, or clears them (null). */
  setCalls?(calls: { x: number; y: number; icon: string; waiting: boolean; scene: boolean }[] | null): void;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const ORDERS: [Order, string, string][] = [
  ['move', 'Move', 'Right-click a tile: the selected find their own way there'],
  ['path', 'Draw path', 'Drag from the selected one: it follows exactly what you draw, through anything in the way'],
  ['hose', 'Hose', 'Firefighters spray the fire at a tile'],
  ['treat', 'Treat', 'Paramedics treat the person you click'],
  ['warn', 'Warn', 'Officers call on the person you click to give up'],
  ['arrest', 'Arrest', 'Officers close in on the person you click and cuff them'],
  ['taser', 'Taser', 'Officers fire a taser at a tile: it hits the first person on the line'],
  ['shoot', 'Shoot', 'Officers fire at a tile: it hits the first person on the line, whoever they are'],
  ['cordon', 'Cordon', "Officers hold a tile: onlookers keep back and a suspect can't slip out past them"],
  ['search', 'Search', 'Officers go to a tile and look around it for anyone hiding'],
  ['breach', 'Breach', 'SWAT goes into the building you click; two or more together stun whoever is inside'],
];

type EmTab = 'calls' | 'units' | 'reports' | 'settings' | 'log';
type Order = 'move' | 'path' | 'hose' | 'treat' | 'warn' | 'arrest' | 'taser' | 'shoot' | 'cordon' | 'search' | 'breach';

const GAME = 'emergency';
const TILE = 14;
const $ = <T extends HTMLElement = HTMLElement>(sel: string) => document.querySelector(sel) as T;
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
const money = (n: number) => (n < 0 ? '-$' : '$') + Math.abs(Math.round(n)).toLocaleString('en-US');
const UNIT_ICON = ['🚒', '🚓', '🚑', '🛡'];
const UNIT_NAME = ['engine', 'patrol car', 'ambulance', 'SWAT team'];
const SWAT = 3;
// The equipment each kind of unit can carry, as the service sells it.
const GEAR: [string, number, string, string][] = [
  ['breathing-gear', 0, 'breathing gear', 'hoses harder and works in the smoke unhurt'],
  ['body-armour', 1, 'body armour', 'officers shrug off most of a gunshot'],
  ['defibrillator', 2, 'a defibrillator', 'paramedics treat twice as fast'],
];
const GEAR_PRICE = 1500;
// The crooked ways: [kind, name, what it does, whether it's done in one district].
const CROOKED: [string, string, string, boolean][] = [
  ['slow-walk', 'Slow-walk a district', 'calls there wait 12 more minutes; the papers may notice', true],
  ['racket', 'Protection racket', "the district's shops pay the service each month, or else", true],
  ['look-away', 'Look the other way', 'officers let crime in the district go, for a cut', true],
  ['overtime', 'Overtime fires', 'fires spread further and every one pays the crews overtime', false],
  ['strike', 'Strike', 'every crew walks out until it ends', false],
];
const KIND_ICON: Record<string, string> = { fire: '🔥', crime: '🚨', injury: '🩹' };
const STYLES = ['Full command', 'Scene command', 'Focus'];
const STYLE_HELP = [
  'You do everything: every call waits for you to pick who goes, and you run every scene. Calls left waiting get worse, then are lost.',
  'The computer picks who goes to each call; you run every scene they reach.',
  'The computer picks who goes and settles each call as it would without you. You step into only the scenes you choose (Take scene on the call).',
];
const TASKS: Record<Order, number> = { move: 0, path: 0, hose: 1, treat: 2, arrest: 3, warn: 4, shoot: 5, taser: 6, breach: 7, cordon: 8, search: 9 };
const ROLE_COLOR = ['#e04f3a', '#f2f2f2', '#3f7fe0', '#9aa0a8', '#f0c030', '#a050d0', '#f08ad0'];
const ROLE_NAME = ['firefighter', 'paramedic', 'officer', 'bystander', 'casualty', 'suspect', 'hostage'];
// Ground, road, building, water, park, burnt: the same colours as the city map's.
const TILE_COLOR = ['#66783e', '#47474b', '#8a8278', '#21505f', '#4f7a34', '#2e2926'];

export class Emergency {
  view: Msg | null = null;
  /** Whether the service's own screen is up in place of the city's. */
  active = false;
  private tab: EmTab = 'calls';
  private public = false;
  /** The scene being watched, if any. */
  private sceneId = 0;
  /**
   * The scene on show after it ended: kept as it last was, under its report card, until the player goes back to the
   * calls. A scene that just vanishes looks like the board broke.
   */
  private ended: Msg | null = null;
  private reportOpen = true;
  /** Whether the scene is put aside to show the city map around it. */
  private sceneHidden = false;
  private running = true;
  private order: Order = 'move';
  private selected: number[] = [];
  private drawing: number[] | null = null;
  /** A selection box being dragged, in scene tiles. */
  private box: { x0: number; y0: number; x1: number; y1: number; moved: boolean } | null = null;
  /** Units ticked for each call, before Send. */
  private picks = new Map<number, Set<number>>();

  constructor(private readonly host: EmergencyHost) {
    $('#btn-emergency').addEventListener('click', () => this.setMode(true));
    $('#em-city').addEventListener('click', () => this.setMode(false));
    document.querySelectorAll<HTMLButtonElement>('#em-speed button').forEach((b) =>
      b.addEventListener('click', () => this.host.conn.send({ speed: { speed: Number(b.dataset.speed) } })));
    window.addEventListener('keydown', (e) => {
      if (!this.active || (e.target as HTMLElement).closest('input, select, dialog')) return;
      if (e.key === 'Escape' && (this.scene || this.ended)) { this.toBoard(); e.preventDefault(); }
    });
    window.addEventListener('resize', () => { if (this.active && this.shown) this.drawScene(); });
    window.setInterval(() => this.stepScene(), 500);
    if (new URLSearchParams(location.search).get('view') === 'dispatch') this.setMode(true);
  }

  /** Switches between the city's screen and the service's. */
  setMode(on: boolean) {
    if (on === this.active) return;
    this.active = on;
    document.body.classList.toggle('dispatch', on);
    document.title = on ? `${this.view?.name ?? 'Dispatch'} · ToyCity` : 'ToyCity';
    this.host.onModeChange?.(on);
    this.render();
  }

  /** The city's day turned: the date on the top bar. */
  onCityTick() { if (this.active) this.renderTop(); }

  /**
   * What the player asked for and is waiting on, shown at once. The city takes it now, but the service only hears at
   * the start of the city's next day, up to ten seconds away at the slowest speed.
   */
  private pending: { text: string; done: (v: Msg) => boolean } | null = null;

  private wait(text: string, done: (v: Msg) => boolean) {
    this.pending = { text, done };
    this.render();
  }

  /** The server refused something: stop waiting on it. */
  refused() {
    if (!this.pending) return;
    this.pending = null;
    this.render();
  }

  onView(body: Msg) {
    if (body.game !== GAME) return;
    const before = this.view;
    this.view = decodeEmergencyView(body.payload as Uint8Array);
    if (this.pending?.done(this.view)) this.pending = null;
    this.public = body.seat === 'public';
    const btn = $('#btn-emergency');
    btn.classList.remove('hidden');
    const open = this.waiting;
    btn.textContent = `🚒 ${open ? `Dispatch (${open})` : 'Dispatch'}`;
    const scenes = this.view.scenes as Msg[];
    // The scene on show ended: keep it up, as it last was, under its report.
    const was = (before?.scenes as Msg[] | undefined)?.find((x) => x.id === this.sceneId);
    if (this.sceneId && was && !scenes.some((x) => x.id === this.sceneId)) {
      this.ended = was;
      this.reportOpen = true;
      this.selected = []; this.drawing = null; this.box = null;
    }
    // A scene of ours that just opened comes up by itself, unless a report is being read.
    if (this.mine && !this.ended && !scenes.some((x) => x.id === this.sceneId)) {
      const fresh = scenes.find((x) => !(before?.scenes as Msg[] | undefined)?.some((b) => b.id === x.id));
      this.sceneId = fresh?.id ?? 0;
      if (fresh) { this.sceneHidden = false; if (!this.active) this.setMode(true); }
      if (!fresh) { this.selected = []; this.drawing = null; }
    }
    for (const card of (this.view.cards as Msg[]).filter((c) => !(before?.cards as Msg[] | undefined)?.some((b) => b.sceneId === c.sceneId)))
      if (before && !(this.active && this.ended?.id === card.sceneId)) this.host.toast(`Scene report: ${card.where}, ${card.score} (${card.outcome})`, card.score < 40 ? 'warning' : 'info');
    this.render();
  }

  private get me() { return this.host.conn.player || 'player'; }
  private get mine() { return this.view?.operator === 'manual' && this.view.owner === this.me; }
  /** Calls waiting for someone to send units. */
  private get waiting() { return ((this.view?.calls ?? []) as Msg[]).filter((c) => c.status === 'waiting' || c.status === 'proposed').length; }

  private command(cmd: Msg) {
    this.host.conn.action({ sideGame: { game: GAME, seat: 'operator', payload: encodeEmergencyCommand(cmd) } });
  }

  private get scene(): Msg | undefined { return (this.view?.scenes as Msg[] | undefined)?.find((s) => s.id === this.sceneId); }
  /** The scene in the middle of the screen: running, or ended under its report. */
  private get shown(): Msg | undefined { return this.ended ?? this.scene; }

  private stepScene() {
    if (this.mine && this.running && this.scene && !this.ended && this.drawing === null) this.command({ step: { sceneId: this.sceneId, steps: 2 } });
  }

  private unit(id: number): Msg | undefined { return (this.view?.units as Msg[] | undefined)?.find((u) => u.id === id); }

  private toBoard() {
    this.sceneId = 0; this.ended = null; this.sceneHidden = false;
    this.selected = []; this.drawing = null; this.box = null;
    this.render();
  }

  // -------------------------------------------------------------------------
  // The screen: top bar, tools, board, and the scene in the middle
  // -------------------------------------------------------------------------

  private render() {
    const v = this.view;
    this.host.setCalls?.(this.active && v ? (v.calls as Msg[]).filter((c) => c.status !== 'done').map((c) => ({
      x: c.x, y: c.y, icon: KIND_ICON[c.kind] ?? '•', waiting: c.status === 'waiting' || c.status === 'proposed', scene: c.status === 'scene',
    })) : null);
    if (!this.active) return;
    this.renderTop();
    this.renderTools();
    this.renderPanel();
    this.renderStage();
  }

  private renderTop() {
    const v = this.view;
    const c = this.host.clock?.();
    $('#em-date').textContent = c?.month ? `${MONTHS[c.month - 1]} ${c.day}, ${c.year}` : '…';
    document.querySelectorAll<HTMLButtonElement>('#em-speed button').forEach((b) => b.classList.toggle('active', Number(b.dataset.speed) === c?.speed));
    $('#em-conn').className = $('#conn').className;
    if (!v) { $('#em-name').textContent = 'No emergency services'; $('#em-stats').innerHTML = ''; $('#em-seat').innerHTML = ''; return; }
    const w = this.waiting;
    $('#em-name').textContent = v.name + (w && !this.shown ? ` (${w} waiting)` : '');
    const op = v.operator === 'manual' ? `run by ${esc(v.owner || 'nobody')}` : `the computer runs it (${esc(v.operator)})`;
    $('#em-stats').innerHTML = `${this.public ? '' : `<span class="stat ${v.cash < 0 ? 'neg' : ''}" title="The service's cash">${money(v.cash)}</span>`}
      <span class="stat" title="What the crews and the public think of the service">respect ${v.respect}</span>
      <span class="stat" title="How well calls go: feeds the service's standing with the city">service ${v.serviceQuality}</span>
      <span class="stat muted">${op}</span>`;
    $('#em-seat').innerHTML = this.pending
      ? `<span class="em-pending">⏳ ${esc(this.pending.text)}</span>`
      : this.mine ? `<button data-em="release">Hand back to the computer</button>` : `<button data-em="claim">Run it yourself</button>`;
    this.wire($('#em-seat'));
  }

  /** The tools down the left: the board's pages, or in a scene what to do with it and the orders. */
  private renderTools() {
    const el = $('#em-tools');
    const v = this.view;
    if (!v) { el.innerHTML = ''; return; }
    const s = this.shown;
    const btn = (meta: string, text: string, on = false, tip = '') => `<button data-em="${meta}" class="${on ? 'active' : ''}" ${tip ? `title="${esc(tip)}"` : ''}>${text}</button>`;
    let html = '';
    if (!s) {
      const w = this.waiting;
      html += '<h4>Board</h4>' + (['calls', 'units', 'reports', 'settings', 'log'] as EmTab[]).map((t) =>
        btn(`tab:${t}`, `${t[0].toUpperCase() + t.slice(1)}${t === 'calls' && w ? ` <span class="badge">${w}</span>` : ''}`, t === this.tab)).join('');
      if (!this.mine && !this.pending) html += '<p class="tip">The computer runs the service. Run it yourself (top bar) to send units and command scenes.</p>';
    } else {
      html += '<h4>Scene</h4>' + btn('board', '← Return to calls');
      html += this.sceneHidden ? btn('scene', 'Back to the scene') : btn('locate', '📍 Where is this?', false, 'Put the scene aside and show the city around it');
      if (this.ended) html += btn('report', this.reportOpen ? 'Close report' : 'Show report', this.reportOpen);
      else if (this.mine) {
        html += btn('run', this.running ? '⏸ Hold' : '▶ Run', !this.running, 'Hold the scene still while you give orders');
        html += btn('leave', 'Hand to crews', false, 'The crews finish it the way the computer would');
      }
      if (this.mine && !this.ended) {
        html += '<h4>Orders</h4>' + ORDERS.map(([o, label, tip]) => btn(`order:${o}`, label, this.order === o, tip)).join('');
        html += `<p class="tip">Click to select (shift adds; drag a box for several), then ${this.order === 'move' ? 'right-click where to go' : this.order === 'path' ? 'drag a path from the selected one' : 'click the target'}.</p>`;
      }
    }
    el.innerHTML = html;
    this.wire(el);
  }

  /** The board's page on the right, or the scene's news. */
  private renderPanel() {
    const el = $('#em-body');
    const v = this.view;
    if (!v) { el.innerHTML = '<p class="muted">No emergency services in this city.</p>'; return; }
    const s = this.shown;
    const wait = this.pending ? `<div class="co-hint em-pending">⏳ ${esc(this.pending.text)} <span class="muted small">It happens when the city's next day starts (not while paused).</span></div>` : '';
    const body = s ? this.renderSceneNews(s) : this.tab === 'calls' ? this.renderCalls() : this.tab === 'units' ? this.renderUnits()
      : this.tab === 'reports' ? this.renderReports() : this.tab === 'settings' ? this.renderSettings() : this.renderLog();
    const title = s ? '' : `<h3>${this.tab[0].toUpperCase() + this.tab.slice(1)}</h3>`;
    el.innerHTML = wait + title + body;
    this.wire(el);
    el.querySelectorAll<HTMLInputElement>('input[data-pick]').forEach((i) => i.addEventListener('change', () => {
      const [call, unit] = i.dataset.pick!.split(':').map(Number);
      const set = this.picks.get(call) ?? new Set<number>();
      if (i.checked) set.add(unit); else set.delete(unit);
      this.picks.set(call, set);
    }));
    el.querySelectorAll<HTMLInputElement>('[data-set]').forEach((i) => i.addEventListener('change', () => this.saveSettings()));
  }

  /** The middle: the scene, filling the map's place, with its report card over it once it ended. */
  private renderStage() {
    const el = $('#em');
    const s = this.shown;
    const show = !!s && !this.sceneHidden;
    el.classList.toggle('hidden', !show);
    if (!show) { el.innerHTML = ''; return; }
    const secs = Math.floor(s!.step / 4);
    const call = (this.view!.calls as Msg[]).find((c) => c.id === s!.callId);
    const card = this.ended ? (this.view!.cards as Msg[]).find((c) => c.sceneId === s!.id) : undefined;
    el.innerHTML = `
      <div class="em-stage-bar"><b>${KIND_ICON[s!.kind] ?? ''} ${esc(call?.text ?? '')}</b>
        <span class="muted">${Math.floor(secs / 60)}:${String(secs % 60).padStart(2, '0')} · ${s!.burning} alight${this.ended ? ' · over' : this.mine && !this.running ? ' · held' : ''}</span></div>
      <div class="em-stage"><canvas id="em-canvas" width="${s!.width * TILE}" height="${s!.height * TILE}"></canvas></div>
      ${this.ended && this.reportOpen ? `<div class="em-report">${this.renderReport(card)}</div>` : ''}`;
    this.wire(el);
    this.drawScene();
  }

  private wire(el: HTMLElement) {
    el.querySelectorAll<HTMLElement>('[data-em]').forEach((b) => b.addEventListener('click', (e) => this.click(b.dataset.em!, e)));
  }

  private click(what: string, e: Event) {
    const [verb, arg] = what.split(':');
    const n = Number(arg);
    switch (verb) {
      case 'tab': this.tab = arg as EmTab; this.toBoard(); break;
      case 'claim':
        if (this.pending) break;
        this.host.conn.action({ seat: { seat: GAME, owner: this.me, level: 0, style: 0, spendCap: 25000 } });
        this.wait('Taking over the service…', (v) => v.operator === 'manual' && v.owner === this.me);
        break;
      case 'release':
        if (this.pending) break;
        this.host.conn.action({ seat: { seat: GAME, owner: '', level: 2, style: 0, spendCap: 25000 } });
        this.wait('Handing the service back to the computer…', (v) => !(v.operator === 'manual' && v.owner === this.me));
        break;
      case 'lease':
        if (this.pending) break;
        this.host.conn.action({ lease: { game: GAME, buildingId: n } });
        this.wait('Leasing the station…', (v) => !(v.openStations as Msg[]).some((o) => o.buildingId === n));
        break;
      case 'go': { const c = (this.view!.calls as Msg[]).find((x) => x.id === n); if (c) this.host.jumpTo(c.x, c.y); break; }
      case 'send': {
        const units = [...(this.picks.get(n) ?? [])];
        if (!units.length) { this.host.toast('Tick the units to send first.', 'warning'); break; }
        this.command({ dispatch: { callId: n, unitIds: units } });
        this.picks.delete(n);
        this.host.toast(`Sent ${units.map((u) => this.unit(u)?.name ?? '?').join(', ')}.`);
        break;
      }
      case 'take': this.command({ take: { callId: n } }); this.host.toast('Taking the scene: it opens when the crews arrive.'); break;
      case 'watch': this.ended = null; this.sceneId = n; this.selected = []; this.sceneHidden = false; this.render(); break;
      case 'board': this.toBoard(); break;
      case 'locate': {
        const s = this.shown;
        const c = s && (this.view!.calls as Msg[]).find((x) => x.id === s.callId);
        this.sceneHidden = true;
        this.render();
        if (c) this.host.jumpTo(c.x, c.y);
        break;
      }
      case 'scene': this.sceneHidden = false; this.render(); break;
      case 'report': this.reportOpen = !this.reportOpen; this.render(); break;
      case 'leave': this.command({ leave: { sceneId: this.sceneId } }); break;
      case 'run': this.running = !this.running; this.render(); break;
      case 'order': this.order = arg as Order; this.render(); break;
      case 'buy': { const [lease, kind] = arg.split('/').map(Number); this.command({ buy: { leaseId: lease, kind } }); break; }
      case 'sell': this.command({ sell: { unitId: n } }); break;
      case 'gear': { const [unit, item] = arg.split('/'); this.command({ gear: { unitId: Number(unit), item } }); break; }
      case 'shifts': this.saveSettings(n); break;
      case 'crooked': { const [kind, district, on] = arg.split('/'); this.command({ crooked: { kind, district: Number(district), on: on === 'on' } }); break; }
    }
    e.preventDefault();
  }

  private renderCalls(): string {
    const v = this.view!;
    const calls = (v.calls as Msg[]).slice().sort((a, b) => Number(a.status === 'done') - Number(b.status === 'done') || b.id - a.id);
    const head = this.renderRatings() + (v.crooked?.strike ? `<p class="em-strike"><b>The crews are on strike.</b> Nobody answers calls until it ends.</p>` : '');
    if (!calls.length) return head + `<p class="muted">No calls. Fires, crimes and injuries in the city come in here.</p>`;
    const ready = (v.units as Msg[]).filter((u) => u.state === 'ready');
    return head + calls.map((c) => {
      const units = (c.unitIds as number[]).map((id) => this.unit(id)?.name ?? '?').join(', ');
      let act = '';
      if (this.mine && (c.status === 'waiting' || c.status === 'proposed')) {
        const choices = [...ready, ...(v.units as Msg[]).filter((u) => u.state === 'proposed' && u.callId === c.id)];
        const set = this.picks.get(c.id) ?? new Set<number>(c.status === 'proposed' ? c.unitIds : []);
        this.picks.set(c.id, set);
        act = choices.length
          ? `<div class="em-picks">${choices.map((u) => `<label><input type="checkbox" data-pick="${c.id}:${u.id}" ${set.has(u.id) ? 'checked' : ''}> ${UNIT_ICON[u.kind]} ${esc(u.name)}</label>`).join('')}</div>
             <button class="mini" data-em="send:${c.id}">Send</button>`
          : `<span class="muted small">No units free.</span>`;
        if (v.style === 2) act += ` <button class="mini" data-em="take:${c.id}" title="Run this scene yourself">Take scene</button>`;
      }
      if (this.mine && c.status === 'rolling' && v.style === 2) act = `<button class="mini" data-em="take:${c.id}">Take scene</button>`;
      if (c.status === 'scene') act = `<button class="mini" data-em="watch:${c.sceneId}">${this.mine ? 'Run scene' : 'Watch'}</button>`;
      const status = c.status === 'done' ? `done: ${c.outcome}` : c.status === 'waiting' && c.waitedDays ? `waiting ${c.waitedDays}d` : c.status;
      return `<div class="em-call ${c.status}">
        <div>${KIND_ICON[c.kind] ?? '•'} <b>${esc(c.text)}</b> <span class="muted">sev ${c.severity} · ${status}${c.minutes ? ` · ${c.minutes} min` : ''}</span>
          <a href="#" data-em="go:${c.id}">map</a></div>
        ${units ? `<div class="muted small">${esc(units)}</div>` : ''}
        ${act ? `<div>${act}</div>` : ''}
      </div>`;
    }).join('');
  }

  private renderUnits(): string {
    const v = this.view!;
    const stations = v.stations as Msg[];
    const open = v.openStations as Msg[];
    let head = '';
    if (!stations.length) head += open.length
      ? `<p class="muted">The service has no stations yet: the city's fire stations, police stations, clinics and hospitals only send units once they're leased to it.</p>`
      : `<p class="muted">The service has no stations. Build a fire station, police station, clinic or hospital, then lease it to ${esc(v.name)}.</p>`;
    if (open.length) head += `<div class="em-station"><b>Not leased yet</b>${open.map((o) => `<div>${esc(o.kind.replace('-', ' '))} at ${o.x},${o.y}
      ${this.mine ? this.pending ? '<span class="muted">⏳</span>' : `<button class="mini" data-em="lease:${o.buildingId}">Lease it</button>` : '<span class="muted small">the mayor, or whoever runs the service, can lease it</span>'}</div>`).join('')}</div>`;
    return head + stations.map((s) => {
      const kind = s.kind === 'fire-station' ? 0 : s.kind === 'police-station' ? 1 : 2;
      const units = (v.units as Msg[]).filter((u) => u.stationLeaseId === s.leaseId);
      return `<div class="em-station"><b>${esc(s.name)}</b>
        ${units.map((u) => `<div>${UNIT_ICON[u.kind]} ${esc(u.name)} <span class="muted">${u.crew} crew · ${u.state}${u.backInDays ? ` (${u.backInDays}d)` : ''}</span>
          ${this.mine && u.state === 'ready' ? `<button class="mini" data-em="sell:${u.id}">Sell</button>` : ''}
          ${GEAR.filter((g) => g[1] === u.kind).map(([item, , name, does]) => (u.gear as string[]).includes(item)
            ? `<div class="small">✓ ${name} <span class="muted">${does}</span></div>`
            : this.mine ? `<div class="small"><button class="mini" data-em="gear:${u.id}/${item}">Fit ${name} (${money(GEAR_PRICE)})</button> <span class="muted">${does}</span></div>` : '').join('')}</div>`).join('')}
        ${this.mine ? `<button class="mini" data-em="buy:${s.leaseId}/${kind}">Buy ${UNIT_NAME[kind]}</button>` : ''}
        ${this.mine && kind === 1 && !units.some((u) => u.kind === SWAT) ? `<button class="mini" data-em="buy:${s.leaseId}/${SWAT}" title="Four officers in armour: they breach buildings and end hostage scenes">Buy a SWAT team</button>` : ''}
      </div>`;
    }).join('');
  }

  private renderReports(): string {
    const cards = (this.view!.cards as Msg[]).slice().reverse();
    if (this.public) return this.renderRatings() + `<p class="muted">Scene reports are the service's own business.</p>`;
    if (!cards.length) return this.renderRatings() + `<p class="muted">Each scene you run gets a report card here. Lives saved count for most; harm your crews cause costs more than a slow response.</p>`;
    return this.renderRatings() + cards.map((c) => `<div class="em-card ${c.score < 40 ? 'bad' : ''}">
      <div><b>${esc(c.where)}</b> <span class="em-score">${c.score}</span> <span class="muted">${c.outcome}</span></div>
      <div class="muted small">${c.responseMinutes} min response · ${c.saved} saved · ${c.lost} lost · ${c.harmedByCrews} hurt by crews · ${c.friendlyFire} friendly fire · ${c.unauthorizedForce} unauthorised force · ${c.propertyLostPercent}% property lost · rules of engagement ${c.rulesOfEngagement}${c.hostagesFreed + c.hostagesLost ? ` · hostages ${c.hostagesFreed} freed, ${c.hostagesLost} lost` : ''}</div>
      ${(c.notes as string[]).map((n) => `<div class="small">${esc(n)}</div>`).join('')}
    </div>`).join('');
  }

  /** Response-time grades over the last thirty days, by kind of call: they count toward the service's rating. */
  private renderRatings(): string {
    const r = (this.view!.ratings as Msg[]).filter((x) => x.calls > 0);
    if (!r.length) return `<p class="muted small">Response ratings: no calls answered in the last 30 days.</p>`;
    return `<div class="em-ratings"><b>Response, last 30 days</b> ${r.map((x) =>
      `<span>${esc(x.kind)} <b class="grade grade-${x.grade}">${x.grade}</b> <span class="muted">${x.minutes} min · ${x.calls}</span></span>`).join(' ')}</div>`;
  }

  /** The districts the service works in: where its stations and calls are. */
  private districts(): number[] {
    const v = this.view!;
    const across = Math.ceil((this.host.cityWidth?.() ?? 128) / 32);
    const at = [...(v.stations as Msg[]), ...(v.calls as Msg[])].map((p) => Math.floor(p.y / 32) * across + Math.floor(p.x / 32));
    return [...new Set(at)].sort((a, b) => a - b);
  }

  /** The crooked ways: only where every player has allowed crooked services, and each a risk of the papers finding out. */
  private renderCrooked(): string {
    const k = (this.view!.crooked ?? { slowWalk: -1, racket: -1, lookAway: -1 }) as Msg;
    let out = `<h4>Crooked ways ${k.exposed ? `<span class="neg">caught out ${k.exposed}×</span>` : ''}</h4>`;
    if (!k.allowed) return out + `<p class="muted small">${esc(k.whyNot || 'Not allowed here: every player must allow crooked services first.')}</p>`;
    for (const [kind, name, note, district] of CROOKED) {
      const at: number = kind === 'slow-walk' ? k.slowWalk : kind === 'racket' ? k.racket : kind === 'look-away' ? k.lookAway : -1;
      const on = district ? at >= 0 : kind === 'overtime' ? k.overtime : k.strike;
      let act = '';
      if (this.mine) act = on ? `<button class="mini" data-em="crooked:${kind}/0/off">Stop${district ? ` (district ${at + 1})` : ''}</button>`
        : district ? this.districts().map((d) => `<button class="mini" data-em="crooked:${kind}/${d}/on">district ${d + 1}</button>`).join(' ')
        : `<button class="mini" data-em="crooked:${kind}/0/on">Start</button>`;
      else if (on) act = `<span class="small">on${district ? `, in district ${at + 1}` : ''}</span>`;
      out += `<div class="em-opt ${on ? 'em-crooked-on' : ''}"><b>${name}</b> <span class="muted small">${note}</span><div>${act}</div></div>`;
    }
    return out;
  }

  private renderSettings(): string {
    const v = this.view!;
    const dis = this.mine ? '' : 'disabled';
    return `
      <p class="muted small">${this.mine ? 'How much you run yourself.' : 'Run the service to change these.'}</p>
      ${STYLES.map((s, i) => `<label class="em-opt"><input type="radio" name="em-style" value="${i}" data-set ${v.style === i ? 'checked' : ''} ${dis}> <b>${s}</b><br><span class="muted small">${STYLE_HELP[i]}</span></label>`).join('')}
      <label class="em-opt"><input type="checkbox" id="em-pick" data-set ${v.pickResponders ? 'checked' : ''} ${dis}> <b>Pick responders</b><br><span class="muted small">The computer proposes who goes and waits a day for you to change it.</span></label>
      <label class="em-opt"><input type="checkbox" id="em-heads" data-set ${v.crewsUseHeads ? 'checked' : ''} ${dis}> <b>Crews use their heads</b><br><span class="muted small">Crews with no orders do the obvious job nearest them. Off: they stand still until told.</span></label>
      <label class="em-opt"><input type="checkbox" id="em-watch" data-set ${v.driversWatchOut ? 'checked' : ''} ${dis}> <b>Drivers watch out</b><br><span class="muted small">Vehicles stop for people in the way. Off: they drive on and hit them. Anyone hit is hurt or killed, the vehicle is off the road for 30 days, and the service's respect and standing fall.</span></label>
      <h4>Shifts</h4>
      <div>${[1, 2, 3].map((n) => this.mine ? `<button class="mini ${v.shifts === n ? 'active' : ''}" data-em="shifts:${n}">${n === 3 ? '3: round the clock' : n}</button>` : v.shifts === n ? `<b>${n}</b>` : '').join(' ')}</div>
      <p class="muted small">Wages scale with the shifts worked. With fewer than three, each unit is off some days, staggered so a station is rarely empty.</p>
      ${this.renderCrooked()}`;
  }

  private saveSettings(shifts = 0) {
    const style = Number((document.querySelector('input[name="em-style"]:checked') as HTMLInputElement | null)?.value ?? this.view!.style);
    this.command({ settings: {
      style, pickResponders: $<HTMLInputElement>('#em-pick').checked, crewsUseHeads: $<HTMLInputElement>('#em-heads').checked,
      driversWatchOut: $<HTMLInputElement>('#em-watch').checked, name: '', shifts,
    } });
  }

  private renderLog(): string {
    const log = (this.view!.log as string[]).slice().reverse();
    return log.length ? log.map((l) => `<div class="small">${esc(l)}</div>`).join('') : `<p class="muted">Nothing yet.</p>`;
  }

  // -------------------------------------------------------------------------
  // The scene
  // -------------------------------------------------------------------------

  /** Who is selected, what just happened at the scene, and who is on the way. */
  private renderSceneNews(s: Msg): string {
    const sel = this.selected.map((id) => {
      const p = (s.people as Msg[]).find((x) => x.id === id);
      if (p) return `${ROLE_NAME[p.role]} (${this.unit(p.unitId)?.name ?? ''})`;
      return this.unit(id)?.name ?? '';
    }).filter(Boolean).join(', ');
    const call = (this.view!.calls as Msg[]).find((c) => c.id === s.callId);
    return `<h3>${KIND_ICON[s.kind] ?? ''} ${esc(call?.text ?? 'Scene')}</h3>
      <p class="muted small">${this.ended ? 'The scene is over.' : this.mine ? (sel ? `Selected: ${esc(sel)}` : 'Nobody selected.') : 'Watching: the crews run it.'}</p>
      ${(s.arriving as Msg[]).length && !this.ended ? `<p class="small">On the way: ${(s.arriving as Msg[]).map((a) => `${esc(this.unit(a.unitId)?.name ?? '?')} in ${Math.max(0, Math.ceil((a.atStep - s.step) / 4))}s`).join(', ')}</p>` : ''}
      <h4>What happened</h4>
      <div class="em-events">${(s.events as string[]).slice().reverse().map((e) => `<div class="small">${esc(e)}</div>`).join('') || '<p class="muted small">Nothing yet.</p>'}</div>`;
  }

  /** The ended scene's report card: the score and what made it. */
  private renderReport(c: Msg | undefined): string {
    if (this.public) return `<h3>Scene over</h3><p class="muted">Scene reports are the service's own business.</p>`;
    if (!c) return `<h3>Scene over</h3><p class="muted">⏳ Writing the report…</p>`;
    const row = (k: string, v: string | number) => `<tr><td>${k}</td><td><b>${v}</b></td></tr>`;
    return `<h3>Report: ${esc(c.where)}</h3>
      <div class="em-score ${c.score < 40 ? 'neg' : ''}">${c.score}<span class="muted small"> / 100 · ${esc(c.outcome)}</span></div>
      <table class="em-card-table">
        ${row('Response', `${c.responseMinutes} min`)}${row('Saved', c.saved)}${row('Lost', c.lost)}
        ${c.harmedByCrews ? row('Hurt by crews', c.harmedByCrews) : ''}${c.friendlyFire ? row('Friendly fire', c.friendlyFire) : ''}
        ${c.unauthorizedForce ? row('Unauthorised force', c.unauthorizedForce) : ''}${row('Property lost', `${c.propertyLostPercent}%`)}
        ${row('Rules of engagement', c.rulesOfEngagement)}${c.hostagesFreed + c.hostagesLost ? row('Hostages', `${c.hostagesFreed} freed, ${c.hostagesLost} lost`) : ''}
      </table>
      ${(c.notes as string[]).map((n) => `<div class="small">${esc(n)}</div>`).join('')}
      <div class="row-flex" style="margin-top:10px"><button data-em="report">See the scene</button><button class="active" data-em="board">Return to calls</button></div>`;
  }

  private drawScene() {
    const s = this.shown;
    const cv = document.querySelector<HTMLCanvasElement>('#em-canvas');
    if (!s || !cv) return;
    // Fit the picture to the middle of the screen, square tiles.
    const stage = cv.parentElement!;
    const k = Math.max(0.5, Math.min((stage.clientWidth - 16) / cv.width, (stage.clientHeight - 16) / cv.height));
    cv.style.width = `${Math.floor(cv.width * k)}px`;
    cv.style.height = `${Math.floor(cv.height * k)}px`;
    const g = cv.getContext('2d')!;
    const tiles = s.tiles as Uint8Array, fire = s.fire as Uint8Array;
    for (let y = 0; y < s.height; y++)
      for (let x = 0; x < s.width; x++) {
        const i = y * s.width + x;
        g.fillStyle = TILE_COLOR[tiles[i]] ?? '#000';
        g.fillRect(x * TILE, y * TILE, TILE, TILE);
        if (fire[i] > 0) { g.fillStyle = `rgba(255,${Math.max(40, 160 - fire[i])},0,${0.35 + fire[i] / 160})`; g.fillRect(x * TILE, y * TILE, TILE, TILE); }
      }
    const path = (cells: number[], colour: string) => {
      if (!cells.length) return;
      g.strokeStyle = colour; g.lineWidth = 2; g.beginPath();
      cells.forEach((c, k) => { const px = (c % s.width) * TILE + TILE / 2, py = Math.floor(c / s.width) * TILE + TILE / 2; if (k) g.lineTo(px, py); else g.moveTo(px, py); });
      g.stroke();
    };
    for (const v of s.vehicles as Msg[]) {
      path(v.path, 'rgba(255,255,255,0.6)');
      g.fillStyle = ['#c0392b', '#2c5aa0', '#e8e8e8', '#20262e'][v.kind];
      g.fillRect(v.x / 16 * TILE - TILE * 0.7, v.y / 16 * TILE - TILE * 0.45, TILE * 1.4, TILE * 0.9);
      if (this.selected.includes(v.id)) { g.strokeStyle = '#ffeb3b'; g.lineWidth = 2; g.strokeRect(v.x / 16 * TILE - TILE * 0.8, v.y / 16 * TILE - TILE * 0.55, TILE * 1.6, TILE * 1.1); }
    }
    for (const p of s.people as Msg[]) {
      if (p.state === 'escaped') continue;
      const px = p.x / 16 * TILE, py = p.y / 16 * TILE;
      if (p.role <= 2) path(p.path, 'rgba(255,235,59,0.5)');
      g.fillStyle = ROLE_COLOR[p.role];
      g.beginPath(); g.arc(px, py, TILE * 0.33, 0, Math.PI * 2); g.fill();
      if (p.armed) { g.strokeStyle = '#000'; g.lineWidth = 2; g.stroke(); }
      if (p.down) { g.strokeStyle = p.state === 'dead' ? '#000' : '#fff'; g.lineWidth = 1.5; g.beginPath(); g.moveTo(px - 4, py - 4); g.lineTo(px + 4, py + 4); g.moveTo(px + 4, py - 4); g.lineTo(px - 4, py + 4); g.stroke(); }
      if (this.selected.includes(p.id)) { g.strokeStyle = '#ffeb3b'; g.lineWidth = 2; g.beginPath(); g.arc(px, py, TILE * 0.5, 0, Math.PI * 2); g.stroke(); }
      if (p.health < 100 && p.state !== 'dead') { g.fillStyle = '#300'; g.fillRect(px - 6, py - 9, 12, 2); g.fillStyle = '#4caf50'; g.fillRect(px - 6, py - 9, 12 * p.health / 100, 2); }
    }
    // Lines of fire: police blue, the suspect's red, a taser's yellow, fading over a few seconds.
    for (const shot of (s.shots ?? []) as Msg[]) {
      const age = (s.step - shot.step) / 12;
      if (age >= 1) continue;
      g.globalAlpha = 1 - age;
      g.strokeStyle = shot.taser ? '#ffd23a' : shot.police ? '#5aa0ff' : '#ff3b30';
      g.lineWidth = 2;
      g.beginPath(); g.moveTo(shot.fromX / 16 * TILE, shot.fromY / 16 * TILE); g.lineTo(shot.toX / 16 * TILE, shot.toY / 16 * TILE); g.stroke();
      if (shot.hit) { g.beginPath(); g.arc(shot.toX / 16 * TILE, shot.toY / 16 * TILE, 4, 0, Math.PI * 2); g.stroke(); }
      g.globalAlpha = 1;
    }
    if (this.drawing) path(this.drawing, '#ffeb3b');
    if (this.box?.moved) {
      const b = this.box;
      g.fillStyle = 'rgba(255,225,77,0.12)'; g.strokeStyle = '#ffe14d'; g.lineWidth = 1.5;
      const x = Math.min(b.x0, b.x1) * TILE, y = Math.min(b.y0, b.y1) * TILE, w = Math.abs(b.x1 - b.x0) * TILE, h = Math.abs(b.y1 - b.y0) * TILE;
      g.fillRect(x, y, w, h); g.strokeRect(x, y, w, h);
    }
    if (!this.mine || this.ended) { cv.onmousedown = cv.onmousemove = cv.onmouseup = null; cv.oncontextmenu = (e) => e.preventDefault(); return; }
    const exact = (e: MouseEvent) => {
      const r = cv.getBoundingClientRect();
      return { x: (e.clientX - r.left) / r.width * s.width, y: (e.clientY - r.top) / r.height * s.height };
    };
    const at = (e: MouseEvent) => { const p = exact(e); return { x: Math.floor(p.x), y: Math.floor(p.y) }; };
    cv.oncontextmenu = (e) => {
      e.preventDefault();
      const t = at(e);
      if (this.selected.length) this.command({ move: { sceneId: s.id, ids: this.selected, x: t.x, y: t.y } });
    };
    cv.onmousedown = (e) => {
      if (e.button !== 0) return;
      const t = at(e);
      const hit = this.hit(t.x, t.y);
      if (this.order === 'path' && this.selected.length === 1 && (!hit || hit === this.selected[0])) { this.drawing = [t.y * s.width + t.x]; return; }
      if (this.order === 'move' || this.order === 'path' || (hit && this.isCrewOrVehicle(hit) && !e.altKey && this.order !== 'treat' && this.order !== 'warn' && this.order !== 'arrest')) {
        if (hit && this.isCrewOrVehicle(hit)) this.selected = e.shiftKey ? [...new Set([...this.selected, hit])] : [hit];
        else {
          if (!e.shiftKey) this.selected = [];
          // On open ground, a drag becomes a box over everyone to select.
          if (this.order === 'move') { const p = exact(e); this.box = { x0: p.x, y0: p.y, x1: p.x, y1: p.y, moved: false }; }
        }
        this.render();
        return;
      }
      if (!this.selected.length) return;
      this.command({ task: { sceneId: s.id, ids: this.selected, task: TASKS[this.order], x: t.x, y: t.y, targetId: hit ?? 0 } });
    };
    cv.onmousemove = (e) => {
      if (this.box) {
        const p = exact(e);
        this.box.x1 = p.x; this.box.y1 = p.y;
        this.box.moved ||= Math.abs(p.x - this.box.x0) + Math.abs(p.y - this.box.y0) > 0.5;
        this.drawScene();
        return;
      }
      if (!this.drawing) return;
      const t = at(e), c = t.y * s.width + t.x;
      if (this.drawing[this.drawing.length - 1] === c) return;
      // Fill a fast drag in with steps between neighbouring tiles.
      let last = this.drawing[this.drawing.length - 1];
      while (last !== c) {
        let lx = last % s.width, ly = Math.floor(last / s.width);
        if (lx !== t.x) lx += Math.sign(t.x - lx); else ly += Math.sign(t.y - ly);
        last = ly * s.width + lx;
        this.drawing.push(last);
      }
      this.drawScene();
    };
    cv.onmouseup = (e) => {
      if (this.box) {
        const b = this.box;
        this.box = null;
        if (b.moved) {
          const [x0, x1] = [Math.min(b.x0, b.x1), Math.max(b.x0, b.x1)], [y0, y1] = [Math.min(b.y0, b.y1), Math.max(b.y0, b.y1)];
          const inside = (x: number, y: number) => x / 16 >= x0 && x / 16 <= x1 && y / 16 >= y0 && y / 16 <= y1;
          const ids = [...(s.people as Msg[]).filter((p) => p.role <= 2 && p.state !== 'escaped' && inside(p.x, p.y)).map((p) => p.id),
            ...(s.vehicles as Msg[]).filter((v) => inside(v.x, v.y)).map((v) => v.id)];
          this.selected = e.shiftKey ? [...new Set([...this.selected, ...ids])] : ids;
        }
        this.render();
        return;
      }
      if (!this.drawing) return;
      this.command({ path: { sceneId: s.id, id: this.selected[0], tiles: this.drawing } });
      this.drawing = null;
    };
  }

  /** The person or vehicle on a tile: crews and vehicles first. */
  private hit(x: number, y: number): number | null {
    const s = this.scene!;
    const near = (px: number, py: number) => Math.abs(px / 16 - 0.5 - x) <= 0.6 && Math.abs(py / 16 - 0.5 - y) <= 0.6;
    const p = (s.people as Msg[]).filter((q) => q.state !== 'escaped' && near(q.x, q.y)).sort((a, b) => Number(a.role > 2) - Number(b.role > 2))[0];
    if (p && p.role <= 2) return p.id;
    const v = (s.vehicles as Msg[]).find((q) => Math.abs(q.x / 16 - 0.5 - x) <= 1 && Math.abs(q.y / 16 - 0.5 - y) <= 1);
    if (v) return v.id;
    return p?.id ?? null;
  }

  private isCrewOrVehicle(id: number): boolean {
    const s = this.scene!;
    const p = (s.people as Msg[]).find((q) => q.id === id);
    return p ? p.role <= 2 : (s.vehicles as Msg[]).some((v) => v.id === id);
  }
}
