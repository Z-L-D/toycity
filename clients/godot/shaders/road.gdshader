// The roads, paths and tracks: their vertex colours (sRGB), shining when wet (Beta 3.2, part 4), and at night pools
// of warm street light along them, a lamp to every other cell (Beta 3.2, 6.4). The project's global `night` (0 day
// to 1 full dark) turns the lamps on.
shader_type spatial;
render_mode cull_disabled;

uniform float wet = 0.0;  // 0 dry to 1 soaked
global uniform float night;

varying vec3 world_pos;

vec3 to_linear(vec3 c, bool srgb_out) {
	if (srgb_out) return c;
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 c = to_linear(COLOR.rgb, OUTPUT_IS_SRGB);
	c *= mix(vec3(1.0), vec3(0.74, 0.75, 0.78), wet);
	ALBEDO = c;
	ROUGHNESS = mix(0.92, 0.28, wet);
	SPECULAR = mix(0.5, 0.9, wet);
	if (night > 0.01) {
		vec2 cell = floor(world_pos.xz);
		if (mod(cell.x + cell.y, 2.0) < 0.5) {
			float d = length(world_pos.xz - (cell + 0.5));
			float pool = 1.0 - smoothstep(0.05, 0.4, d);
			// A wet road throws the light back more.
			EMISSION = vec3(1.0, 0.74, 0.42) * pool * pool * (0.55 + 0.35 * wet) * night;
		}
	}
}
