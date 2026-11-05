// Trees, scrub and reeds (Beta 4.1, part 1). Each instance is one crown from TreeLayout. Vertex colours carry the
// part's colour, and their alpha how far up the plant it is (0 at the foot to 1 at the top), which sets how far the
// wind sways it. The uniform `kind` is the plant (1 broadleaf, 2 conifer, 3 scrub, 4 reeds, 5 orchard), one material
// each; each crown's shade comes from a hash of where it stands. The project's global `month` turns broadleaf leaves: fresh green in spring, deep in summer, gold and
// rust in the autumn, and bare grey-brown in winter. Snow settles on the upward faces as on the roofs.
shader_type spatial;
render_mode cull_back;

global uniform float night;
global uniform float snow_mm;
global uniform float snow_median_y;
global uniform float month;

uniform int kind = 1;
varying float tone;
varying float leafy;
varying vec3 wpos;
varying float world_y;
varying float up;

vec3 to_linear(vec3 c, bool srgb_out) {
	if (srgb_out) return c;
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

float hash(vec3 p) {
	p = fract(p * vec3(443.897, 441.423, 437.195));
	p += dot(p, p.yzx + 19.19);
	return fract((p.x + p.y) * p.z);
}

// How much leaf a broadleaf tree has on it by the month: bare from December to March, full from May to October.
float leafiness(float m) {
	if (m < 3.5) return 0.0;
	if (m < 5.0) return (m - 3.5) / 1.5;
	if (m < 10.5) return 1.0;
	if (m < 11.8) return 1.0 - (m - 10.5) / 1.3;
	return 0.0;
}

void vertex() {
	up = COLOR.a;
	bool crown = COLOR.g > COLOR.r * 1.05;
	leafy = (kind == 1 || kind == 5) ? leafiness(month) : 1.0;
	// A bare crown is its branches: drawn tighter round the trunk.
	if (crown && leafy < 1.0) VERTEX.xz *= mix(0.7, 1.0, leafy);
	vec3 origin = MODEL_MATRIX[3].xyz;
	tone = hash(origin * 3.17 + 0.5);
	// A slow sway in the wind, more at the top, out of step from tree to tree.
	float phase = origin.x * 1.7 + origin.z * 2.3;
	float sway = (sin(TIME * 1.3 + phase) + 0.4 * sin(TIME * 3.1 + phase * 1.9)) * 0.012 * up * up;
	VERTEX.x += sway;
	VERTEX.z += sway * 0.6;
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_y = wpos.y;
}

// A broadleaf crown's colour by the month: spring, summer, autumn and winter.
vec3 leaves(float m, float shade) {
	vec3 spring = vec3(0.47, 0.62, 0.25), summer = vec3(0.25, 0.38, 0.17);
	vec3 autumn = mix(vec3(0.72, 0.50, 0.16), vec3(0.60, 0.26, 0.12), shade), winter = vec3(0.34, 0.29, 0.26);
	if (m < 3.0) return winter;
	if (m < 4.5) return mix(winter, spring, (m - 3.0) / 1.5);
	if (m < 6.0) return mix(spring, summer, (m - 4.5) / 1.5);
	if (m < 9.0) return summer;
	if (m < 10.5) return mix(summer, autumn, (m - 9.0) / 1.5);
	if (m < 11.5) return mix(autumn, winter, m - 10.5);
	return winter;
}

void fragment() {
	vec3 c = COLOR.rgb;
	bool crown = COLOR.g > COLOR.r * 1.05;  // the green parts; trunks are brown
	vec3 wp = wpos;
	// Bare branches let the light through: a crown with fewer leaves has holes in it.
	if (crown && leafy < 1.0 && hash(floor(wp * 90.0)) > 0.35 + 0.65 * leafy) discard;
	if (crown) {
		if (kind == 1 || kind == 5) c = leaves(month, tone) * (0.85 + 0.3 * tone);
		else c *= 0.85 + 0.3 * tone;
	}
	c = to_linear(c, OUTPUT_IS_SRGB);
	vec3 n = (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
	// Light falls off towards the inside of the crown.
	c *= 0.7 + 0.3 * up;
	if (snow_mm > 0.0 && n.y > 0.55 && kind != 4) {
		float mm = 0.7 * snow_mm * clamp(1.0 + (world_y - snow_median_y) * 16.0 / 400.0, 0.5, 2.0);
		float white = clamp((mm - 5.0) / 45.0, 0.0, 1.0) * (kind == 2 ? 1.0 : 0.7);
		vec3 cell = floor(wpos * 40.0);
		c = mix(c, to_linear(vec3(0.9, 0.92, 0.95), OUTPUT_IS_SRGB), step(hash(cell), white));
	}
	ALBEDO = c * (1.0 - 0.45 * night);
	ROUGHNESS = 0.9;
	SPECULAR = 0.2;
}
