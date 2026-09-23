#!/usr/bin/env python3
"""Verdant Crown — asset batch 1c: the BOSS (crown_guardian).

Low-poly asset conventions:
  * box-and-bevel kit, metres, FLAT shading, faces Blender +Y (Godot -Z forward)
  * GAME-READY 4-node rig — every part origin moved to its joint BEFORE parenting:

      CrownGuardian  (MESH root, origin ground-centred: torso + hips + legs/feet merged)
        Head         (neck pivot)   stooped head + crown + painted slit-eyes; JAW LATER
        Arm_L        (shoulder pivot) pauldron + upper arm + fist (telegraph swings later)
        Arm_R        (shoulder pivot)

  * ONE material (mat_crown_guardian); all tones ride the "Col" vertex-colour
    attribute: grey stone, darker stone, dark cracks, gold crown, dark face.
  * Budget: <= 1500 tris.  Authored at TRUE size (~3.0 m; scene x2 note stale).

Re-run (from the Blender MCP):
    path = ".../Assets/Models/build_crown_guardian.py"
    ns = {"__file__": path, "__name__": "__main__"}
    exec(compile(open(path).read(), path, "exec"), ns)
"""

import bpy
import bmesh
import math
import json
import struct
import pathlib
from mathutils import Vector, Matrix

# ---------------------------------------------------------------- config ---
MODEL = "crown_guardian"
BUDGET = 1500
ROOT = pathlib.Path(__file__).resolve().parent
GLB_PATH = ROOT / "glb" / f"{MODEL}.glb"
PREV_PATH = ROOT / "previews" / f"{MODEL}_eevee.png"
BLEND_PATH = ROOT / "source" / f"{MODEL}.blend"

COL = "Col"          # vertex-colour attribute name -> glTF COLOR_0
PARTS = []           # Head, Arm_L, Arm_R (root is built separately)

# palette (linear RGBA). One material per model; colours ride on the mesh.
STONE = (0.24, 0.26, 0.29, 1.0)    # base grey stone (correction r1: was 0.45 = white plastic)
STONE_D = (0.15, 0.16, 0.18, 1.0)  # feet / fists / brow shadow
CRACK = (0.03, 0.035, 0.045, 1.0)  # darker cracks
GOLD = (0.72, 0.48, 0.08, 1.0)     # crown accent
DARK = (0.02, 0.02, 0.025, 1.0)    # carved slit-eyes / frown

MAT = None


# ------------------------------------------------------------------- kit ---
def wipe_scene():
    """Remove every object (and orphan data) so a re-run starts clean."""
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
        bsdf.inputs["Roughness"].default_value = 0.95
        # Sample the vertex-colour attribute, or every render comes out white
        # while the painted Col data sits unused.
        attr = mat.node_tree.nodes.new("ShaderNodeAttribute")
        attr.attribute_name = COL
        attr.location = (-380, 200)
        mat.node_tree.links.new(attr.outputs["Color"], bsdf.inputs["Base Color"])
    mat.diffuse_color = (1, 1, 1, 1)
    return mat


def _finish(o, material, bevel):
    """Bevel, flat-shade, give every face the Col attribute, register."""
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


def cone(name, radius1, radius2, depth, loc, rot=(0, 0, 0), verts=4):
    bpy.ops.mesh.primitive_cone_add(vertices=verts, radius1=radius1,
                                    radius2=radius2, depth=depth,
                                    location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return _finish(o, MAT, 0.0)


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


def tilt_about(objs, pivot, deg):
    """Stoop the head: rotate mesh data about a world pivot (paints first,
    axis-aligned tests, then tip). Objects are still unparented here."""
    R = Matrix.Translation(Vector(pivot)) @ \
        Matrix.Rotation(math.radians(deg), 4, "X") @ \
        Matrix.Translation(-Vector(pivot))
    for o in objs:
        o.data.transform(R)
        o.data.update()


def set_origin(o, point):
    """Move the origin onto a joint — ALWAYS before parenting."""
    bpy.ops.object.select_all(action='DESELECT')
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    bpy.context.scene.cursor.location = point
    bpy.ops.object.origin_set(type='ORIGIN_CURSOR')


def adopt(child, parent):
    """Parent while preserving the world transform."""
    child.parent = parent
    child.matrix_parent_inverse = parent.matrix_world.inverted()


def tri_count(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)


# ------------------------------------------------------------------ build ---
def build():
    global MAT
    wipe_scene()
    MAT = make_material("mat_crown_guardian")

    # ---- ROOT mesh: chest + hips + legs/feet (origin stays 0,0,0) --------
    chest = box("Chest", (1.50, 0.90, 1.10), (0, 0, 1.85), bevel=0.12)
    paint(chest, STONE)
    chest_front = lambda c, n: n.y > 0.9 and c.y > 0.2
    subdivide(chest, chest_front, cuts=4)
    # jagged darker crack, bottom-right -> top-left (cell centres, tol < half-cell)
    crack_cells = [(0.252, 1.514), (0.252, 1.682), (0.0, 1.85),
                   (0.0, 2.018)]   # ends under emblem -> seam into gold, not checker
    paint(chest, CRACK, lambda c, n: chest_front(c, n) and any(
        abs(c.x - cx) < 0.04 and abs(c.z - cz) < 0.05
        for cx, cz in crack_cells))                            # cracks
    paint(chest, GOLD, lambda c, n: chest_front(c, n)
          and abs(c.x) < 0.04 and abs(c.z - 2.186) < 0.05)     # crown emblem

    hips = box("Hips", (1.00, 0.70, 0.45), (0, 0, 1.15), bevel=0.08)
    paint(hips, STONE)

    legs, feet = [], []
    for side, sx in (("L", -1), ("R", 1)):
        leg = box(f"LegMass_{side}", (0.42, 0.50, 0.85),
                  (sx * 0.33, 0, 0.62), bevel=0.06)
        paint(leg, STONE)
        legs.append(leg)
        foot = box(f"Foot_{side}", (0.50, 0.86, 0.26),
                   (sx * 0.33, 0.16, 0.13), bevel=0.05)
        paint(foot, STONE_D)
        feet.append(foot)

    root = join("CrownGuardian", [chest, hips] + legs + feet)
    # origin: join keeps active object's origin = (0,0,0) ground-centred

    # ---- Head: mass + painted stern face + gold crown, then STOOP --------
    NECK = (0, 0.02, 2.40)
    neck = box("NeckMass", (0.30, 0.30, 0.25), NECK, bevel=0.04)
    paint(neck, STONE_D)
    head = box("HeadMass", (0.55, 0.60, 0.50), (0, 0.14, 2.68), bevel=0.06)
    paint(head, STONE)
    head_front = lambda c, n: n.y > 0.9 and c.y > 0.2
    subdivide(head, head_front, cuts=3)
    # front face after bevel: x[-0.215,0.215] z[2.49,2.87], 4x4 cells
    paint(head, DARK, lambda c, n: head_front(c, n)
          and 2.66 < c.z < 2.79 and abs(c.x) > 0.10)           # slit-eyes
    paint(head, STONE_D, lambda c, n: head_front(c, n)
          and c.z > 2.79)                                      # heavy brow
    paint(head, DARK, lambda c, n: head_front(c, n)
          and c.z < 2.59 and abs(c.x) < 0.11)                  # frown

    band = box("CrownBand", (0.64, 0.68, 0.13), (0, 0.14, 2.90), bevel=0.03)
    paint(band, GOLD)
    spikes = []
    for i, (sx, sy) in enumerate([(-0.25, 0.42), (0.25, 0.42),
                                  (0.0, 0.45), (0.26, -0.14), (-0.26, -0.14)]):
        s = cone(f"CrownSpike_{i}", 0.075, 0.0, 0.14, (sx, sy, 3.015))
        paint(s, GOLD)
        spikes.append(s)

    # stoop: tip everything forward about the neck AFTER painting
    head_bits = [head, neck, band] + spikes
    tilt_about(head_bits, NECK, -14)

    head_part = join("Head", head_bits)
    set_origin(head_part, NECK)
    PARTS.append(head_part)

    # ---- Arms: pauldron + upper arm + fist; shoulder pivots -------------
    for side, sx in (("L", -1), ("R", 1)):
        pauldron = box(f"Pauldron_{side}", (0.50, 0.55, 0.25),
                       (sx * 0.86, 0, 2.32), bevel=0.06)
        paint(pauldron, STONE)
        arm = box(f"ArmMass_{side}", (0.38, 0.42, 1.20),
                  (sx * 0.86, 0.02, 1.70), bevel=0.05)
        paint(arm, STONE)
        fist = box(f"Fist_{side}", (0.44, 0.50, 0.40),
                   (sx * 0.86, 0.06, 0.95), bevel=0.06)
        paint(fist, STONE_D)
        arm_part = join(f"Arm_{side}", [pauldron, arm, fist])
        set_origin(arm_part, (sx * 0.86, 0, 2.28))             # shoulder
        PARTS.append(arm_part)

    # ---- Parent (origins already on joints) -----------------------------
    for p in PARTS:
        adopt(p, root)

    report(root)
    return root


def report(root):
    print(f"--- {MODEL} ---")
    total = 0
    for p in [root] + PARTS:
        n = tri_count(p)
        total += n
        print(f"  {p.name:16s} tris={n:4d} origin_world="
              f"{tuple(round(v, 3) for v in p.matrix_world.translation)}")
    print(f"  TOTAL {total} / budget {BUDGET} -> "
          f"{'OK' if total <= BUDGET else 'OVER BUDGET'}")
    xs, ys, zs = [], [], []
    for o in [root] + PARTS:
        for corner in o.bound_box:
            w = o.matrix_world @ Vector(corner)
            xs.append(w.x); ys.append(w.y); zs.append(w.z)
    print(f"  bounds x[{min(xs):.2f},{max(xs):.2f}] "
          f"y(front/back)[{min(ys):.2f},{max(ys):.2f}] "
          f"z(height)[{min(zs):.2f},{max(zs):.2f}]")
    print(f"  facing: front y={max(ys):.3f} > back |y|={abs(min(ys)):.3f} "
          f"-> {'OK' if max(ys) > abs(min(ys)) else 'FAIL'} "
          f"(glTF assert -z_min > z_max)")
    print(f"  height {max(zs):.3f} m (target ~3.0)")


# ------------------------------------------------------------------ stage ---
def aim(o, target):
    d = Vector(target) - o.location
    o.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()


def stage(cam_loc=(3.6, 5.2, 2.6), target=(0, 0, 1.5),
          key=1000, fill=350, rim=600, ground=30):
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

    lights = [((-4.0, 5.0, 6.0), key, 5.0),
              ((5.0, 3.0, 3.0), fill, 4.0),
              ((1.0, -5.0, 4.5), rim, 4.0)]
    for i, (loc, energy, size) in enumerate(lights):
        bpy.ops.object.light_add(type="AREA", location=loc)
        l = bpy.context.active_object
        l.name = f"_Light{i}"
        l.data.energy = energy
        l.data.size = size
        aim(l, target)


def render_eevee(path):
    s = bpy.context.scene
    s.render.engine = "BLENDER_EEVEE"
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
    bpy.ops.object.select_all(action='DESELECT')
    # Root AND descendants: root-only selection exports an empty husk.
    root.select_set(True)
    for desc in root.children_recursive:
        desc.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.gltf(
        filepath=str(GLB_PATH),
        export_format="GLB",
        use_selection=True,
        export_apply=False,          # pivots are already baked
        export_materials="EXPORT",
        export_yup=True,
        export_vertex_color="ACTIVE",
        export_normals=True,
    )
    print(f"  wrote {GLB_PATH}")


def verify_glb():
    """Parse the GLB JSON: nodes>=4 with meshes, COLOR_0, tri<=1500, facing."""
    data = pathlib.Path(GLB_PATH).read_bytes()
    gltf = json.loads(data[20:20 + struct.unpack("<I", data[12:16])[0]])
    nodes = gltf["nodes"]
    mesh_nodes = [n.get("name") for n in nodes if "mesh" in n]
    tris, color0 = 0, True
    zmin, zmax = None, None
    for m in gltf.get("meshes", []):
        for p in m["primitives"]:
            attrs = p.get("attributes", {})
            if "COLOR_0" not in attrs:
                color0 = False
            acc = gltf["accessors"][attrs["POSITION"]]
            lo, hi = acc["min"][2], acc["max"][2]
            zmin = lo if zmin is None else min(zmin, lo)
            zmax = hi if zmax is None else max(zmax, hi)
            if "indices" in p:
                tris += gltf["accessors"][p["indices"]]["count"] // 3
            else:
                tris += acc["count"] // 3
    facing = (-zmin) > zmax   # Blender +Y front -> glTF -Z front
    print(f"  GLB nodes={len(nodes)} mesh_nodes={mesh_nodes}")
    print(f"  GLB tris={tris}/{BUDGET} COLOR_0={color0} "
          f"facing(-z_min={-zmin:.2f} > z_max={zmax:.2f})={facing}")
    assert len(mesh_nodes) >= 4, "need >=4 mesh nodes"
    assert color0, "COLOR_0 missing"
    assert tris <= BUDGET, "over tri budget"
    assert facing, "facing assert failed"
    print("  GLB verify OK")


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
