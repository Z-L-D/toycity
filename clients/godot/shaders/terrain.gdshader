// The ground: SimCity 4's heightfield drawn as it would look from the air. What it's made of goes by its slope and
// height, as real ground does: meadow and drier grass with patches of bare earth on the flats, earth then rock as the
// slope steepens, a sandy shore just above the waterline, a bed that darkens with depth, and snow on high gentle ground.
// Noise in world space breaks every material up so no two cells look alike. Over it go the city's marks, one texel a
// cell (cells): a zone's light tint on empty zoned land, and the roads until they have their own shapes. Then the map
// layers: a data overlay (one texel a cell), the selected cell, and an area pulsing for what an alert is about.
// COLOR.a is 0 on the map's side walls and the skirts under chunk edges: those are bare soil.
shader_type spatial;
render_mode cull_disabled;

uniform sampler2D cells : filter_nearest, repeat_disable;
uniform sampler2D overlay : filter_nearest, repeat_disable;
// Per cell: r earthworks, g soil depth (0 bare rock, 1 eight metres or more), b rock kind / 4.
uniform sampler2D ground_layers : filter_linear, repeat_disable;
uniform bool use_overlay = false;
uniform vec2 map_size = vec2(128.0);
uniform vec2 selected = vec2(-1.0);
uniform vec4 flash_rect = vec4(-1.0);  // x0, y0, x1, y1 in cells, inclusive
uniform float flash_strength = 0.0;
uniform float water_y = -1000.0;     // the water's surface, in world units
uniform float height_step = 0.125;   // one height step (2 m), in world units

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

void fragment() {
	bool srgb = OUTPUT_IS_SRGB;
	vec2 p = world_pos.xz;
	// Metres above the water, and how steep (0 flat, 1 a wall).
	float metres = (world_pos.y - water_y) / height_step * 2.0;
	float slope = 1.0 - clamp(world_normal.y, 0.0, 1.0);
	float large = fbm(p * 0.07), mid = fbm(p * 0.6 + 3.0), fine = noise(p * 6.0);

	vec3 meadow = vec3(0.33, 0.42, 0.20), dry = vec3(0.45, 0.45, 0.26), earth = vec3(0.42, 0.34, 0.24);
	vec3 rock = vec3(0.47, 0.45, 0.42), rock_dark = vec3(0.32, 0.30, 0.28), sand = vec3(0.71, 0.65, 0.50);
	vec3 snow = vec3(0.93, 0.94, 0.96), bed = vec3(0.45, 0.42, 0.32), deep = vec3(0.12, 0.17, 0.16);

	vec3 ground = mix(meadow, dry, smoothstep(0.35, 0.75, large));
	ground = mix(ground, ground * 0.82, smoothstep(0.45, 0.8, mid));
	// Bare earth in patches on the flats, and coming through as the ground tilts.
	ground = mix(ground, earth, clamp(smoothstep(0.62, 0.8, mid * 0.6 + large * 0.4) * 0.7 + smoothstep(0.18, 0.38, slope + (mid - 0.5) * 0.15), 0.0, 1.0));
	// The ground's layers (Beta 3.2): soil over rock of four kinds.
	vec4 layers = texture(ground_layers, p / map_size);
	float made = layers.r, soil = layers.g, kind = layers.b * 4.0;
	// Each rock its colour: shale blue-grey, sandstone tan, limestone pale, granite pinkish grey.
	vec3 rock_kind = mix(vec3(0.38, 0.39, 0.42), vec3(0.62, 0.51, 0.37), clamp(kind - 1.0, 0.0, 1.0));
	rock_kind = mix(rock_kind, vec3(0.70, 0.68, 0.62), clamp(kind - 2.0, 0.0, 1.0));
	rock_kind = mix(rock_kind, vec3(0.56, 0.48, 0.46), clamp(kind - 3.0, 0.0, 1.0));
	rock = mix(rock, rock_kind, step(0.5, kind));
	rock_dark = rock * 0.68;
	// Rock on the steep, banded light and dark; where the soil is thin it shows on gentler ground too.
	float bare = 1.0 - smoothstep(0.03, 0.3, soil + (mid - 0.5) * 0.1);
	float rockiness = smoothstep(0.38, 0.55, slope + (mid - 0.5) * 0.2 + bare * 0.25);
	// Made ground is soil: a road's banks and a pad's sides are seeded grass with earth showing through where
	// they're steep, never rock, unless the cut went down into the rock: then it's a raw rock face.
	rockiness = max(rockiness * (1.0 - made), made * bare * smoothstep(0.2, 0.4, slope));
	ground = mix(ground, mix(rock_dark, rock, smoothstep(0.3, 0.7, fbm(vec2(p.x + p.y, world_pos.y * 8.0)))), rockiness);
	vec3 bank = mix(meadow * 0.92, dry, 0.25 + 0.3 * mid);
	bank = mix(bank, earth, smoothstep(0.25, 0.55, slope) * (0.35 + 0.4 * fine));
	ground = mix(ground, bank, made * (1.0 - bare) * smoothstep(0.08, 0.25, slope));
	// Snow above about 300 m, where it can lie.
	float snowy = smoothstep(280.0, 340.0, metres + (large - 0.5) * 60.0) * (1.0 - smoothstep(0.35, 0.6, slope));
	ground = mix(ground, snow, snowy);
	// The shore: sand for the first metre and a half, then the bed darkening with depth.
	float shore = 1.0 - smoothstep(0.6, 1.6 + mid, metres);
	ground = mix(ground, sand, shore * (1.0 - rockiness));
	if (metres < 0.0) ground = mix(bed, deep, clamp(-metres / 8.0, 0.0, 1.0));
	// Fine grain, so close up it reads as ground rather than paint.
	ground *= 0.9 + 0.2 * fine;
	if (COLOR.a < 0.5) ground = mix(earth * 0.7, rock_dark, smoothstep(0.3, 0.7, mid)) * (0.85 + 0.15 * fine);

	vec3 base = to_linear(ground, srgb);
	vec2 c = floor(p);
	if (COLOR.a > 0.5) {
		vec4 mark = texture(cells, (c + 0.5) / map_size);
		base = mix(base, to_linear(mark.rgb, srgb) * (0.94 + 0.12 * fine), mark.a);
	}
	if (use_overlay) {
		vec4 o = texture(overlay, (c + 0.5) / map_size);
		base = mix(base, to_linear(o.rgb, srgb), o.a);
	}
	if (c == selected) {
		vec2 f = fract(p);
		float edge = step(min(min(f.x, f.y), min(1.0 - f.x, 1.0 - f.y)), 0.08);
		base = mix(base, vec3(1.0), 0.25 + 0.6 * edge);
	}
	if (flash_strength > 0.0 && c.x >= flash_rect.x && c.y >= flash_rect.y && c.x <= flash_rect.z && c.y <= flash_rect.w)
		base = mix(base, vec3(1.0, 0.78, 0.25), flash_strength);
	ALBEDO = base;
	ROUGHNESS = mix(0.95, 0.6, snowy);
	SPECULAR = 0.2;
}
