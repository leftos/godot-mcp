shader_type canvas_item;
vec4 tinted(sampler2D tex, vec2 uv) { return texture(tex, uv) * 0.5; }
void fragment() { COLOR = tinted(TEXTURE, UV); }
