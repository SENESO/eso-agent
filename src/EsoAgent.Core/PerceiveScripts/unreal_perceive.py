# EsoAgent Unreal Engine perception script (item 36).
# Run via UnrealEditor-Cmd <project.uproject> -run=pythonscript -script=unreal_perceive.py
# (or the editor's -ExecutePythonScript). Configuration arrives via environment:
#   ESO_PERCEIVE_OUTDIR  where perceive.json is also written
#   ESO_PERCEIVE_MODE    "level" (default) | "assets"
#   ESO_PERCEIVE_PATH    asset mode: content path filter, e.g. /Game/Weapons
# Prints the level as structured JSON between ESO_PERCEIVE_JSON_BEGIN / END.
# Read-only: it never saves assets or the level.
import json
import os

import unreal


def v3(v):
    return [round(float(v.x), 3), round(float(v.y), 3), round(float(v.z), 3)]


def rot3(r):
    # Unreal rotator: pitch (Y), yaw (Z), roll (X), degrees.
    return {"pitch": round(float(r.pitch), 3), "yaw": round(float(r.yaw), 3), "roll": round(float(r.roll), 3)}


def perceive_level():
    subsystem = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)
    actors = subsystem.get_all_level_actors()
    items = []
    camera = None
    for actor in actors:
        try:
            cls = actor.get_class().get_name()
        except Exception:
            continue
        loc = actor.get_actor_location()
        rot = actor.get_actor_rotation()
        scale = actor.get_actor_scale3d()
        item = {
            "name": actor.get_actor_label() or actor.get_name(),
            "class": cls,
            "location": v3(loc),
            "rotation": rot3(rot),
            "scale": v3(scale),
        }
        try:
            origin, extent = actor.get_actor_bounds(False)
            item["boundsOrigin"] = v3(origin)
            item["boundsExtent"] = v3(extent)
        except Exception:
            pass
        try:
            comps = actor.get_components_by_class(unreal.ActorComponent)
            item["components"] = [
                {"name": c.get_name(), "class": c.get_class().get_name()} for c in comps[:40]
            ]
        except Exception:
            item["components"] = []
        try:
            item["tags"] = [str(t) for t in actor.tags]
        except Exception:
            pass
        if cls == "CameraActor" and camera is None:
            cam_comp = actor.get_component_by_class(unreal.CameraComponent)
            camera = {
                "name": item["name"],
                "location": v3(loc),
                "rotation": rot3(rot),
                "fov": float(cam_comp.field_of_view) if cam_comp else 90.0,
                "aspect": float(cam_comp.aspect_ratio) if cam_comp else 1.7777,
            }
        items.append(item)
    if camera is None:
        # Fall back to the level viewport camera when the editor provides it.
        try:
            loc, rot = unreal.EditorLevelLibrary.get_level_viewport_camera_info()
            camera = {"name": "(viewport)", "location": v3(loc), "rotation": rot3(rot),
                      "fov": 90.0, "aspect": 1.7777}
        except Exception:
            camera = None
    return {"app": "unreal", "kind": "perceive", "camera": camera, "items": items, "artifacts": []}


def perceive_assets(path):
    registry = unreal.AssetRegistryHelpers.get_asset_registry()
    assets = registry.get_assets_by_path(path, recursive=True)
    items = []
    for a in assets:
        items.append({
            "name": str(a.asset_name),
            "class": str(a.asset_class_path.asset_name),
            "package": str(a.package_name),
            "path": str(a.package_path),
        })
    return {"app": "unreal", "kind": "perceive-assets", "queryPath": path, "items": items, "artifacts": []}


def main():
    mode = os.environ.get("ESO_PERCEIVE_MODE", "level")
    outdir = os.environ.get("ESO_PERCEIVE_OUTDIR", "")
    if mode == "assets":
        payload = perceive_assets(os.environ.get("ESO_PERCEIVE_PATH", "/Game"))
    else:
        payload = perceive_level()
    text = json.dumps(payload)
    if outdir:
        try:
            os.makedirs(outdir, exist_ok=True)
            with open(os.path.join(outdir, "perceive.json"), "w") as fh:
                fh.write(text)
        except Exception:
            pass
    print("ESO_PERCEIVE_JSON_BEGIN")
    print(text)
    print("ESO_PERCEIVE_JSON_END")


main()
