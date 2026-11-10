// Where the trees on each cell stand (Beta 4.1, part 1): a port of the client model's TreeLayout, so both clients put
// each crown in the same place. Each cell's crowns are scattered from a hash of its index; orchards stand in rows.

export const BROADLEAF = 1, CONIFER = 2, SCRUB = 3, REEDS = 4, ORCHARD = 5;

export interface TreeSpot { x: number; y: number; kind: number; scale: number; shade: number }

function hash(x: number): number {
  x >>>= 0;
  x ^= x >>> 16; x = Math.imul(x, 0x7feb352d) >>> 0;
  x ^= x >>> 15; x = Math.imul(x, 0x846ca68b) >>> 0;
  x ^= x >>> 16;
  return x >>> 0;
}
const unit = (h: number) => (h & 0xffffff) / 0x1000000;

/** The crowns on cell (x, y) of a map `width` wide, from its plants byte (kind * 16 + crowns), all of them drawn. */
/** Sides of a cell that a crown keeps off (a road, a building), as the 3D client's TreeLayout has them. */
export const WEST = 1, EAST = 2, NORTH = 4, SOUTH = 8;
const reach = (kind: number) => (kind === 1 ? 0.3 : kind === 2 ? 0.22 : kind === 3 ? 0.13 : kind === 5 ? 0.17 : 0.09);

export function treeSpots(x: number, y: number, width: number, plants: number, into: TreeSpot[], keepOff = 0) {
  const kind = plants >> 4, n = plants & 15;
  if (kind === 0 || n === 0) return;
  const seed = hash((Math.imul(y * width + x, 0x9e3779b9) + 0x632be5ab) >>> 0);
  for (let k = 0; k < n; k++) {
    const h1 = hash((seed + Math.imul(k, 0x85ebca6b)) >>> 0), h2 = hash(h1), h3 = hash(h2);
    let px: number, py: number;
    if (kind === ORCHARD) {
      px = 0.125 + 0.25 * (k % 4) + (unit(h1) - 0.5) * 0.04;
      py = 0.25 + 0.5 * (Math.floor(k / 4) % 2) + (unit(h2) - 0.5) * 0.04;
    } else { px = 0.08 + 0.84 * unit(h1); py = 0.08 + 0.84 * unit(h2); }
    const scale = 0.75 + 0.5 * unit(h3);
    if (keepOff) {
      // Stand far enough in that the crown doesn't hang over the road or building beside.
      const m = Math.min(0.48, reach(kind) * scale);
      px = Math.min(Math.max(px, keepOff & WEST ? m : 0), keepOff & EAST ? 1 - m : 1);
      py = Math.min(Math.max(py, keepOff & NORTH ? m : 0), keepOff & SOUTH ? 1 - m : 1);
    }
    into.push({ x: x + px, y: y + py, kind, scale, shade: unit(hash(h3)) });
  }
}

type RGB = [number, number, number];

/** A broadleaf crown's colour by the month (1..12 with a fraction): as the 3D client's tree shader turns them. */
export function leafColour(month: number, shade: number): RGB {
  const spring: RGB = [120, 158, 64], summer: RGB = [61, 102, 41], winter: RGB = [87, 74, 66];
  const autumn: RGB = [184 - 31 * shade, 128 - 62 * shade, 41 - 10 * shade];
  const mix = (a: RGB, b: RGB, t: number): RGB => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
  if (month < 3) return winter;
  if (month < 4.5) return mix(winter, spring, (month - 3) / 1.5);
  if (month < 6) return mix(spring, summer, (month - 4.5) / 1.5);
  if (month < 9) return summer;
  if (month < 10.5) return mix(summer, autumn, (month - 9) / 1.5);
  if (month < 11.5) return mix(autumn, winter, month - 10.5);
  return winter;
}

/** Each kind's own colour (broadleaf and orchards go by the month instead). */
export function plantColour(kind: number, month: number, shade: number): RGB {
  const v = 0.85 + 0.3 * shade;
  const base: RGB = kind === CONIFER ? [41, 77, 46] : kind === SCRUB ? [94, 102, 51] : kind === REEDS ? [140, 145, 77] : leafColour(month, shade);
  return [base[0] * v, base[1] * v, base[2] * v];
}
