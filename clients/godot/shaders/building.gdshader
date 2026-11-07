// Voxel buildings and the life they show. Per-instance data (INSTANCE_CUSTOM):
//   r  height in cells above which nothing is drawn: a building going up layer by layer, or crumbling down
//   g  wear, 0 new to 1 falling apart: grime in the corners of the voxels
//   b  graffiti, 0 to 1: tags on the lower walls
//   a  flags: 1 burning, 2 abandoned, 4 crumbling, in the whole part; the share of windows lit at night times 0.9
//      in the fraction (Beta 3.2, 6.4)
// The project's global `night` (0 day to 1 full dark) turns the lit windows on. Snow lies on the roofs (Beta 4, part
// 4) by the globals `snow_mm` and `snow_median_y`, as on level ground at the roof's height (see terrain.gdshader).
shader_type spatial;
render_mode cull_back;

varying vec3 local;
varying vec4 life;
varying float world_y;

// RC part 5: a surface with a finish draws its texture (and relief) instead of the vertex colour. Glass lights up at
// night in place of the windows painted on plain walls.
uniform bool textured = false;
uniform sampler2D finish_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D finish_normal : hint_normal, filter_linear_mipmap, repeat_enable;
uniform bool has_normal = false;
uniform vec2 uv_scale = vec2(1.0);
uniform float finish_roughness = 0.85;
uniform float finish_metallic = 0.0;
uniform bool glassy = false;

global uniform float night;
global uniform float snow_mm;
global uniform float snow_median_y;

const float VOXEL = 1.0 / 32.0;

float hash(vec3 p) {
	p = fract(p * vec3(443.897, 441.423, 437.195));
	p += dot(p, p.yzx + 19.19);
	return fract((p.x + p.y) * p.z);
}

// Vertex colours are sRGB. The Compatibility renderer works in sRGB throughout (OUTPUT_IS_SRGB), so they're
// converted only for Forward+ and Mobile, as StandardMaterial3D's vertex_color_is_srgb does.
vec3 to_linear(vec3 c, bool srgb_out) {
	if (srgb_out) return c;
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void vertex() {
	local = VERTEX;
	life = INSTANCE_CUSTOM;
	world_y = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y;
}

void fragment() {
	int flags = int(floor(life.a + 0.001));
	float lit = clamp(fract(life.a + 0.001) / 0.9, 0.0, 1.0);
	bool burning = (flags & 1) != 0, abandoned = (flags & 2) != 0, crumbling = (flags & 4) != 0;
	// The voxel this fragment belongs to, nudged inside the face so each voxel gets one random number.
	vec3 n = (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
	vec3 cell = floor((local - n * VOXEL * 0.5) / VOXEL);
	float r = hash(cell);

	float cut = floor(life.r / VOXEL) * VOXEL;
	if (local.y > cut + 0.0005) discard;
	// Crumbling: voxels near the top break away first.
	if (crumbling && r < 0.35 && local.y > cut - 6.0 * VOXEL) discard;

	vec3 c = textured ? texture(finish_tex, UV * uv_scale).rgb : to_linear(COLOR.rgb, OUTPUT_IS_SRGB);
	if (textured && has_normal) NORMAL_MAP = texture(finish_normal, UV * uv_scale).rgb;
	bool building = life.r < 50.0 && !crumbling;
	// The working edge of a building going up: scaffold planks.
	if (building && local.y > cut - VOXEL * 1.5) c = mix(vec3(0.55, 0.38, 0.18), vec3(0.75, 0.6, 0.35), r);
	// Wear: grime, more of it low down and the older the building.
	float grime = life.g * (0.55 + 0.45 * r) * (1.0 - clamp(local.y * 0.25, 0.0, 0.6));
	c = mix(c, c * vec3(0.55, 0.5, 0.42), grime);
	// Graffiti on the lower walls.
	bool wall = abs(n.y) < 0.5;
	if (wall && local.y < 0.2 && hash(cell + 7.0) < life.b * 0.35)
		c = to_linear(vec3(hash(cell + 1.0), hash(cell + 2.0), hash(cell + 3.0)) * 0.8 + 0.2, OUTPUT_IS_SRGB);
	if (abandoned) c = mix(vec3(dot(c, vec3(0.3, 0.59, 0.11))), c, 0.35) * 0.7;
	if (crumbling) c = mix(c, vec3(0.6, 0.57, 0.5), 0.5);
	// Snow on the roofs and ledges: every upward face, unless it's burning; a little thinner than on the ground,
	// from the warmth inside, and patchy voxel by voxel while it's thin.
	if (snow_mm > 0.0 && n.y > 0.7 && !burning) {
		float mm = 0.8 * snow_mm * clamp(1.0 + (world_y - snow_median_y) * 16.0 / 400.0, 0.5, 2.0);
		float white = clamp((mm - 3.0) / 37.0, 0.0, 1.0);
		c = mix(c, to_linear(vec3(0.92, 0.93, 0.96), OUTPUT_IS_SRGB), step(r, white * 1.1));
	}
	ALBEDO = c;
	ROUGHNESS = textured ? finish_roughness : 0.95;
	METALLIC = textured ? finish_metallic : 0.0;
	// A finished building's real windows: each pane of glass (two metres a patch) lit or not by the room behind it.
	if (textured && glassy && night > 0.01 && lit > 0.0 && !burning) {
		vec3 room = floor(local * 8.0);
		if (hash(room + 3.0) < lit) {
			vec3 lamp = mix(vec3(1.0, 0.72, 0.38), vec3(0.85, 0.9, 1.0), step(0.8, hash(room + 11.0)));
			EMISSION = lamp * (1.2 + 0.8 * hash(room + 5.0)) * night;
		}
		ALBEDO = c * (1.0 - 0.6 * night);
	}
	// Night (6.4): windows in rows a storey apart on the walls, some of them lit, warm or cool by the room.
	if (!textured && night > 0.01 && wall && lit > 0.0 && !burning) {
		vec3 along = abs(n.x) > 0.5 ? vec3(0.0, cell.y, cell.z) : vec3(cell.x, cell.y, 0.0);
		float h = abs(n.x) > 0.5 ? cell.z : cell.x;
		bool pane = mod(cell.y, 6.0) >= 2.0 && mod(cell.y, 6.0) < 4.0 && mod(h, 4.0) >= 1.0 && cell.y >= 2.0;
		if (pane) {
			vec3 window = floor(along / vec3(4.0, 6.0, 4.0));
			float w = hash(window + n * 3.0);
			if (w < lit) {
				vec3 lamp = mix(vec3(1.0, 0.72, 0.38), vec3(0.85, 0.9, 1.0), step(0.8, hash(window + 11.0)));
				EMISSION = lamp * (1.2 + 0.8 * hash(window + 5.0)) * night;
				ALBEDO = c * (1.0 - 0.6 * night);
			} else ALBEDO = c * (1.0 - 0.5 * night);
		}
	}
	if (burning) {
		// Charred walls, and flames licking the upper floors.
		ALBEDO = c * 0.35;
		float flicker = 0.5 + 0.5 * sin(TIME * 9.0 + r * 30.0 + local.y * 12.0);
		if (r > 0.45 && hash(cell + floor(TIME * 6.0)) > 0.4)
			EMISSION = mix(vec3(1.0, 0.25, 0.02), vec3(1.0, 0.75, 0.2), flicker) * 2.5;
	}
}
