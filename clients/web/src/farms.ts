// Farm country (Beta 4.1, part 2): how the web map colours the farm layer. A cell's farm byte packs what it is (low two
// bits: 1 field, 2 farm track, 3 farmstead), a field's crop (bits 2-4) and whether its rows run north-south (bit 5),
// as the sim's FarmLand does.

export const FIELD = 1, TRACK = 2, YARD = 3;
export const CROP_NAMES = ['wheat', 'corn', 'soybeans', 'hay', 'canola'];

export const farmWhat = (f: number) => f & 3;
export const farmCrop = (f: number) => (f >> 2) & 7;
export const rowsNorthSouth = (f: number) => (f & 32) !== 0;

type RGB = [number, number, number];

/** Each crop's colour at the middle of each month, January to December: the Godot ground shader's table (terrain.gdshader). */
const CROPS: RGB[] = [
  // wheat (sown in autumn)
  [92, 102, 64], [94, 107, 64], [87, 120, 54], [79, 128, 48], [92, 133, 51], [184, 163, 82],
  [199, 173, 102], [178, 161, 112], [107, 87, 61], [102, 94, 64], [94, 107, 64], [92, 102, 64],
  // corn
  [140, 122, 89], [128, 110, 79], [107, 87, 59], [102, 82, 56], [105, 97, 61], [69, 107, 41],
  [51, 94, 33], [56, 97, 36], [153, 140, 82], [173, 148, 97], [148, 128, 92], [140, 122, 89],
  // soybeans
  [128, 110, 82], [120, 102, 74], [107, 87, 59], [105, 84, 59], [107, 92, 61], [84, 120, 48],
  [71, 115, 43], [76, 117, 46], [178, 161, 69], [133, 102, 61], [128, 110, 82], [128, 110, 82],
  // hay
  [107, 112, 71], [107, 115, 71], [97, 122, 61], [87, 130, 54], [87, 135, 54], [128, 135, 71],
  [102, 128, 59], [133, 135, 76], [97, 125, 61], [97, 120, 64], [105, 115, 71], [107, 112, 71],
  // canola
  [102, 92, 64], [97, 102, 61], [82, 117, 48], [87, 128, 48], [219, 199, 51], [115, 122, 56],
  [168, 153, 102], [107, 87, 59], [92, 107, 56], [84, 115, 54], [92, 107, 61], [102, 97, 64],
];

/** A crop's colour on a day of the year: `month` is 1..12 with the day as a fraction. */
export function cropColour(crop: number, month: number): RGB {
  const t = month - 1.5;
  const i0 = ((Math.floor(t) % 12) + 12) % 12, i1 = (i0 + 1) % 12, f = t - Math.floor(t);
  const a = CROPS[crop * 12 + i0] ?? CROPS[0], b = CROPS[crop * 12 + i1] ?? CROPS[0];
  return [a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f];
}
