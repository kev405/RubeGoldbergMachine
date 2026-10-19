using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Trampoline.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL del trampolín: lona (textura), aro de tubo y resortes helicoidales pintados.
    /// Se cambian únicamente MeshFilter y MeshRenderer. El MeshCollider convexo conserva su propia malla
    /// (el cilindro) y su material físico rebotador; el Transform no se toca.
    ///
    /// Todo se modela en metros (relativo al pivote) y se divide por la escala local, de modo que cabe
    /// dentro del disco del collider: radio 0.7 m, alto ±0.05 m con la escala actual (1.4, 0.05, 1.4).
    /// </summary>
    public static class TrampolineSetup
    {
        const string Root = "Assets/Trampoline";
        const string TexDir = Root + "/Textures";
        const string MeshPath = Root + "/Mesh/TrampolineMesh.asset";
        const string MatDir = Root + "/Materials";
        const string TargetName = "Trampolin";
        const string OriginalMatGuid = "c487b4e3722a248449efca4261efdfc0";

        // Medidas (m). Deben coincidir con generate_trampoline_texture.py (--mat-radius, --springs).
        const float MatRadius = 0.50f, MatTop = 0.040f, MatThickness = 0.004f;
        const float FrameRadius = 0.655f, FrameTube = 0.030f, FrameY = 0.0f;
        const int Springs = 36;
        const float SpringCoil = 0.011f, SpringWire = 0.0028f;
        const int SpringTurns = 10;

        [MenuItem("Tools/Trampoline/Apply to Trampolin (visual only)", priority = 0)]
        public static void Apply()
        {
            GameObject go = FindTarget();
            if (go == null) return;
            ConfigureImporters();
            var mats = new[] { MatMaterial(), FrameMaterial(), SpringMaterial() };
            Mesh mesh = SaveMesh(BuildMesh(go.transform.localScale));

            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            string before = Snapshot(go);
            Undo.RecordObjects(new Object[] { mf, mr }, "Trampolín: aspecto");
            mf.sharedMesh = mesh;
            mr.sharedMaterials = mats;
            EditorUtility.SetDirty(mf);
            EditorUtility.SetDirty(mr);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            string after = Snapshot(go);

            if (before == after) Debug.Log($"[Trampoline] Listo. Física sin cambios: {after}", go);
            else Debug.LogError($"[Trampoline] ¡La física cambió! Antes: {before} / Después: {after}", go);
        }

        [MenuItem("Tools/Trampoline/Revert Trampolin to cylinder", priority = 1)]
        public static void Revert()
        {
            GameObject go = FindTarget();
            if (go == null) return;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Mesh cyl = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            Undo.RecordObjects(new Object[] { mf, mr }, "Trampolín: revertir");
            mf.sharedMesh = cyl;
            var original = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(OriginalMatGuid));
            if (original != null) mr.sharedMaterials = new[] { original };
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            Debug.Log("[Trampoline] Revertido al cilindro original.", go);
        }

        static GameObject FindTarget()
        {
            var sel = Selection.activeGameObject;
            if (sel != null && sel.GetComponent<MeshCollider>() != null && sel.name.StartsWith("Trampol")) return sel;
            var go = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => t.name == TargetName).Select(t => t.gameObject).FirstOrDefault();
            if (go == null) Debug.LogError($"[Trampoline] No encontré '{TargetName}'. Selecciónalo y vuelve a intentarlo.");
            return go;
        }

        static string Snapshot(GameObject go)
        {
            var t = go.transform;
            var mc = go.GetComponent<MeshCollider>();
            string mat = mc && mc.sharedMaterial ? $"{mc.sharedMaterial.name} (rebote {mc.sharedMaterial.bounciness})" : "-";
            return $"pos {t.localPosition}, rot {t.localEulerAngles}, escala {t.localScale}, " +
                   $"collider {(mc && mc.sharedMesh ? mc.sharedMesh.name : "-")} convex={(mc ? mc.convex : false)}, material {mat}";
        }

        // --------------------------------------------------------------------
        class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int>[] Sub = { new List<int>(), new List<int>(), new List<int>() };
            readonly Vector3 inv, scale;
            public Builder(Vector3 s) { scale = s; inv = new Vector3(1f / s.x, 1f / s.y, 1f / s.z); }

            public int Add(Vector3 p, Vector3 n, Vector2 uv)
            {
                V.Add(Vector3.Scale(p, inv));
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

            public void Quad(int sub, int bl, int br, int tl, int tr)
            {
                Tri(sub, bl, tl, tr);
                Tri(sub, bl, tr, br);
            }

            // Tubo de sección circular a lo largo de una polilínea (marcos de transporte paralelo)
            public void Tube(int sub, IList<Vector3> path, float radius, int sides)
            {
                int n = path.Count, start = V.Count;
                Vector3 prevN = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    Vector3 t = (path[Mathf.Min(i + 1, n - 1)] - path[Mathf.Max(i - 1, 0)]).normalized;
                    Vector3 nrm = i == 0 ? Vector3.Cross(t, Mathf.Abs(t.y) < 0.9f ? Vector3.up : Vector3.right).normalized
                                         : (prevN - Vector3.Dot(prevN, t) * t).normalized;
                    Vector3 bin = Vector3.Cross(t, nrm);
                    prevN = nrm;
                    for (int j = 0; j <= sides; j++)
                    {
                        float a = j / (float)sides * Mathf.PI * 2f;
                        Vector3 d = nrm * Mathf.Cos(a) + bin * Mathf.Sin(a);
                        Add(path[i] + d * radius, d, new Vector2(j / (float)sides, i / (float)(n - 1)));
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

            public Mesh Build()
            {
                var m = new Mesh { name = "TrampolineMesh" };
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

        static Vector2 MatUV(float x, float z) => new Vector2(x / (2f * MatRadius) + 0.5f, z / (2f * MatRadius) + 0.5f);

        static Mesh BuildMesh(Vector3 scale)
        {
            var b = new Builder(scale);
            const int seg = 144, rings = 24;

            // 1) Lona: cara superior, inferior y canto (submalla 0)
            foreach (bool top in new[] { true, false })
            {
                float y = top ? MatTop : MatTop - MatThickness;
                Vector3 n = top ? Vector3.up : Vector3.down;
                int start = b.V.Count;
                for (int i = 0; i <= rings; i++)
                {
                    float rr = MatRadius * i / rings;
                    for (int j = 0; j <= seg; j++)
                    {
                        float th = j / (float)seg * Mathf.PI * 2f;
                        float x = rr * Mathf.Cos(th), z = rr * Mathf.Sin(th);
                        b.Add(new Vector3(x, y, z), n, MatUV(x, z));
                    }
                }
                int row = seg + 1;
                for (int i = 0; i < rings; i++)
                    for (int j = 0; j < seg; j++)
                    {
                        int bl = start + i * row + j;
                        b.Quad(0, bl, bl + 1, bl + row, bl + row + 1);
                    }
            }
            {
                int start = b.V.Count;
                for (int j = 0; j <= seg; j++)
                {
                    float th = j / (float)seg * Mathf.PI * 2f;
                    Vector3 d = new Vector3(Mathf.Cos(th), 0f, Mathf.Sin(th));
                    Vector2 uv = MatUV(d.x * MatRadius * 0.995f, d.z * MatRadius * 0.995f);
                    b.Add(d * MatRadius + Vector3.up * MatTop, d, uv);
                    b.Add(d * MatRadius + Vector3.up * (MatTop - MatThickness), d, uv);
                }
                for (int j = 0; j < seg; j++)
                {
                    int a = start + j * 2;
                    b.Quad(0, a + 1, a + 3, a, a + 2);
                }
            }

            // 2) Aro de tubo (submalla 1)
            var ring = new List<Vector3>();
            for (int j = 0; j <= 160; j++)
            {
                float th = j / 160f * Mathf.PI * 2f;
                ring.Add(new Vector3(FrameRadius * Mathf.Cos(th), FrameY, FrameRadius * Mathf.Sin(th)));
            }
            b.Tube(1, ring, FrameTube, 20);

            // 3) Resortes (submalla 2): gancho en la anilla de la lona, hélice, gancho al aro
            for (int k = 0; k < Springs; k++)
            {
                float th = 2f * Mathf.PI * k / Springs;
                Vector3 dir = new Vector3(Mathf.Cos(th), 0f, Mathf.Sin(th));
                Vector3 A = dir * (MatRadius - 0.008f) + Vector3.up * (MatTop - 0.002f);
                Vector3 B = dir * (FrameRadius - FrameTube * 0.6f) + Vector3.up * (FrameY + FrameTube * 0.4f);
                Vector3 axis = (B - A).normalized;
                Vector3 e1 = Vector3.Cross(axis, Vector3.up).normalized;
                Vector3 e2 = Vector3.Cross(axis, e1).normalized;
                float len = Vector3.Distance(A, B);
                float hook = 0.016f;

                var path = new List<Vector3> { A };
                Vector3 h0 = A + axis * hook;
                path.Add(h0 + e2 * SpringCoil * 0.6f);
                const int perTurn = 14;
                int total = SpringTurns * perTurn;
                for (int i = 0; i <= total; i++)
                {
                    float t = i / (float)total;
                    float phi = t * SpringTurns * Mathf.PI * 2f + Mathf.PI * 0.5f;
                    Vector3 c = A + axis * Mathf.Lerp(hook, len - hook, t);
                    path.Add(c + (e1 * Mathf.Cos(phi) + e2 * Mathf.Sin(phi)) * SpringCoil);
                }
                path.Add(B - axis * hook + e2 * SpringCoil * 0.6f);
                path.Add(B);
                b.Tube(2, path, SpringWire, 6);
            }
            return b.Build();
        }

        static Mesh SaveMesh(Mesh m)
        {
            EnsureFolder(Path.GetDirectoryName(MeshPath));
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(m, existing);
                Object.DestroyImmediate(m);
                m = existing;
            }
            else AssetDatabase.CreateAsset(m, MeshPath);
            EditorUtility.SetDirty(m);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Trampoline] Malla: {m.vertexCount} vértices, 3 submallas (lona, aro, resortes).");
            return m;
        }

        // --------------------------------------------------------------------
        static void ConfigureImporters()
        {
            void Tex(string file, System.Action<TextureImporter> tweak)
            {
                if (!(AssetImporter.GetAtPath($"{TexDir}/{file}") is TextureImporter ti))
                {
                    Debug.LogWarning($"[Trampoline] Falta {TexDir}/{file}");
                    return;
                }
                ti.textureType = TextureImporterType.Default;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.mipmapEnabled = true;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 8;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                tweak(ti);
                ti.SaveAndReimport();
            }
            Tex("Trampoline_basecolor.png", ti => ti.sRGBTexture = true);
            Tex("Trampoline_normal.png", ti => ti.textureType = TextureImporterType.NormalMap);
            Tex("Trampoline_metallic_smoothness.png", ti =>
            {
                ti.sRGBTexture = false;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = false;
            });
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

        static Material MatMaterial()
        {
            var mat = LoadOrCreate("M_TrampolineMat", out bool urp);
            var baseMap = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Trampoline_basecolor.png");
            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Trampoline_normal.png");
            var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Trampoline_metallic_smoothness.png");
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

        // Aro de acero galvanizado
        static Material FrameMaterial() => Solid("M_TrampolineFrame", new Color(0.62f, 0.63f, 0.65f), 0.9f, 0.55f);
        // Resortes pintados (esmalte amarillo). Cambia el color en el material si prefieres otro.
        static Material SpringMaterial() => Solid("M_TrampolineSprings", new Color(0.95f, 0.72f, 0.08f), 0.25f, 0.7f);

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
