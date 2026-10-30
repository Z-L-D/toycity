// The emergency services' own view: a dispatch board over the map. Calls come in, units go out, and a call played
// at the scene opens a small top-down picture of it where the player moves crews, draws paths and gives orders.
// Nothing here aims or drives carefully for the player: the switches in Settings decide how much the crews do alone.
import { decodeEmergencyView, encodeEmergencyCommand, type Connection, type Msg } from './net';

export interface EmergencyHost {
  conn: Connection;
  jumpTo(x: number, y: number): void;
  toast(text: string, severity?: string): void;
}

type EmTab = 'calls' | 'units' | 'reports' | 'settings' | 'log';
type Order = 'move' | 'path' | 'hose' | 'treat' | 'warn' | 'arrest' | 'taser' | 'shoot';

const GAME = 'emergency';
const TILE = 14;
const $ = <T extends HTMLElement = HTMLElement>(sel: string) => document.querySelector(sel) as T;
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
const money = (n: number) => (n < 0 ? '-$' : '$') + Math.abs(Math.round(n)).toLocaleString('en-US');
const UNIT_ICON = ['🚒', '🚓', '🚑'];
const UNIT_NAME = ['engine', 'patrol car', 'ambulance'];
const KIND_ICON: Record<string, string> = { fire: '🔥', crime: '🚨', injury: '🩹' };
const STYLES = ['Full command', 'Scene command', 'Focus'];
const STYLE_HELP = [
  'You do everything: every call waits for you to pick who goes, and you run every scene. Calls left waiting get worse, then are lost.',
  'The computer picks who goes to each call; you run every scene they reach.',
  'The computer picks who goes and settles each call as it would without you. You step into only the scenes you choose (Take scene on the call).',
];
const TASKS: Record<Order, number> = { move: 0, path: 0, hose: 1, treat: 2, arrest: 3, warn: 4, shoot: 5, taser: 6 };
const ROLE_COLOR = ['#e04f3a', '#f2f2f2', '#3f7fe0', '#9aa0a8', '#f0c030', '#a050d0'];
const ROLE_NAME = ['firefighter', 'paramedic', 'officer', 'bystander', 'casualty', 'suspect'];
const TILE_COLOR = ['#c9c0a0', '#6e6e6e', '#a88d6c', '#4a7fe0', '#7cbf6a', '#3a3330'];

export class Emergency {
  view: Msg | null = null;
  private open = false;
  private tab: EmTab = 'calls';
  private public = false;
  /** The scene being watched, if any. */
  private sceneId = 0;
  private running = true;
  private order: Order = 'move';
  private selected: number[] = [];
  private drawing: number[] | null = null;
  /** Units ticked for each call, before Send. */
  private picks = new Map<number, Set<number>>();

  constructor(private readonly host: EmergencyHost) {
    $('#btn-emergency').addEventListener('click', () => this.toggle());
    window.setInterval(() => this.stepScene(), 500);
  }

  private toggle() {
    this.open = !this.open;
    $('#em').classList.toggle('hidden', !this.open);
    this.render();
  }

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
    const open = (this.view.calls as Msg[]).filter((c) => c.status === 'waiting' || c.status === 'proposed').length;
    btn.textContent = `🚒 ${open ? `Dispatch (${open})` : 'Dispatch'}`;
    // A scene of ours that just opened comes up by itself.
    const scenes = this.view.scenes as Msg[];
    if (this.mine && !scenes.some((s) => s.id === this.sceneId)) {
      const fresh = scenes.find((s) => !(before?.scenes as Msg[] | undefined)?.some((b) => b.id === s.id));
      this.sceneId = fresh?.id ?? 0;
      if (fresh && !this.open) this.toggle();
      if (!fresh) { this.selected = []; this.drawing = null; }
    }
    for (const card of (this.view.cards as Msg[]).filter((c) => !(before?.cards as Msg[] | undefined)?.some((b) => b.sceneId === c.sceneId)))
      if (before) this.host.toast(`Scene report: ${card.where}, ${card.score} (${card.outcome})`, card.score < 40 ? 'warning' : 'info');
    if (this.open) this.render();
  }

  private get me() { return this.host.conn.player || 'player'; }
  private get mine() { return this.view?.operator === 'manual' && this.view.owner === this.me; }

  private command(cmd: Msg) {
    this.host.conn.action({ sideGame: { game: GAME, seat: 'operator', payload: encodeEmergencyCommand(cmd) } });
  }

  private get scene(): Msg | undefined { return (this.view?.scenes as Msg[] | undefined)?.find((s) => s.id === this.sceneId); }

  private stepScene() {
    if (this.mine && this.running && this.scene && this.drawing === null) this.command({ step: { sceneId: this.sceneId, steps: 2 } });
  }

  private unit(id: number): Msg | undefined { return (this.view?.units as Msg[] | undefined)?.find((u) => u.id === id); }

  // -------------------------------------------------------------------------
  // The board
  // -------------------------------------------------------------------------

  private render() {
    const v = this.view;
    const el = $('#em');
    if (!this.open || !v) return;
    const op = v.operator === 'manual' ? `run by ${esc(v.owner || 'nobody')}` : v.operator;
    const head = `
      <div class="em-head">
        <b>🚒 ${esc(v.name)}</b>
        ${this.public ? '' : `<span>${money(v.cash)}</span>`}
        <span title="What the crews and the public think of the service">respect ${v.respect}</span>
        <span title="How well calls go: feeds the service's standing with the city">service ${v.serviceQuality}</span>
        <span class="muted">${op}</span>
        <span class="spacer"></span>
        ${this.pending ? `<span class="em-pending">⏳ ${esc(this.pending.text)} <span class="muted small">It happens when the city's next day starts (not while paused).</span></span>`
          : this.mine ? `<button class="mini" data-em="release">Hand back to the computer</button>` : `<button class="mini" data-em="claim">Run it yourself</button>`}
        <button class="mini" data-em="close">✕</button>
      </div>
      <nav class="em-tabs">${(['calls', 'units', 'reports', 'settings', 'log'] as EmTab[]).map((t) =>
        `<button data-emtab="${t}" class="${t === this.tab ? 'active' : ''}">${t[0].toUpperCase() + t.slice(1)}</button>`).join('')}</nav>`;
    const body = this.scene ? this.renderScene() : this.tab === 'calls' ? this.renderCalls() : this.tab === 'units' ? this.renderUnits()
      : this.tab === 'reports' ? this.renderReports() : this.tab === 'settings' ? this.renderSettings() : this.renderLog();
    el.innerHTML = head + `<div class="em-body">${body}</div>`;
    el.querySelectorAll<HTMLButtonElement>('[data-emtab]').forEach((b) => b.addEventListener('click', () => {
      this.tab = b.dataset.emtab as EmTab; this.sceneId = 0; this.render();
    }));
    el.querySelectorAll<HTMLElement>('[data-em]').forEach((b) => b.addEventListener('click', (e) => this.click(b.dataset.em!, e)));
    el.querySelectorAll<HTMLInputElement>('input[data-pick]').forEach((i) => i.addEventListener('change', () => {
      const [call, unit] = i.dataset.pick!.split(':').map(Number);
      const set = this.picks.get(call) ?? new Set<number>();
      if (i.checked) set.add(unit); else set.delete(unit);
      this.picks.set(call, set);
    }));
    el.querySelectorAll<HTMLInputElement>('[data-set]').forEach((i) => i.addEventListener('change', () => this.saveSettings()));
    if (this.scene) this.drawScene();
  }

  private click(what: string, e: Event) {
    const [verb, arg] = what.split(':');
    const n = Number(arg);
    switch (verb) {
      case 'close': this.toggle(); break;
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
        break;
      }
      case 'take': this.command({ take: { callId: n } }); break;
      case 'watch': this.sceneId = n; this.selected = []; this.render(); break;
      case 'board': this.sceneId = 0; this.render(); break;
      case 'leave': this.command({ leave: { sceneId: this.sceneId } }); break;
      case 'run': this.running = !this.running; this.render(); break;
      case 'order': this.order = arg as Order; this.render(); break;
      case 'buy': { const [lease, kind] = arg.split('/').map(Number); this.command({ buy: { leaseId: lease, kind } }); break; }
      case 'sell': this.command({ sell: { unitId: n } }); break;
    }
    e.preventDefault();
  }

  private renderCalls(): string {
    const v = this.view!;
    const calls = (v.calls as Msg[]).slice().sort((a, b) => Number(a.status === 'done') - Number(b.status === 'done') || b.id - a.id);
    if (!calls.length) return `<p class="muted">No calls. Fires, crimes and injuries in the city come in here.</p>`;
    const ready = (v.units as Msg[]).filter((u) => u.state === 'ready');
    return calls.map((c) => {
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
          ${this.mine && u.state === 'ready' ? `<button class="mini" data-em="sell:${u.id}">Sell</button>` : ''}</div>`).join('')}
        ${this.mine ? `<button class="mini" data-em="buy:${s.leaseId}/${kind}">Buy ${UNIT_NAME[kind]}</button>` : ''}
      </div>`;
    }).join('');
  }

  private renderReports(): string {
    const cards = (this.view!.cards as Msg[]).slice().reverse();
    if (this.public) return `<p class="muted">Scene reports are the service's own business.</p>`;
    if (!cards.length) return `<p class="muted">Each scene you run gets a report card here. Lives saved count for most; harm your crews cause costs more than a slow response.</p>`;
    return cards.map((c) => `<div class="em-card ${c.score < 40 ? 'bad' : ''}">
      <div><b>${esc(c.where)}</b> <span class="em-score">${c.score}</span> <span class="muted">${c.outcome}</span></div>
      <div class="muted small">${c.responseMinutes} min response · ${c.saved} saved · ${c.lost} lost · ${c.harmedByCrews} hurt by crews · ${c.friendlyFire} friendly fire · ${c.unauthorizedForce} unauthorised force · ${c.propertyLostPercent}% property lost</div>
      ${(c.notes as string[]).map((n) => `<div class="small">${esc(n)}</div>`).join('')}
    </div>`).join('');
  }

  private renderSettings(): string {
    const v = this.view!;
    const dis = this.mine ? '' : 'disabled';
    return `
      <p class="muted small">${this.mine ? 'How much you run yourself.' : 'Run the service to change these.'}</p>
      ${STYLES.map((s, i) => `<label class="em-opt"><input type="radio" name="em-style" value="${i}" data-set ${v.style === i ? 'checked' : ''} ${dis}> <b>${s}</b><br><span class="muted small">${STYLE_HELP[i]}</span></label>`).join('')}
      <label class="em-opt"><input type="checkbox" id="em-pick" data-set ${v.pickResponders ? 'checked' : ''} ${dis}> <b>Pick responders</b><br><span class="muted small">The computer proposes who goes and waits a day for you to change it.</span></label>
      <label class="em-opt"><input type="checkbox" id="em-heads" data-set ${v.crewsUseHeads ? 'checked' : ''} ${dis}> <b>Crews use their heads</b><br><span class="muted small">Crews with no orders do the obvious job nearest them. Off: they stand still until told.</span></label>
      <label class="em-opt"><input type="checkbox" id="em-watch" data-set ${v.driversWatchOut ? 'checked' : ''} ${dis}> <b>Drivers watch out</b><br><span class="muted small">Vehicles stop for people in the way. Off: they drive on and hit them. Anyone hit is hurt or killed, the vehicle is off the road for 30 days, and the service's respect and standing fall.</span></label>`;
  }

  private saveSettings() {
    const style = Number((document.querySelector('input[name="em-style"]:checked') as HTMLInputElement | null)?.value ?? this.view!.style);
    this.command({ settings: {
      style, pickResponders: $<HTMLInputElement>('#em-pick').checked, crewsUseHeads: $<HTMLInputElement>('#em-heads').checked,
      driversWatchOut: $<HTMLInputElement>('#em-watch').checked, name: '',
    } });
  }

  private renderLog(): string {
    const log = (this.view!.log as string[]).slice().reverse();
    return log.length ? log.map((l) => `<div class="small">${esc(l)}</div>`).join('') : `<p class="muted">Nothing yet.</p>`;
  }

  // -------------------------------------------------------------------------
  // The scene
  // -------------------------------------------------------------------------

  private renderScene(): string {
    const s = this.scene!;
    const orders: [Order, string, string][] = [
      ['move', 'Move', 'Right-click a tile: the selected find their own way there'],
      ['path', 'Draw path', 'Drag from the selected one: it follows exactly what you draw, through anything in the way'],
      ['hose', 'Hose', 'Firefighters spray the fire at a tile'],
      ['treat', 'Treat', 'Paramedics treat the person you click'],
      ['warn', 'Warn', 'Officers call on the person you click to give up'],
      ['arrest', 'Arrest', 'Officers close in on the person you click and cuff them'],
      ['taser', 'Taser', 'Officers fire a taser at a tile: it hits the first person on the line'],
      ['shoot', 'Shoot', 'Officers fire at a tile: it hits the first person on the line, whoever they are'],
    ];
    const sel = this.selected.map((id) => {
      const p = (s.people as Msg[]).find((x) => x.id === id);
      if (p) return `${ROLE_NAME[p.role]} (${this.unit(p.unitId)?.name ?? ''})`;
      return this.unit(id)?.name ?? '';
    }).filter(Boolean).join(', ');
    const secs = Math.floor(s.step / 4);
    return `
      <div class="em-scene-bar">
        <button class="mini" data-em="board">← Board</button>
        <b>${KIND_ICON[s.kind] ?? ''} ${esc((this.view!.calls as Msg[]).find((c) => c.id === s.callId)?.text ?? '')}</b>
        <span class="muted">${Math.floor(secs / 60)}:${String(secs % 60).padStart(2, '0')} · ${s.burning} alight</span>
        <span class="spacer"></span>
        ${this.mine ? `<button class="mini" data-em="run">${this.running ? '⏸ Hold' : '▶ Run'}</button>
        <button class="mini" data-em="leave" title="The crews finish it the way the computer would">Hand to crews</button>` : ''}
      </div>
      <canvas id="em-canvas" width="${s.width * TILE}" height="${s.height * TILE}"></canvas>
      ${this.mine ? `<div class="em-orders">${orders.map(([o, label, tip]) => `<button class="mini ${this.order === o ? 'active' : ''}" data-em="order:${o}" title="${tip}">${label}</button>`).join('')}</div>
      <div class="muted small">Click to select (shift adds), then ${this.order === 'move' ? 'right-click where to go' : this.order === 'path' ? 'drag a path from the selected one' : 'click the target'}. ${sel ? `Selected: ${esc(sel)}` : ''}</div>` : '<p class="muted small">Watching.</p>'}
      <div class="em-events">${(s.events as string[]).slice().reverse().map((e) => `<div class="small">${esc(e)}</div>`).join('')}</div>
      ${(s.arriving as Msg[]).length ? `<div class="muted small">On the way: ${(s.arriving as Msg[]).map((a) => `${this.unit(a.unitId)?.name ?? '?'} in ${Math.max(0, Math.ceil((a.atStep - s.step) / 4))}s`).join(', ')}</div>` : ''}`;
  }

  private drawScene() {
    const s = this.scene!;
    const cv = $<HTMLCanvasElement>('#em-canvas');
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
      g.fillStyle = ['#c0392b', '#2c5aa0', '#e8e8e8'][v.kind];
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
    if (this.drawing) path(this.drawing, '#ffeb3b');
    if (!this.mine) return;
    const at = (e: MouseEvent) => {
      const r = cv.getBoundingClientRect();
      return { x: Math.floor((e.clientX - r.left) / r.width * s.width), y: Math.floor((e.clientY - r.top) / r.height * s.height) };
    };
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
        else if (!e.shiftKey) this.selected = [];
        this.render();
        return;
      }
      if (!this.selected.length) return;
      this.command({ task: { sceneId: s.id, ids: this.selected, task: TASKS[this.order], x: t.x, y: t.y, targetId: hit ?? 0 } });
    };
    cv.onmousemove = (e) => {
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
    cv.onmouseup = () => {
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
