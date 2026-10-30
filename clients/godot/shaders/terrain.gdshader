// The ground, coloured per cell (vertex colours), with the map layers drawn over it: a data overlay or the water
// pipes (one texel per cell), the selected cell, and an area pulsing for what an alert or petition is about.
// Near and under the water the colour goes by height, not by cell: a sandy edge above the waterline and a bed that
// darkens with depth. Coloured by cell, the bed showed through the water as squares, a jagged line under the
// smooth waterline of sloped ground.
shader_type spatial;
render_mode cull_disabled;

uniform sampler2D overlay : filter_nearest, repeat_disable;
uniform bool use_overlay = false;
uniform vec2 map_size = vec2(128.0);
uniform vec2 selected = vec2(-1.0);
uniform vec4 flash_rect = vec4(-1.0);  // x0, y0, x1, y1 in cells, inclusive
uniform float flash_strength = 0.0;
uniform float water_y = -1000.0;     // the water's surface, in world units
uniform float height_step = 0.125;   // one height step, in world units

varying vec2 cell;
varying float world_y;

// Vertex colours are sRGB. The Compatibility renderer works in sRGB throughout (OUTPUT_IS_SRGB), so they're
// converted only for Forward+ and Mobile, as StandardMaterial3D's vertex_color_is_srgb does.
vec3 to_linear(vec3 c, bool srgb_out) {
	if (srgb_out) return c;
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void vertex() {
	// A point just inside its own cell, so a wall takes the colour of the cell it belongs to.
	cell = (MODEL_MATRIX * vec4(VERTEX - NORMAL * 0.01, 1.0)).xz;
	world_y = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y;
}

void fragment() {
	vec3 base = to_linear(COLOR.rgb, OUTPUT_IS_SRGB);
	// The shore: sand just above the waterline, then a bed that darkens with depth.
	float above = (world_y - water_y) / height_step;
	vec3 sand = to_linear(vec3(0.70, 0.64, 0.47), OUTPUT_IS_SRGB);
	vec3 deep = to_linear(vec3(0.16, 0.26, 0.30), OUTPUT_IS_SRGB);
	if (above < 0.0) base = mix(sand * 0.85, deep, clamp(-above / 3.0, 0.0, 1.0));
	else if (above < 0.45) base = mix(sand, base, smoothstep(0.15, 0.45, above));
	if (use_overlay) {
		vec4 o = texture(overlay, (floor(cell) + 0.5) / map_size);
		base = mix(base, to_linear(o.rgb, OUTPUT_IS_SRGB), o.a);
	}
	vec2 c = floor(cell);
	if (c == selected) {
		vec2 f = fract(cell);
		float edge = step(min(min(f.x, f.y), min(1.0 - f.x, 1.0 - f.y)), 0.08);
		base = mix(base, vec3(1.0), 0.25 + 0.6 * edge);
	}
	if (flash_strength > 0.0 && c.x >= flash_rect.x && c.y >= flash_rect.y && c.x <= flash_rect.z && c.y <= flash_rect.w)
		base = mix(base, vec3(1.0, 0.78, 0.25), flash_strength);
	ALBEDO = base;
	ROUGHNESS = 1.0;
}
