using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Car.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL del carrito:
    ///   Cuerpo  -> carrocería de racer clásico (loft de car_profile.json) + ejes + base del mástil + parabrisas.
    ///   Rueda*  -> neumático con labrado (wheel_profile.json) + rin cromado de 6 rayos + tapón rojo.
    /// Solo cambian MeshFilter y MeshRenderer. BoxCollider de Cuerpo, colliders del mástil, Rigidbody
    /// (masa, restricciones) y Transforms no se tocan. Las ruedas no tienen collider.
    /// Todo se modela en metros y se divide por la escala local de cada objeto.
    /// </summary>
    public static class CarSetup
    {
        const string Root = "Assets/Car";
        const string TexDir = Root + "/Textures";
        const string MeshDir = Root + "/Mesh";
        const string MatDir = Root + "/Materials";
        const string OriginalBodyMat = "87f840ac9657eb941b958418fe0c58e4";
        const string OriginalWheelMat = "70a4e3380e113d84e9762c3c931785f9";
        const int Around = 96;

        [System.Serializable] class BodyProfile { public float[] x, w, yb, yt, n; }
        [System.Serializable] class WheelProfile { public float[] r, y, v; }

        // --------------------------------------------------------------------
        [MenuItem("Tools/Car/Apply to Carrito (visual only)", priority = 0)]
        public static void Apply()
        {
            var bp = Load<BodyProfile>("car_profile.json");
            var wp = Load<WheelProfile>("wheel_profile.json");
            if (bp == null || wp == null) return;
            Transform car = FindCar();
            if (car == null) return;
            Transform body = car.Find("Cuerpo");
            if (body == null) { Debug.LogError("[Car] No encontré 'Cuerpo' dentro del carrito."); return; }

            ConfigureImporters();
            Material paint = Textured("M_CarPaint", "Car");
            Material tire = Textured("M_CarTire", "Tire");
            Material dark = Solid("M_CarDarkMetal", new Color(0.12f, 0.12f, 0.13f), 0.8f, 0.5f);
            Material glass = Solid("M_CarGlass", new Color(0.10f, 0.14f, 0.16f), 0.2f, 0.95f);
            Material chrome = Solid("M_CarChrome", new Color(0.86f, 0.86f, 0.88f), 1f, 0.92f);
            Material hub = Solid("M_CarHub", new Color(0.72f, 0.035f, 0.03f), 0f, 0.85f);

            string before = Snapshot(car);
            Mesh bodyMesh = Save(BuildBody(bp, body.localScale, body.localPosition, car), "CarBody");
            SetVisual(body, bodyMesh, new[] { paint, dark, glass });

            var wheels = car.Cast<Transform>().Where(t => t.name.StartsWith("Rueda")).ToList();
            if (wheels.Count > 0)
            {
                Mesh wheelMesh = Save(BuildWheel(wp, wheels[0].localScale), "CarWheel");
                foreach (var w in wheels) SetVisual(w, wheelMesh, new[] { tire, chrome, hub });
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(car.gameObject.scene);
            string after = Snapshot(car);
            if (before == after) Debug.Log($"[Car] Listo ({wheels.Count} ruedas). Física sin cambios: {after}", car);
            else Debug.LogError($"[Car] ¡La física cambió! Antes: {before} / Después: {after}", car);
        }

        [MenuItem("Tools/Car/Add wheel spin (visual only)", priority = 1)]
        public static void AddSpinner()
        {
            Transform car = FindCar();
            if (car == null) return;
            if (car.GetComponent<WheelSpinner>() == null) Undo.AddComponent<WheelSpinner>(car.gameObject);
            Debug.Log("[Car] WheelSpinner agregado: las ruedas giran según lo que avanza el carrito (no afecta la física).", car);
        }

        [MenuItem("Tools/Car/Revert Carrito to primitives", priority = 2)]
        public static void Revert()
        {
            Transform car = FindCar();
            if (car == null) return;
            Mesh Prim(PrimitiveType p)
            {
                var g = GameObject.CreatePrimitive(p);
                var m = g.GetComponent<MeshFilter>().sharedMesh;
                Object.DestroyImmediate(g);
                return m;
            }
            Material Mat(string guid) => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            Transform body = car.Find("Cuerpo");
            if (body != null) SetVisual(body, Prim(PrimitiveType.Cube), new[] { Mat(OriginalBodyMat) });
            foreach (Transform w in car)
                if (w.name.StartsWith("Rueda")) SetVisual(w, Prim(PrimitiveType.Cylinder), new[] { Mat(OriginalWheelMat) });
            var spinner = car.GetComponent<WheelSpinner>();
            if (spinner != null) Undo.DestroyObjectImmediate(spinner);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(car.gameObject.scene);
            Debug.Log("[Car] Carrito revertido a primitivas.", car);
        }

        // --------------------------------------------------------------------
        static T Load<T>(string file) where T : class
        {
            var ta = AssetDatabase.LoadAssetAtPath<TextAsset>($"{TexDir}/{file}");
            if (ta == null) { Debug.LogError($"[Car] Falta {TexDir}/{file}"); return null; }
            return JsonUtility.FromJson<T>(ta.text);
        }

        static Transform FindCar()
        {
            var sel = Selection.activeTransform;
            if (sel != null && sel.GetComponent<Rigidbody>() != null && sel.Find("Cuerpo") != null) return sel;
            var t = Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(r => r.transform).FirstOrDefault(x => x.name == "Carrito" && x.Find("Cuerpo") != null);
            if (t == null) Debug.LogError("[Car] No encontré 'Carrito' (con Rigidbody e hijo 'Cuerpo').");
            return t;
        }

        static void SetVisual(Transform t, Mesh mesh, Material[] mats)
        {
            var mf = t.GetComponent<MeshFilter>();
            var mr = t.GetComponent<MeshRenderer>();
            if (mf == null || mr == null) return;
            Undo.RecordObjects(new Object[] { mf, mr }, "Carrito: aspecto");
            mf.sharedMesh = mesh;
            if (mats != null && mats.All(m => m != null)) mr.sharedMaterials = mats;
            EditorUtility.SetDirty(mf);
            EditorUtility.SetDirty(mr);
        }

        static string Snapshot(Transform car)
        {
            var rb = car.GetComponent<Rigidbody>();
            var sb = new System.Text.StringBuilder();
            sb.Append($"masa {rb.mass}, restricciones {rb.constraints}; ");
            foreach (var c in car.GetComponentsInChildren<Collider>(true))
            {
                sb.Append($"{c.name}:{c.GetType().Name}");
                if (c is BoxCollider bc) sb.Append($"[{bc.size} @ {bc.center}]");
                sb.Append($" pos {c.transform.localPosition} esc {c.transform.localScale}; ");
            }
            return sb.ToString();
        }

        // --------------------------------------------------------------------
        class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int>[] Sub = { new List<int>(), new List<int>(), new List<int>() };
            readonly Vector3 inv, scale;
            public Vector3 Offset = Vector3.zero;     // se resta a las posiciones (en metros) antes de escalar
            public Builder(Vector3 s) { scale = s; inv = new Vector3(1f / s.x, 1f / s.y, 1f / s.z); }

            public int Add(Vector3 p, Vector3 n, Vector2 uv)
            {
                V.Add(Vector3.Scale(p - Offset, inv));
                N.Add(Vector3.Scale(n, scale).normalized);
                UV.Add(uv);
                return V.Count - 1;
            }

            public void Tri(int sub, int a, int b, int c)
            {
                Vector3 n = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
                if (n.sqrMagnitude < 1e-16f) return;
                if (Vector3.Dot(n, N[a] + N[b] + N[c]) >= 0f) Sub[sub].AddRange(new[] { a, b, c });
                else Sub[sub].AddRange(new[] { a, c, b });
            }

            public void Quad(int sub, int bl, int br, int tl, int tr) { Tri(sub, bl, tl, tr); Tri(sub, bl, tr, br); }

            public void Lathe(int sub, IList<Vector2> rh, IList<float> v, Vector2 vRange, float side = 1f)
            {
                int n = rh.Count, start = V.Count;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = rh[Mathf.Max(i - 1, 0)], b = rh[Mathf.Min(i + 1, n - 1)];
                    Vector2 tan = (b - a).normalized;
                    Vector2 pn = new Vector2(-tan.y, tan.x) * (Orient(rh) * side);   // side = -1: superficie que mira al eje
                    for (int j = 0; j <= Around; j++)
                    {
                        float u = j / (float)Around, th = u * Mathf.PI * 2f;
                        float c = Mathf.Cos(th), s = Mathf.Sin(th);
                        Add(new Vector3(rh[i].x * c, rh[i].y, rh[i].x * s), new Vector3(pn.x * c, pn.y, pn.x * s),
                            new Vector2(u, Mathf.Lerp(vRange.x, vRange.y, v[i])));
                    }
                }
                int row = Around + 1;
                for (int i = 0; i < n - 1; i++)
                    for (int j = 0; j < Around; j++)
                    {
                        int bl = start + i * row + j;
                        Quad(sub, bl, bl + 1, bl + row, bl + row + 1);
                    }
            }

            public void Tube(int sub, Vector3 a, Vector3 b, float radius, int sides = 16)
            {
                Vector3 t = (b - a).normalized;
                Vector3 n0 = Vector3.Cross(t, Mathf.Abs(t.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 b0 = Vector3.Cross(t, n0);
                int start = V.Count;
                foreach (var p in new[] { a, b })
                    for (int j = 0; j <= sides; j++)
                    {
                        float ang = j / (float)sides * Mathf.PI * 2f;
                        Vector3 d = n0 * Mathf.Cos(ang) + b0 * Mathf.Sin(ang);
                        Add(p + d * radius, d, new Vector2(0.5f, 0.5f));
                    }
                for (int j = 0; j < sides; j++) Quad(sub, start + j, start + j + 1, start + sides + 1 + j, start + sides + 2 + j);
            }

            public void Box(int sub, Vector3 min, Vector3 max, Vector2 uv)
            {
                Vector3 c = (min + max) * 0.5f, e = (max - min) * 0.5f;
                Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
                for (int k = 0; k < 3; k++)
                    foreach (float sgn in new[] { 1f, -1f })
                    {
                        Vector3 n = axes[k] * sgn;
                        Vector3 u = axes[(k + 1) % 3], w = axes[(k + 2) % 3];
                        Vector3 fc = c + Vector3.Scale(n, e);
                        Vector3 du = Vector3.Scale(u, e), dw = Vector3.Scale(w, e);
                        int s0 = Add(fc - du - dw, n, uv), s1 = Add(fc + du - dw, n, uv);
                        int s2 = Add(fc - du + dw, n, uv), s3 = Add(fc + du + dw, n, uv);
                        Quad(sub, s0, s1, s2, s3);
                    }
            }

            public Mesh Build(string name)
            {
                var m = new Mesh { name = name };
                if (V.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.SetVertices(V);
                m.SetNormals(N);
                m.SetUVs(0, UV);
                m.subMeshCount = Sub.Length;
                for (int i = 0; i < Sub.Length; i++) m.SetTriangles(Sub[i], i);
                m.RecalculateTangents();
                m.RecalculateBounds();
                return m;
            }
        }

        // Punto de la sección superelíptica (misma fórmula que generate_car.py)
        static Vector2 Section(float t, float w, float yb, float yt, float n)
        {
            float a = 2f * Mathf.PI * t - Mathf.PI * 0.5f;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            float yc = (yt + yb) * 0.5f, h = (yt - yb) * 0.5f;
            float z = w * Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), 2f / n);
            float y = yc + h * Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), 2f / n);
            return new Vector2(y, z);
        }

        static float Interp(float[] xs, float[] ys, float x)
        {
            if (x <= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++)
                if (x <= xs[i]) return Mathf.Lerp(ys[i - 1], ys[i], (x - xs[i - 1]) / (xs[i] - xs[i - 1]));
            return ys[ys.Length - 1];
        }

        static Mesh BuildBody(BodyProfile p, Vector3 scale, Vector3 bodyPos, Transform car)
        {
            var b = new Builder(scale);
            int ns = p.x.Length;
            float x0 = p.x[0], x1 = p.x[ns - 1];

            // 1) Carrocería (submalla 0): rejilla de puntos y normales por diferencias finitas
            var P = new Vector3[ns, Around + 1];
            for (int i = 0; i < ns; i++)
                for (int j = 0; j <= Around; j++)
                {
                    Vector2 yz = Section(j / (float)Around, p.w[i], p.yb[i], p.yt[i], p.n[i]);
                    P[i, j] = new Vector3(p.x[i], yz.x, yz.y);
                }
            int start = b.V.Count;
            for (int i = 0; i < ns; i++)
                for (int j = 0; j <= Around; j++)
                {
                    Vector3 dx = P[Mathf.Min(i + 1, ns - 1), j] - P[Mathf.Max(i - 1, 0), j];
                    int jp = j == Around ? 1 : j + 1, jm = j == 0 ? Around - 1 : j - 1;
                    Vector3 dt = P[i, jp] - P[i, jm];
                    Vector3 n = Vector3.Cross(dx, dt).normalized;
                    float yc = (p.yt[i] + p.yb[i]) * 0.5f;
                    if (Vector3.Dot(n, new Vector3(0f, P[i, j].y - yc, P[i, j].z)) < 0f) n = -n;
                    b.Add(P[i, j], n, new Vector2((p.x[i] - x0) / (x1 - x0), j / (float)Around));
                }
            int row = Around + 1;
            for (int i = 0; i < ns - 1; i++)
                for (int j = 0; j < Around; j++)
                {
                    int bl = start + i * row + j;
                    b.Quad(0, bl, bl + 1, bl + row, bl + row + 1);
                }
            // tapas: morro (rejilla negra) y cola
            foreach (int i in new[] { 0, ns - 1 })
            {
                Vector3 n = i == 0 ? Vector3.left : Vector3.right;
                Vector2 uv = i == 0 ? new Vector2(0.004f, 0.5f) : new Vector2(0.995f, 0.25f);
                int c = b.Add(new Vector3(p.x[i], (p.yt[i] + p.yb[i]) * 0.5f, 0f), n, uv);
                int ring = b.V.Count;
                for (int j = 0; j <= Around; j++) b.Add(P[i, j], n, uv);
                for (int j = 0; j < Around; j++) b.Tri(0, c, ring + j, ring + j + 1);
            }

            // 2) Metal oscuro (submalla 1): ejes hacia las ruedas y base del mástil
            foreach (Transform w in car)
                if (w.name.StartsWith("Rueda"))
                {
                    Vector3 wc = w.localPosition - bodyPos;           // centro de la rueda relativo al Cuerpo
                    float zIn = Mathf.Sign(wc.z) * Interp(p.x, p.w, wc.x) * 0.7f;
                    b.Tube(1, new Vector3(wc.x, wc.y, zIn), new Vector3(wc.x, wc.y, wc.z - Mathf.Sign(wc.z) * 0.012f), 0.011f);
                }
            Transform mast = car.Find("Mastil");
            if (mast != null)
            {
                Vector3 mc = mast.localPosition - bodyPos;
                float mastBottom = mc.y - mast.localScale.y * 0.5f;
                float baseY = Interp(p.x, p.yt, mc.x) - 0.015f;
                if (mastBottom > baseY)
                {
                    float hw = mast.localScale.x * 0.6f;
                    b.Box(1, new Vector3(mc.x - hw, baseY, -hw), new Vector3(mc.x + hw, mastBottom + 0.005f, hw), new Vector2(0.5f, 0.5f));
                }
            }

            // 3) Parabrisas (submalla 2), curvado y con inclinación, visible por ambas caras
            float xs = -0.055f;
            float baseTop = Interp(p.x, p.yt, xs);
            int k = 12;
            foreach (float side in new[] { 1f, -1f })
            {
                int s0 = b.V.Count;
                for (int i = 0; i <= k; i++)
                {
                    float z = Mathf.Lerp(-0.085f, 0.085f, i / (float)k);
                    float bend = 0.02f * (z / 0.085f) * (z / 0.085f);         // se curva hacia atrás en los lados
                    Vector3 bottom = new Vector3(xs + bend, baseTop - 0.004f, z);
                    Vector3 top = new Vector3(xs + bend + 0.028f, baseTop + 0.058f, z * 0.92f);
                    Vector3 n = new Vector3(-0.9f, 0.4f, 0f).normalized * side;
                    b.Add(bottom, n, new Vector2(0.5f, 0.5f));
                    b.Add(top, n, new Vector2(0.5f, 0.5f));
                }
                for (int i = 0; i < k; i++) b.Quad(2, s0 + i * 2, s0 + i * 2 + 2, s0 + i * 2 + 1, s0 + i * 2 + 3);
            }
            return b.Build("CarBody");
        }

        // Rueda en el espacio del cilindro de Unity (eje Y). Medidas en metros.
        static Mesh BuildWheel(WheelProfile p, Vector3 scale)
        {
            var b = new Builder(scale);
            // Neumático (submalla 0)
            var tire = new List<Vector2>();
            for (int i = 0; i < p.r.Length; i++) tire.Add(new Vector2(p.r[i], p.y[i]));
            b.Lathe(0, tire, p.v, new Vector2(0f, 1f));

            // Rin cromado (submalla 1): pestañas + barril
            float rIn = p.r[0], halfW = Mathf.Abs(p.y[0]);
            var rim = new List<Vector2>
            {
                new Vector2(rIn + 0.003f, halfW + 0.0025f), new Vector2(rIn, halfW + 0.0025f), new Vector2(rIn - 0.0025f, halfW),
                new Vector2(rIn - 0.0025f, -halfW), new Vector2(rIn, -halfW - 0.0025f), new Vector2(rIn + 0.003f, -halfW - 0.0025f),
            };
            b.Lathe(1, rim, rim.Select((_, i) => i / (float)(rim.Count - 1)).ToList(), new Vector2(0.5f, 0.5f), side: -1f);   // barril visto desde el eje
            // 6 rayos
            for (int k = 0; k < 6; k++)
            {
                float th = k * Mathf.PI / 3f;
                // rayo: caja a lo largo de +X que luego se gira alrededor del eje de la rueda
                int before = b.V.Count;
                b.Box(1, new Vector3(0.012f, -0.004f, -0.0045f), new Vector3(rIn - 0.002f, 0.004f, 0.0045f), new Vector2(0.5f, 0.5f));
                Quaternion rot = Quaternion.AngleAxis(-th * Mathf.Rad2Deg, Vector3.up);
                for (int i = before; i < b.V.Count; i++)
                {
                    Vector3 world = Vector3.Scale(b.V[i], scale);
                    b.V[i] = Vector3.Scale(rot * world, new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z));
                    b.N[i] = Vector3.Scale(rot * Vector3.Scale(b.N[i], new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z)), scale).normalized;
                }
            }
            // Tapón central rojo (submalla 2)
            var hub = new List<Vector2>
            {
                new Vector2(0f, 0.021f), new Vector2(0.008f, 0.0205f), new Vector2(0.013f, 0.018f), new Vector2(0.015f, 0.014f),
                new Vector2(0.015f, -0.014f), new Vector2(0.013f, -0.018f), new Vector2(0.008f, -0.0205f), new Vector2(0f, -0.021f),
            };
            b.Lathe(2, hub, hub.Select((_, i) => i / (float)(hub.Count - 1)).ToList(), new Vector2(0.5f, 0.5f));
            return b.Build("CarWheel");
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
            foreach (string prefix in new[] { "Car", "Tire" })
            {
                void Tex(string suffix, System.Action<TextureImporter> tweak)
                {
                    if (!(AssetImporter.GetAtPath($"{TexDir}/{prefix}_{suffix}.png") is TextureImporter ti))
                    {
                        Debug.LogWarning($"[Car] Falta {TexDir}/{prefix}_{suffix}.png");
                        return;
                    }
                    ti.textureType = TextureImporterType.Default;
                    ti.wrapModeU = prefix == "Tire" ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                    ti.wrapModeV = TextureWrapMode.Clamp;
                    ti.mipmapEnabled = true;
                    ti.filterMode = FilterMode.Trilinear;
                    ti.anisoLevel = 8;
                    ti.maxTextureSize = 2048;
                    ti.textureCompression = TextureImporterCompression.CompressedHQ;
                    tweak(ti);
                    ti.SaveAndReimport();
                }
                Tex("basecolor", ti => ti.sRGBTexture = true);
                Tex("normal", ti => ti.textureType = TextureImporterType.NormalMap);
                Tex("metallic_smoothness", ti =>
                {
                    ti.sRGBTexture = false;
                    ti.alphaSource = TextureImporterAlphaSource.FromInput;
                    ti.alphaIsTransparency = false;
                });
            }
        }

        static Material LoadOrCreate(string name, out bool urp)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            urp = shader != null;
            if (!urp) shader = Shader.Find("Standard");
            EnsureFolder(MatDir);
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
            else mat.shader = shader;
            return mat;
        }

        static Material Textured(string name, string prefix)
        {
            var mat = LoadOrCreate(name, out bool urp);
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

        static Material Solid(string name, Color c, float metallic, float smooth)
        {
            var mat = LoadOrCreate(name, out bool urp);
            if (urp) { mat.SetColor("_BaseColor", c); mat.SetFloat("_Smoothness", smooth); }
            else { mat.SetColor("_Color", c); mat.SetFloat("_Glossiness", smooth); }
            mat.SetFloat("_Metallic", metallic);
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
