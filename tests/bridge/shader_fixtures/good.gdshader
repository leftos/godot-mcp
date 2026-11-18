shader_type spatial;
global uniform vec4 tint;
void fragment() {
	ALBEDO = tint.rgb;
}
