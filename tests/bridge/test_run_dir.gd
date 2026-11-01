extends "res://gd_test.gd"
## The folder each run keeps its fixtures in (run_dir.gd): user:// is one folder per project name,
## shared by every worktree and every run at once, so a folder of the run's own is what keeps two
## runs from reading or removing each other's files.

## A quote, built from its code so this file's own scan for one finds none.
const QUOTE := 34
const RunDir := preload("res://run_dir.gd")


func test_run_dir_is_this_process() -> void:
	assert_true(
		RunDir.root().contains(str(OS.get_process_id())), "the folder is named for this process"
	)
	assert_true(
		DirAccess.dir_exists_absolute(RunDir.root()), "this run's folder exists while its tests run"
	)


func test_no_test_names_a_shared_path() -> void:
	var needles: PackedStringArray = [char(QUOTE) + "user" + "://", "OS." + "get_temp_dir()"]
	var offenders: PackedStringArray = []
	for file_name: String in DirAccess.get_files_at("res://"):
		if not file_name.begins_with("test_") or not file_name.ends_with(".gd"):
			continue
		var lines: PackedStringArray = FileAccess.get_file_as_string("res://" + file_name).split(
			"\n"
		)
		for index in lines.size():
			for needle: String in needles:
				if lines[index].contains(needle):
					offenders.append("%s:%d" % [file_name, index + 1])
	assert_eq(offenders, PackedStringArray(), "test files naming a shared path")
