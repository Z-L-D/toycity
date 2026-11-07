// The build menu of the UI overhaul (Beta 4, part 2, variation A), ported from the client model's BuildMenu.cs:
// a rail of categories down the left, each opening a flyout of groups and the things in them with their prices.
// Picking a thing sets the tool. What's in it comes from the city's content.
import type { City, BuildingType, Profile } from './state';
import icons from '../../../content/ui-icons.json';

export type ItemKind = 'zone' | 'dezone' | 'path' | 'utility' | 'building' | 'terraform' | 'river' | 'dam' | 'trees' | 'disaster' | 'bulldoze';

export interface BuildItem {
  name: string;
  price: string;
  icon: string;
  kind: ItemKind;
  zone?: number;
  density?: number;
  profile?: string;
  type?: string;
  utility?: number;
  terraform?: number;
  disaster?: number;
  /** The tree planter's plant kind (Beta 4.1): 0 what suits the ground, 1 broadleaf, 2 conifers, 5 orchard. */
  treeKind?: number;
  /** Why it can't be picked yet ("Reach 2,000 people"), or "" when it can. */
  locked: string;
}

export interface BuildGroup { name: string; items: BuildItem[] }
export interface BuildCategory { id: string; name: string; icon: string; key: string; description: string; groups: BuildGroup[] }

export const DENSITY_NAMES = ['Low density', 'Medium density', 'High density'];
export const TERRAFORM_NAMES = ['Raise', 'Lower', 'Level', 'Smooth', 'Even slope'];
export const DISASTER_NAMES = ['Fire', 'Tornado', 'Earthquake', 'Clockwork Colossus'];
/** The tree planter's choices and the plant kind each plants (Planner.TreeChoices). */
export const TREE_CHOICES: [string, number][] = [['Trees', 0], ['Broadleaf', 1], ['Conifers', 2], ['Orchard', 5]];
export const TREES_PER_CELL = 12;

/** An icon from the shared set, as an inline SVG. */
export function icon(name: string, size = 20): string {
  const body = (icons as Record<string, string>)[name] ?? (icons as Record<string, string>).civic;
  return `<svg class="ic" viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`;
}

/** The rail button lit and the flyout's state. */
export class BuildMenuState {
  open: string | null = null;
  group = 0;
  pinned = false;

  toggle(category: string) {
    if (this.open === category) this.open = null;
    else { this.open = category; this.group = 0; }
  }

  /** After a tile is picked: the flyout closes unless pinned. */
  picked() { if (!this.pinned) this.open = null; }

  shown(menu: BuildCategory[]): { category: BuildCategory; group: number } | null {
    const cat = menu.find((c) => c.id === this.open);
    if (!cat) return null;
    return { category: cat, group: Math.max(0, Math.min(this.group, cat.groups.length - 1)) };
  }
}

const money = (n: number) => (n <= 0 ? '' : `$${n.toLocaleString('en-US')}`);

function typeIcon(t: BuildingType): string {
  switch (t.category) {
    case 'transit': return t.id.includes('depot') || t.id.includes('yard') ? 'rail' : t.id.includes('subway') ? 'subway' : 'bus';
    case 'power': case 'water': case 'garbage': case 'police': case 'fire': case 'health': case 'education': return t.category;
    case 'park': return 'parks';
    case 'landmark': case 'reward': return 'landmarks';
    default: return 'civic';
  }
}

/** The menu for a city, from its content: the same categories, groups and order as BuildMenu.For. */
export function menuFor(c: City): BuildCategory[] {
  const lockedText = (t: BuildingType) => {
    if (t.unlockText && !c.unlocked.has(t.id)) return t.unlockText;
    if (t.unique && [...c.buildings.values()].some((b) => b.typeId === t.id)) return 'Built';
    return '';
  };
  const place = (t: BuildingType): BuildItem => ({ name: t.name, price: money(t.cost), icon: typeIcon(t), kind: 'building', type: t.id, locked: lockedText(t) });
  const types = (which: (t: BuildingType) => boolean) =>
    c.types.filter((t) => t.placeable && which(t)).sort((a, b) => a.cost - b.cost || a.name.localeCompare(b.name)).map(place);
  const pathItem = (p: Profile): BuildItem => ({
    name: p.name, price: p.costPerCell > 0 ? `$${p.costPerCell} / cell` : '', icon: p.rail || p.tram ? 'tram' : 'road', kind: 'path', profile: p.id, locked: '',
  });
  const paths = (which: (p: Profile) => boolean) => c.profiles.filter((p) => !p.custom && which(p)).map(pathItem);
  const nonEmpty = (gs: BuildGroup[]) => gs.filter((g) => g.items.length > 0);

  const menu: BuildCategory[] = [];
  const zoneGroups: BuildGroup[] = (['Residential', 'Commercial', 'Industrial'] as const).map((name, z) => ({
    name,
    items: [0, 1, 2].map((d) => ({ name: `${name}, ${DENSITY_NAMES[d].toLowerCase()}`, price: '', icon: 'zones', kind: 'zone' as const, zone: z + 1, density: d, locked: '' })),
  }));
  zoneGroups.push({ name: 'Dezone', items: [{ name: 'Dezone', price: '', icon: 'bulldoze', kind: 'dezone', locked: '' }] });
  menu.push({ id: 'zones', name: 'Zones', icon: 'zones', key: 'Z', description: 'Mark land for homes, shops and work.', groups: zoneGroups });

  menu.push({
    id: 'transport', name: 'Transport', icon: 'transport', key: 'R', description: 'Paths, roads, highways and rail, and the stops and depots on them.',
    groups: nonEmpty([
      { name: 'Paths', items: paths((p) => !p.cars && !p.rail) },
      { name: 'Roads', items: paths((p) => p.cars && p.frontage && !p.tram) },
      { name: 'Highways', items: paths((p) => p.cars && !p.frontage) },
      { name: 'Rail and tram', items: paths((p) => p.rail || !!p.tram) },
      { name: 'Your designs', items: c.profiles.filter((p) => p.custom).map(pathItem) },
      { name: 'Stops', items: types((t) => t.category === 'transit' && !t.id.includes('depot') && !t.id.includes('yard')) },
      { name: 'Depots', items: types((t) => t.category === 'transit' && (t.id.includes('depot') || t.id.includes('yard'))) },
    ]),
  });

  const power = types((t) => t.category === 'power');
  power.push({ name: 'Power line', price: '$5 / cell', icon: 'power', kind: 'utility', utility: 0, locked: '' });
  const water = types((t) => t.category === 'water');
  water.push({ name: 'Water and sewer pipe', price: '$3 / cell', icon: 'water', kind: 'utility', utility: 1, locked: '' });
  menu.push({
    id: 'utilities', name: 'Utilities', icon: 'utilities', key: 'P', description: 'Power, water and sewage, garbage, and what runs under the city.',
    groups: nonEmpty([
      { name: 'Power', items: power },
      { name: 'Water', items: water },
      { name: 'Garbage', items: types((t) => t.category === 'garbage') },
      { name: 'Underground', items: [{ name: 'Subway tunnel', price: '$150 / cell', icon: 'subway', kind: 'utility', utility: 2, locked: '' }] },
    ]),
  });

  menu.push({
    id: 'civic', name: 'Civic', icon: 'civic', key: 'E', description: 'Safety, health, schools and the town hall.',
    groups: nonEmpty([
      { name: 'Police', items: types((t) => t.category === 'police') },
      { name: 'Fire', items: types((t) => t.category === 'fire') },
      { name: 'Health', items: types((t) => t.category === 'health') },
      { name: 'Education', items: types((t) => t.category === 'education') },
      { name: 'Government', items: types((t) => t.category === 'government' || t.category === 'civic') },
    ]),
  });
  menu.push({ id: 'parks', name: 'Parks', icon: 'parks', key: '', description: 'Green space lifts the value of the land around it.', groups: [{ name: 'Parks', items: types((t) => t.category === 'park') }] });
  menu.push({
    id: 'landmarks', name: 'Landmarks', icon: 'landmarks', key: '', description: 'One of a kind, and the rewards a city earns.',
    groups: nonEmpty([{ name: 'Landmarks', items: types((t) => t.category === 'landmark') }, { name: 'Rewards', items: types((t) => t.category === 'reward') }]),
  });
  menu.push({
    id: 'land', name: 'Land', icon: 'land', key: 'T', description: 'Shape the ground. Roads grade their own way.',
    groups: [
      { name: 'Terraform', items: TERRAFORM_NAMES.map((n, k) => ({ name: n, price: '', icon: 'land', kind: 'terraform' as const, terraform: k, locked: '' })) },
      { name: 'Water', items: [{ name: 'River', price: '', icon: 'water', kind: 'river', locked: '' }, { name: 'Dam', price: '', icon: 'water', kind: 'dam', locked: '' }] },
      { name: 'Plants', items: TREE_CHOICES.map(([n, k]) => ({ name: n, price: `$${TREES_PER_CELL} / cell`, icon: 'parks', kind: 'trees' as const, treeKind: k, locked: '' })) },
    ],
  });
  menu.push({
    id: 'disasters', name: 'Disasters', icon: 'disasters', key: 'K', description: 'Set one loose on your own city.',
    groups: [{ name: 'Disasters', items: DISASTER_NAMES.map((n, k) => ({ name: n, price: '', icon: 'disasters', kind: 'disaster' as const, disaster: k, locked: '' })) }],
  });
  menu.push({ id: 'bulldoze', name: 'Bulldoze', icon: 'bulldoze', key: 'B', description: 'Clear anything you built.', groups: [{ name: 'Bulldoze', items: [{ name: 'Bulldoze', price: '', icon: 'bulldoze', kind: 'bulldoze', locked: '' }] }] });
  return menu;
}
