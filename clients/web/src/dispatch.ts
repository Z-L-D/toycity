// Direct dispatch on the city map (Beta 4, part 1), as the shared client model has it (DispatchMap.cs): where each
// unit is now, eased along its leg by the day clock; which units are picked (a click, shift-clicks, a box, or a
// numbered group); and the orders a right-click or a button sends. Auto-dispatch still runs underneath, so these
// orders sit on top of it. A unit busy with a call can't take one: it's left out and the player is told.
import type { Msg } from './net';

/** Each kind's colour: engines red, patrol cars blue, ambulances white with a green stripe, SWAT dark. */
export const UNIT_BODY = ['#d8342a', '#2f6fe0', '#f4f4f2', '#232a33'];
export const UNIT_STRIPE = ['#f4f4f2', '#f4f4f2', '#2fae5a', '#9aa4b0'];
export const PICKED = '#ffe14d';

export interface UnitMarker {
  id: number; kind: number; name: string; x: number; y: number; dx: number; dy: number; moving: boolean; picked: boolean;
  /** For a picked unit: the cells still to drive, then the places queued after, to draw its route. */
  route: number[];
}

/** Where a unit is at a minute of the day clock, in cells (+0.5 is a cell's centre), and which way it faces. */
export function unitPosition(u: Msg, minute: number, width: number): { x: number; y: number; dx: number; dy: number; moving: boolean } | null {
  if (width <= 0) return null;
  const at = (c: number) => [c % width + 0.5, Math.floor(c / width) + 0.5];
  const leg = u.leg as number[];
  // Ordered since the last day: it leaves at the next (its start is still -1).
  if (leg.length > 1 && u.legStart < 0) { const [sx, sy] = at(leg[0]), [nx, ny] = at(leg[1]); return { x: sx, y: sy, dx: nx - sx, dy: ny - sy, moving: true }; }
  if (leg.length > 1 && u.legMinutes > 0 && minute < u.legStart + u.legMinutes) {
    const t = Math.min(1, Math.max(0, (minute - u.legStart) / u.legMinutes)) * (leg.length - 1);
    const k = Math.min(leg.length - 2, Math.floor(t));
    const [ax, ay] = at(leg[k]), [bx, by] = at(leg[k + 1]);
    const f = t - k;
    return { x: ax + (bx - ax) * f, y: ay + (by - ay) * f, dx: bx - ax, dy: by - ay, moving: true };
  }
  const cell = u.cell >= 0 ? u.cell : u.home;
  if (cell < 0) return null;
  const [x, y] = at(cell);
  return { x, y, dx: 1, dy: 0, moving: false };
}

const free = (u: Msg) => u.state === 'ready' || u.state === 'proposed';

export class DispatchMap {
  picked: number[] = [];
  /** Set after "Follow": the next unit clicked is the one they follow. */
  pickingLeader = false;
  private groups = new Map<number, number[]>();

  constructor(
    private readonly view: () => Msg | null,
    private readonly send: (cmd: Msg) => void,
    private readonly toast: (text: string, severity?: string) => void,
    private readonly width: () => number,
  ) {}

  markers(minute: number): UnitMarker[] {
    const v = this.view();
    if (!v) return [];
    const out: UnitMarker[] = [];
    for (const u of v.units as Msg[]) {
      if (u.state === 'suspended') continue;
      const p = unitPosition(u, minute, this.width());
      if (!p) continue;
      const picked = this.picked.includes(u.id);
      let route: number[] = [];
      if (picked) {
        const leg = u.leg as number[];
        if (p.moving && u.legStart < 0) route = leg.slice(1);
        else if (p.moving) {
          const t = Math.min(1, Math.max(0, (minute - u.legStart) / u.legMinutes)) * (leg.length - 1);
          route = leg.slice(Math.floor(t) + 1);
        }
        route = route.concat(u.waypoints as number[]);
      }
      out.push({ id: u.id, kind: u.kind, name: u.name, ...p, picked, route });
    }
    return out;
  }

  unitAt(x: number, y: number, minute: number, reach = 1.5): number | null {
    let best: number | null = null, bestD = reach;
    for (const m of this.markers(minute)) {
      const d = Math.abs(m.x - x) + Math.abs(m.y - y);
      if (d <= bestD) { bestD = d; best = m.id; }
    }
    return best;
  }

  click(unit: number | null, add: boolean) {
    if (this.pickingLeader) {
      this.pickingLeader = false;
      if (unit !== null) this.follow(unit);
      return;
    }
    if (unit !== null) {
      if (!add) this.picked = [unit];
      else if (this.picked.includes(unit)) this.picked = this.picked.filter((i) => i !== unit);
      else this.picked.push(unit);
    } else if (!add) this.picked = [];
  }

  box(ids: number[], add: boolean) {
    this.pickingLeader = false;
    if (!add) this.picked = [];
    for (const id of ids) if (!this.picked.includes(id)) this.picked.push(id);
  }

  setGroup(n: number) {
    if (!this.picked.length) { this.groups.delete(n); return; }
    this.groups.set(n, [...this.picked]);
    this.toast(`Group ${n}: ${this.picked.length} unit${this.picked.length === 1 ? '' : 's'}.`);
  }

  hasGroup(n: number) { return this.groups.has(n); }

  recallGroup(n: number): boolean {
    const ids = this.groups.get(n);
    if (!ids) return false;
    const live = new Set(((this.view()?.units ?? []) as Msg[]).map((u) => u.id));
    this.picked = ids.filter((i) => live.has(i));
    return true;
  }

  private unit(id: number): Msg | undefined { return ((this.view()?.units ?? []) as Msg[]).find((u) => u.id === id); }

  private orderable(): number[] {
    const units = this.picked.map((i) => this.unit(i)).filter((u): u is Msg => !!u);
    const busy = units.filter((u) => !free(u));
    if (!units.length) { this.toast('Pick some units first: click one, shift-click more, or drag a box.', 'warning'); return []; }
    if (busy.length === units.length) { this.toast(busy.length === 1 ? `${busy[0].name} is busy with a call.` : "They're all busy with calls.", 'warning'); return []; }
    if (busy.length) this.toast(`${busy.map((u) => u.name).join(', ')} ${busy.length === 1 ? 'is' : 'are'} busy with a call, so left out.`);
    return units.filter(free).map((u) => u.id);
  }

  private order(kind: string, ids: number[], o: Msg = {}) {
    if (ids.length) this.send({ order: { kind, unitIds: ids, ...o } });
  }

  callAt(x: number, y: number): Msg | undefined {
    return ((this.view()?.calls ?? []) as Msg[]).filter((c) => c.status !== 'done' && Math.abs(c.x - x) <= 1 && Math.abs(c.y - y) <= 1)
      .sort((a, b) => Math.abs(a.x - x) + Math.abs(a.y - y) - (Math.abs(b.x - x) + Math.abs(b.y - y)))[0];
  }

  /** A right click: on a call they answer it, on another unit they follow it, anywhere else they drive there. */
  rightClick(x: number, y: number, unit: number | null, queue: boolean) {
    if (!this.picked.length) return;
    if (unit !== null && !this.picked.includes(unit)) { this.follow(unit); return; }
    const call = this.callAt(x, y);
    if (call) { this.order('scene', this.orderable(), { callId: call.id }); return; }
    this.order('go', this.orderable(), { x, y, queue });
  }

  hold() { this.order('hold', this.orderable()); }
  goHome() { this.order('return', this.orderable()); }

  startFollow() {
    if (!this.picked.length) { this.toast('Pick the units that should follow first.', 'warning'); return; }
    this.pickingLeader = true;
  }

  private follow(lead: number) { this.order('follow', this.orderable().filter((i) => i !== lead), { followId: lead }); }

  hint(): string {
    if (this.pickingLeader) return 'Click the unit they should follow.';
    if (!this.picked.length) return 'Click a unit to pick it (shift adds; drag a box for several; ctrl+1–9 makes a group, 1–9 picks it again).';
    return 'Right-click a place to send them (shift queues more places), a call to answer it, or a unit to follow it.';
  }

  doing(u: Msg): string {
    if (u.state === 'on call' || u.state === 'at scene') return u.state;
    if (u.state === 'suspended') return 'off the road';
    if (u.order === 'held') return 'holding';
    if (u.order === 'following') return `following ${this.unit(u.followId)?.name ?? 'a unit'}`;
    if ((u.leg as number[]).length) return u.order === 'posted' ? 'driving out' : u.cell === u.home ? 'going home' : 'driving';
    if (u.order === 'posted') return 'posted';
    return u.cell === u.home || u.cell < 0 ? 'at the station' : 'ready';
  }

  pickedText(): string {
    const units = this.picked.map((i) => this.unit(i)).filter((u): u is Msg => !!u);
    if (!units.length) return 'No units picked.';
    return units.length <= 4 ? units.map((u) => `${u.name} (${this.doing(u)})`).join(', ')
      : `${units.length} units: ${units.slice(0, 3).map((u) => u.name).join(', ')} and ${units.length - 3} more`;
  }
}

/**
 * The day clock's minute for moving things, run on smoothly between the city's days (TickClock in the shared model).
 * At most speeds the city's day comes round faster than its clock would run, so the top bar's clock waits and jumps;
 * a unit eased by it would sit, then leap. This runs across each day's step over as long as the last day took.
 */
export class TickClock {
  private minute = Number.NEGATIVE_INFINITY;
  private at = 0;
  private interval = 2000;

  now(clockMinute: number, step: number, speed: number, now = performance.now()): number {
    if (clockMinute !== this.minute) {
      if (Number.isFinite(this.minute) && clockMinute > this.minute) this.interval = Math.min(120_000, Math.max(100, now - this.at));
      this.minute = clockMinute; this.at = now;
    }
    if (speed <= 0 || step <= 0) return clockMinute;
    return clockMinute + Math.min(1, Math.max(0, (now - this.at) / this.interval)) * (step - 0.001);
  }
}
