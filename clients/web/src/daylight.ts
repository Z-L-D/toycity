// The light over the city (Beta 3.2, Part 6), as ToyCity.ClientModel's Daylight works it out for the Godot client:
// the sun's path for a city at 45° north by the day clock's hour and the calendar's month, how warm it is near the
// horizon, and how dark it is for the lights to come on. The web map tints itself by it and lights its windows and
// streets at night.

import type { Building } from './state';

const LATITUDE = 45;
const MINUTES_PER_DAY = 24 * 60;

const smooth = (e0: number, e1: number, x: number) => { const t = Math.min(1, Math.max(0, (x - e0) / (e1 - e0))); return t * t * (3 - 2 * t); };

/** The sun's elevation and azimuth in degrees at an hour (0-24) of a month (1-12). */
export function sunAt(hour: number, month: number): { elevation: number; azimuth: number } {
  const day = Math.min(12, Math.max(1, month)) * 30 - 15;
  const decl = (23.44 * Math.sin((2 * Math.PI * (day - 81)) / 365) * Math.PI) / 180;
  const lat = (LATITUDE * Math.PI) / 180, h = ((hour - 12) * 15 * Math.PI) / 180;
  const el = Math.asin(Math.max(-1, Math.min(1, Math.sin(lat) * Math.sin(decl) + Math.cos(lat) * Math.cos(decl) * Math.cos(h))));
  let az = (Math.acos(Math.max(-1, Math.min(1, (Math.sin(decl) - Math.sin(el) * Math.sin(lat)) / (Math.cos(el) * Math.cos(lat))))) * 180) / Math.PI;
  if (h > 0) az = 360 - az;
  return { elevation: (el * 180) / Math.PI, azimuth: az };
}

export interface MapLight {
  /** 0 in daylight to 1 at full dark. */
  night: number;
  /** 0 to 1: how golden the low sun is. */
  golden: number;
  /** 0 clear to 1 heavy rain or snow. */
  overcast: number;
}

interface LightStats { month?: number; weather?: string; roads?: { weatherKind?: string; falling?: boolean } | null }

/** How overcast the day is, 0 to 1, as Daylight.Overcast. */
export function overcast(s: LightStats): number {
  const kind = s.roads?.weatherKind ?? '';
  if (kind !== 'rain' && kind !== 'snow' && kind !== 'ice') return 0;
  const text = (s.weather ?? '').toLowerCase();
  const heavy = text.includes('heavy') || text.includes('storm') || text.includes('blizzard');
  return (s.roads?.falling ? 0.75 : 0.4) + (heavy ? 0.25 : 0);
}

/** The map's light at a minute of the day clock. */
export function mapLight(s: LightStats, minute: number): MapLight {
  const { elevation: el } = sunAt((minute % MINUTES_PER_DAY) / 60, s.month || 6);
  const oc = overcast(s);
  const dark = 1 - smooth(-10, 4, el);
  const night = Math.min(1, dark + 0.35 * oc * (1 - smooth(5, 25, el)));
  const golden = el > -4 ? (1 - smooth(4, 16, el)) * (1 - oc) : 0;
  return { night, golden, overcast: oc };
}

/** The share of a building's windows lit at night, as BuildingLife.LitShare. */
export function litShare(b: Building): number {
  if (!b.powered || b.abandoned || b.constructionDaysLeft > 0) return 0;
  if (b.residents > 0) return Math.min(0.65, Math.max(0.3, 0.3 + b.residents / 400));
  if (b.jobs > 0) return 0.12 + 0.3 * Math.min(1, Math.max(0, b.workers / b.jobs));
  return b.zone === 0 ? 0.45 : 0;
}
