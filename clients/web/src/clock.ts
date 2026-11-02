// Both clocks as a player reads them (Beta 3.2): the day clock's hour and the calendar's month and year, "Tue 14:32 ·
// March, year 3". The server moves the day clock a step each calendar day (48 minutes at a month a clock day, 2 at
// two years); between steps the hands run on at the speed's pace (a clock day an hour, half an hour, ten minutes),
// never past where the next step puts them. ToyCity.ClientModel's GameClock is the same for the Godot client.

/** A day of the day clock at each speed, in real milliseconds. */
export const CLOCK_DAY_MS = [0, 3_600_000, 1_800_000, 600_000];
const MINUTES_PER_DAY = 24 * 60;
export const PACES = [30, 60, 120, 360, 720];
const WEEKDAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

export function paceName(pace: number): string {
  return ({ 30: 'A month a day', 60: 'Two months a day', 120: 'Four months a day', 360: 'A year a day', 720: 'Two years a day' } as Record<number, string>)[pace] ?? `${pace} days a day`;
}

interface ClockStats { clockMinute?: number | { toNumber(): number }; clockMinutesPerTick?: number; speed?: number; month?: number; year?: number }

const num = (v: number | { toNumber(): number } | undefined) => (v == null ? 0 : typeof v === 'number' ? v : v.toNumber());

export class GameClock {
  private minute = -1;
  private at = 0;

  /** The day clock in minutes since it started, run on from the last tick to now. */
  minuteNow(s: ClockStats, now = performance.now()): number {
    const m = num(s.clockMinute);
    if (m !== this.minute) { this.minute = m; this.at = now; }
    const step = s.clockMinutesPerTick ?? 0, speed = Math.max(0, Math.min(3, s.speed ?? 0));
    if (step <= 0 || speed === 0) return m;
    const ran = (now - this.at) * MINUTES_PER_DAY / CLOCK_DAY_MS[speed];
    return m + Math.floor(Math.min(step - 1, Math.max(0, ran)));
  }

  text(s: ClockStats, now = performance.now()): string {
    return clockText(this.minuteNow(s, now), s.month ?? 0, s.year ?? 0);
  }
}

export function clockText(minute: number, month: number, year: number): string {
  const wd = WEEKDAYS[Math.floor(minute / MINUTES_PER_DAY) % 7];
  const h = Math.floor((minute % MINUTES_PER_DAY) / 60), mm = minute % 60;
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${wd} ${pad(h)}:${pad(mm)} · ${MONTHS[month - 1] ?? ''}, year ${Math.max(1, year - 1999)}`;
}

/** Turns of the day clock (48 minutes each) as a span: "48 min", "2 h 24 min", "1 day". */
export function span(turns: number): string {
  const m = turns * 48;
  if (m === MINUTES_PER_DAY) return '1 day';
  if (m < 60) return `${m} min`;
  return m % 60 === 0 ? `${m / 60} h` : `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')} min`;
}
