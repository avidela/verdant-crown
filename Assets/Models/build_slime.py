#!/usr/bin/env python3
"""Verdant Crown — asset batch 1b: the slime (PROP, joined single mesh).

Low-poly asset conventions:
  * metres, FLAT shading, faces Blender +Y (Godot -Z forward), origin on ground
  * PROP: everything is ONE joined mesh — slime is whole-instance-animated
    (bob/squash), no articulation needed.
  * ONE material (mat_slime); all tones ride the "Col" vertex-colour attribute
    (Godot renders unshaded, albedo = vertex colour).
  * Eyes are PAINTED via subdivide + face paint — not glued boxes.
  * Budget: <= 300 tris.

Carried over from build_hero.py (debugged 2026-09-22):
  (a) export() selects the root AND root.children_recursive (root-only export
      produced a 172-byte husk),
  (b) make_material() wires ShaderNodeAttribute(COL) -> Base Color so renders
      show the painted vertex colours.

Re-run (from the Blender MCP):
    path = ".../Assets/Models/build_slime.py"
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
MODEL = "slime"
BUDGET = 300
ROOT = pathlib.Path(__file__).resolve().parent
GLB_PATH = ROOT / "glb" / f"{MODEL}.glb"
PREV_PATH = ROOT / "previews" / f"{MODEL}_eevee.png"
BLEND_PATH = ROOT / "source" / f"{MODEL}.blend"

COL = "Col"          # vertex-colour attribute -> glTF COLOR_0
PARTS = []

# palette (linear RGBA)
SLIME = (0.10, 0.48, 0.14, 1.0)    # body green
SLIME_D = (0.05, 0.25, 0.08, 1.0)  # under-shade / back green
DARK = (0.010, 0.012, 0.010, 1.0)  # eyes

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
        # FIX (b) from build_hero.py: sample the Col vertex-colour attribute,
        # or every render comes out white while the paint sits unused.
        attr = mat.node_tree.nodes.new("ShaderNodeAttribute")
        attr.attribute_name = COL
        attr.location = (-380, 200)
        mat.node_tree.links.new(attr.outputs["Color"], bsdf.inputs["Base Color"])
    mat.diffuse_color = (1, 1, 1, 1)
    return mat


def finish(o, material):
    """Flat-shade, give every face a white Col attribute, attach material."""
    bpy.ops.object.shade_flat()
    o.data.materials.append(material)
    a = o.data.color_attributes.new(name=COL, type="FLOAT_COLOR", domain="CORNER")
    for d in a.data:
        d.color = (1, 1, 1, 1)
    o.data.color_attributes.active_color = a
    return o


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


def tri_count(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)


# ------------------------------------------------------------------ build ---
def build():
    global MAT
    wipe_scene()
    MAT = make_material("mat_slime")

    bpy.ops.mesh.primitive_uv_sphere_add(segments=12, ring_count=7,
                                         radius=1.0, location=(0, 0, 0))
    o = bpy.context.active_object
    o.name = "Slime"
    o.scale = (0.33, 0.32, 0.35)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    # --- shape: dome blob, wider lower body, slight front bulge (+Y),
    #     flat base ------------------------------------------------------
    me = o.data
    zmin = min(v.co.z for v in me.vertices)
    zmax = max(v.co.z for v in me.vertices)
    for v in me.vertices:
        t = (v.co.z - zmin) / (zmax - zmin)
        f = 1.0 + 0.35 * (1.0 - t) ** 2          # wider bottom
        v.co.x *= f
        v.co.y *= f
        if v.co.y > 0:
            v.co.y *= 1.06                        # facing bulge (+Y front)
        if v.co.z < zmin + 0.08:                  # flatten the base
            v.co.z = zmin + 0.01
    zmin = min(v.co.z for v in me.vertices)
    for v in me.vertices:
        v.co.z -= zmin                            # sit on the ground (z=0)
    me.update()

    finish(o, MAT)
    paint(o, SLIME)                               # base green everywhere
    paint(o, SLIME_D,                             # shade: underside + back
          lambda c, n: c.z < 0.14 or n.y < -0.55)

    # --- eyes: subdivide the front patch, paint whole faces -------------
    region = lambda c, n: n.y > 0.5 and 0.34 < c.z < 0.58 and abs(c.x) < 0.22
    subdivide(o, region, cuts=2)
    paint(o, DARK,                                # two dark eyes, painted
          lambda c, n: n.y > 0.5 and 0.045 <= abs(c.x) <= 0.155
          and 0.41 < c.z < 0.51)

    PARTS.append(o)
    report()
    return o


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
    print(f"  height {max(zs) - min(zs):.3f} m (target ~0.70)")


# ------------------------------------------------------------------ stage ---
def aim(o, target):
    d = Vector(target) - o.location
    o.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()


def stage(cam_loc=(1.2, 1.7, 0.9), target=(0, 0, 0.33),
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
    # FIX (a) from build_hero.py: select root AND descendants; root-only
    # selection exported a 172-byte husk (0 meshes) on this build.
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
