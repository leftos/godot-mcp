extends "res://gd_test.gd"
## The headless scene edits' C# refusal (headless/scene_edit.gd csharp_refusal): refused only while
## the build failed and the scene uses C#, quoting the configuration and the compiler errors.

const SCENE_EDIT_SCRIPT := "../../headless/scene_edit.gd"
const ERRORS := "D:/p/CsProbeNode.cs:10: CS1002 ; expected\n(and 3 more)"

var _edit: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_EDIT_SCRIPT).simplify_path()
)


func test_csharp_refusal_quotes_the_configuration_and_the_errors() -> void:
	var prep := {"build": "failed", "buildConfiguration": "Debug", "buildErrors": ERRORS}
	assert_eq(
		_edit.csharp_refusal("res://main.tscn", true, prep),
		(
			"res://main.tscn uses C# scripts and the project's Debug C# build failed; fix it first:\n"
			+ ERRORS
		),
		"a failed build's refusal quotes its errors"
	)


func test_csharp_refusal_without_errors_points_at_validate() -> void:
	assert_eq(
		_edit.csharp_refusal("res://main.tscn", true, {"build": "failed"}),
		(
			"res://main.tscn uses C# scripts and the project's C# build failed; fix it first"
			+ " (validate lists the errors)."
		),
		"no quoted errors or configuration"
	)


func test_csharp_refusal_is_empty_unless_the_build_failed_and_csharp_is_used() -> void:
	var failed := {"build": "failed", "buildConfiguration": "Debug", "buildErrors": ERRORS}
	assert_eq(_edit.csharp_refusal("res://a.tscn", false, failed), "", "a scene without C#")
	for build in ["built", "up-to-date", "no-csproj", "skipped", ""]:
		var prep := {"build": build, "buildConfiguration": "Debug", "buildErrors": ERRORS}
		assert_eq(_edit.csharp_refusal("res://a.tscn", true, prep), "", "build %s" % build)
