// The water: a flat sheet at the waterline, as SimCity 4's, coloured by how deep it is over the ground beneath
// (read from the corner heights): clear and green over the shallows, deep blue far out, with foam where it laps the
// shore. Slow crossing ripples move its surface so the sun glints on it.
shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_opaque;

uniform sampler2D ground : filter_linear, repeat_disable;  // corner heights in world units, (width + 1) by (height + 1)
uniform vec2 map_size = vec2(128.0);
uniform float height_step = 0.125;

varying vec3 world_pos;

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

vec3 to_linear(vec3 c, bool srgb_out) {
	if (srgb_out) return c;
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

float waves(vec2 p, float t) {
	return noise(p * 1.7 + vec2(t * 0.35, t * 0.2)) * 0.6 + noise(p * 4.1 - vec2(t * 0.5, -t * 0.3)) * 0.4;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	bool srgb = OUTPUT_IS_SRGB;
	vec2 p = world_pos.xz;
	// Depth in metres: the water's surface over the ground at this point.
	float bed = texture(ground, (p + 0.5) / (map_size + 1.0)).r;
	float depth = max(0.0, (world_pos.y - bed) / height_step * 2.0);
	vec3 shallow = vec3(0.30, 0.52, 0.48), mid = vec3(0.10, 0.33, 0.42), far = vec3(0.04, 0.16, 0.30);
	vec3 col = mix(shallow, mid, smoothstep(0.0, 3.0, depth));
	col = mix(col, far, smoothstep(3.0, 10.0, depth));
	float t = TIME;
	// Foam in a ragged band where it meets the shore.
	float foam = (1.0 - smoothstep(0.0, 0.7, depth)) * smoothstep(0.45, 0.75, waves(p * 1.5, t * 1.4));
	col = mix(col, vec3(0.92, 0.95, 0.95), foam * 0.8);
	ALBEDO = to_linear(col, srgb);
	// Ripples: the slope of the wave field tips the surface's normal.
	float e = 0.05, h0 = waves(p, t), hx = waves(p + vec2(e, 0.0), t), hz = waves(p + vec2(0.0, e), t);
	vec3 n = normalize(vec3(-(hx - h0) / e * 0.06, 1.0, -(hz - h0) / e * 0.06));
	NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
	ROUGHNESS = 0.06;
	METALLIC = 0.0;
	SPECULAR = 0.7;
	// See into the shallows; deep water is opaque.
	ALPHA = mix(0.55, 0.93, smoothstep(0.0, 4.0, depth)) + foam * 0.3;
}
