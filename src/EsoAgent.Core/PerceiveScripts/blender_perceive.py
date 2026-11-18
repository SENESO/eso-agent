# EsoAgent Blender perception script (item 36).
# Run: blender --background <file.blend> --python blender_perceive.py -- --outdir <dir> [--id-pass]
# Prints the scene as structured JSON between ESO_PERCEIVE_JSON_BEGIN / END
# sentinels on stdout. NEVER saves the .blend — all mutations are in-memory.
import bpy
import json
import os
import sys
from mathutils import Vector


def parse_args():
    argv = sys.argv
    args = argv[argv.index("--") + 1:] if "--" in argv else []
    outdir = "."
    id_pass = False
    i = 0
    while i < len(args):
        if args[i] == "--outdir" and i + 1 < len(args):
            outdir = args[i + 1]
            i += 2
        elif args[i] == "--id-pass":
            id_pass = True
            i += 1
        else:
            i += 1
    return outdir, id_pass


def id_color(index):
    # index 0 is the black background; objects start at 1. The exact inverse
    # mapping lives in the agent (BmpIdPass) — colors must match byte-for-byte.
    n = index + 1
    return ((n & 0xFF) / 255.0, ((n >> 8) & 0xFF) / 255.0, ((n >> 16) & 0xFF) / 255.0)


def vec3(v):
    return [round(float(v[0]), 5), round(float(v[1]), 5), round(float(v[2]), 5)]


def screen_bbox(scene, cam_obj, obj):
    # Exact native projection of the object's bound-box corners.
    from bpy_extras.object_utils import world_to_camera_view
    xs, ys, behind = [], [], True
    for corner in obj.bound_box:
        co = world_to_camera_view(scene, cam_obj, obj.matrix_world @ Vector(corner))
        xs.append(co.x)
        ys.append(co.y)
        if co.z > 0:
            behind = False
    if behind or not xs:
        return None
    x0, x1 = min(xs), max(xs)
    y0, y1 = min(ys), max(ys)
    # world_to_camera_view: origin bottom-left. Emit top-left origin fractions.
    return {
        "x": round(x0, 5), "y": round(1.0 - y1, 5),
        "w": round(x1 - x0, 5), "h": round(y1 - y0, 5),
        "partiallyOutside": bool(x0 < 0 or y0 < 0 or x1 > 1 or y1 > 1),
    }


def dump_scene(scene):
    dg = bpy.context.evaluated_depsgraph_get()
    cam_obj = scene.camera
    items = []
    for obj in scene.objects:
        loc, rot, scale = obj.matrix_world.decompose()
        item = {
            "name": obj.name,
            "type": obj.type,
            "parent": obj.parent.name if obj.parent else None,
            "location": vec3(loc),
            "rotationEulerDeg": [round(float(a), 3) for a in
                                 (rot.to_euler().x, rot.to_euler().y, rot.to_euler().z)],
            "scale": vec3(scale),
            "dimensions": vec3(obj.dimensions),
            "visible": not obj.hide_render,
        }
        if obj.type == "MESH":
            try:
                ev = obj.evaluated_get(dg)
                item["mesh"] = {
                    "vertices": len(ev.data.vertices),
                    "edges": len(ev.data.edges),
                    "polygons": len(ev.data.polygons),
                }
            except Exception:
                item["mesh"] = {
                    "vertices": len(obj.data.vertices),
                    "edges": len(obj.data.edges),
                    "polygons": len(obj.data.polygons),
                }
            item["materials"] = [slot.material.name for slot in obj.material_slots if slot.material]
            item["modifiers"] = [{"name": m.name, "type": m.type} for m in obj.modifiers]
            if cam_obj is not None:
                sb = screen_bbox(scene, cam_obj, obj)
                if sb:
                    item["screenBbox"] = sb
        elif obj.type == "CAMERA":
            item["camera"] = {
                "lensMm": obj.data.lens,
                "sensorWidthMm": obj.data.sensor_width,
                "clipStart": obj.data.clip_start,
                "clipEnd": obj.data.clip_end,
            }
        elif obj.type == "LIGHT":
            item["light"] = {"kind": obj.data.type, "energy": obj.data.energy}
        items.append(item)
    camera = None
    if cam_obj is not None:
        loc, rot, _ = cam_obj.matrix_world.decompose()
        camera = {
            "name": cam_obj.name,
            "location": vec3(loc),
            "rotationEulerDeg": [round(float(a), 3) for a in
                                 (rot.to_euler().x, rot.to_euler().y, rot.to_euler().z)],
            "lensMm": cam_obj.data.lens,
            "sensorWidthMm": cam_obj.data.sensor_width,
            "resolution": [scene.render.resolution_x, scene.render.resolution_y],
        }
    return items, camera


def do_id_pass(scene, outdir):
    renderable = [o for o in scene.objects
                  if o.type in ("MESH", "CURVE", "SURFACE", "META", "FONT", "GPENCIL")
                  and not o.hide_render]
    legend = []
    saved = []
    for i, obj in enumerate(renderable):
        r, g, b = id_color(i)
        mat = bpy.data.materials.get(f"ESO_ID_{i}")
        if mat is None:
            mat = bpy.data.materials.new(name=f"ESO_ID_{i}")
        mat.diffuse_color = (r, g, b, 1.0)
        saved.append((obj, list(obj.data.materials) if hasattr(obj.data, "materials") else None))
        if hasattr(obj.data, "materials"):
            obj.data.materials.clear()
            obj.data.materials.append(mat)
        legend.append({
            "name": obj.name, "index": i,
            "color": [round(r * 255), round(g * 255), round(b * 255)],
        })
    # Flat world: pure black background so unassigned pixels decode as index -1.
    old_world = scene.world
    world = bpy.data.worlds.get("ESO_ID_WORLD") or bpy.data.worlds.new("ESO_ID_WORLD")
    world.color = (0.0, 0.0, 0.0)
    scene.world = world
    old_engine = scene.render.engine
    scene.render.engine = "BLENDER_WORKBENCH"
    shading = scene.display.shading
    old_light, old_color_type = shading.light, shading.color_type
    shading.light = "FLAT"
    shading.color_type = "MATERIAL"
    try:
        shading.show_object_outline = False
    except Exception:
        pass
    scene.render.image_settings.file_format = "BMP"
    scene.render.filepath = os.path.join(outdir, "idpass.bmp")
    bpy.ops.render.render(write_still=True)
    # Depth + normal passes as 16-bit PNGs (visualization-grade; the exact
    # numbers live in the JSON dump — camera-space distances per object).
    vl = bpy.context.view_layer
    vl.use_pass_z = True
    vl.use_pass_normal = True
    scene.use_nodes = True
    tree = scene.node_tree
    tree.nodes.clear()
    rl = tree.nodes.new("CompositorNodeRLayers")
    for socket, fname in (("Depth", "depth"), ("Normal", "normal")):
        out = tree.nodes.new("CompositorNodeOutputFile")
        out.base_path = outdir
        out.format.file_format = "PNG"
        out.format.color_depth = "16"
        out.file_slots[0].path = fname
        tree.links.new(rl.outputs[socket], out.inputs[0])
    scene.render.filepath = os.path.join(outdir, "beauty.png")
    scene.render.image_settings.file_format = "PNG"
    bpy.ops.render.render(write_still=True)
    scene.use_nodes = False
    # Restore (in-memory only; the file on disk is never written).
    scene.render.engine = old_engine
    shading.light, shading.color_type = old_light, old_color_type
    scene.world = old_world
    for obj, mats in saved:
        if mats is not None and hasattr(obj.data, "materials"):
            obj.data.materials.clear()
            for m in mats:
                obj.data.materials.append(m)
    with open(os.path.join(outdir, "legend.json"), "w") as fh:
        json.dump({"items": legend}, fh)
    return [
        {"kind": "idpass-bmp", "path": os.path.join(outdir, "idpass.bmp")},
        {"kind": "legend", "path": os.path.join(outdir, "legend.json")},
        {"kind": "depth-png", "path": os.path.join(outdir, "depth0001.png")},
        {"kind": "normal-png", "path": os.path.join(outdir, "normal0001.png")},
    ]


def main():
    outdir, id_pass = parse_args()
    os.makedirs(outdir, exist_ok=True)
    scene = bpy.context.scene
    items, camera = dump_scene(scene)
    artifacts = []
    if id_pass:
        artifacts = do_id_pass(scene, outdir)
    payload = {
        "app": "blender",
        "kind": "perceive",
        "file": bpy.data.filepath,
        "camera": camera,
        "items": items,
        "artifacts": artifacts,
    }
    print("ESO_PERCEIVE_JSON_BEGIN")
    print(json.dumps(payload))
    print("ESO_PERCEIVE_JSON_END")


main()
