"""
make_bowling_ball.py  -  Blender 4.2+ / 5.x
Bola de bolos: esfera de RADIO 0.5 (diámetro 1, igual que la esfera de Unity) con 3 agujeros
(2 dedos + pulgar) biselados, UV equirectangular sin costura visible y 2 materiales:
  slot 0 = superficie (textura de remolino), slot 1 = interior de los agujeros.

  blender --background --python make_bowling_ball.py -- --out ../export/BowlingBall.fbx [--save-blend ...]
"""
import math
import os
import sys

import bpy
import bmesh
from mathutils import Vector

R = 0.5
ARGS = {"out": os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "export", "BowlingBall.fbx"),
        "save_blend": ""}
if "--" in sys.argv:
    a = sys.argv[sys.argv.index("--") + 1:]
    for i in range(0, len(a) - 1, 2):
        ARGS[a[i].lstrip("-").replace("-", "_")] = a[i + 1]

# Agujeros: (nombre, dirección, radio, profundidad). Proporciones de una bola real (21,6 cm):
# dedos ~2,2 cm, pulgar ~2,5 cm, ~8 cm de separación pulgar-dedos.
GRIP = Vector((0.0, -1.0, 0.18)).normalized()          # centro del agarre (lejos de los polos)


def offset(direction: Vector, yaw_deg: float, pitch_deg: float) -> Vector:
    """Desplaza una dirección sobre la esfera: yaw alrededor de Z, pitch hacia arriba/abajo."""
    side = direction.cross(Vector((0, 0, 1))).normalized()
    up = side.cross(direction).normalized()
    d = (direction + side * math.tan(math.radians(yaw_deg)) + up * math.tan(math.radians(pitch_deg)))
    return d.normalized()


HOLES = [
    ("finger_L", offset(GRIP, -7.5, 12.0), 0.051, 0.30),
    ("finger_R", offset(GRIP, 7.5, 12.0), 0.051, 0.30),
    ("thumb", offset(GRIP, 0.0, -24.0), 0.060, 0.34),
]


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def sphere():
    bpy.ops.mesh.primitive_uv_sphere_add(segments=128, ring_count=64, radius=R, location=(0, 0, 0))
    ob = bpy.context.active_object
    ob.name = "BowlingBall"
    return ob


def drill(ob):
    for name, d, radius, depth in HOLES:
        length = depth + 0.08
        center = d * (R + 0.04 - length / 2)
        bpy.ops.mesh.primitive_cylinder_add(vertices=64, radius=radius, depth=length, location=center)
        cyl = bpy.context.active_object
        cyl.name = f"cut_{name}"
        cyl.rotation_mode = "QUATERNION"
        cyl.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d)
        mod = ob.modifiers.new(name, "BOOLEAN")
        mod.operation = "DIFFERENCE"
        mod.solver = "EXACT"
        mod.object = cyl
        bpy.context.view_layer.objects.active = ob
        bpy.ops.object.modifier_apply(modifier=mod.name)
        bpy.data.objects.remove(cyl, do_unlink=True)

    bev = ob.modifiers.new("RimBevel", "BEVEL")         # redondea el borde de los agujeros
    bev.limit_method = "ANGLE"
    bev.angle_limit = math.radians(40)
    bev.width = 0.007
    bev.segments = 4
    bev.harden_normals = False
    bpy.ops.object.modifier_apply(modifier=bev.name)


def materials_and_uv(ob):
    me = ob.data
    for n in ("M_BowlingBall", "M_BowlingBall_Holes"):
        me.materials.append(bpy.data.materials.get(n) or bpy.data.materials.new(n))

    bm = bmesh.new()
    bm.from_mesh(me)
    uv = bm.loops.layers.uv.verify()
    for f in bm.faces:
        c = f.calc_center_median()
        f.material_index = 1 if c.length < R - 0.004 else 0     # paredes y fondo de los agujeros

        us, vs, poles = [], [], []
        for lp in f.loops:
            p = lp.vert.co.normalized() if lp.vert.co.length > 1e-6 else Vector((1, 0, 0))
            lon = math.atan2(p.y, p.x)
            lat = math.asin(max(-1.0, min(1.0, p.z)))
            us.append(lon / (2 * math.pi) + 0.5)
            vs.append(lat / math.pi + 0.5)
            poles.append(abs(p.z) > 0.9999)
        if max(us) - min(us) > 0.5:                              # cara que cruza la costura: desenrollar
            us = [u + 1.0 if u < 0.5 else u for u in us]
        if any(poles) and not all(poles):                        # polo: u del promedio de sus vecinos
            avg = sum(u for u, p in zip(us, poles) if not p) / max(1, poles.count(False))
            us = [avg if p else u for u, p in zip(us, poles)]
        for lp, u, v in zip(f.loops, us, vs):
            lp[uv].uv = (u, v)
    bm.to_mesh(me)
    bm.free()

    # API de malla (funciona también sin interfaz): todo suave salvo aristas de más de 50°
    if hasattr(me, "set_sharp_from_angle"):              # Blender 4.1+
        me.shade_smooth()
        me.set_sharp_from_angle(angle=math.radians(50))
    else:                                                # Blender <= 4.0
        for poly in me.polygons:
            poly.use_smooth = True
        me.use_auto_smooth = True
        me.auto_smooth_angle = math.radians(50)


def export(ob, path):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.export_scene.fbx(filepath=os.path.abspath(path), use_selection=True, object_types={"MESH"},
                             use_mesh_modifiers=True, mesh_smooth_type="OFF", apply_unit_scale=True,
                             apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
                             bake_space_transform=True, add_leaf_bones=False, path_mode="STRIP")


def main():
    reset()
    ob = sphere()
    drill(ob)
    materials_and_uv(ob)
    me = ob.data
    sharp = me.attributes.get("sharp_face")
    flat = sum(d.value for d in sharp.data) if sharp else 0
    holes = sum(1 for p in me.polygons if p.material_index == 1)
    radii = [v.co.length for v in me.vertices]
    print(f"[ball] {len(me.vertices)} vértices, {len(me.polygons)} caras, {holes} en agujeros, "
          f"radio máx {max(radii):.4f}, caras planas {flat}")
    export(ob, ARGS["out"])
    print(f"[ball] FBX -> {os.path.abspath(ARGS['out'])}")
    if ARGS["save_blend"]:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(ARGS["save_blend"]))


if __name__ == "__main__":
    main()
