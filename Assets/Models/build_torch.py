#!/usr/bin/env python3
"""Verdant Crown — asset batch 2a: torch (PROP, joined single mesh).

Low-poly asset conventions:
  * metres, ONE joined mesh, ONE material, Col vertex colours, flat-shaded,
    origin on the ground, facing Blender +Y (Godot -Z forward).
  * Budget: <= 120 tris.

Design notes — wall torch, ~0.50 m tall:
  * Dark iron bracket (bevelled back plate + arm + clamp band) gripping a
    wooden handle that leans 12 deg forward, orange/yellow flame on top.
  * Flame colour is a PAINTED vertical gradient (per-corner vertex colours:
    yellow low -> orange -> red-orange tip) — no shaders, no emission, the
    unshaded art pipeline just reads albedo = Col.
  * Bracket back plate sits at -Y (the wall side), flame leans to +Y, so
    the facing assert (y_max > |y_min|) holds honestly.
  * Origin on the ground (lowest point z = 0).

Re-run (from the Blender MCP):
    path = ".../Assets/Models/build_torch.py"
    ns = {"__file__": path, "__name__": "__main__"}
    exec(compile(open(path).read(), path, "exec"), ns)
    summary = ns["SUMMARY"]
"""

import bpy
import bmesh
import json
import math
import pathlib
import struct
from mathutils import Vector

# ---------------------------------------------------------------- config ---
MODEL = "torch"
BUDGET = 120
OBJECT_NAME = "WallTorch"
LEAN_DEG = 12.0          # handle/clamp/flame lean forward (+Y)
ROOT = pathlib.Path(__file__).resolve().parent
GLB_PATH = ROOT / "glb" / f"{MODEL}.glb"
PREV_PATH = ROOT / "previews" / f"{MODEL}_eevee.png"
BLEND_PATH = ROOT / "source" / f"{MODEL}.blend"

COL = "Col"
PARTS = []

# palette (linear RGBA)
IRON = (0.045, 0.045, 0.050, 1.0)      # dark bracket
IRON_TOP = (0.130, 0.130, 0.145, 1.0)  # upward facets catch light
IRON_FRONT = (0.075, 0.075, 0.085, 1.0)
WOOD = (0.30, 0.155, 0.055, 1.0)       # handle base
WOOD_HI = (0.46, 0.25, 0.095, 1.0)     # front-lit wood
WOOD_D = (0.16, 0.075, 0.028, 1.0)     # back / underside
# flame gradient stops (z, rgba): yellow low -> orange -> red-orange tip
FLAME_STOPS = [(0.33, (1.00, 0.72, 0.16, 1.0)),
               (0.39, (1.00, 0.88, 0.32, 1.0)),
               (0.44, (1.00, 0.45, 0.05, 1.0)),
               (0.495, (0.93, 0.12, 0.01, 1.0))]

MAT = None


# ------------------------------------------------------------------- kit ---
def wipe_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for coll in (bpy.data.meshes, bpy.data.materials,
                 bpy.data.cameras, bpy.data.lights, bpy.data.worlds):
        for block in list(coll):
            if block.users == 0:
                coll.remove(block)
    PARTS.clear()


def make_material(name):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (1, 1, 1, 1)
        bsdf.inputs["Roughness"].default_value = 0.9
        attr = mat.node_tree.nodes.new("ShaderNodeAttribute")
        attr.attribute_name = COL
        attr.location = (-380, 200)
        mat.node_tree.links.new(attr.outputs["Color"], bsdf.inputs["Base Color"])
    mat.diffuse_color = (1, 1, 1, 1)
    return mat


def finish(o, material):
    bpy.ops.object.select_all(action="DESELECT")
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    bpy.ops.object.shade_flat()
    o.data.materials.append(material)
    a = o.data.color_attributes.new(name=COL, type="FLOAT_COLOR", domain="CORNER")
    for d in a.data:
        d.color = (1, 1, 1, 1)
    o.data.color_attributes.active_color = a
    return o


def paint(o, color, test=None):
    a = o.data.color_attributes[COL]
    for p in o.data.polygons:
        if test is None or test(p.center.copy(), p.normal.copy()):
            for li in p.loop_indices:
                a.data[li].color = color


def gradient_paint(o, stops, test=None):
    """Vertical painted gradient via per-corner colours (no shaders)."""
    a = o.data.color_attributes[COL]
    for p in o.data.polygons:
        if test is not None and not test(p.center.copy(), p.normal.copy()):
            continue
        for li in p.loop_indices:
            z = o.data.vertices[o.data.loops[li].vertex_index].co.z
            if z <= stops[0][0]:
                col = stops[0][1]
            elif z >= stops[-1][0]:
                col = stops[-1][1]
            else:
                col = stops[-1][1]
                for i in range(len(stops) - 1):
                    z0, c0 = stops[i]
                    z1, c1 = stops[i + 1]
                    if z0 <= z <= z1:
                        t = (z - z0) / (z1 - z0)
                        col = tuple(c0[k] + (c1[k] - c0[k]) * t
                                    for k in range(4))
                        break
            a.data[li].color = col


def tri_count(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)


def box(name, size, loc, rot=(0, 0, 0), bevel=0.0):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.active_object
    o.name = name
    o.scale = size
    o.rotation_euler = rot
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    if bevel > 0:
        m = o.modifiers.new(name="Bevel", type="BEVEL")
        m.width = bevel
        m.segments = 1
        bpy.ops.object.modifier_apply(modifier="Bevel")
    return o


def taper_top(o, f=0.82):
    """Narrow the top of a part (wooden handle grows toward the flame)."""
    me = o.data
    zs = [v.co.z for v in me.vertices]
    z0, z1 = min(zs), max(zs)
    cx = sum(v.co.x for v in me.vertices) / len(me.vertices)
    cy = sum(v.co.y for v in me.vertices) / len(me.vertices)
    for v in me.vertices:
        t = (v.co.z - z0) / (z1 - z0)
        k = 1.0 + (f - 1.0) * t
        v.co.x = cx + (v.co.x - cx) * k
        v.co.y = cy + (v.co.y - cy) * k
    me.update()


def join_parts(name):
    bpy.ops.object.select_all(action="DESELECT")
    for p in PARTS:
        p.select_set(True)
    bpy.context.view_layer.objects.active = PARTS[0]
    bpy.ops.object.join()
    o = bpy.context.active_object
    o.name = name
    o.data.materials.clear()
    o.data.materials.append(MAT)
    for poly in o.data.polygons:
        poly.material_index = 0
    bpy.ops.object.shade_flat()
    PARTS.clear()
    PARTS.append(o)
    return o


def set_min_z(o, min_z=0.0):
    zs = [v.co.z for v in o.data.vertices]
    dz = min(zs) - min_z
    if abs(dz) > 1e-6:
        for v in o.data.vertices:
            v.co.z -= dz
        o.data.update()


# ------------------------------------------------------------------ build ---
def build():
    global MAT
    wipe_scene()
    MAT = make_material("mat_torch")
    lean = math.radians(LEAN_DEG)
    axis = (0.0, math.sin(lean), math.cos(lean))   # handle +Z axis after lean

    # --- iron back plate (against the wall, -Y) ----------------------------
    o = box("Plate", (0.09, 0.03, 0.19), (0, -0.075, 0.095), bevel=0.006)
    finish(o, MAT)
    paint(o, IRON)
    paint(o, IRON_FRONT, lambda c, n: n.y > 0.95)
    paint(o, IRON_TOP, lambda c, n: n.z > 0.95)
    PARTS.append(o)

    # --- arm from the plate out to the handle ------------------------------
    o = box("Arm", (0.05, 0.11, 0.04), (0, -0.025, 0.115))
    finish(o, MAT)
    paint(o, IRON)
    paint(o, IRON_TOP, lambda c, n: n.z > 0.95)
    PARTS.append(o)

    # --- wooden handle, leaning forward, tapered toward the flame ----------
    o = box("Handle", (0.055, 0.055, 0.28), (0, 0.03, 0.23),
            rot=(-lean, 0, 0))
    taper_top(o, 0.82)
    finish(o, MAT)
    paint(o, WOOD)
    paint(o, WOOD_HI, lambda c, n: n.y > 0.9)
    paint(o, WOOD_D, lambda c, n: n.y < -0.9 or n.z < -0.9)
    PARTS.append(o)

    # --- iron clamp band: grips the handle to the arm ----------------------
    o = box("Clamp", (0.08, 0.08, 0.05), (0, 0.0119, 0.145), rot=(-lean, 0, 0))
    finish(o, MAT)
    paint(o, IRON)
    paint(o, IRON_TOP, lambda c, n: n.z > 0.95)
    paint(o, IRON_FRONT, lambda c, n: n.y > 0.95)
    PARTS.append(o)

    # --- flame: cone with painted vertical gradient ------------------------
    flame_loc = (0.0, 0.068, 0.412)   # base buried in the handle top
    bpy.ops.mesh.primitive_cone_add(vertices=6, radius1=0.075, radius2=0.006,
                                    depth=0.16, location=flame_loc,
                                    rotation=(-lean, 0, 0))
    o = bpy.context.active_object
    o.name = "Flame"
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    finish(o, MAT)
    gradient_paint(o, FLAME_STOPS)
    PARTS.append(o)

    # --- join to ONE mesh --------------------------------------------------
    o = join_parts(OBJECT_NAME)
    set_min_z(o, 0.0)

    report()
    return o


def report():
    o = PARTS[0]
    n = tri_count(o)
    xs = [v.co.x for v in o.data.vertices]
    ys = [v.co.y for v in o.data.vertices]
    zs = [v.co.z for v in o.data.vertices]
    y_max, y_min = max(ys), min(ys)
    facing_ok = y_max > abs(y_min)
    print(f"--- {MODEL} ---")
    print(f"  {o.name:10s} tris={n:4d} / budget {BUDGET} -> "
          f"{'OK' if n <= BUDGET else 'OVER BUDGET'}")
    print(f"  bounds x[{min(xs):.2f},{max(xs):.2f}] "
          f"y(front/flame back/plate)[{y_min:.2f},{y_max:.2f}] "
          f"z(height)[{min(zs):.2f},{max(zs):.2f}]")
    print(f"  facing: y_max={y_max:.3f} vs |y_min|={abs(y_min):.3f} -> "
          f"{'OK' if facing_ok else 'FAIL'} (glTF assert -z_min > z_max)")
    print(f"  height {max(zs) - min(zs):.3f} m (target ~0.50)")
    return {"tris": n, "budget": BUDGET, "y_max": y_max, "y_min": y_min,
            "facing_ok": facing_ok, "height": max(zs) - min(zs),
            "z_min": min(zs)}


# ------------------------------------------------------------------ stage ---
def aim(o, target, up="Y"):
    d = Vector(target) - o.location
    o.rotation_euler = d.to_track_quat("-Z", up).to_euler()


def stage(cam_loc=(0.9, 1.3, 0.75), target=(0, 0, 0.28),
          key=90, fill=30, rim=60, ground=6):
    bpy.ops.mesh.primitive_plane_add(size=ground, location=(0, 0, 0))
    g = bpy.context.active_object
    g.name = "_Ground"
    gmat = bpy.data.materials.get("_stage_grey") or \
        bpy.data.materials.new("_stage_grey")
    gmat.use_nodes = True
    gmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = \
        (0.30, 0.30, 0.32, 1)
    g.data.materials.append(gmat)

    world = bpy.context.scene.world or bpy.data.worlds.new("World")
    bpy.context.scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    bg.inputs[0].default_value = (0.03, 0.03, 0.04, 1)
    bg.inputs[1].default_value = 1.0

    bpy.ops.object.camera_add(location=cam_loc)
    cam = bpy.context.active_object
    cam.name = "_Cam"
    aim(cam, target)
    bpy.context.scene.camera = cam

    lights = [((-1.6, 2.2, 2.4), key, 2.5),
              ((2.0, 1.0, 1.3), fill, 2.0),
              ((0.3, -2.4, 1.9), rim, 2.0)]
    for i, (loc, energy, size) in enumerate(lights):
        bpy.ops.object.light_add(type="AREA", location=loc)
        l = bpy.context.active_object
        l.name = f"_Light{i}"
        l.data.energy = energy
        l.data.size = size
        aim(l, target)


def render_eevee(path, w=960, h=720):
    s = bpy.context.scene
    try:
        s.render.engine = "BLENDER_EEVEE"
    except TypeError:
        s.render.engine = "BLENDER_EEVEE_NEXT"
    s.render.resolution_x = w
    s.render.resolution_y = h
    s.render.film_transparent = False
    s.view_settings.view_transform = "Standard"
    s.render.image_settings.file_format = "PNG"
    s.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)
    print(f"  wrote {path}")


# ----------------------------------------------------------------- export ---
def export(root):
    bpy.ops.object.select_all(action="DESELECT")
    root.select_set(True)
    for desc in root.children_recursive:
        desc.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.gltf(
        filepath=str(GLB_PATH),
        export_format="GLB",
        use_selection=True,
        export_apply=False,
        export_materials="EXPORT",
        export_yup=True,
        export_vertex_color="ACTIVE",
        export_normals=True,
    )
    print(f"  wrote {GLB_PATH}")


def verify_glb():
    data = pathlib.Path(GLB_PATH).read_bytes()
    js = json.loads(data[20:20 + struct.unpack("<I", data[12:16])[0]])
    nodes = js.get("nodes", [])
    meshes = js.get("meshes", [])
    tris, color0, zmins, zmaxs = 0, False, [], []
    for m in meshes:
        for p in m["primitives"]:
            attrs = p.get("attributes", {})
            if "COLOR_0" in attrs:
                color0 = True
            pos = js["accessors"][attrs["POSITION"]]
            zmins.append(pos["min"][2])
            zmaxs.append(pos["max"][2])
            if "indices" in p:
                tris += js["accessors"][p["indices"]]["count"] // 3
            else:
                tris += pos["count"] // 3
    zmin, zmax = min(zmins), max(zmaxs)
    facing_ok = -zmin > zmax
    ok = tris <= BUDGET and color0 and len(meshes) >= 1 and len(nodes) >= 1
    print(f"  GLB verify: nodes={len(nodes)} meshes={len(meshes)} "
          f"COLOR_0={color0} tris={tris}/{BUDGET} -> {'OK' if ok else 'FAIL'}")
    print(f"  GLB facing: zmin={zmin:.3f} zmax={zmax:.3f} -> "
          f"-zmin={-zmin:.3f} > {zmax:.3f}: {'OK' if facing_ok else 'FAIL'}")
    return {"nodes": len(nodes), "meshes": len(meshes), "color0": color0,
            "tris": tris, "facing_ok": facing_ok, "ok": ok}


# -------------------------------------------------------------------- main ---
if __name__ == "__main__":
    for d in (ROOT / "glb", ROOT / "previews", ROOT / "source"):
        d.mkdir(parents=True, exist_ok=True)
    root = build()
    stage()
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND_PATH))
    export(root)
    v = verify_glb()
    render_eevee(PREV_PATH, w=960, h=720)
    r = report()
    SUMMARY = {"model": MODEL, "verify": v, "report": r,
               "glb": str(GLB_PATH), "preview": str(PREV_PATH)}
    print("DONE", MODEL)
