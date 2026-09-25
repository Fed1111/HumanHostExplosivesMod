"""Builds the 0.3.0 explosive models procedurally in Blender: mesh (OBJ), baked diffuse texture
(1024 PNG) and a raw transparent icon render (finish_icons.py crops/resizes it afterwards).

Run headless:
    blender.exe -b --factory-startup -P tools/make_models.py -- --repo <mod repo> [--only molotov,contact,mine,improvised]

Conventions, matched to Assets/Grenade/grenade.obj (what ObjLoader and the tuned hand offsets
expect): metres, Y-up in the file, origin at the bottom centre, one triangulated mesh with UVs.
ObjLoader mirrors X on import, which only flips the handedness of asymmetric decoration.

Materials are procedural (object-space coordinates, so they read the same in the icon and the
bake) and use Metallic 0 throughout: the DIFFUSE/COLOR bake of a metallic surface is black, and
the in-game material is a plain HDRP/Lit with only a base colour map anyway.
"""
import argparse
import math
import os
import sys

import bmesh
import bpy
from mathutils import Vector

print("[make_models] Blender", bpy.app.version)

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
ap = argparse.ArgumentParser()
ap.add_argument("--repo", required=True)
ap.add_argument("--only", default="molotov,contact,mine,improvised")
ap.add_argument("--samples", type=int, default=96)
args = ap.parse_args(argv)
ASSETS = os.path.join(args.repo, "Assets")
RAW = os.path.join(args.repo, "tools", "_render")
os.makedirs(RAW, exist_ok=True)


# ---------------------------------------------------------------- scene helpers

def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.images, bpy.data.curves, bpy.data.lights, bpy.data.cameras):
        for item in list(block):
            block.remove(item)


def link(obj):
    bpy.context.scene.collection.objects.link(obj)
    return obj


def mesh_obj(name, bm):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    return link(bpy.data.objects.new(name, me))


def lathe(name, profile, segments=48, cap_bottom=True, cap_top=True):
    """Revolves a list of (radius, z) points around Z. Radius 0 at an end closes it."""
    bm = bmesh.new()
    rings = []
    for r, z in profile:
        ring = []
        for i in range(segments):
            a = 2 * math.pi * i / segments
            ring.append(bm.verts.new((r * math.cos(a), r * math.sin(a), z)))
        rings.append(ring)
    for k in range(len(rings) - 1):
        a, b = rings[k], rings[k + 1]
        for i in range(segments):
            j = (i + 1) % segments
            bm.faces.new((a[i], a[j], b[j], b[i]))
    if cap_bottom:
        bm.faces.new(list(reversed(rings[0])))
    if cap_top:
        bm.faces.new(rings[-1])
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return mesh_obj(name, bm)


def cylinder(name, r, h, z0=0.0, segments=32, loc=(0, 0, 0), rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_cylinder_add(vertices=segments, radius=r, depth=h,
                                        location=(loc[0], loc[1], loc[2] + z0 + h / 2), rotation=rot)
    o = bpy.context.active_object
    o.name = name
    return o


def torus(name, R, r, z, major_seg=48, minor_seg=8):
    bpy.ops.mesh.primitive_torus_add(major_radius=R, minor_radius=r, major_segments=major_seg,
                                     minor_segments=minor_seg, location=(0, 0, z))
    o = bpy.context.active_object
    o.name = name
    return o


def box(name, size, loc, rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.scale = size
    o.name = name
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return o


def tube_curve(name, points, radius, resolution=6):
    cu = bpy.data.curves.new(name, "CURVE")
    cu.dimensions = "3D"
    cu.bevel_depth = radius
    cu.bevel_resolution = 2
    cu.resolution_u = resolution
    sp = cu.splines.new("BEZIER")
    sp.bezier_points.add(len(points) - 1)
    for bp, p in zip(sp.bezier_points, points):
        bp.co = p
        bp.handle_left_type = bp.handle_right_type = "AUTO"
    cu.use_fill_caps = True
    o = link(bpy.data.objects.new(name, cu))
    bpy.context.view_layer.objects.active = o
    o.select_set(True)
    bpy.ops.object.convert(target="MESH")
    return bpy.context.active_object


def set_mat(obj, mat):
    obj.data.materials.clear()
    obj.data.materials.append(mat)


def bevel(obj, width, segments=2):
    m = obj.modifiers.new("bevel", "BEVEL")
    m.width = width
    m.segments = segments
    m.limit_method = "ANGLE"
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=m.name)


# ---------------------------------------------------------------- material helpers

def lin(c):
    """Colours in this file are written as sRGB (what a colour picker shows); node inputs are linear."""
    def f(x):
        return x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4
    return (f(c[0]), f(c[1]), f(c[2]))


class M:
    """Tiny node-graph builder: every material ends in one Principled BSDF."""

    def __init__(self, name, roughness=0.6):
        self.mat = bpy.data.materials.new(name)
        self.mat.use_nodes = True
        self.nt = self.mat.node_tree
        self.bsdf = self.nt.nodes["Principled BSDF"]
        self.bsdf.inputs["Metallic"].default_value = 0.0
        self.bsdf.inputs["Roughness"].default_value = roughness
        self._coord = None

    def n(self, kind, **props):
        node = self.nt.nodes.new(kind)
        for k, v in props.items():
            setattr(node, k, v)
        return node

    def l(self, out, inp):
        self.nt.links.new(out, inp)

    @property
    def coord(self):
        if self._coord is None:
            self._coord = self.n("ShaderNodeTexCoord").outputs["Object"]
        return self._coord

    def xyz(self):
        s = self.n("ShaderNodeSeparateXYZ")
        self.l(self.coord, s.inputs[0])
        return s.outputs

    def noise(self, scale, detail=6.0, roughness=0.6):
        nz = self.n("ShaderNodeTexNoise")
        nz.inputs["Scale"].default_value = scale
        nz.inputs["Detail"].default_value = detail
        nz.inputs["Roughness"].default_value = roughness
        self.l(self.coord, nz.inputs["Vector"])
        return nz.outputs["Fac"]

    def ramp(self, fac, stops):
        r = self.n("ShaderNodeValToRGB")
        els = r.color_ramp.elements
        while len(els) > len(stops):
            els.remove(els[-1])
        while len(els) < len(stops):
            els.new(0.5)
        for el, (pos, col) in zip(els, stops):
            el.position = pos
            c = lin(col)
            el.color = (c[0], c[1], c[2], 1.0)
        self.l(fac, r.inputs["Fac"])
        return r.outputs["Color"]

    def mix(self, fac, a, b):
        m = self.n("ShaderNodeMix", data_type="RGBA")
        if isinstance(fac, (int, float)):
            m.inputs[0].default_value = fac
        else:
            self.l(fac, m.inputs[0])
        for idx, val in ((6, a), (7, b)):
            if isinstance(val, tuple):
                c = lin(val)
                m.inputs[idx].default_value = (c[0], c[1], c[2], 1.0)
            else:
                self.l(val, m.inputs[idx])
        return m.outputs[2]

    def math(self, op, a, b=None, clamp=True):
        m = self.n("ShaderNodeMath", operation=op, use_clamp=clamp)
        for idx, val in ((0, a), (1, b)):
            if val is None:
                continue
            if isinstance(val, (int, float)):
                m.inputs[idx].default_value = val
            else:
                self.l(val, m.inputs[idx])
        return m.outputs[0]

    def band(self, value, lo, hi):
        """1 inside [lo, hi], else 0."""
        return self.math("MULTIPLY", self.math("GREATER_THAN", value, lo), self.math("LESS_THAN", value, hi))

    def radius_xy(self):
        o = self.xyz()
        x2 = self.math("MULTIPLY", o[0], o[0], clamp=False)
        y2 = self.math("MULTIPLY", o[1], o[1], clamp=False)
        return self.math("SQRT", self.math("ADD", x2, y2, clamp=False), clamp=False)

    def color(self, sock):
        self.l(sock, self.bsdf.inputs["Base Color"])
        return self.mat


def flat(name, col, roughness=0.6, grime=0.25, scale=180.0):
    m = M(name, roughness)
    dirt = m.ramp(m.noise(scale), [(0.35, (1, 1, 1)), (0.75, (1 - grime, 1 - grime, 1 - grime))])
    mul = m.n("ShaderNodeMix", data_type="RGBA", blend_type="MULTIPLY")
    mul.inputs[0].default_value = 1.0
    c = lin(col)
    mul.inputs[6].default_value = (c[0], c[1], c[2], 1)
    m.l(dirt, mul.inputs[7])
    return m.color(mul.outputs[2])


# ---------------------------------------------------------------- pipeline steps

def join_all(name):
    objs = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1:
        bpy.ops.object.join()
    o = bpy.context.active_object
    o.name = name
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return o


def smooth(obj, angle_deg=40):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(angle_deg))


def uv_unwrap(obj):
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.02)
    bpy.ops.object.mode_set(mode="OBJECT")


def setup_cycles(samples):
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = samples
    sc.cycles.use_denoising = True


def bake(obj, png_path, size=1024):
    img = bpy.data.images.new(os.path.basename(png_path), size, size, alpha=False)
    for slot in obj.material_slots:
        nt = slot.material.node_tree
        node = nt.nodes.new("ShaderNodeTexImage")
        node.image = img
        nt.nodes.active = node
    setup_cycles(8)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.bake(type="DIFFUSE", pass_filter={"COLOR"}, margin=8, use_clear=True)
    img.filepath_raw = png_path
    img.file_format = "PNG"
    img.save()
    # Take the bake node out again so the icon render uses the procedural look.
    for slot in obj.material_slots:
        nt = slot.material.node_tree
        for node in [n for n in nt.nodes if n.type == "TEX_IMAGE" and n.image == img]:
            nt.nodes.remove(node)
    print("[make_models] baked", png_path)


def export_obj(obj, path):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.wm.obj_export(filepath=path, export_selected_objects=True, export_uv=True,
                          export_normals=True, export_materials=False, export_triangulated_mesh=True,
                          forward_axis="NEGATIVE_Z", up_axis="Y", apply_modifiers=True)
    print("[make_models] exported", path, "tris", sum(len(p.vertices) - 2 for p in obj.data.polygons))


def render_icon(obj, path, samples, elevation=25, azimuth=35, tilt=None):
    sc = bpy.context.scene
    setup_cycles(samples)
    sc.render.film_transparent = True
    sc.render.resolution_x = sc.render.resolution_y = 1024
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.view_settings.view_transform = "Standard"
    world = bpy.data.worlds.new("w")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.35
    sc.world = world

    if tilt is not None:
        obj.rotation_euler = tilt
        bpy.context.view_layer.update()

    corners = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
    center = sum(corners, Vector()) / 8
    radius = max((c - center).length for c in corners)

    cam_data = bpy.data.cameras.new("cam")
    cam_data.lens = 50
    cam = link(bpy.data.objects.new("cam", cam_data))
    el, az = math.radians(elevation), math.radians(azimuth)
    fov = 2 * math.atan(18 / 50)
    dist = radius / math.sin(fov / 2) * 1.05
    cam.location = center + Vector((math.cos(el) * math.sin(az), -math.cos(el) * math.cos(az), math.sin(el))) * dist
    cam.rotation_euler = (center - cam.location).to_track_quat("-Z", "Y").to_euler()
    sc.camera = cam

    def area(name, energy, loc, size):
        ld = bpy.data.lights.new(name, "AREA")
        ld.energy = energy
        ld.size = size
        lo = link(bpy.data.objects.new(name, ld))
        lo.location = center + Vector(loc) * dist
        lo.rotation_euler = (center - lo.location).to_track_quat("-Z", "Y").to_euler()

    area("key", 60 * dist * dist, (0.6, -0.8, 0.9), 0.6 * dist)
    area("fill", 20 * dist * dist, (-1.0, -0.5, 0.3), 0.8 * dist)
    area("rim", 45 * dist * dist, (-0.2, 1.0, 0.8), 0.4 * dist)

    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    if tilt is not None:
        obj.rotation_euler = (0, 0, 0)
    print("[make_models] rendered", path)


def finish(name, folder, stem, samples, **icon_kw):
    obj = join_all(name)
    smooth(obj)
    uv_unwrap(obj)
    out_dir = os.path.join(ASSETS, folder)
    os.makedirs(out_dir, exist_ok=True)
    bake(obj, os.path.join(out_dir, stem + ".png"))
    export_obj(obj, os.path.join(out_dir, stem + ".obj"))
    render_icon(obj, os.path.join(RAW, stem + "_raw.png"), samples, **icon_kw)


# ---------------------------------------------------------------- models

def build_molotov():
    reset()
    # Glass: dark amber-green bottle, liquid visibly darker/warmer below the fill line.
    g = M("glass", 0.15)
    z = g.xyz()[2]
    grime = g.ramp(g.noise(90), [(0.3, (1, 1, 1)), (0.8, (0.78, 0.74, 0.66))])
    body = g.mix(g.math("GREATER_THAN", z, 0.105), (0.30, 0.20, 0.06), (0.22, 0.30, 0.20))
    streak = g.ramp(g.noise(22, 2.0), [(0.45, (0, 0, 0)), (0.62, (1, 1, 1))])
    body = g.mix(g.math("MULTIPLY", streak, 0.25), body, (0.62, 0.66, 0.58))
    mul = g.n("ShaderNodeMix", data_type="RGBA", blend_type="MULTIPLY")
    mul.inputs[0].default_value = 1.0
    g.l(body, mul.inputs[6])
    g.l(grime, mul.inputs[7])
    glass = g.color(mul.outputs[2])

    r = M("rag", 0.95)
    soot = r.ramp(r.noise(60, 8.0), [(0.35, (0.80, 0.76, 0.66)), (0.62, (0.52, 0.47, 0.38)), (0.80, (0.16, 0.13, 0.10))])
    rz = r.xyz()[2]
    tip = r.math("GREATER_THAN", rz, 0.232)
    rag = r.color(r.mix(tip, soot, (0.10, 0.08, 0.06)))

    profile = [(0.0, 0.0), (0.029, 0.0), (0.0335, 0.004), (0.034, 0.012), (0.034, 0.138), (0.032, 0.150),
               (0.026, 0.162), (0.018, 0.172), (0.0125, 0.182), (0.0120, 0.208), (0.0140, 0.210),
               (0.0140, 0.218), (0.0110, 0.220), (0.0, 0.220)]
    bottle = lathe("bottle", profile, 48)
    set_mat(bottle, glass)

    wrap = lathe("wrap", [(0.0, 0.186), (0.0150, 0.186), (0.0158, 0.192), (0.0152, 0.200), (0.0160, 0.208),
                          (0.0162, 0.216), (0.0, 0.216)], 24)
    set_mat(wrap, rag)
    # Rag tail stuffed in the neck, hanging out over the lip.
    for i, (ang, lean, h) in enumerate(((0, 18, 0.040), (120, -14, 0.034), (230, 10, 0.030))):
        a = math.radians(ang)
        tail = box("tail%d" % i, (0.012, 0.0035, h),
                   (0.004 * math.cos(a), 0.004 * math.sin(a), 0.218 + h / 2 - 0.004),
                   (math.radians(lean), 0, a))
        set_mat(tail, rag)
    finish("molotov", "Molotov", "molotov", args.samples, elevation=18, azimuth=30,
           tilt=(0, math.radians(-18), 0))


def build_contact():
    reset()
    src = os.path.join(ASSETS, "Grenade", "grenade.obj")
    bpy.ops.wm.obj_import(filepath=src, forward_axis="NEGATIVE_Z", up_axis="Y")
    obj = bpy.context.selected_objects[0]
    bpy.context.view_layer.objects.active = obj
    # The importer puts the Y-up -> Z-up conversion in the object's rotation; bake it into the
    # mesh so object-space Z (used for the bands below) is height, 0..0.10.
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    m = M("contact", 0.55)
    tex = m.n("ShaderNodeTexImage")
    tex.image = bpy.data.images.load(os.path.join(ASSETS, "Grenade", "grenade.png"))
    uv = m.n("ShaderNodeUVMap")
    m.l(uv.outputs["UV"], tex.inputs["Vector"])
    z = m.xyz()[2]
    lum = m.n("ShaderNodeRGBToBW")
    m.l(tex.outputs["Color"], lum.inputs[0])
    # Keep the original's shading/wear by tinting with its luminance rather than painting flat.
    shade = m.math("ADD", m.math("MULTIPLY", lum.outputs[0], 1.6), 0.25)
    red = m.n("ShaderNodeMix", data_type="RGBA", blend_type="MULTIPLY")
    red.inputs[0].default_value = 1.0
    red.inputs[6].default_value = lin((0.62, 0.07, 0.04)) + (1,)
    shade_rgb = m.n("ShaderNodeCombineXYZ")
    for i in range(3):
        m.l(shade, shade_rgb.inputs[i])
    m.l(shade_rgb.outputs[0], red.inputs[7])
    yellow = m.n("ShaderNodeMix", data_type="RGBA", blend_type="MULTIPLY")
    yellow.inputs[0].default_value = 1.0
    yellow.inputs[6].default_value = lin((0.85, 0.62, 0.05)) + (1,)
    m.l(shade_rgb.outputs[0], yellow.inputs[7])
    col = m.mix(m.math("MULTIPLY", m.band(z, 0.050, 0.064), 0.9), tex.outputs["Color"], red.outputs[2])
    col = m.mix(m.math("MULTIPLY", m.math("GREATER_THAN", z, 0.086), 0.85), col, yellow.outputs[2])
    set_mat(obj, m.color(col))

    out_dir = os.path.join(ASSETS, "ContactGrenade")
    os.makedirs(out_dir, exist_ok=True)
    # Bake through the grenade's OWN UVs - the in-game mesh is grenade.obj itself.
    bake(obj, os.path.join(out_dir, "contact_grenade.png"))
    render_icon(obj, os.path.join(RAW, "contact_grenade_raw.png"), args.samples)


def build_mine():
    reset()
    body = M("olive", 0.7)
    wear = body.ramp(body.noise(55, 8.0, 0.7), [(0.60, (0, 0, 0)), (0.72, (1, 1, 1))])
    base = body.mix(body.ramp(body.noise(240), [(0.3, (1, 1, 1)), (0.7, (0.8, 0.8, 0.8))]), (0.20, 0.24, 0.13), (0.29, 0.33, 0.18))
    base = body.mix(body.math("MULTIPLY", wear, 0.8), base, (0.42, 0.40, 0.36))
    rz = body.radius_xy()
    z = body.xyz()[2]
    ring = body.math("MULTIPLY", body.band(rz, 0.055, 0.061), body.math("GREATER_THAN", z, 0.040))
    base = body.mix(ring, base, (0.78, 0.62, 0.08))
    stencil = body.math("MULTIPLY", body.band(rz, 0.064, 0.0665), body.math("GREATER_THAN", z, 0.040))
    olive = body.color(body.mix(stencil, base, (0.78, 0.62, 0.08)))
    black = flat("plate", (0.05, 0.05, 0.05), 0.8, 0.4, 300)
    steel = flat("steel", (0.30, 0.30, 0.28), 0.45, 0.35, 260)

    disc = lathe("disc", [(0.0, 0.0), (0.074, 0.0), (0.080, 0.006), (0.080, 0.036), (0.076, 0.042),
                          (0.066, 0.045), (0.040, 0.045), (0.0, 0.045)], 64)
    set_mat(disc, olive)
    plate = lathe("plate", [(0.0, 0.045), (0.036, 0.045), (0.036, 0.050), (0.032, 0.053), (0.0, 0.053)], 48)
    set_mat(plate, black)
    for i in range(12):
        a = 2 * math.pi * i / 12
        rib = box("rib%d" % i, (0.016, 0.003, 0.0025), (0.022 * math.cos(a), 0.022 * math.sin(a), 0.0540), (0, 0, a))
        set_mat(rib, black)
    fuze = cylinder("fuze", 0.009, 0.014, 0.0, 20, loc=(0.084, 0.0, 0.022), rot=(0, math.radians(90), 0))
    fuze.location = (0.086, 0, 0.022)
    set_mat(fuze, steel)
    cap = cylinder("cap", 0.006, 0.006, 0.0, 16, loc=(0.0, 0.0, 0.0), rot=(0, math.radians(90), 0))
    cap.location = (0.095, 0, 0.022)
    set_mat(cap, black)
    finish("mine", "Mine", "mine", args.samples, elevation=32, azimuth=30)


def build_improvised():
    reset()
    tin = M("tin", 0.5)
    rust = tin.ramp(tin.noise(40, 10.0, 0.75), [(0.45, (0.55, 0.56, 0.55)), (0.60, (0.46, 0.30, 0.18)), (0.78, (0.30, 0.15, 0.07))])
    tin_mat = tin.color(rust)
    tape = flat("tape", (0.42, 0.43, 0.44), 0.85, 0.3, 120)
    nail = flat("nail", (0.18, 0.18, 0.19), 0.5, 0.3, 400)
    red = flat("wire_red", (0.65, 0.05, 0.04), 0.4, 0.15)
    blk = flat("wire_black", (0.04, 0.04, 0.04), 0.4, 0.15)
    spring_m = flat("spring", (0.40, 0.38, 0.30), 0.45, 0.3, 400)

    can = lathe("can", [(0.0, 0.0), (0.038, 0.0), (0.040, 0.003), (0.040, 0.097), (0.041, 0.100),
                        (0.0385, 0.100), (0.0385, 0.096), (0.0, 0.096)], 48)
    set_mat(can, tin_mat)
    for z in (0.012, 0.086):
        rim = torus("ridge%.3f" % z, 0.0402, 0.0016, z, 48, 6)
        set_mat(rim, tin_mat)
    band = lathe("tape", [(0.0412, 0.034), (0.0416, 0.036), (0.0418, 0.050), (0.0416, 0.064), (0.0412, 0.066)],
                 48, cap_bottom=False, cap_top=False)
    set_mat(band, tape)

    import random
    rnd = random.Random(7)
    for i in range(14):
        top = i < 5
        if top:
            a = rnd.uniform(0, 2 * math.pi)
            rr = rnd.uniform(0.012, 0.030)
            base = Vector((rr * math.cos(a), rr * math.sin(a), 0.096))
            direction = Vector((rnd.uniform(-0.25, 0.25), rnd.uniform(-0.25, 0.25), 1)).normalized()
        else:
            a = 2 * math.pi * (i - 5) / 9 + rnd.uniform(-0.2, 0.2)
            z = rnd.choice((0.022, 0.028, 0.074, 0.080)) + rnd.uniform(-0.003, 0.003)
            base = Vector((0.038 * math.cos(a), 0.038 * math.sin(a), z))
            direction = Vector((math.cos(a), math.sin(a), rnd.uniform(-0.2, 0.3))).normalized()
        length = rnd.uniform(0.018, 0.028)
        mid = base + direction * (length / 2 - 0.004)
        rot = direction.to_track_quat("Z", "Y").to_euler()
        shank = cylinder("shank%d" % i, 0.0015, length, -length / 2, 8, loc=(0, 0, 0))
        shank.location = mid
        shank.rotation_euler = rot
        set_mat(shank, nail)
        head = cylinder("head%d" % i, 0.0038, 0.0012, -0.0006, 12, loc=(0, 0, 0))
        head.location = base + direction * (length - 0.004)
        head.rotation_euler = rot
        set_mat(head, nail)

    for k in range(4):
        coil = torus("coil%d" % k, 0.009, 0.0014, 0.099 + k * 0.0035, 24, 6)
        set_mat(coil, spring_m)
    trigger = cylinder("trigger", 0.014, 0.003, 0.113, 24)
    set_mat(trigger, nail)

    tube_curve("wireR", [(0.006, 0.0, 0.106), (0.030, 0.012, 0.104), (0.043, 0.010, 0.080), (0.0425, 0.004, 0.064)], 0.0019)
    set_mat(bpy.context.active_object, red)
    tube_curve("wireB", [(-0.004, 0.005, 0.106), (0.020, 0.030, 0.100), (0.033, 0.029, 0.078), (0.031, 0.028, 0.064)], 0.0019)
    set_mat(bpy.context.active_object, blk)
    finish("improvised_mine", "ImprovisedMine", "improvised_mine", args.samples, elevation=24, azimuth=40)


BUILDERS = {"molotov": build_molotov, "contact": build_contact, "mine": build_mine, "improvised": build_improvised}
for key in args.only.split(","):
    print("[make_models] ===", key)
    BUILDERS[key.strip()]()
print("[make_models] done")
