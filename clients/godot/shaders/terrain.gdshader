// The ground: SimCity 4's heightfield drawn as it would look from the air. What it's made of goes by its slope and
// height, as real ground does: meadow and drier grass with patches of bare earth on the flats, earth then rock as the
// slope steepens, a sandy shore just above the waterline, a bed that darkens with depth, and snow on high gentle ground.
// Noise in world space breaks every material up so no two cells look alike. Over it go the city's marks, one texel a
// cell (cells): a zone's light tint on empty zoned land, and the roads until they have their own shapes. Then the map
// layers: a data overlay (one texel a cell), the selected cell, and an area pulsing for what an alert is about.
// Lying snow (Beta 4, part 4) comes from the project's globals `snow_mm`, the city's depth on open level ground at
// its median height, and `snow_median_y`, that height in world units: spread as SnowDepth.At does, deeper on high
// ground and on slopes facing north, none on ground too steep to hold it, and patchy while it's thin.
// COLOR.a is 0 on the map's side walls and the skirts under chunk edges: those are bare soil.
// Release candidate, part 6: what the ground is made of is now real material, each a texture made in code
// (ClientModel GroundTextures): meadow, dry grass, earth, tilled soil, mud, sand, river gravel, scree, bedrock, the
// four rocks, snow, and the litter under broadleaf and conifer woods. They're blended by the same rules, each
// blend leaning on the materials' heights so stones stand out of grass and grass lies over earth. With the
// "Relief" ground look their relief is lit (detail normals, roughness and the shading of their hollows), and steep
// ground takes its texture from the side, so cliffs show their beds rather than stretched streaks. `ground_look` 0
// draws every material in its average colour, as before.
shader_type spatial;
render_mode cull_disabled;

uniform sampler2D cells : filter_nearest, repeat_disable;
uniform sampler2D overlay : filter_nearest, repeat_disable;
// Per cell: r earthworks, g soil depth (0 bare rock, 1 eight metres or more), b rock kind / 4.
uniform sampler2D ground_layers : filter_linear, repeat_disable;
// The rivers' banks (CityView.PaintBanks): red, the water's surface of the river beside each cell, in world units;
// green, how near the river is, 1 on it falling to 0 three cells off.
uniform sampler2D river_banks : filter_linear, repeat_disable;
uniform bool use_overlay = false;
// The underground view (Beta 3.2): the ground as a cutaway, dark soil over the rock, with what's under it drawn on top.
uniform bool underground = false;
uniform vec2 map_size = vec2(128.0);
uniform vec2 selected = vec2(-1.0);
uniform vec4 flash_rect = vec4(-1.0);  // x0, y0, x1, y1 in cells, inclusive
uniform float flash_strength = 0.0;
uniform float water_y = -1000.0;     // the water's surface, in world units
uniform float height_step = 0.125;   // one height step (2 m), in world units
global uniform float snow_mm;
global uniform float month;
// Farm country (Beta 4.1, part 2), a texel a cell: r what (1 field, 2 track, 3 farmstead) / 3, g a field's crop / 7,
// b a track's directions (1 east-west, 2 north-south) / 3, a 1 where a field's rows run north-south.
uniform sampler2D farms : filter_nearest, repeat_disable;
uniform bool has_farms = false;
global uniform float snow_median_y;
// The ground's materials (GroundTextureSet), a layer each: colour with height in alpha, and normal, roughness, AO.
uniform sampler2DArray ground_albedo : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray ground_detail : filter_linear_mipmap_anisotropic, repeat_enable;
uniform float tile_metres[16];
// 0 plain colours, 1 textured, 2 relief (and 3, finer textures: the same here).
uniform int ground_look = 0;
// The plant layer, a texel a cell: the canopy (0 open to 1 closed) of broadleaf woods and orchards, conifers,
// scrub and reeds.
uniform sampler2D plants : filter_linear, repeat_disable;
uniform bool has_plants = false;

varying vec3 world_pos;
varying vec3 world_normal;

// Colours are written in sRGB. The Compatibility renderer works in sRGB throughout (OUTPUT_IS_SRGB), so they're
// converted only for Forward+ and Mobile.
vec3 to_linear(vec3 c, bool srgb_out) {
	if (srgb_out) return c;
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

float hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p) {
	float v = 0.0, a = 0.5;
	for (int k = 0; k < 4; k++) { v += a * noise(p); p = p * 2.03 + 17.1; a *= 0.5; }
	return v;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

// Each crop's colour at the middle of each month, January to December: wheat (sown in autumn), corn, soybeans, hay,
// canola. Bare earth between, stubble after the harvest. The web map has the same table (clients/web/src/farms.ts).
const vec3 CROPS[60] = vec3[60](
	vec3(0.36, 0.40, 0.25), vec3(0.37, 0.42, 0.25), vec3(0.34, 0.47, 0.21), vec3(0.31, 0.50, 0.19), vec3(0.36, 0.52, 0.20), vec3(0.72, 0.64, 0.32),
	vec3(0.78, 0.68, 0.40), vec3(0.70, 0.63, 0.44), vec3(0.42, 0.34, 0.24), vec3(0.40, 0.37, 0.25), vec3(0.37, 0.42, 0.25), vec3(0.36, 0.40, 0.25),
	vec3(0.55, 0.48, 0.35), vec3(0.50, 0.43, 0.31), vec3(0.42, 0.34, 0.23), vec3(0.40, 0.32, 0.22), vec3(0.41, 0.38, 0.24), vec3(0.27, 0.42, 0.16),
	vec3(0.20, 0.37, 0.13), vec3(0.22, 0.38, 0.14), vec3(0.60, 0.55, 0.32), vec3(0.68, 0.58, 0.38), vec3(0.58, 0.50, 0.36), vec3(0.55, 0.48, 0.35),
	vec3(0.50, 0.43, 0.32), vec3(0.47, 0.40, 0.29), vec3(0.42, 0.34, 0.23), vec3(0.41, 0.33, 0.23), vec3(0.42, 0.36, 0.24), vec3(0.33, 0.47, 0.19),
	vec3(0.28, 0.45, 0.17), vec3(0.30, 0.46, 0.18), vec3(0.70, 0.63, 0.27), vec3(0.52, 0.40, 0.24), vec3(0.50, 0.43, 0.32), vec3(0.50, 0.43, 0.32),
	vec3(0.42, 0.44, 0.28), vec3(0.42, 0.45, 0.28), vec3(0.38, 0.48, 0.24), vec3(0.34, 0.51, 0.21), vec3(0.34, 0.53, 0.21), vec3(0.50, 0.53, 0.28),
	vec3(0.40, 0.50, 0.23), vec3(0.52, 0.53, 0.30), vec3(0.38, 0.49, 0.24), vec3(0.38, 0.47, 0.25), vec3(0.41, 0.45, 0.28), vec3(0.42, 0.44, 0.28),
	vec3(0.40, 0.36, 0.25), vec3(0.38, 0.40, 0.24), vec3(0.32, 0.46, 0.19), vec3(0.34, 0.50, 0.19), vec3(0.86, 0.78, 0.20), vec3(0.45, 0.48, 0.22),
	vec3(0.66, 0.60, 0.40), vec3(0.42, 0.34, 0.23), vec3(0.36, 0.42, 0.22), vec3(0.33, 0.45, 0.21), vec3(0.36, 0.42, 0.24), vec3(0.40, 0.38, 0.25));

vec3 crop_colour(int crop, float m) {
	float t = m - 1.5;
	float i0 = mod(floor(t), 12.0), i1 = mod(i0 + 1.0, 12.0);
	return mix(CROPS[crop * 12 + int(i0)], CROPS[crop * 12 + int(i1)], fract(t));
}

// The materials, in GroundTextures.List's order.
const int MEADOW = 0, DRY = 1, EARTH = 2, TILLED = 3, MUD = 4, SAND = 5, GRAVEL = 6, SCREE = 7, ROCK = 8, SHALE = 9,
	SANDSTONE = 10, LIMESTONE = 11, GRANITE = 12, SNOW = 13, LEAVES = 14, NEEDLES = 15;
// Each one's average colour (sRGB), for the plain look and far off: GroundTextures.List's, which the web map uses too.
const vec3 PLAIN[16] = vec3[16](
	vec3(0.337, 0.408, 0.200), vec3(0.514, 0.482, 0.310), vec3(0.373, 0.302, 0.216), vec3(0.349, 0.282, 0.212),
	vec3(0.318, 0.267, 0.200), vec3(0.710, 0.647, 0.498), vec3(0.502, 0.463, 0.404), vec3(0.494, 0.467, 0.424),
	vec3(0.475, 0.459, 0.427), vec3(0.388, 0.400, 0.431), vec3(0.627, 0.518, 0.376), vec3(0.694, 0.671, 0.616),
	vec3(0.588, 0.518, 0.494), vec3(0.878, 0.898, 0.933), vec3(0.435, 0.314, 0.200), vec3(0.380, 0.278, 0.200));

// A spot of ground: its colour (sRGB), its normal (world), roughness, ambient occlusion and the height of its relief.
struct Surf { vec3 c; vec3 n; float r; float ao; float h; };

// Where and how a fragment samples: its position and how that changes across the pixel, its normal, the weights of
// the three projections (top, east-west, north-south) and the look.
struct Frame { vec3 wp; vec3 dx; vec3 dy; vec3 n; vec3 tri; int look; };

vec2 turn(vec2 v, float a) { float c = cos(a), s = sin(a); return vec2(c * v.x - s * v.y, s * v.x + c * v.y); }

// One projection of a material: its uv, the uv's change across the pixel, and how its normal's lean maps to the world.
void sample_one(int l, vec2 uv, vec2 gx, vec2 gy, float w, bool side, inout vec4 a, inout vec4 d) {
	// Each material turned and shifted its own way, so their tiles don't line up. Seen from the side it isn't turned,
	// so a rock's beds stay level.
	float ang = side ? 0.0 : float(l) * 1.37;
	vec2 off = vec2(0.31, 0.67) * float(l);
	a += w * textureGrad(ground_albedo, vec3(turn(uv, ang) + off, float(l)), turn(gx, ang), turn(gy, ang));
	vec4 dd = textureGrad(ground_detail, vec3(turn(uv, ang) + off, float(l)), turn(gx, ang), turn(gy, ang));
	// Turn the normal's lean back by the same angle.
	vec2 lean = turn(dd.rg * 2.0 - 1.0, -ang);
	d += w * vec4(lean, dd.b, dd.a);
}

Surf mat(int l, Frame f) {
	Surf s;
	if (f.look == 0) { s.c = PLAIN[l]; s.n = f.n; s.r = 0.92; s.ao = 1.0; s.h = 0.5; return s; }
	float k = 16.0 / tile_metres[l];  // world units are 16 m cells
	vec4 a = vec4(0.0), d = vec4(0.0);
	vec3 n = f.n;
	if (f.look == 1 || f.tri.x < 0.02 && f.tri.z < 0.02) {
		sample_one(l, f.wp.xz * k, f.dx.xz * k, f.dy.xz * k, 1.0, false, a, d);
		if (f.look >= 2) n = normalize(n + vec3(d.x, 0.0, d.y) * 0.9);
	} else {
		vec4 ax = vec4(0.0), dx = vec4(0.0), az = vec4(0.0), dz = vec4(0.0), ay = vec4(0.0), dy = vec4(0.0);
		// Down the picture is down the cliff, so a rock's beds lie level.
		if (f.tri.y > 0.02) sample_one(l, f.wp.xz * k, f.dx.xz * k, f.dy.xz * k, 1.0, false, ay, dy);
		if (f.tri.x > 0.02) sample_one(l, vec2(f.wp.z, -f.wp.y) * k, vec2(f.dx.z, -f.dx.y) * k, vec2(f.dy.z, -f.dy.y) * k, 1.0, true, ax, dx);
		if (f.tri.z > 0.02) sample_one(l, vec2(f.wp.x, -f.wp.y) * k, vec2(f.dx.x, -f.dx.y) * k, vec2(f.dy.x, -f.dy.y) * k, 1.0, true, az, dz);
		a = ay * f.tri.y + ax * f.tri.x + az * f.tri.z;
		d = dy * f.tri.y + dx * f.tri.x + dz * f.tri.z;
		vec3 lean = vec3(dy.x, 0.0, dy.y) * f.tri.y + vec3(0.0, -dx.y, dx.x) * f.tri.x + vec3(dz.x, -dz.y, 0.0) * f.tri.z;
		n = normalize(n + lean * 0.9);
	}
	s.c = a.rgb; s.h = a.a; s.n = n; s.r = d.b; s.ao = f.look >= 2 ? d.a : 1.0;
	return s;
}

// Lays a material over what's there, w of the way; between, its higher parts come through first.
Surf over(Surf s, Surf t, float w) {
	float a = clamp(w, 0.0, 1.0);
	float k = clamp(a + (t.h - s.h) * 2.4 * a * (1.0 - a), 0.0, 1.0);
	Surf o;
	o.c = mix(s.c, t.c, k); o.n = normalize(mix(s.n, t.n, k)); o.r = mix(s.r, t.r, k); o.ao = mix(s.ao, t.ao, k); o.h = mix(s.h, t.h, k);
	return o;
}

// The same, sampling the material only where it shows at all.
Surf put(Surf s, int l, float w, Frame f) {
	if (w < 0.004) return s;
	return over(s, mat(l, f), w);
}

// The same with its colour changed, by brightness k and a tint mixed in t of the way.
Surf put_tinted(Surf s, int l, float w, Frame f, vec3 tint, float t, float k) {
	if (w < 0.004) return s;
	Surf m = mat(l, f);
	m.c = mix(m.c, tint * (m.c / max(PLAIN[l], vec3(0.02))), t) * k;
	return over(s, m, w);
}

void fragment() {
	bool srgb = OUTPUT_IS_SRGB;
	vec2 p = world_pos.xz;
	vec3 nrm = normalize(world_normal);
	// Metres above the water, and how steep (0 flat, 1 a wall).
	float metres = (world_pos.y - water_y) / height_step * 2.0;
	float slope = 1.0 - clamp(nrm.y, 0.0, 1.0);
	float large = fbm(p * 0.07), mid = fbm(p * 0.6 + 3.0), fine = noise(p * 6.0);
	Frame f;
	f.wp = world_pos; f.dx = dFdx(world_pos); f.dy = dFdy(world_pos); f.n = nrm; f.look = ground_look;
	// The side projections are weighed by the face as drawn, not the smoothed normal: on a crag the smoothed normal
	// leans back from the face, and a projection from above would smear the rock down it.
	vec3 face = normalize(cross(f.dy, f.dx));
	vec3 tw = pow(abs(face), vec3(4.0));
	f.tri = tw / (tw.x + tw.y + tw.z);
	if (ground_look < 2 || max(slope, 1.0 - abs(face.y)) < 0.3) f.tri = vec3(0.0, 1.0, 0.0);
	vec3 deep = vec3(0.12, 0.17, 0.16);

	// How damp the ground is: by a river, and low by the water.
	vec2 rb = texture(river_banks, p / map_size).rg;
	float moist = max(smoothstep(0.0, 0.6, rb.g), 1.0 - smoothstep(1.0, 10.0, metres));
	// Grass dries in patches, on slopes facing the sun (south, +z) and in the height of summer, less where it's damp.
	float summer = exp(-pow((month - 7.5) / 1.6, 2.0));
	float dryness = clamp(smoothstep(0.35, 0.75, large) + 0.25 * max(nrm.z, 0.0) * smoothstep(0.05, 0.3, slope) + 0.3 * summer - 0.1, 0.0, 1.0) * (1.0 - 0.8 * moist);

	Surf s = mat(MEADOW, f);
	s = put(s, DRY, dryness, f);
	// Bare earth in patches on the flats, and coming through as the ground tilts.
	s = put(s, EARTH, clamp(smoothstep(0.62, 0.8, mid * 0.6 + large * 0.4) * 0.7 + smoothstep(0.18, 0.38, slope + (mid - 0.5) * 0.15), 0.0, 1.0), f);
	// The ground's layers (Beta 3.2): soil over rock of four kinds.
	vec4 layers = texture(ground_layers, p / map_size);
	float made = layers.r, soil = layers.g, kind = layers.b * 4.0;
	float bare = 1.0 - smoothstep(0.03, 0.3, soil + (mid - 0.5) * 0.1);
	float rockiness = smoothstep(0.38, 0.55, slope + (mid - 0.5) * 0.2 + bare * 0.25);
	// Made ground is soil: a road's banks and a pad's sides are seeded grass with earth showing through where
	// they're steep, never rock, unless the cut went down into the rock: then it's a raw rock face.
	rockiness = max(rockiness * (1.0 - made), made * bare * smoothstep(0.2, 0.4, slope));
	// Scree: broken rock lying below the crags, on ground just too steep to grass over.
	float scree = smoothstep(0.26, 0.4, slope + (fine - 0.5) * 0.1) * (1.0 - smoothstep(0.42, 0.6, slope)) * smoothstep(0.1, 0.6, bare + (large - 0.5) * 0.4) * (1.0 - made);
	s = put(s, SCREE, scree * 0.85, f);
	// The rock: the kind under the cell, blended to the next kind across a boundary.
	if (rockiness > 0.004) {
		int k0 = int(floor(kind)), k1 = int(ceil(kind));
		int l0 = k0 < 1 ? ROCK : SHALE + k0 - 1, l1 = k1 < 1 ? ROCK : SHALE + min(k1, 4) - 1;
		Surf rk = mat(l0, f);
		if (l1 != l0) rk = over(rk, mat(l1, f), fract(kind));
		// Banded light and dark as it weathers.
		rk.c *= mix(0.78, 1.08, smoothstep(0.3, 0.7, fbm(vec2(p.x + p.y, world_pos.y * 8.0))));
		s = over(s, rk, rockiness);
	}
	s = put(s, MEADOW, made * (1.0 - bare) * smoothstep(0.08, 0.25, slope) * 0.7, f);
	s = put(s, EARTH, made * (1.0 - bare) * smoothstep(0.25, 0.55, slope) * (0.35 + 0.4 * fine), f);
	// Under the trees: leaf litter in broadleaf woods and orchards, needles under conifers, dry grass and earth among
	// scrub, mud in the reeds. The litter shows as the canopy closes.
	if (has_plants) {
		vec4 pl = texture(plants, p / map_size);
		float open_ground = (1.0 - rockiness) * (1.0 - made);
		s = put(s, LEAVES, smoothstep(0.25, 0.75, pl.r + (mid - 0.5) * 0.35) * open_ground, f);
		s = put(s, NEEDLES, smoothstep(0.25, 0.75, pl.g + (mid - 0.5) * 0.35) * open_ground, f);
		s = put(s, DRY, smoothstep(0.1, 0.6, pl.b) * 0.6 * open_ground, f);
		s = put(s, EARTH, smoothstep(0.3, 0.9, pl.b + (fine - 0.5) * 0.4) * 0.45 * open_ground, f);
		s = put(s, MUD, smoothstep(0.1, 0.6, pl.a) * 0.55 * open_ground, f);
	}
	// Farm country: fields in rows by crop and season, farm tracks, farmsteads' yards.
	if (has_farms) {
		vec2 cell = floor(p);
		vec4 farm = texture(farms, (cell + 0.5) / map_size);
		int what = int(round(farm.r * 3.0));
		vec2 fc = fract(p);
		if (what == 1) {
			int crop = int(round(farm.g * 7.0));
			vec3 field = crop_colour(crop, month);
			// The rows: a ridge every two metres, plain on bare earth and in a crop that's up.
			float along = farm.a > 0.5 ? p.x : p.y;
			float rows = 0.5 + 0.5 * sin(along * 6.2831853 * 8.0);
			float k = 0.9 + 0.14 * rows + 0.06 * (mid - 0.5);
			Surf tilled = mat(TILLED, f);
			// The crop's colour over the soil's grain: the earth's texture lit by the field's colour.
			tilled.c = field * (tilled.c / max(PLAIN[TILLED], vec3(0.02))) * k;
			tilled.h = mix(tilled.h, rows, 0.5);
			s = over(s, tilled, 0.94);
		} else if (what == 2) {
			int dirs = int(round(farm.b * 3.0));
			float dd = 1.0;
			if ((dirs & 1) != 0) dd = min(dd, abs(fc.y - 0.5));
			if ((dirs & 2) != 0) dd = min(dd, abs(fc.x - 0.5));
			// Two wheel ruts down a track about five metres wide, a grass verge either side.
			float track = 1.0 - smoothstep(0.13, 0.17, dd);
			float ruts = (1.0 - smoothstep(0.02, 0.04, abs(dd - 0.07))) * 0.25;
			s = put_tinted(s, EARTH, track, f, vec3(0.54, 0.46, 0.35), 0.3, 1.0 - ruts);
		} else if (what == 3) {
			s = put(s, GRAVEL, 0.6, f);
			s = put(s, MEADOW, smoothstep(0.4, 0.7, mid + (fine - 0.5) * 0.3) * 0.7, f);
		}
	}
	// Snow above about 300 m, where it can lie.
	float snowy = smoothstep(280.0, 340.0, metres + (large - 0.5) * 60.0) * (1.0 - smoothstep(0.35, 0.6, slope));
	s = put(s, SNOW, snowy, f);
	// Lying snow: a world unit is a 16 m cell across and 16 m up, so the normal gives the true grade.
	if (snow_mm > 0.0 && metres > 0.0) {
		float above = (world_pos.y - snow_median_y) * 16.0;
		float hn = length(nrm.xz);
		float grade = hn / max(nrm.y, 0.05);
		float northness = hn > 0.01 ? -nrm.z / hn : 0.0;
		float mm = snow_mm * clamp(1.0 + above / 400.0, 0.5, 2.0) * (1.0 + 0.35 * northness) * (1.0 - clamp((grade - 0.6) / 0.4, 0.0, 1.0));
		float white = clamp((mm - 3.0) / 37.0, 0.0, 1.0);
		float lying = smoothstep(0.15, 0.6, white + (mid - 0.5) * 0.5 + (fine - 0.5) * 0.1);
		s = put(s, SNOW, lying, f);
		snowy = max(snowy, lying);
	}
	// The shore: sand for the first metre and a half, then the bed darkening with depth.
	float shore = 1.0 - smoothstep(0.6, 1.6 + mid, metres);
	s = put(s, SAND, shore * (1.0 - rockiness), f);
	if (metres < 0.0) {
		s = put(s, MUD, smoothstep(0.0, 3.0, -metres), f);
		s.c = mix(s.c * 0.85, deep, clamp(-metres / 8.0, 0.0, 1.0));
	}
	// A river's banks, as the Flint Hills creek has them: by height over the river's water, a bed of gravel and silt
	// under it, a dark wet band of mud and cobbles at the waterline, then lusher grass on the floodplain beside it.
	if (rb.g > 0.01 && metres > 0.0) {
		float over_water = (world_pos.y - rb.r) / height_step * 2.0;  // metres over the river's water
		float near = smoothstep(0.0, 0.6, rb.g);
		float wet_band = 1.0 - smoothstep(0.5, 1.3 + 0.6 * mid, over_water);
		float plain = (1.0 - smoothstep(2.0, 5.0 + 2.0 * mid, over_water)) * (1.0 - wet_band) * (1.0 - rockiness) * (1.0 - snowy);
		s = put_tinted(s, MEADOW, plain * near * 0.8, f, vec3(0.26, 0.40, 0.16), 0.5, 0.95 + 0.1 * fine);
		if (wet_band * near > 0.004) {
			Surf wet = over(mat(MUD, f), mat(GRAVEL, f), smoothstep(0.45, 0.7, fbm(p * 3.0) + (mid - 0.5) * 0.4));
			wet.c *= 0.68; wet.r *= 0.5;
			s = over(s, wet, wet_band * near * (1.0 - snowy * 0.5));
		}
		if (over_water < 0.0) {
			Surf bed = over(mat(MUD, f), mat(GRAVEL, f), smoothstep(0.35, 0.65, fbm(p * 3.0)));
			bed.c = mix(bed.c, deep, clamp(-over_water / 5.0, 0.0, 1.0));
			s = over(s, bed, near);
		}
	}
	// Broad variation over the tiles, so the textures' repeats don't show from far off: the meadow's own texture
	// laid again seven times larger, as patches of light and dark, and slow noise over that.
	if (ground_look > 0) {
		float km = 16.0 / tile_metres[MEADOW] * 0.14;
		vec3 macro = textureGrad(ground_albedo, vec3(turn(p * km, 0.6) + 0.37, float(MEADOW)), turn(f.dx.xz * km, 0.6), turn(f.dy.xz * km, 0.6)).rgb;
		float ratio = dot(macro, vec3(0.333)) / dot(PLAIN[MEADOW], vec3(0.333));
		// Only on the grass: projected from above, it would smear down a cliff.
		float grassy = (1.0 - snowy) * (1.0 - rockiness) * (1.0 - smoothstep(0.3, 0.5, slope));
		s.c *= mix(1.0, clamp(ratio, 0.6, 1.5), 0.55 * grassy);
	}
	s.c *= (0.88 + 0.24 * large) * (0.94 + 0.12 * mid);
	// The land's shape shading its hollows and valley bottoms (CityView.Hollow), as the sky lights them.
	float open_sky = COLOR.a > 0.5 ? COLOR.r * 1.25 : 1.0;
	s.c *= mix(1.0, open_sky, 0.8);
	if (ground_look == 0) s.c *= 0.9 + 0.2 * fine;
	if (COLOR.a < 0.5) {
		s = mat(EARTH, f);
		s.c = mix(s.c * 0.7, PLAIN[ROCK] * 0.68, smoothstep(0.3, 0.7, mid)) * (0.85 + 0.15 * fine);
		s.n = nrm;
	}

	vec3 base = to_linear(s.c, srgb);
	vec2 c = floor(p);
	if (COLOR.a > 0.5) {
		vec4 mark = texture(cells, (c + 0.5) / map_size);
		base = mix(base, to_linear(mark.rgb, srgb) * (0.94 + 0.12 * fine), mark.a);
		s.n = normalize(mix(s.n, nrm, mark.a));
	}
	if (underground) {
		vec3 soil_cut = mix(vec3(0.30, 0.22, 0.15), vec3(0.22, 0.20, 0.19), bare) * (0.85 + 0.25 * mid + 0.1 * fine);
		base = mix(base * 0.35, to_linear(soil_cut, srgb), 0.8);
		s.n = nrm;
	}
	if (use_overlay) {
		vec4 o = texture(overlay, (c + 0.5) / map_size);
		base = mix(base, to_linear(o.rgb, srgb), o.a);
	}
	if (c == selected) {
		vec2 fc = fract(p);
		float edge = step(min(min(fc.x, fc.y), min(1.0 - fc.x, 1.0 - fc.y)), 0.08);
		base = mix(base, vec3(1.0), 0.25 + 0.6 * edge);
	}
	if (flash_strength > 0.0 && c.x >= flash_rect.x && c.y >= flash_rect.y && c.x <= flash_rect.z && c.y <= flash_rect.w)
		base = mix(base, vec3(1.0, 0.78, 0.25), flash_strength);
	ALBEDO = base;
	ROUGHNESS = ground_look == 0 ? mix(0.95, 0.6, snowy) : clamp(s.r, 0.05, 1.0);
	SPECULAR = 0.2;
	if (ground_look >= 2) {
		NORMAL = normalize((VIEW_MATRIX * vec4(s.n, 0.0)).xyz);
		AO = s.ao;
		AO_LIGHT_AFFECT = 0.35;
	}
}
