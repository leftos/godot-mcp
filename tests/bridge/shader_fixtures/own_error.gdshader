shader_type spatial;
#include "ok.gdshaderinc"
void fragment() {
	ALBEDO = vec3(ok_value(), 0.0);
}
