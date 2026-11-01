extends RefCounted
## The folder one gdtest run writes its fixtures under: user:// resolves to one folder per project
## name, so every worktree and every run at once shares it unless the run keeps a folder of its own.


## The folder this run writes its files under, named for the process running it.
static func root() -> String:
	return "user://gdtest-%d/" % OS.get_process_id()


## The path of name inside this run's folder.
static func path(name: String) -> String:
	return root() + name


## Removes this run's folder with everything in it; a path left behind prints one line and changes
## nothing else.
static func remove_root() -> void:
	var folder: String = root()
	if DirAccess.dir_exists_absolute(folder):
		_remove_tree(folder)


## Deletes folder's subfolders, then its files, then the folder itself.
static func _remove_tree(folder: String) -> void:
	for sub_dir: String in DirAccess.get_directories_at(folder):
		_remove_tree(folder.path_join(sub_dir))
	for file_name: String in DirAccess.get_files_at(folder):
		_remove(folder.path_join(file_name))
	_remove(folder)


static func _remove(path: String) -> void:
	var error: Error = DirAccess.remove_absolute(path)
	if error != OK:
		print("gdtest: could not remove %s: error %d" % [path, error])
