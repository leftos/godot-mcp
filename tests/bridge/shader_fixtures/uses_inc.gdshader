shader_type canvas_item;
#include "res://shader_fixtures/inc.gdshaderinc"

void fragment() {
	COLOR = vec4(helper());
}
