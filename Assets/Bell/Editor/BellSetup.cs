using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Bell.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL de la campana:
    ///   CuerpoCampana -> campana torneada hueca (perfil de bell_profile.json) + badajo (submalla de hierro).
    ///   Cuello        -> vástago con argolla alrededor del eje de la bisagra.
    /// Se cambian únicamente MeshFilter y MeshRenderer. El MeshCollider de CuerpoCampana conserva su propia
    /// malla (el cilindro), y el Rigidbody / HingeJoint de "Campana" no se tocan.
    ///
    /// Las mallas se diseñan en metros y se dividen por la escala local de cada hijo, así encajan aunque
    /// el objeto tenga escala no uniforme (0.8, 0.35, 0.8).
    /// </summary>
    public static class BellSetup
    {
        const string Root = "Assets/Bell";
        const string TexDir = Root + "/Textures";
        const string ProfilePath = TexDir + "/bell_profile.json";
        const string MeshDir = Root + "/Mesh";
        const string MatDir = Root + "/Materials";
        const string OriginalMatGuid = "6b2ff65886e9cf34190e118ddc8cc721";
        const int Segments = 96;

        [System.Serializable]
        class Profile { public float radius, height, lipV; public float[] r, h, v; }

        // --------------------------------------------------------------------
        [MenuItem("Tools/Bell/Apply to Campana (visual only)", priority = 0)]
        public static void Apply()
        {
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(ProfilePath);
            if (json == null) { Debug.LogError($"[Bell] Falta {ProfilePath}"); return; }
            var prof = JsonUtility.FromJson<Profile>(json.text);

            Transform bell = FindBell();
            if (bell == null) return;
            Transform body = bell.Find("CuerpoCampana");
            Transform neck = bell.Find("Cuello");
            if (body == null) { Debug.LogError("[Bell] No encontré 'CuerpoCampana' dentro de la campana."); return; }

            ConfigureImporters();
            Material brass = BrassMaterial();
            Material iron = IronMaterial();

            string before = Snapshot(bell);
            Mesh bodyMesh = SaveMesh(BuildBody(prof, body.localScale), "BellBody");
            SetVisual(body, bodyMesh, new[] { brass, iron });
            if (neck != null)
            {
                Mesh neckMesh = SaveMesh(BuildNeck(neck.localScale, neck.localPosition.y, TopOfBody(body, prof)), "BellNeck");
                SetVisual(neck, neckMesh, new[] { brass });
            }
            string after = Snapshot(bell);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(bell.gameObject.scene);

            if (before == after) Debug.Log($"[Bell] Listo. Física sin cambios: {after}", bell);
            else Debug.LogError($"[Bell] ¡La física cambió! Antes: {before} / Después: {after}", bell);
        }

        [MenuItem("Tools/Bell/Revert Campana to cylinders", priority = 1)]
        public static void Revert()
        {
            Transform bell = FindBell();
            if (bell == null) return;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Mesh cyl = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            var original = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(OriginalMatGuid));
            foreach (string n in new[] { "CuerpoCampana", "Cuello" })
            {
                Transform t = bell.Find(n);
                if (t != null) SetVisual(t, cyl, original != null ? new[] { original } : null);
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(bell.gameObject.scene);
            Debug.Log("[Bell] Campana revertida a cilindros.", bell);
        }

        // --------------------------------------------------------------------
        static Transform FindBell()
        {
            var sel = Selection.activeTransform;
            if (sel != null && sel.GetComponent<HingeJoint>() != null && sel.Find("CuerpoCampana") != null) return sel;
            var t = Object.FindObjectsByType<HingeJoint>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(h => h.transform).FirstOrDefault(x => x.Find("CuerpoCampana") != null);
            if (t == null) Debug.LogError("[Bell] No encontré la campana (objeto con HingeJoint e hijo 'CuerpoCampana').");
            return t;
        }

        static float TopOfBody(Transform body, Profile p) => body.localPosition.y + p.height * 0.5f;

        static void SetVisual(Transform t, Mesh mesh, Material[] mats)
        {
            var mf = t.GetComponent<MeshFilter>();
            var mr = t.GetComponent<MeshRenderer>();
            if (mf == null || mr == null) return;
            Undo.RecordObjects(new Object[] { mf, mr }, "Campana: aspecto");
            mf.sharedMesh = mesh;
            if (mats != null) mr.sharedMaterials = mats;
            EditorUtility.SetDirty(mf);
            EditorUtility.SetDirty(mr);
        }

        static string Snapshot(Transform bell)
        {
            var rb = bell.GetComponent<Rigidbody>();
            var hj = bell.GetComponent<HingeJoint>();
            var sb = new System.Text.StringBuilder();
            sb.Append($"masa {(rb ? rb.mass : 0)}, CoM auto {(rb ? rb.automaticCenterOfMass : false)}, ");
            if (hj) sb.Append($"bisagra {hj.anchor}/{hj.axis}, ");
            foreach (var c in bell.GetComponentsInChildren<Collider>(true))
            {
                sb.Append($"{c.name}:{c.GetType().Name}");
                if (c is MeshCollider mc) sb.Append($"[{(mc.sharedMesh ? mc.sharedMesh.name : "-")},convex={mc.convex}]");
                sb.Append($" @ {c.transform.localPosition} x {c.transform.localScale}; ");
            }
            return sb.ToString();
        }

        // --------------------------------------------------------------------
        // Torneado genérico: perfil (radio, altura, v) en metros -> malla en el espacio local del objeto.
        class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<List<int>> Sub = new List<List<int>>();
            readonly Vector3 inv, scale;
            public Builder(Vector3 localScale)
            {
                scale = localScale;
                inv = new Vector3(1f / localScale.x, 1f / localScale.y, 1f / localScale.z);
            }

            public int Add(Vector3 worldPos, Vector3 worldNormal, Vector2 uv)
            {
                V.Add(Vector3.Scale(worldPos, inv));
                N.Add(Vector3.Scale(worldNormal, scale).normalized);       // normal: inversa-transpuesta
                UV.Add(uv);
                return V.Count - 1;
            }

            // Triángulo con orden corregido para que su cara mire hacia la normal deseada
            public void Tri(int sub, int a, int b, int c)
            {
                while (Sub.Count <= sub) Sub.Add(new List<int>());
                Vector3 n = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
                if (n.sqrMagnitude < 1e-14f) return;                        // degenerado (eje)
                if (Vector3.Dot(n, N[a] + N[b] + N[c]) >= 0f) Sub[sub].AddRange(new[] { a, b, c });
                else Sub[sub].AddRange(new[] { a, c, b });
            }

            public void Lathe(int sub, IList<Vector2> rh, IList<float> v, Vector2 uRange, Vector2 vRange, float yOffset = 0f)
            {
                int n = rh.Count, start = V.Count;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = rh[Mathf.Max(i - 1, 0)], b = rh[Mathf.Min(i + 1, n - 1)];
                    Vector2 tan = (b - a).normalized;
                    Vector2 pn = new Vector2(-tan.y, tan.x) * Orient(rh);   // normal hacia fuera del sólido
                    for (int j = 0; j <= Segments; j++)
                    {
                        float u = j / (float)Segments, th = u * Mathf.PI * 2f;
                        float c = Mathf.Cos(th), s = Mathf.Sin(th);
                        Add(new Vector3(rh[i].x * c, rh[i].y + yOffset, rh[i].x * s),
                            new Vector3(pn.x * c, pn.y, pn.x * s),
                            new Vector2(Mathf.Lerp(uRange.x, uRange.y, u), Mathf.Lerp(vRange.x, vRange.y, v[i])));
                    }
                }
                int row = Segments + 1;
                for (int i = 0; i < n - 1; i++)
                    for (int j = 0; j < Segments; j++)
                    {
                        int bl = start + i * row + j, br = bl + 1, tl = bl + row, tr = tl + 1;
                        Tri(sub, bl, tl, tr);
                        Tri(sub, bl, tr, br);
                    }
            }

            public Mesh Build(string name)
            {
                var m = new Mesh { name = name };
                if (V.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.SetVertices(V);
                m.SetNormals(N);
                m.SetUVs(0, UV);
                m.subMeshCount = Sub.Count;
                for (int i = 0; i < Sub.Count; i++) m.SetTriangles(Sub[i], i);
                m.RecalculateTangents();
                m.RecalculateBounds();
                return m;
            }
        }

        static Mesh BuildBody(Profile p, Vector3 scale)
        {
            var b = new Builder(scale);
            var rh = new List<Vector2>();
            for (int i = 0; i < p.r.Length; i++) rh.Add(new Vector2(p.r[i], p.h[i]));
            b.Lathe(0, rh, p.v, new Vector2(0, 1), new Vector2(0, 1));

            // Badajo (submalla 1, hierro): varilla desde el techo interior + bola cerca de la boca
            float top = p.h.Where((h, i) => p.r[i] < 0.01f).Max() - 0.004f;   // techo interior aprox.
            float lip = p.h.Min();
            float k = p.height / 0.7f;
            float ballR = 0.06f * k, ballC = lip + 0.12f * k, rodR = 0.012f * k;
            var cl = new List<Vector2> { new Vector2(0f, top), new Vector2(rodR, top - 0.002f), new Vector2(rodR, ballC + ballR * 0.9f) };
            for (int i = 0; i <= 16; i++)
            {
                float a = Mathf.Lerp(0.45f, Mathf.PI, i / 16f);
                cl.Add(new Vector2(ballR * Mathf.Sin(a), ballC + ballR * Mathf.Cos(a)));
            }
            var cv = Enumerable.Repeat(0.5f, cl.Count).ToList();
            b.Lathe(1, cl, cv, new Vector2(0, 0), new Vector2(0, 0));
            return b.Build("BellBody");
        }

        // Cuello: vástago + argolla (toroide en el plano XY, alrededor del eje Z de la bisagra).
        // neckY = posición local del Cuello en la Campana; bodyTop = altura de la corona en la Campana.
        static Mesh BuildNeck(Vector3 scale, float neckY, float bodyTop)
        {
            var b = new Builder(scale);
            float hingeY = -neckY;                 // la bisagra está en el origen de la Campana
            float baseY = bodyTop - neckY;         // corona de la campana en el espacio del Cuello
            const float R = 0.055f, r = 0.016f;
            float ringBottom = hingeY - R;
            var stem = new List<Vector2>
            {
                new Vector2(0.000f, baseY), new Vector2(0.080f, baseY), new Vector2(0.078f, baseY + 0.012f),
                new Vector2(0.055f, baseY + 0.030f), new Vector2(0.038f, baseY + 0.055f), new Vector2(0.034f, baseY + 0.08f),
                new Vector2(0.034f, ringBottom - 0.012f), new Vector2(0.028f, ringBottom + 0.004f), new Vector2(0f, ringBottom + 0.008f),
            };
            var sv = stem.Select((_, i) => i / (float)(stem.Count - 1)).ToList();
            b.Lathe(0, stem, sv, new Vector2(0, 1), new Vector2(0.10f, 0.30f));

            // Argolla
            int ringSeg = 48, tubeSeg = 16, start = b.V.Count;
            for (int i = 0; i <= ringSeg; i++)
            {
                float t = i / (float)ringSeg * Mathf.PI * 2f;
                Vector3 dir = new Vector3(Mathf.Cos(t), Mathf.Sin(t), 0f);
                Vector3 c = new Vector3(0f, hingeY, 0f) + dir * R;
                for (int j = 0; j <= tubeSeg; j++)
                {
                    float f = j / (float)tubeSeg * Mathf.PI * 2f;
                    Vector3 n = dir * Mathf.Cos(f) + Vector3.forward * Mathf.Sin(f);
                    b.Add(c + n * r, n, new Vector2(i / (float)ringSeg, Mathf.Lerp(0.15f, 0.25f, j / (float)tubeSeg)));
                }
            }
            int rowT = tubeSeg + 1;
            for (int i = 0; i < ringSeg; i++)
                for (int j = 0; j < tubeSeg; j++)
                {
                    int a = start + i * rowT + j, bb = a + 1, c = a + rowT, d = c + 1;
                    b.Tri(0, a, c, d);
                    b.Tri(0, a, d, bb);
                }
            return b.Build("BellNeck");
        }

        static Mesh SaveMesh(Mesh m, string name)
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
            void Tex(string file, System.Action<TextureImporter> tweak)
            {
                if (!(AssetImporter.GetAtPath($"{TexDir}/{file}") is TextureImporter ti))
                {
                    Debug.LogWarning($"[Bell] Falta {TexDir}/{file}");
                    return;
                }
                ti.textureType = TextureImporterType.Default;
                ti.wrapModeU = TextureWrapMode.Repeat;
                ti.wrapModeV = TextureWrapMode.Clamp;
                ti.mipmapEnabled = true;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 8;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                tweak(ti);
                ti.SaveAndReimport();
            }
            Tex("Bell_basecolor.png", ti => ti.sRGBTexture = true);
            Tex("Bell_normal.png", ti => ti.textureType = TextureImporterType.NormalMap);
            Tex("Bell_metallic_smoothness.png", ti =>
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

        static Material BrassMaterial()
        {
            var mat = LoadOrCreate("M_BellBrass", out bool urp);
            var baseMap = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Bell_basecolor.png");
            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Bell_normal.png");
            var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Bell_metallic_smoothness.png");
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

        static Material IronMaterial()
        {
            var mat = LoadOrCreate("M_BellIron", out bool urp);
            var c = new Color(0.10f, 0.095f, 0.09f);
            if (urp)
            {
                mat.SetColor("_BaseColor", c);
                mat.SetFloat("_Metallic", 0.85f);
                mat.SetFloat("_Smoothness", 0.35f);
            }
            else
            {
                mat.SetColor("_Color", c);
                mat.SetFloat("_Metallic", 0.85f);
                mat.SetFloat("_Glossiness", 0.35f);
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
