extends SceneTree

# EsoAgent builtin perceive script for Godot 4 (item 36/38).
# Run: godot --headless --path <project> --script <this file> -- --eso-scene="res://scene.tscn"
# Dumps the scene's node tree as structured JSON between the ESO sentinels:
# name, class, tree path, local transform (Node3D/Node2D), mesh/camera facts.
# Read-only: the scene is instantiated in memory, never added to the tree,
# never saved — _ready/_process never run, so transforms are the AUTHORED
# local values from the packed scene, exactly as data should be.

func _init() -> void:
	var scene_path := ""
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--eso-scene="):
			scene_path = a.trim_prefix("--eso-scene=")
	if scene_path == "":
		scene_path = ProjectSettings.get_setting("application/run/main_scene", "")
	var out := {"app": "godot", "scene": scene_path, "items": [], "notes": []}
	if scene_path == "":
		out["notes"].append("No scene given (--eso-scene=...) and no main scene configured in the project.")
		_emit(out)
		return
	var packed = load(scene_path)
	if packed == null or not (packed is PackedScene):
		out["notes"].append("Could not load a PackedScene at " + scene_path)
		_emit(out)
		return
	var root_node: Node = packed.instantiate()
	_walk(root_node, out["items"], "")
	_emit(out)

func _walk(node: Node, items: Array, prefix: String) -> void:
	var my_path := str(node.name) if prefix == "" else prefix + "/" + str(node.name)
	var item := {"name": str(node.name), "type": node.get_class(), "path": my_path}
	if node is Node3D:
		var t: Transform3D = node.transform
		item["location"] = [t.origin.x, t.origin.y, t.origin.z]
		item["scale"] = [t.basis.x.length(), t.basis.y.length(), t.basis.z.length()]
		var e := t.basis.get_euler()
		item["rotationDeg"] = [rad_to_deg(e.x), rad_to_deg(e.y), rad_to_deg(e.z)]
	elif node is Node2D:
		item["position"] = [node.position.x, node.position.y]
		item["rotationDeg"] = node.rotation_degrees
		item["scale"] = [node.scale.x, node.scale.y]
	if node is MeshInstance3D and node.mesh != null:
		item["mesh"] = node.mesh.resource_name if node.mesh.resource_name != "" else node.mesh.resource_path
	if node is Camera3D:
		item["fov"] = node.fov
		item["near"] = node.near
		item["far"] = node.far
	if node is Light3D:
		item["lightEnergy"] = node.light_energy
	if node.get_script() != null:
		item["script"] = node.get_script().resource_path
	items.append(item)
	for child in node.get_children():
		_walk(child, items, my_path)

func _emit(out: Dictionary) -> void:
	print("ESO_PERCEIVE_JSON_BEGIN")
	print(JSON.stringify(out))
	print("ESO_PERCEIVE_JSON_END")
	quit()
