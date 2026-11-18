shader_type spatial;
#define TINT vec3(1.0)
#include "res://fx/missing.gdshaderinc"
void fragment() { ALBEDO = TINT; }
