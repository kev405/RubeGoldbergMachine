using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Fan.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL del ventilador vintage (hierro esmaltado + latón):
    ///   Aspa1 / Aspa2 -> par de palas de latón con torsión (Aspa1 lleva además el buje). Siguen siendo hijas
    ///                    de "Aspas", el Transform que FanZone hace girar, así que giran igual que antes.
    ///   Motor         -> carcasa con aletas + eje + rejilla de latón (fija, no gira).
    ///   Poste         -> base pesada, tubo, collarín y rótula.
    /// Solo cambian MeshFilter y MeshRenderer. El BoxCollider trigger (zona de viento) y FanZone no se tocan.
    /// Cada pieza se modela en metros en el marco del ventilador (o de Aspas) y se lleva al espacio local
    /// del objeto con su matriz, así que funciona aunque tengan rotación o escala no uniforme.
    /// </summary>
    public static class FanSetup
    {
        const string Root = "Assets/Fan";
        const string TexDir = Root + "/Textures";
        const string MeshDir = Root + "/Mesh";
        const string MatDir = Root + "/Materials";
        const string OriginalBodyMat = "90e2075ce89ef964aab0798289e22809";
        const string OriginalBladeMat = "5a75fa75770df8a42bd280847fd557e8";

        const float AxisY = 0.8f;          // altura del eje del motor (= Aspas)
        const float CageR = 0.63f;
        const float BladeTip = 0.56f;

        // --------------------------------------------------------------------
        [MenuItem("Tools/Fan/Apply to Ventilador (visual only)", priority = 0)]
        public static void Apply()
        {
            var zone = FindFan();
            if (zone == null) return;
            Transform fan = zone.transform;
            Transform blades = new SerializedObject(zone).FindProperty("blades").objectReferenceValue as Transform;
            if (blades == null) blades = fan.Find("Aspas");
            if (blades == null) { Debug.LogError("[Fan] FanZone no tiene asignado 'Blades' y no encontré 'Aspas'."); return; }

            ConfigureImporters();
            Material brass = Textured("M_FanBrass", "FanBrass");
            Material enamel = Textured("M_FanEnamel", "FanEnamel");
            string before = Snapshot(fan);

            Transform motor = fan.Find("Motor");
            if (motor != null)
            {
                var b = new Builder(motor.worldToLocalMatrix * fan.localToWorldMatrix);
                BuildMotor(b);
                SetVisual(motor, Save(b.Build("FanMotor"), "FanMotor"), new[] { enamel, brass });
            }
            Transform pole = fan.Find("Poste");
            if (pole != null)
            {
                var b = new Builder(pole.worldToLocalMatrix * fan.localToWorldMatrix);
                BuildPole(b, motor != null ? motor.localPosition.z : -0.3f);
                SetVisual(pole, Save(b.Build("FanPole"), "FanPole"), new[] { enamel, brass });
            }
            var bladeParts = blades.Cast<Transform>().Where(t => t.GetComponent<MeshFilter>() != null).ToList();
            for (int i = 0; i < bladeParts.Count; i++)
            {
                Transform part = bladeParts[i];
                var b = new Builder(part.worldToLocalMatrix * blades.localToWorldMatrix);
                Vector3 axis = (part.localRotation * Vector3.up).normalized;      // eje largo de la barra original
                BuildBladePair(b, axis, withHub: i == 0);
                string name = i == 0 ? "FanBladesHub" : $"FanBlades{i + 1}";
                SetVisual(part, Save(b.Build(name), name), new[] { brass, enamel });
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(fan.gameObject.scene);
            string after = Snapshot(fan);
            if (before == after) Debug.Log($"[Fan] Listo. Física sin cambios: {after}", fan);
            else Debug.LogError($"[Fan] ¡Algo de la física cambió! Antes: {before} / Después: {after}", fan);
        }

        [MenuItem("Tools/Fan/Revert Ventilador to primitives", priority = 1)]
        public static void Revert()
        {
            var zone = FindFan();
            if (zone == null) return;
            Transform fan = zone.transform;
            Mesh Prim(PrimitiveType p)
            {
                var g = GameObject.CreatePrimitive(p);
                var m = g.GetComponent<MeshFilter>().sharedMesh;
                Object.DestroyImmediate(g);
                return m;
            }
            Material Mat(string guid) => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            foreach (string n in new[] { "Poste", "Motor" })
            {
                Transform t = fan.Find(n);
                if (t != null) SetVisual(t, Prim(PrimitiveType.Cylinder), new[] { Mat(OriginalBodyMat) });
            }
            Transform blades = new SerializedObject(zone).FindProperty("blades").objectReferenceValue as Transform;
            if (blades != null)
                foreach (Transform t in blades)
                    if (t.GetComponent<MeshFilter>() != null) SetVisual(t, Prim(PrimitiveType.Cube), new[] { Mat(OriginalBladeMat) });
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(fan.gameObject.scene);
            Debug.Log("[Fan] Ventilador revertido a primitivas.", fan);
        }

        // --------------------------------------------------------------------
        static FanZone FindFan()
        {
            var sel = Selection.activeGameObject;
            FanZone z = sel != null ? sel.GetComponentInParent<FanZone>() : null;
            if (z == null) z = Object.FindObjectsByType<FanZone>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            if (z == null) Debug.LogError("[Fan] No hay ningún FanZone en la escena.");
            return z;
        }

        static void SetVisual(Transform t, Mesh mesh, Material[] mats)
        {
            var mf = t.GetComponent<MeshFilter>();
            var mr = t.GetComponent<MeshRenderer>();
            if (mf == null || mr == null) return;
            Undo.RecordObjects(new Object[] { mf, mr }, "Ventilador: aspecto");
            mf.sharedMesh = mesh;
            if (mats != null && mats.All(m => m != null)) mr.sharedMaterials = mats;
            EditorUtility.SetDirty(mf);
            EditorUtility.SetDirty(mr);
        }

        static string Snapshot(Transform fan)
        {
            var sb = new System.Text.StringBuilder();
            var so = new SerializedObject(fan.GetComponent<FanZone>());
            sb.Append($"FanZone fuerza {so.FindProperty("force").floatValue}, velocidad {so.FindProperty("bladeSpeed").floatValue}; ");
            foreach (var c in fan.GetComponentsInChildren<Collider>(true))
            {
                sb.Append($"{c.name}:{c.GetType().Name} trigger={c.isTrigger}");
                if (c is BoxCollider bc) sb.Append($"[{bc.size} @ {bc.center}]");
                sb.Append("; ");
            }
            foreach (var t in fan.GetComponentsInChildren<Transform>(true))
                sb.Append($"{t.name} {t.localPosition} {t.localEulerAngles} {t.localScale}; ");
            return sb.ToString();
        }

        // --------------------------------------------------------------------
        class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int>[] Sub = { new List<int>(), new List<int>() };
            readonly Matrix4x4 m, mn;
            public Builder(Matrix4x4 toLocal) { m = toLocal; mn = toLocal.inverse.transpose; }

            public int Add(Vector3 p, Vector3 n, Vector2 uv)
            {
                V.Add(m.MultiplyPoint3x4(p));
                N.Add(mn.MultiplyVector(n).normalized);
                UV.Add(uv);
                return V.Count - 1;
            }

            public void Tri(int sub, int a, int b, int c)
            {
                Vector3 n = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
                if (n.sqrMagnitude < 1e-18f) return;
                if (Vector3.Dot(n, N[a] + N[b] + N[c]) >= 0f) Sub[sub].AddRange(new[] { a, b, c });
                else Sub[sub].AddRange(new[] { a, c, b });
            }

            public void Quad(int sub, int bl, int br, int tl, int tr) { Tri(sub, bl, tl, tr); Tri(sub, bl, tr, br); }

            /// Torneado alrededor de un eje: perfil (radio, altura a lo largo del eje).
            public void Lathe(int sub, Vector3 origin, Vector3 axis, IList<Vector2> rh, int seg = 64)
            {
                axis = axis.normalized;
                Vector3 e1 = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 e2 = Vector3.Cross(axis, e1);
                int n = rh.Count, start = V.Count;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = rh[Mathf.Max(i - 1, 0)], b = rh[Mathf.Min(i + 1, n - 1)];
                    Vector2 tan = (b - a).normalized;
                    Vector2 pn = new Vector2(-tan.y, tan.x) * Orient(rh);   // normal hacia fuera del sólido
                    for (int j = 0; j <= seg; j++)
                    {
                        float u = j / (float)seg, th = u * Mathf.PI * 2f;
                        Vector3 rad = e1 * Mathf.Cos(th) + e2 * Mathf.Sin(th);
                        Add(origin + axis * rh[i].y + rad * rh[i].x, rad * pn.x + axis * pn.y, new Vector2(u, i / (float)(n - 1)));
                    }
                }
                int row = seg + 1;
                for (int i = 0; i < n - 1; i++)
                    for (int j = 0; j < seg; j++)
                    {
                        int bl = start + i * row + j;
                        Quad(sub, bl, bl + 1, bl + row, bl + row + 1);
                    }
            }

            public void Tube(int sub, IList<Vector3> path, float radius, int sides = 8)
            {
                int n = path.Count, start = V.Count;
                Vector3 prev = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    Vector3 t = (path[Mathf.Min(i + 1, n - 1)] - path[Mathf.Max(i - 1, 0)]).normalized;
                    Vector3 nr = i == 0 ? Vector3.Cross(t, Mathf.Abs(t.y) < 0.9f ? Vector3.up : Vector3.right).normalized
                                        : (prev - Vector3.Dot(prev, t) * t).normalized;
                    Vector3 bn = Vector3.Cross(t, nr);
                    prev = nr;
                    for (int j = 0; j <= sides; j++)
                    {
                        float a = j / (float)sides * Mathf.PI * 2f;
                        Vector3 d = nr * Mathf.Cos(a) + bn * Mathf.Sin(a);
                        Add(path[i] + d * radius, d, new Vector2(i / (float)(n - 1), j / (float)sides));
                    }
                }
                int row = sides + 1;
                for (int i = 0; i < n - 1; i++)
                    for (int j = 0; j < sides; j++)
                    {
                        int bl = start + i * row + j;
                        Quad(sub, bl, bl + 1, bl + row, bl + row + 1);
                    }
            }

            public void Ring(int sub, Vector3 center, Vector3 axis, float R, float r, int seg = 96)
            {
                axis = axis.normalized;
                Vector3 e1 = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 e2 = Vector3.Cross(axis, e1);
                var path = new List<Vector3>();
                for (int j = 0; j <= seg; j++)
                {
                    float th = j / (float)seg * Mathf.PI * 2f;
                    path.Add(center + (e1 * Mathf.Cos(th) + e2 * Mathf.Sin(th)) * R);
                }
                Tube(sub, path, r, 8);
            }

            public Mesh Build(string name)
            {
                var mesh = new Mesh { name = name };
                if (V.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(V);
                mesh.SetNormals(N);
                mesh.SetUVs(0, UV);
                mesh.subMeshCount = Sub.Length;
                for (int i = 0; i < Sub.Length; i++) mesh.SetTriangles(Sub[i], i);
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        // ---------------- piezas (metros, marco del ventilador: +Z = hacia donde sopla) ----------------
        static void BuildMotor(Builder b)
        {
            Vector3 o = new Vector3(0f, AxisY, 0f);
            var prof = new List<Vector2>
            {
                new Vector2(0f, -0.47f), new Vector2(0.06f, -0.465f), new Vector2(0.10f, -0.45f),
                new Vector2(0.13f, -0.425f), new Vector2(0.145f, -0.40f), new Vector2(0.15f, -0.39f),
            };
            for (int k = 0; k < 6; k++)                           // aletas de enfriamiento
            {
                float z = -0.375f + k * 0.026f;
                prof.Add(new Vector2(0.150f, z));
                prof.Add(new Vector2(0.163f, z + 0.004f));
                prof.Add(new Vector2(0.163f, z + 0.012f));
                prof.Add(new Vector2(0.150f, z + 0.016f));
            }
            prof.AddRange(new[]
            {
                new Vector2(0.15f, -0.21f), new Vector2(0.14f, -0.18f), new Vector2(0.11f, -0.158f),
                new Vector2(0.07f, -0.145f), new Vector2(0.03f, -0.137f), new Vector2(0.013f, -0.132f),
                new Vector2(0.012f, -0.13f), new Vector2(0.012f, -0.025f), new Vector2(0f, -0.025f),
            });
            b.Lathe(0, o, Vector3.forward, prof, 64);

            // Rejilla de latón (submalla 1): aro, alambres radiales y anillos delante y detrás
            b.Ring(1, o, Vector3.forward, CageR, 0.009f, 128);
            float Front(float r) => 0.13f * Mathf.Sqrt(Mathf.Max(0f, 1f - (r / CageR) * (r / CageR)));
            float Back(float r) => -0.16f * Mathf.Sqrt(Mathf.Max(0f, 1f - (r / CageR) * (r / CageR)));
            for (int k = 0; k < 24; k++)
            {
                float th = k / 24f * Mathf.PI * 2f;
                Vector3 d = new Vector3(Mathf.Cos(th), Mathf.Sin(th), 0f);
                var front = new List<Vector3>();
                for (int i = 0; i <= 14; i++)
                {
                    float r = Mathf.Lerp(CageR, 0.07f, i / 14f);
                    front.Add(o + d * r + Vector3.forward * Front(r));
                }
                b.Tube(1, front, 0.0045f, 6);
                if (k % 3 == 0) continue;                         // detrás: 16 alambres
                var back = new List<Vector3>();
                for (int i = 0; i <= 12; i++)
                {
                    float r = Mathf.Lerp(CageR, 0.12f, i / 12f);
                    back.Add(o + d * r + Vector3.forward * Back(r));
                }
                b.Tube(1, back, 0.0045f, 6);
            }
            foreach (float r in new[] { 0.16f, 0.30f, 0.45f, 0.56f }) b.Ring(1, o + Vector3.forward * Front(r), Vector3.forward, r, 0.004f);
            foreach (float r in new[] { 0.30f, 0.48f }) b.Ring(1, o + Vector3.forward * Back(r), Vector3.forward, r, 0.004f);
            // medallón frontal
            b.Lathe(1, o, Vector3.forward, new List<Vector2>
            {
                new Vector2(0f, 0.142f), new Vector2(0.05f, 0.140f), new Vector2(0.068f, 0.134f),
                new Vector2(0.075f, 0.126f), new Vector2(0.074f, 0.118f), new Vector2(0.06f, 0.116f), new Vector2(0f, 0.116f),
            }, 48);
            // aro que une la rejilla trasera al motor
            b.Ring(1, o + Vector3.forward * Back(0.12f), Vector3.forward, 0.12f, 0.006f, 64);
        }

        static void BuildPole(Builder b, float poleZ)
        {
            Vector3 foot = new Vector3(0f, 0f, poleZ);
            b.Lathe(0, foot, Vector3.up, new List<Vector2>
            {
                new Vector2(0f, 0.075f), new Vector2(0.045f, 0.073f), new Vector2(0.10f, 0.066f), new Vector2(0.17f, 0.052f),
                new Vector2(0.23f, 0.037f), new Vector2(0.27f, 0.024f), new Vector2(0.285f, 0.012f), new Vector2(0.287f, 0f), new Vector2(0f, 0f),
            }, 72);
            b.Ring(1, foot + Vector3.up * 0.026f, Vector3.up, 0.262f, 0.007f, 96);   // filete de latón en la base
            float top = AxisY - 0.16f;
            b.Lathe(0, foot, Vector3.up, new List<Vector2>
            {
                new Vector2(0.032f, 0.07f), new Vector2(0.022f, 0.10f), new Vector2(0.022f, top - 0.03f), new Vector2(0.03f, top - 0.02f),
            }, 32);
            b.Lathe(1, foot, Vector3.up, new List<Vector2>                                  // collarín de latón
            {
                new Vector2(0.026f, top - 0.075f), new Vector2(0.033f, top - 0.07f), new Vector2(0.033f, top - 0.05f), new Vector2(0.026f, top - 0.045f),
            }, 32);
            // rótula horizontal bajo el motor y perilla de ajuste
            Vector3 knuckle = new Vector3(0f, top - 0.005f, poleZ);
            b.Lathe(0, knuckle + Vector3.left * 0.05f, Vector3.right, new List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(0.034f, 0.002f), new Vector2(0.036f, 0.01f), new Vector2(0.036f, 0.09f), new Vector2(0.034f, 0.098f), new Vector2(0f, 0.1f),
            }, 32);
            b.Lathe(1, knuckle + Vector3.right * 0.05f, Vector3.right, new List<Vector2>
            {
                new Vector2(0.012f, 0f), new Vector2(0.012f, 0.012f), new Vector2(0.026f, 0.016f), new Vector2(0.026f, 0.03f), new Vector2(0f, 0.034f),
            }, 24);
        }

        // Par de palas opuestas a lo largo de 'axis' (en el plano XY de Aspas), con torsión.
        static void BuildBladePair(Builder b, Vector3 axis, bool withHub)
        {
            axis = new Vector3(axis.x, axis.y, 0f).normalized;
            const int nr = 36, nc = 8;
            const float r0 = 0.06f, thick = 0.0028f;
            foreach (float sgn in new[] { 1f, -1f })
            {
                Vector3 d = axis * sgn;
                Vector3 side = Vector3.Cross(Vector3.forward, d);       // dirección de la cuerda (en el plano)
                var top = new int[nr + 1, nc + 1];
                var bot = new int[nr + 1, nc + 1];
                for (int i = 0; i <= nr; i++)
                {
                    float t = i / (float)nr, r = Mathf.Lerp(r0, BladeTip, t);
                    float chord = r < 0.13f ? 0.035f : 0.05f + 0.17f * Mathf.Sin(Mathf.PI * 0.5f * Mathf.Clamp01((r - 0.12f) / 0.30f));
                    if (r > 0.46f) chord *= Mathf.Sqrt(Mathf.Max(0.02f, 1f - Mathf.Pow((r - 0.46f) / (BladeTip - 0.46f), 2f)));
                    float pitch = Mathf.Lerp(32f, 18f, t) * Mathf.Deg2Rad;
                    Vector3 c = side * Mathf.Cos(pitch) + Vector3.forward * Mathf.Sin(pitch);
                    Vector3 n = Vector3.Cross(c, d).normalized;
                    if (n.z < 0f) n = -n;
                    float sweep = 0.025f * t * t;
                    float bow = 0.008f * Mathf.Sin(Mathf.PI * t);       // leve curvatura de la pala
                    for (int j = 0; j <= nc; j++)
                    {
                        float s = j / (float)nc - 0.5f;
                        Vector3 p = d * r + c * (s * chord + sweep) + n * bow * (1f - 4f * s * s);
                        Vector2 uv = new Vector2(t, j / (float)nc);
                        top[i, j] = b.Add(p + n * thick, n, uv);
                        bot[i, j] = b.Add(p - n * thick, -n, uv);
                    }
                }
                for (int i = 0; i < nr; i++)
                    for (int j = 0; j < nc; j++)
                    {
                        b.Quad(0, top[i, j], top[i, j + 1], top[i + 1, j], top[i + 1, j + 1]);
                        b.Quad(0, bot[i, j], bot[i, j + 1], bot[i + 1, j], bot[i + 1, j + 1]);
                    }
                // cantos (bordes de ataque/salida y punta) con normal hacia fuera
                for (int i = 0; i < nr; i++)
                    foreach (int j in new[] { 0, nc })
                        EdgeQuad(b, top[i, j], top[i + 1, j], bot[i, j], bot[i + 1, j], top[i, j == 0 ? 1 : nc - 1]);
                for (int j = 0; j < nc; j++)
                    EdgeQuad(b, top[nr, j], top[nr, j + 1], bot[nr, j], bot[nr, j + 1], top[nr - 1, j]);
            }
            if (withHub)
                b.Lathe(0, Vector3.zero, Vector3.forward, new List<Vector2>
                {
                    new Vector2(0f, 0.058f), new Vector2(0.025f, 0.055f), new Vector2(0.045f, 0.043f), new Vector2(0.058f, 0.022f),
                    new Vector2(0.064f, 0.0f), new Vector2(0.064f, -0.022f), new Vector2(0.05f, -0.032f), new Vector2(0f, -0.032f),
                }, 48);
        }

        // Cara del canto: duplica los vértices con una normal lateral para que el borde se vea nítido
        static void EdgeQuad(Builder b, int t0, int t1, int b0, int b1, int interior)
        {
            Vector3 p0 = b.V[t0], p1 = b.V[t1], q0 = b.V[b0], q1 = b.V[b1];
            Vector3 mid = (p0 + p1 + q0 + q1) * 0.25f;
            Vector3 outDir = Vector3.Cross(p1 - p0, q0 - p0).normalized;
            if (Vector3.Dot(outDir, mid - b.V[interior]) < 0f) outDir = -outDir;   // siempre hacia fuera de la pala
            // Los vértices ya están en espacio local: se añaden sin transformar
            int a = AddLocal(b, p0, outDir), c = AddLocal(b, p1, outDir), d = AddLocal(b, q0, outDir), e = AddLocal(b, q1, outDir);
            b.Quad(0, a, c, d, e);
        }

        static int AddLocal(Builder b, Vector3 local, Vector3 n)
        {
            b.V.Add(local);
            b.N.Add(n);
            b.UV.Add(new Vector2(0.5f, 0.5f));
            return b.V.Count - 1;
        }

        static Mesh Save(Mesh m, string name)
        {
            EnsureFolder(MeshDir);
            string path = $"{MeshDir}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(m, existing);
                Object.DestroyImmediate(m);
                m = existing;
            }
            else AssetDatabase.CreateAsset(m, path);
            EditorUtility.SetDirty(m);
            AssetDatabase.SaveAssets();
            return m;
        }

        // --------------------------------------------------------------------
        static void ConfigureImporters()
        {
            foreach (string prefix in new[] { "FanBrass", "FanEnamel" })
                foreach (string suffix in new[] { "basecolor", "normal", "metallic_smoothness" })
                {
                    if (!(AssetImporter.GetAtPath($"{TexDir}/{prefix}_{suffix}.png") is TextureImporter ti))
                    {
                        Debug.LogWarning($"[Fan] Falta {TexDir}/{prefix}_{suffix}.png");
                        continue;
                    }
                    ti.textureType = suffix == "normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    ti.sRGBTexture = suffix == "basecolor";
                    if (suffix == "metallic_smoothness")
                    {
                        ti.alphaSource = TextureImporterAlphaSource.FromInput;
                        ti.alphaIsTransparency = false;
                    }
                    ti.wrapMode = TextureWrapMode.Repeat;            // texturas repetibles
                    ti.mipmapEnabled = true;
                    ti.filterMode = FilterMode.Trilinear;
                    ti.anisoLevel = 8;
                    ti.maxTextureSize = 1024;
                    ti.textureCompression = TextureImporterCompression.CompressedHQ;
                    ti.SaveAndReimport();
                }
        }

        static Material Textured(string name, string prefix)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            bool urp = shader != null;
            if (!urp) shader = Shader.Find("Standard");
            EnsureFolder(MatDir);
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
            else mat.shader = shader;
            var baseMap = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{prefix}_basecolor.png");
            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{prefix}_normal.png");
            var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{prefix}_metallic_smoothness.png");
            if (urp)
            {
                mat.SetFloat("_WorkflowMode", 1f);
                mat.SetTexture("_BaseMap", baseMap);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetTexture("_BumpMap", nrm);
                mat.SetFloat("_BumpScale", 1f);
                mat.SetTexture("_MetallicGlossMap", ms);
                mat.SetFloat("_Smoothness", 1f);
                mat.SetFloat("_SmoothnessTextureChannel", 0f);
                Keyword(mat, "_NORMALMAP", nrm != null);
                Keyword(mat, "_METALLICSPECGLOSSMAP", ms != null);
            }
            else
            {
                mat.SetTexture("_MainTex", baseMap);
                mat.SetTexture("_BumpMap", nrm);
                mat.SetTexture("_MetallicGlossMap", ms);
                mat.SetFloat("_GlossMapScale", 1f);
                Keyword(mat, "_NORMALMAP", nrm != null);
                Keyword(mat, "_METALLICGLOSSMAP", ms != null);
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// Sentido del perfil (radio, altura) cerrado por el eje: +1 si va en sentido horario
        /// (arriba -> por fuera -> abajo), -1 si va al revés. Así las normales siempre apuntan hacia fuera
        /// del sólido, sin importar en qué orden se escribieron los puntos.
        static float Orient(IList<Vector2> rh)
        {
            var pts = new List<Vector2>(rh) { new Vector2(0f, rh[rh.Count - 1].y), new Vector2(0f, rh[0].y) };
            float area = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % pts.Count];
                area += a.x * b.y - b.x * a.y;
            }
            return area > 0f ? -1f : 1f;
        }

        static void Keyword(Material m, string kw, bool on)
        {
            if (on) m.EnableKeyword(kw); else m.DisableKeyword(kw);
        }

        static void EnsureFolder(string path)
        {
            path = path.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
