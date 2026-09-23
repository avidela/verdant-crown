#!/usr/bin/env python3
"""Verdant Crown — sprint 3 character 1: the hero (knight, sword).

Asset conventions:
  * box-and-bevel kit, metres, FLAT shading, faces Blender +Y (Godot -Z forward)
  * GAME-READY 6-node rig — every part origin moved to its joint BEFORE parenting:

      Hero          (empty, origin on the ground, centred)
        Torso       (waist pivot)   tunic, belt, pauldrons, neck
        Head        (neck pivot)    head + helm + visor; face painted on Col
        SwordArm    (shoulder pivot) arm + fist + SWORD BAKED INTO THIS MESH
        OffArm      (shoulder pivot)
        Leg_L/R     (hip pivots)    leg + boot

  * ONE material (mat_hero); every multi-tone detail lives in the "Col"
    vertex-colour attribute (Godot renders unshaded, albedo = vertex colour).
  * Budget: <= 900 tris.

Re-run (from the Blender MCP):
    path = ".../Assets/Models/build_hero.py"
    ns = {"__file__": path, "__name__": "__main__"}
    exec(compile(open(path).read(), path, "exec"), ns)
"""

import bpy
import bmesh
import math
import pathlib
from mathutils import Vector

# ---------------------------------------------------------------- config ---
MODEL = "hero"
BUDGET = 900
ROOT = pathlib.Path(__file__).resolve().parent
GLB_PATH = ROOT / "glb" / f"{MODEL}.glb"
PREV_PATH = ROOT / "previews" / f"{MODEL}_eevee.png"
CYCLES_PATH = ROOT / "previews" / f"{MODEL}_cycles.png"
BLEND_PATH = ROOT / "source" / f"{MODEL}.blend"

COL = "Col"          # vertex-colour attribute name -> glTF COLOR_0
PARTS = []           # the 6 rig parts, in rig order

# palette (linear RGBA). One material per model; colours ride on the mesh.
GREEN = (0.09, 0.36, 0.13, 1.0)     # tunic
GREEN_D = (0.05, 0.17, 0.08, 1.0)   # trousers
SKIN = (0.78, 0.50, 0.33, 1.0)
STEEL = (0.48, 0.52, 0.58, 1.0)     # helm / pauldrons / guard
STEEL_L = (0.66, 0.70, 0.76, 1.0)   # blade
LEATHER = (0.16, 0.09, 0.04, 1.0)   # boots / belt / grip
DARK = (0.015, 0.015, 0.02, 1.0)    # eyes / mouth

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
        bsdf.inputs["Roughness"].default_value = 0.9
        # Sample the vertex-colour attribute, or every render comes out white
        # while the painted Col data sits unused (asset run 2026-09-22).
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


def _new_mesh_obj():
    bpy.ops.object.select_all(action="DESELECT")
    o = bpy.context.active_object
    # bake location/rotation/scale so mesh-local == world while building
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return o


def box(name, size, loc, rot=(0, 0, 0), bevel=0.0):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    o.scale = size
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return _finish(o, MAT, bevel)


def cone(name, radius1, radius2, depth, loc, rot=(0, 0, 0), verts=6):
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


def set_origin(o, point):
    """Move the origin onto a joint — ALWAYS before parenting."""
    bpy.ops.object.select_all(action="DESELECT")
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    bpy.context.scene.cursor.location = point
    bpy.ops.object.origin_set(type="ORIGIN_CURSOR")


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
    MAT = make_material("mat_hero")

    # ---- Torso: tunic + belt + buckle + pauldrons + neck ------------------
    torso = box("TorsoMass", (0.46, 0.28, 0.50), (0, 0, 1.11), bevel=0.06)
    paint(torso, GREEN)
    belt = box("Belt", (0.50, 0.315, 0.11), (0, 0, 0.90), bevel=0.025)
    paint(belt, LEATHER)
    buckle = box("Buckle", (0.09, 0.05, 0.075), (0, 0.165, 0.90))
    paint(buckle, STEEL)
    pauldron_l = box("Pauldron_L", (0.20, 0.30, 0.15), (-0.27, 0, 1.31),
                     bevel=0.045)
    paint(pauldron_l, STEEL)
    pauldron_r = box("Pauldron_R", (0.20, 0.30, 0.15), (0.27, 0, 1.31),
                     bevel=0.045)
    paint(pauldron_r, STEEL)
    neck = box("Neck", (0.15, 0.15, 0.14), (0, 0, 1.40))
    paint(neck, SKIN)
    torso_part = join("Torso", [torso, belt, buckle, pauldron_l,
                                pauldron_r, neck])
    set_origin(torso_part, (0, 0, 0.88))
    PARTS.append(torso_part)

    # ---- Head: skin + painted face + helm + visor brim --------------------
    head = box("HeadMass", (0.30, 0.30, 0.22), (0, 0, 1.55), bevel=0.045)
    paint(head, SKIN)
    front = lambda c, n: n.y > 0.85 and abs(c.y) > 0.05
    subdivide(head, front, cuts=2)
    # front face after bevel: x in [-0.105,0.105], z in [1.485,1.615]
    paint(head, DARK, lambda c, n: n.y > 0.85 and abs(c.x) > 0.03
          and c.z > 1.529)                                   # eyes
    paint(head, DARK, lambda c, n: n.y > 0.85 and abs(c.x) < 0.03
          and c.z < 1.528)                                   # mouth
    helm = box("Helm", (0.34, 0.34, 0.10), (0, 0, 1.65), bevel=0.035)
    paint(helm, STEEL)
    visor = box("Visor", (0.30, 0.06, 0.05), (0, 0.17, 1.625))
    paint(visor, STEEL)
    head_part = join("Head", [head, helm, visor])
    set_origin(head_part, (0, -0.14, 1.47))
    PARTS.append(head_part)

    # ---- SwordArm: arm + fist + SWORD BAKED IN (the game swings this) ----
    R25 = math.radians(25.0)
    arm = box("SwordArmMass", (0.14, 0.15, 0.44), (0.30, 0, 1.07),
              bevel=0.035)
    paint(arm, GREEN)
    hand = box("SwordHand", (0.135, 0.145, 0.17), (0.30, 0.02, 0.80),
               bevel=0.03)
    paint(hand, SKIN)
    grip = box("Grip", (0.05, 0.055, 0.17), (0.30, 0.016, 0.7725),
               rot=(R25, 0, 0))
    paint(grip, LEATHER)
    guard = box("Guard", (0.20, 0.05, 0.055), (0.30, 0.05, 0.70),
                rot=(R25, 0, 0))
    paint(guard, STEEL)
    blade = box("Blade", (0.075, 0.022, 0.64), (0.30, 0.185, 0.410),
                rot=(R25, 0, 0))
    paint(blade, STEEL_L)
    sword_arm = join("SwordArm", [arm, hand, grip, guard, blade])
    set_origin(sword_arm, (0.30, 0, 1.30))
    PARTS.append(sword_arm)

    # ---- OffArm ----------------------------------------------------------
    off_arm_mass = box("OffArmMass", (0.14, 0.15, 0.44), (-0.30, 0, 1.07),
                       bevel=0.035)
    paint(off_arm_mass, GREEN)
    off_hand = box("OffHand", (0.135, 0.145, 0.17), (-0.30, 0.02, 0.80),
                   bevel=0.03)
    paint(off_hand, SKIN)
    off_arm = join("OffArm", [off_arm_mass, off_hand])
    set_origin(off_arm, (-0.30, 0, 1.30))
    PARTS.append(off_arm)

    # ---- Legs (hip pivots) ----------------------------------------------
    for side, sx in (("L", -1), ("R", 1)):
        leg = box(f"LegMass_{side}", (0.17, 0.19, 0.76),
                  (sx * 0.14, 0, 0.49), bevel=0.035)
        paint(leg, GREEN_D)
        boot = box(f"Boot_{side}", (0.19, 0.31, 0.19),
                   (sx * 0.14, 0.05, 0.095), bevel=0.04)
        paint(boot, LEATHER)
        leg_part = join(f"Leg_{side}", [leg, boot])
        set_origin(leg_part, (sx * 0.14, 0, 0.86))
        PARTS.append(leg_part)

    # ---- Root + parent (origins already on joints) -----------------------
    root = bpy.data.objects.new("Hero", None)
    bpy.context.scene.collection.objects.link(root)
    root.location = (0, 0, 0)
    for p in PARTS:
        adopt(p, root)

    report(root)
    return root


def report(root):
    print(f"--- {MODEL} ---")
    total = 0
    for p in [root] + PARTS:
        n = tri_count(p) if p.type == "MESH" else 0
        total += n
        print(f"  {p.name:12s} tris={n:4d} origin_world="
              f"{tuple(round(v, 3) for v in p.matrix_world.translation)}")
    print(f"  TOTAL {total} / budget {BUDGET} -> "
          f"{'OK' if total <= BUDGET else 'OVER BUDGET'}")
    # world bounds from all evaluated meshes
    xs, ys, zs = [], [], []
    for o in bpy.data.objects:
        if o.type != "MESH":
            continue
        for corner in o.bound_box:
            w = o.matrix_world @ Vector(corner)
            xs.append(w.x); ys.append(w.y); zs.append(w.z)
    print(f"  bounds x[{min(xs):.2f},{max(xs):.2f}] "
          f"y(front/back)[{min(ys):.2f},{max(ys):.2f}] "
          f"z(height)[{min(zs):.2f},{max(zs):.2f}]")
    print(f"  facing: front y={max(ys):.3f} > back |y|={abs(min(ys)):.3f} "
          f"-> {'OK' if max(ys) > abs(min(ys)) else 'FAIL'} "
          f"(glTF assert -z_min > z_max)")
    print(f"  height {max(zs):.3f} m (target ~1.70)")


# ------------------------------------------------------------------ stage ---
def aim(o, target):
    d = Vector(target) - o.location
    o.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()


def stage(cam_loc=(2.3, 3.1, 2.1), target=(0, 0, 0.9),
          key=500, fill=150, rim=260, ground=20):
    # ground (preview only — export uses use_selection from the root)
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
    s.render.engine = "BLENDER_EEVEE"
    s.render.resolution_x = 960
    s.render.resolution_y = 720
    s.render.film_transparent = False
    s.view_settings.view_transform = "Standard"
    s.render.image_settings.file_format = "PNG"
    s.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)
    print(f"  wrote {path}")


def render_cycles(path):
    s = bpy.context.scene
    s.render.engine = "CYCLES"
    prefs = bpy.context.preferences.addons["cycles"].preferences
    prefs.compute_device_type = "HIP"
    prefs.get_devices()
    for d in prefs.devices:
        d.use = (d.type == "HIP")
    s.cycles.device = "GPU"
    s.cycles.samples = 96
    s.view_settings.view_transform = "Standard"
    s.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)
    print(f"  wrote {path}")
    s.render.engine = "BLENDER_EEVEE"


# ----------------------------------------------------------------- export ---
def export(root):
    bpy.ops.object.select_all(action="DESELECT")
    # Explicitly select the root AND every descendant: selecting only the root
    # exported an empty husk (172-byte GLB, Hero node, 0 meshes) — the exporter
    # did not pull children in on this build.
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


# -------------------------------------------------------------------- main ---
if __name__ == "__main__":
    for d in (ROOT / "glb", ROOT / "previews", ROOT / "source"):
        d.mkdir(parents=True, exist_ok=True)
    root = build()
    stage()
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND_PATH))
    export(root)
    render_eevee(PREV_PATH)
    render_cycles(CYCLES_PATH)
    print("DONE")
