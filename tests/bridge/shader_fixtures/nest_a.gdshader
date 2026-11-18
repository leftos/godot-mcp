shader_type spatial;
#include "nest_b.gdshaderinc"
void fragment() {
	ALBEDO = vec3(nested());
}
