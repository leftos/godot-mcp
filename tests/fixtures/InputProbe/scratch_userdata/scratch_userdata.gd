extends "res://scratch/scratch_probe.gd"
## Notes where its user:// is and what the seed left there.


func _init() -> void:
	add_step("where", func() -> void: set_note(OS.get_user_data_dir()))
	add_step("seeded", func() -> void: set_note(FileAccess.get_file_as_string("user://seed.txt")))
