// Voxel buildings and the life they show. Per-instance data (INSTANCE_CUSTOM):
//   r  height in cells above which nothing is drawn: a building going up layer by layer, or crumbling down
//   g  wear, 0 new to 1 falling apart: grime in the corners of the voxels
//   b  graffiti, 0 to 1: tags on the lower walls
//   a  flags: 1 burning, 2 abandoned, 4 crumbling
shader_type spatial;
render_mode cull_back;

varying vec3 local;
varying vec4 life;

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
}

void fragment() {
	int flags = int(life.a + 0.5);
	bool burning = (flags & 1) != 0, abandoned = (flags & 2) != 0, crumbling = (flags & 4) != 0;
	// The voxel this fragment belongs to, nudged inside the face so each voxel gets one random number.
	vec3 n = (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
	vec3 cell = floor((local - n * VOXEL * 0.5) / VOXEL);
	float r = hash(cell);

	float cut = floor(life.r / VOXEL) * VOXEL;
	if (local.y > cut + 0.0005) discard;
	// Crumbling: voxels near the top break away first.
	if (crumbling && r < 0.35 && local.y > cut - 6.0 * VOXEL) discard;

	vec3 c = to_linear(COLOR.rgb, OUTPUT_IS_SRGB);
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
	ALBEDO = c;
	ROUGHNESS = 0.95;
	if (burning) {
		// Charred walls, and flames licking the upper floors.
		ALBEDO = c * 0.35;
		float flicker = 0.5 + 0.5 * sin(TIME * 9.0 + r * 30.0 + local.y * 12.0);
		if (r > 0.45 && hash(cell + floor(TIME * 6.0)) > 0.4)
			EMISSION = mix(vec3(1.0, 0.25, 0.02), vec3(1.0, 0.75, 0.2), flicker) * 2.5;
	}
}
