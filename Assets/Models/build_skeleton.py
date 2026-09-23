#!/usr/bin/env python3
"""Verdant Crown — asset batch 1b: the skeleton (PROP, joined single mesh).

Low-poly asset conventions:
  * metres, FLAT shading, faces Blender +Y (Godot -Z forward), origin on ground
  * PROP: everything JOINED into one mesh — skeleton is a whole-instance
    animated contact attacker (lunge/bob), no articulation.
  * ONE material (mat_skeleton); all tones ride the "Col" vertex-colour
    attribute. Dark eye sockets + ribcage bands are PAINTED (subdivide +
    face paint), not glued boxes.
  * Budget: <= 800 tris.

Carried over from build_hero.py (debugged 2026-09-22):
  (a) export() selects the root AND root.children_recursive (root-only export
      produced a 172-byte husk),
  (b) make_material() wires ShaderNodeAttribute(COL) -> Base Color so renders
      show the painted vertex colours.

Re-run (from the Blender MCP):
    path = ".../Assets/Models/build_skeleton.py"
    ns = {"__file__": path, "__name__": "__main__"}
    exec(compile(open(path).read(), path, "exec"), ns)
"""

import bpy
import bmesh
import json
import pathlib
import struct
from mathutils import Vector

# ---------------------------------------------------------------- config ---
MODEL = "skeleton"
BUDGET = 800
ROOT = pathlib.Path(__file__).resolve().parent
GLB_PATH = ROOT / "glb" / f"{MODEL}.glb"
PREV_PATH = ROOT / "previews" / f"{MODEL}_eevee.png"
BLEND_PATH = ROOT / "source" / f"{MODEL}.blend"

COL = "Col"          # vertex-colour attribute -> glTF COLOR_0
PARTS = []

# palette (linear RGBA)
BONE = (0.85, 0.82, 0.72, 1.0)    # bone white
BAND = (0.13, 0.12, 0.10, 1.0)    # rib gaps / sockets / nasal
DARK = (0.015, 0.015, 0.02, 1.0)  # eye sockets

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
        # FIX (b) from build_hero.py: sample the Col vertex-colour attribute.
        attr = mat.node_tree.nodes.new("ShaderNodeAttribute")
        attr.attribute_name = COL
        attr.location = (-380, 200)
        mat.node_tree.links.new(attr.outputs["Color"], bsdf.inputs["Base Color"])
    mat.diffuse_color = (1, 1, 1, 1)
    return mat


def _finish(o, material, bevel):
    if bevel > 0:
        m = o.modifiers.new(name="Bevel", type="BEVEL")
        m.width = bevel
        m.segments = 1
        bpy.ops.object.modifier_apply(modifier="Bevel")
    bpy.ops.object.shade_flat()
    o.data.materials.append(material)
    a = o.data.color_attributes.new(name=COL, type="FLOAT_COLOR", domain="CORNER")
    for d in a.data:
        d.color = (1, 1, 1, 1)
    o.data.color_attributes.active_color = a
    return o


def box(name, size, loc, rot=(0, 0, 0), bevel=0.0):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    o.scale = size
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return _finish(o, MAT, bevel)


def paint(o, color, test=None):
    """Set the Col attribute of matching faces (test in mesh-local == world)."""
    a = o.data.color_attributes[COL]
    for p in o.data.polygons:
        if test is None or test(p.center.copy(), p.normal.copy()):
            for li in p.loop_indices:
                a.data[li].color = color


def subdivide(o, test, cuts=2):
    """Grid-subdivide just the faces matching test (for painted detail)."""
    bm = bmesh.new()
    bm.from_mesh(o.data)
    faces = [f for f in bm.faces if test(f.calc_center_median().copy(),
                                          f.normal.copy())]
    if faces:
        edges = set(e for f in faces for e in f.edges)
        bmesh.ops.subdivide_edges(bm, edges=list(edges), cuts=cuts,
                                  use_grid_fill=True)
        bm.to_mesh(o.data)
        bm.free()
        o.data.update()
    else:
        bm.free()


def join(name, objs):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    o = bpy.context.active_object
    o.name = name
    bpy.ops.object.shade_flat()
    return o


def tri_count(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)


# ------------------------------------------------------------------ build ---
def build():
    global MAT
    wipe_scene()
    MAT = make_material("mat_skeleton")

    # ---- Skull: box + painted sockets / nose / mouth ---------------------
    skull = box("Skull", (0.22, 0.24, 0.26), (0, 0, 1.67), bevel=0.035)
    paint(skull, BONE)
    front = lambda c, n: n.y > 0.8 and abs(c.y) > 0.06
    subdivide(skull, front, cuts=4)
    # front face after bevel: x in [-0.075,0.075], z in [1.575,1.765]
    paint(skull, DARK,                              # eye sockets
          lambda c, n: n.y > 0.8 and 0.04 < abs(c.x) < 0.07
          and 1.655 < c.z < 1.70)
    paint(skull, DARK,                              # nasal slit
          lambda c, n: n.y > 0.8 and abs(c.x) < 0.01
          and 1.61 < c.z < 1.66)
    paint(skull, DARK,                              # mouth slit
          lambda c, n: n.y > 0.8 and abs(c.x) < 0.046
          and 1.576 < c.z < 1.612)
    PARTS.append(skull)

    neck = box("Neck", (0.09, 0.09, 0.12), (0, 0, 1.50))
    paint(neck, BONE)
    PARTS.append(neck)

    # ---- Ribcage: box + 3 painted dark bands (front + sides) ------------
    torso = box("Ribcage", (0.34, 0.20, 0.54), (0, 0, 1.20), bevel=0.035)
    paint(torso, BONE)
    # ONLY the 3 big flat faces (n > 0.95): the looser 0.7 threshold also
    # caught ~9 bevel edge-strips (normals at 0.707) and blew the budget
    # to 896 tris on the first run.
    rib_area = lambda c, n: (n.y > 0.95 or abs(n.x) > 0.95) \
        and 0.96 < c.z < 1.44
    subdivide(torso, rib_area, cuts=4)

    def band(c, n):
        if not (n.y > 0.95 or abs(n.x) > 0.95):
            return False
        return (1.06 < c.z < 1.14 or 1.19 < c.z < 1.27
                or 1.32 < c.z < 1.40)
    paint(torso, BAND, band)                        # 3 rib gaps, wrapped
    PARTS.append(torso)

    pelvis = box("Pelvis", (0.24, 0.15, 0.16), (0, 0, 0.90), bevel=0.03)
    paint(pelvis, BONE)
    PARTS.append(pelvis)

    # ---- Arms (thin, embedded into the torso side, no weapon) -----------
    for side, sx in (("L", -1), ("R", 1)):
        arm = box(f"Arm_{side}", (0.09, 0.10, 0.62),
                  (sx * 0.20, 0, 1.10), bevel=0.02)
        paint(arm, BONE)
        PARTS.append(arm)
        hand = box(f"Hand_{side}", (0.09, 0.11, 0.13),
                   (sx * 0.20, 0.01, 0.76), bevel=0.02)
        paint(hand, BONE)
        PARTS.append(hand)

    # ---- Legs + feet (feet stick out +Y -> facing assert) ----------------
    for side, sx in (("L", -1), ("R", 1)):
        leg = box(f"Leg_{side}", (0.11, 0.13, 0.82),
                  (sx * 0.115, 0, 0.42), bevel=0.02)
        paint(leg, BONE)
        PARTS.append(leg)
        foot = box(f"Foot_{side}", (0.13, 0.28, 0.10),
                   (sx * 0.115, 0.06, 0.05))
        paint(foot, BONE)
        PARTS.append(foot)

    # ---- PROP: join everything into one mesh -----------------------------
    root = join("Skeleton", PARTS)
    PARTS[:] = [root]
    report()
    return root


def report():
    o = PARTS[0]
    n = tri_count(o)
    xs = [v.co.x for v in o.data.vertices]
    ys = [v.co.y for v in o.data.vertices]
    zs = [v.co.z for v in o.data.vertices]
    print(f"--- {MODEL} ---")
    print(f"  {o.name:12s} tris={n:4d} / budget {BUDGET} -> "
          f"{'OK' if n <= BUDGET else 'OVER BUDGET'}")
    print(f"  bounds x[{min(xs):.2f},{max(xs):.2f}] "
          f"y(front/back)[{min(ys):.2f},{max(ys):.2f}] "
          f"z(height)[{min(zs):.2f},{max(zs):.2f}]")
    print(f"  facing: front y={max(ys):.3f} > back |y|={abs(min(ys)):.3f} -> "
          f"{'OK' if max(ys) > abs(min(ys)) else 'FAIL'} "
          f"(glTF assert -z_min > z_max)")
    print(f"  height {max(zs) - min(zs):.3f} m (target ~1.80)")


# ------------------------------------------------------------------ stage ---
def aim(o, target):
    d = Vector(target) - o.location
    o.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()


def stage(cam_loc=(2.3, 3.1, 2.1), target=(0, 0, 0.95),
          key=500, fill=150, rim=260, ground=20):
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

    lights = [((-3.0, 4.0, 5.0), key, 4.0),
              ((4.0, 2.0, 2.5), fill, 3.0),
              ((0.5, -4.5, 3.5), rim, 3.0)]
    for i, (loc, energy, size) in enumerate(lights):
        bpy.ops.object.light_add(type="AREA", location=loc)
        l = bpy.context.active_object
        l.name = f"_Light{i}"
        l.data.energy = energy
        l.data.size = size
        aim(l, target)


def render_eevee(path):
    s = bpy.context.scene
    try:
        s.render.engine = "BLENDER_EEVEE"
    except TypeError:
        s.render.engine = "BLENDER_EEVEE_NEXT"
    s.render.resolution_x = 960
    s.render.resolution_y = 720
    s.render.film_transparent = False
    s.view_settings.view_transform = "Standard"
    s.render.image_settings.file_format = "PNG"
    s.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)
    print(f"  wrote {path}")


# ----------------------------------------------------------------- export ---
def export(root):
    bpy.ops.object.select_all(action="DESELECT")
    # FIX (a) from build_hero.py: select root AND descendants.
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
    print(f"  GLB verify: nodes={len(nodes)} meshes={len(meshes)} "
          f"COLOR_0={color0} tris={tris} "
          f"{'OK' if tris <= BUDGET and color0 and meshes else 'FAIL'}")
    print(f"  GLB facing: zmin={zmin:.3f} zmax={zmax:.3f} -> "
          f"-zmin={-zmin:.3f} > {zmax:.3f}: {'OK' if facing_ok else 'FAIL'}")


# -------------------------------------------------------------------- main ---
if __name__ == "__main__":
    for d in (ROOT / "glb", ROOT / "previews", ROOT / "source"):
        d.mkdir(parents=True, exist_ok=True)
    root = build()
    stage()
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND_PATH))
    export(root)
    verify_glb()
    render_eevee(PREV_PATH)
    print("DONE")
