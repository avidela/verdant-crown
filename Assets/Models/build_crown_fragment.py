#!/usr/bin/env python3
"""Verdant Crown — asset batch 2a: crown_fragment (PROP, joined single mesh).

Low-poly asset conventions:
  * metres, ONE joined mesh, ONE material, Col vertex colours, flat-shaded,
    origin on the ground, facing Blender +Y (Godot -Z forward).
  * Budget: <= 150 tris.

Design notes:
  * A floating crown shard ~0.5 m: a jagged gold wedge (custom bmesh —
    zig-zag profile extruded in Y, front face triangle-fanned so ONE facet
    can be painted as a catch-light) rising out of an emerald-green base
    setting (bevelled box) with a protruding emerald gem on the front.
  * Gold runs a painted vertical gradient (dark at the base -> bright at
    the tips) via per-corner vertex colours — no shaders, unshaded pipeline.
  * Tilted back 30 deg after painting so a top-down camera catches the face,
    then re-centred with a +0.02 m front bias so the facing assert holds.
  * Geometry floats: lowest point at z = 0.10, origin at (0,0,0) on the
    ground — it hovers when dropped onto terrain.

Re-run (from the Blender MCP):
    path = ".../Assets/Models/build_crown_fragment.py"
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
MODEL = "crown_fragment"
BUDGET = 150
OBJECT_NAME = "CrownFragment"
TILT_DEG = 30.0
Y_BIAS = 0.02
HOVER_Z = 0.10           # lowest point floats above the ground
ROOT = pathlib.Path(__file__).resolve().parent
GLB_PATH = ROOT / "glb" / f"{MODEL}.glb"
PREV_PATH = ROOT / "previews" / f"{MODEL}_eevee.png"
BLEND_PATH = ROOT / "source" / f"{MODEL}.blend"

COL = "Col"
PARTS = []

# palette (linear RGBA)
GOLD_LO = (0.36, 0.20, 0.025, 1.0)   # gold at the wedge base
GOLD_MID = (0.62, 0.38, 0.05, 1.0)
GOLD_HI = (0.97, 0.72, 0.16, 1.0)    # gold at the jagged tips
CATCH = (1.00, 0.92, 0.55, 1.0)      # painted facet catch-light
EM = (0.030, 0.45, 0.14, 1.0)        # emerald setting
EM_HI = (0.10, 0.72, 0.26, 1.0)      # setting top rim
EM_D = (0.012, 0.24, 0.075, 1.0)     # setting shade
GEM_HI = (0.18, 0.88, 0.34, 1.0)     # gem front face

# jagged profile in XZ: 3 peaks, flat bottom (crown-band shard)
PROFILE = [(-0.18, 0.20), (-0.11, 0.50), (-0.03, 0.33), (0.05, 0.60),
           (0.12, 0.36), (0.19, 0.48), (0.21, 0.20)]
Y_FRONT, Y_BACK = 0.05, -0.05

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
    """Paint a vertical gradient: stops = [(z, rgba), ...] ascending.

    Per-corner colours — interpolates across big flat faces, so the wedge's
    single front fan still reads as a smooth dark->bright gold ramp.
    """
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


def make_wedge():
    """Jagged crown-band shard: zig-zag profile extruded in Y.

    Front and back faces are fans from an interior centroid so the front is
    individual triangles — one of them (the tall peak's left slope) is the
    painted catch-light facet.
    """
    bm = bmesh.new()
    front = [bm.verts.new((x, Y_FRONT, z)) for x, z in PROFILE]
    back = [bm.verts.new((x, Y_BACK, z)) for x, z in PROFILE]
    cx = sum(p[0] for p in PROFILE) / len(PROFILE)
    cz = sum(p[1] for p in PROFILE) / len(PROFILE)
    cf = bm.verts.new((cx, Y_FRONT, cz))
    cb = bm.verts.new((cx, Y_BACK, cz))
    n = len(PROFILE)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((cf, front[j], front[i]))
        bm.faces.new((cb, back[i], back[j]))
        bm.faces.new((front[i], front[j], back[j], back[i]))
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    me = bpy.data.meshes.new("WedgeMesh")
    bm.to_mesh(me)
    bm.free()
    o = bpy.data.objects.new("Wedge", me)
    bpy.context.collection.objects.link(o)
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
    MAT = make_material("mat_crown_fragment")

    # --- jagged gold wedge -------------------------------------------------
    o = make_wedge()
    finish(o, MAT)
    gradient_paint(o, [(0.20, GOLD_LO), (0.36, GOLD_MID), (0.60, GOLD_HI)])
    # painted catch-light: the front fan triangle on the tall peak's left
    # slope (coplanar tris, so no seam — only the colour breaks the facet)
    paint(o, CATCH,
          lambda c, n: n.y > 0.9 and c.x < 0.035 and c.z > 0.42)
    PARTS.append(o)

    # --- emerald base setting ---------------------------------------------
    o = box("Setting", (0.40, 0.13, 0.14), (0.01, 0, 0.17), bevel=0.015)
    finish(o, MAT)
    paint(o, EM)
    paint(o, EM_HI, lambda c, n: n.z > 0.95)          # top rim glint
    paint(o, EM_D, lambda c, n: n.y < -0.95 or n.z < -0.95)  # back/underside
    PARTS.append(o)

    # --- protruding emerald gem (also gives honest front dominance) --------
    o = box("Gem", (0.11, 0.05, 0.08), (0.01, 0.07, 0.165))
    finish(o, MAT)
    paint(o, EM_D)
    paint(o, GEM_HI, lambda c, n: n.y > 0.95)
    PARTS.append(o)

    # --- join to ONE mesh --------------------------------------------------
    o = join_parts(OBJECT_NAME)

    # --- tilt for top-down readability, settle to a floating hover ---------
    o.rotation_euler = (math.radians(TILT_DEG), 0, 0)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
    settle(o, min_z=HOVER_Z, y_bias=Y_BIAS)

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
    print(f"  {o.name:16s} tris={n:4d} / budget {BUDGET} -> "
          f"{'OK' if n <= BUDGET else 'OVER BUDGET'}")
    print(f"  bounds x[{min(xs):.2f},{max(xs):.2f}] "
          f"y(front/back)[{y_min:.2f},{y_max:.2f}] "
          f"z(height)[{min(zs):.2f},{max(zs):.2f}]")
    print(f"  facing: y_max={y_max:.3f} vs |y_min|={abs(y_min):.3f} -> "
          f"{'OK' if facing_ok else 'FAIL'} (glTF assert -z_min > z_max)")
    print(f"  height {max(zs) - min(zs):.3f} m (target ~0.50), "
          f"hover gap {min(zs):.3f} m")
    return {"tris": n, "budget": BUDGET, "y_max": y_max, "y_min": y_min,
            "facing_ok": facing_ok, "height": max(zs) - min(zs),
            "z_min": min(zs)}


# ------------------------------------------------------------------ stage ---
def aim(o, target, up="Y"):
    d = Vector(target) - o.location
    o.rotation_euler = d.to_track_quat("-Z", up).to_euler()


def stage(cam_loc=(0.95, 1.35, 0.85), target=(0, 0, 0.35),
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
