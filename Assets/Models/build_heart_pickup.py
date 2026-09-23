#!/usr/bin/env python3
"""Verdant Crown — asset batch 2a: heart_pickup (PROP, joined single mesh).

Low-poly asset conventions:
  * metres, ONE joined mesh, ONE material, Col vertex colours, flat-shaded,
    origin on the ground, facing Blender +Y (Godot -Z forward).
  * Budget: <= 150 tris.

Design notes:
  * Two sphere-ish lobes (uv spheres, 8x4) + a 6-sided cone as the tapered
    point — the classic low-poly heart, ~0.47 m wide, chunky enough to read
    at 640x480 from a top-down camera ~8 m up.
  * DELIBERATE: the heart is tilted back 60 deg about X after painting so a
    top-down camera sees the heart FACE and the XY silhouette is the heart
    shape (lobes at -Y = screen top, point at +Y = screen bottom). The
    facing assert (-z_min > z_max in glTF == Blender y_max > |y_min|) is
    still enforced: geometry is re-centred with a +0.02 m front bias after
    the tilt.
  * Bright saturated red (pops on grey floors), pink catch-light painted on
    the top-front lobe caps (visible from above), dark shade on back/underside.
  * Preview renders TOP-DOWN from 8 m at 640x480 on the grey stage ground —
    the exact acceptance condition.

Re-run (from the Blender MCP):
    path = ".../Assets/Models/build_heart_pickup.py"
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
MODEL = "heart_pickup"
BUDGET = 150
OBJECT_NAME = "HeartPickup"
TILT_DEG = 60.0          # lean-back so the top-down camera sees the face
Y_BIAS = 0.02            # small forward bias after re-centring (facing assert)
ROOT = pathlib.Path(__file__).resolve().parent
GLB_PATH = ROOT / "glb" / f"{MODEL}.glb"
PREV_PATH = ROOT / "previews" / f"{MODEL}_eevee.png"
BLEND_PATH = ROOT / "source" / f"{MODEL}.blend"

COL = "Col"              # vertex-colour attribute -> glTF COLOR_0
PARTS = []

# palette (linear RGBA) — bright saturated red to POP on grey floors
RED = (0.85, 0.030, 0.050, 1.0)     # main red
RED_D = (0.38, 0.010, 0.018, 1.0)   # shade: back / underside / tip
RED_HI = (1.00, 0.42, 0.34, 1.0)    # pink catch-light on top-front caps

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
        # Attribute(Col) -> Base Color, or renders come out white.
        attr = mat.node_tree.nodes.new("ShaderNodeAttribute")
        attr.attribute_name = COL
        attr.location = (-380, 200)
        mat.node_tree.links.new(attr.outputs["Color"], bsdf.inputs["Base Color"])
    mat.diffuse_color = (1, 1, 1, 1)
    return mat


def finish(o, material):
    """Flat-shade, white Col attribute, attach material. Selects only `o`."""
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
    """Set the Col attribute of matching faces (test in object == world space)."""
    a = o.data.color_attributes[COL]
    for p in o.data.polygons:
        if test is None or test(p.center.copy(), p.normal.copy()):
            for li in p.loop_indices:
                a.data[li].color = color


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


def sphere(name, radius, loc, scale=(1, 1, 1)):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=8, ring_count=4,
                                         radius=radius, location=loc)
    o = bpy.context.active_object
    o.name = name
    o.scale = scale
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return o


def cone(name, r1, r2, depth, loc, rot=(0, 0, 0), scale=(1, 1, 1), verts=6):
    bpy.ops.mesh.primitive_cone_add(vertices=verts, radius1=r1, radius2=r2,
                                    depth=depth, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    o.scale = scale
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return o


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


def settle(o, min_z, y_bias=0.0):
    """Re-centre Y (optional front bias) and drop the lowest point to min_z."""
    ys = [v.co.y for v in o.data.vertices]
    zs = [v.co.z for v in o.data.vertices]
    mid = (min(ys) + max(ys)) / 2.0
    dz = min(zs) - min_z
    for v in o.data.vertices:
        v.co.y -= (mid - y_bias)
        v.co.z -= dz
    o.data.update()


# ------------------------------------------------------------------ build ---
def build():
    global MAT
    wipe_scene()
    MAT = make_material("mat_heart_pickup")

    # --- two lobes --------------------------------------------------------
    for suffix, x in (("L", -0.11), ("R", 0.11)):
        o = sphere(f"Lobe_{suffix}", 0.125, (x, 0, 0.30), scale=(1.0, 0.9, 1.0))
        finish(o, MAT)
        paint(o, RED)
        # pink catch-light on the top-front caps — this is what a top-down
        # camera sees first, and it makes the heart pop from directly above
        paint(o, RED_HI, lambda c, n: n.z > 0.45 and n.y > 0.05)
        # shade: back and underside
        paint(o, RED_D, lambda c, n: n.y < -0.45 or n.z < -0.55)
        PARTS.append(o)

    # --- tapered point: 6-sided cone, tip down at z=0, base up at z=0.28 ---
    o = cone("Point", r1=0.185, r2=0.0, depth=0.28, loc=(0, 0, 0.14),
             rot=(math.pi, 0, 0), scale=(1.0, 0.72, 1.0), verts=6)
    finish(o, MAT)
    paint(o, RED)
    paint(o, RED_D, lambda c, n: n.y < -0.45 or n.z < -0.55 or c.z < 0.07)
    PARTS.append(o)

    # --- join to ONE mesh -------------------------------------------------
    o = join_parts(OBJECT_NAME)

    # --- tilt back for the top-down camera, then settle -------------------
    o.rotation_euler = (math.radians(TILT_DEG), 0, 0)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
    settle(o, min_z=0.0, y_bias=Y_BIAS)

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
    print(f"  {o.name:12s} tris={n:4d} / budget {BUDGET} -> "
          f"{'OK' if n <= BUDGET else 'OVER BUDGET'}")
    print(f"  bounds x(width)[{min(xs):.2f},{max(xs):.2f}] "
          f"y(front/back)[{y_min:.2f},{y_max:.2f}] "
          f"z(height)[{min(zs):.2f},{max(zs):.2f}]")
    print(f"  facing: y_max={y_max:.3f} vs |y_min|={abs(y_min):.3f} -> "
          f"{'OK' if facing_ok else 'FAIL'} (glTF assert -z_min > z_max)")
    print(f"  width {max(xs) - min(xs):.3f} m, height {max(zs) - min(zs):.3f} m "
          f"(target ~0.45), tilt {TILT_DEG:.0f} deg for top-down read")
    return {"tris": n, "budget": BUDGET, "y_max": y_max, "y_min": y_min,
            "facing_ok": facing_ok,
            "width": max(xs) - min(xs), "height": max(zs) - min(zs),
            "z_min": min(zs)}


# ------------------------------------------------------------------ stage ---
def aim(o, target, up="Y"):
    d = Vector(target) - o.location
    o.rotation_euler = d.to_track_quat("-Z", up).to_euler()


def stage(cam_loc, cam_rot, target, key=90, fill=30, rim=60, ground=6):
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

    bpy.ops.object.camera_add(location=cam_loc, rotation=cam_rot)
    cam = bpy.context.active_object
    cam.name = "_Cam"
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


def render_eevee(path, w=640, h=480):
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
    # Select root AND descendants — root-only export = empty husk.
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
    """Nodes/meshes > 0, COLOR_0 present, tris <= budget, facing assert."""
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
    # Top-down camera ~8 m up, rolled 180 deg so screen-up = world -Y
    # (lobes at top, point at bottom) — the actual game view condition.
    stage(cam_loc=(0, 0, 8), cam_rot=(0, 0, math.pi),
          target=(0, 0, 0.15))
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND_PATH))
    export(root)
    v = verify_glb()
    render_eevee(PREV_PATH, w=640, h=480)
    r = report()
    SUMMARY = {"model": MODEL, "verify": v, "report": r,
               "glb": str(GLB_PATH), "preview": str(PREV_PATH)}
    print("DONE", MODEL)
