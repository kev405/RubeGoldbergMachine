using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Marble.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL de la Canica: malla = cáscara de vidrio (radio 0.5, igual al SphereCollider) + 4 aletas de
    /// color retorcidas en espiral dentro del vidrio. Submalla 0 = aletas (recorte alfa), submalla 1 = vidrio
    /// (transparente). Rigidbody, SphereCollider y Transform no se tocan: el script lo comprueba antes y después.
    /// </summary>
    public static class MarbleSetup
    {
        const string Root = "Assets/Marble";
        const string TexDir = Root + "/Textures";
        const string MatDir = Root + "/Materials";
        const string MeshPath = Root + "/Mesh/Marble.asset";
        const string OriginalMatGuid = "ac8bc30ffbb317542acbc19b07b63822";

        // Forma de la espiral (en unidades locales: la esfera mide 1, radio 0.5)
        const float ShellRadius = 0.5f;     // = SphereCollider.radius
        const float VaneRadius = 0.43f;     // las aletas quedan dentro de un vidrio "grueso"
        const int VaneCount = 4;
        const float Turns = 1.1f;           // vueltas de la espiral de polo a polo

        [MenuItem("Tools/Marble/Apply to Canica (visual only)", priority = 0)]
        public static void Apply()
        {
            var targets = FindMarbles();
            if (targets.Count == 0) { Debug.LogError("[Marble] No encontré la 'Canica' (selecciónala o nómbrala Canica)."); return; }

            ConfigureImporters();
            var mats = new[] { VaneMaterial(), GlassMaterial() };
            Mesh mesh = Save(BuildMesh());

            foreach (var t in targets)
            {
                string before = Snapshot(t);
                var mf = t.GetComponent<MeshFilter>();
                var mr = t.GetComponent<MeshRenderer>();
                Undo.RecordObjects(new Object[] { mf, mr }, "Canica: aspecto");
                mf.sharedMesh = mesh;
                mr.sharedMaterials = mats;
                EditorUtility.SetDirty(mf);
                EditorUtility.SetDirty(mr);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(t.gameObject.scene);
                string after = Snapshot(t);
                if (before == after) Debug.Log($"[Marble] {t.name} listo. Física sin cambios: {after}", t);
                else Debug.LogError($"[Marble] ¡La física cambió en {t.name}! Antes: {before} / Después: {after}", t);
            }
        }

        [MenuItem("Tools/Marble/Revert Canica to sphere", priority = 1)]
        public static void Revert()
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh sphere = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            var original = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(OriginalMatGuid));
            foreach (var t in FindMarbles())
            {
                var mf = t.GetComponent<MeshFilter>();
                var mr = t.GetComponent<MeshRenderer>();
                Undo.RecordObjects(new Object[] { mf, mr }, "Canica: revertir");
                mf.sharedMesh = sphere;
                if (original != null) mr.sharedMaterials = new[] { original };
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(t.gameObject.scene);
                Debug.Log($"[Marble] {t.name} revertida a la esfera original.", t);
            }
        }

        static List<Transform> FindMarbles()
        {
            var sel = Selection.transforms.Where(t => t.GetComponent<SphereCollider>() && t.GetComponent<MeshFilter>()).ToList();
            if (sel.Count > 0) return sel;
            return Object.FindObjectsByType<SphereCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(c => c.transform)
                .Where(t => t.name.StartsWith("Canica") && t.GetComponent<MeshFilter>())
                .ToList();
        }

        static string Snapshot(Transform t)
        {
            var rb = t.GetComponent<Rigidbody>();
            var sc = t.GetComponent<SphereCollider>();
            return $"pos {t.localPosition} rot {t.localRotation.eulerAngles} escala {t.localScale}; " +
                   $"masa {(rb ? rb.mass : 0)} centro {(rb ? rb.centerOfMass : Vector3.zero)} inercia {(rb ? rb.inertiaTensor : Vector3.zero)}; " +
                   $"collider r {(sc ? sc.radius : 0)} @ {(sc ? sc.center : Vector3.zero)}";
        }

        // --------------------------------------------------------------------
        static Mesh BuildMesh()
        {
            var V = new List<Vector3>(); var N = new List<Vector3>(); var UV = new List<Vector2>();
            var vanes = new List<int>(); var glass = new List<int>();

            void Tri(List<int> sub, int a, int b, int c)
            {
                Vector3 n = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
                if (n.sqrMagnitude < 1e-16f) return;
                if (Vector3.Dot(n, N[a] + N[b] + N[c]) >= 0f) sub.AddRange(new[] { a, b, c });
                else sub.AddRange(new[] { a, c, b });
            }

            // --- Aletas helicoidales: punto = (r cos θ, h, r sin θ), θ = θk + twist·h --------------------
            float twist = Turns * 2f * Mathf.PI / (2f * VaneRadius);
            const int rows = 48, cols = 12;
            for (int k = 0; k < VaneCount; k++)
            {
                float th0 = k * 2f * Mathf.PI / VaneCount;
                float u0 = (k + 0.016f) / VaneCount, du = 0.968f / VaneCount;
                foreach (float side in new[] { 1f, -1f })            // dos caras, normales opuestas
                {
                    int start = V.Count;
                    for (int i = 0; i <= rows; i++)
                    {
                        float tv = i / (float)rows;
                        float h = Mathf.Lerp(-VaneRadius, VaneRadius, tv) * 0.985f;
                        float rmax = Mathf.Sqrt(Mathf.Max(VaneRadius * VaneRadius - h * h, 0f));
                        float th = th0 + twist * h;
                        var dr = new Vector3(Mathf.Cos(th), 0f, Mathf.Sin(th));
                        for (int j = 0; j <= cols; j++)
                        {
                            float tu = j / (float)cols;
                            float r = rmax * tu;
                            var dh = new Vector3(-r * Mathf.Sin(th) * twist, 1f, r * Mathf.Cos(th) * twist);
                            Vector3 n = Vector3.Cross(dr, dh).normalized * side;
                            V.Add(dr * r + Vector3.up * h + n * 0.0012f);
                            N.Add(n);
                            UV.Add(new Vector2(u0 + du * tu, tv));
                        }
                    }
                    int row = cols + 1;
                    for (int i = 0; i < rows; i++)
                        for (int j = 0; j < cols; j++)
                        {
                            int bl = start + i * row + j;
                            Tri(vanes, bl, bl + row, bl + row + 1);
                            Tri(vanes, bl, bl + row + 1, bl + 1);
                        }
                }
            }

            // --- Cáscara de vidrio: esfera UV (equirectangular, igual que el generador) -------------------
            const int seg = 64, ring = 48;
            int g0 = V.Count;
            for (int i = 0; i <= ring; i++)
            {
                float t = i / (float)ring, th = t * Mathf.PI;
                for (int j = 0; j <= seg; j++)
                {
                    float u = j / (float)seg, ph = u * 2f * Mathf.PI;
                    var d = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), -Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                    V.Add(d * ShellRadius); N.Add(d); UV.Add(new Vector2(u, t));
                }
            }
            for (int i = 0; i < ring; i++)
                for (int j = 0; j < seg; j++)
                {
                    int bl = g0 + i * (seg + 1) + j;
                    Tri(glass, bl, bl + seg + 1, bl + seg + 2);
                    Tri(glass, bl, bl + seg + 2, bl + 1);
                }

            var mesh = new Mesh { name = "Marble" };
            mesh.SetVertices(V); mesh.SetNormals(N); mesh.SetUVs(0, UV);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(vanes, 0);
            mesh.SetTriangles(glass, 1);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh Save(Mesh m)
        {
            EnsureFolder(Path.GetDirectoryName(MeshPath));
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            if (existing != null) { EditorUtility.CopySerialized(m, existing); Object.DestroyImmediate(m); m = existing; }
            else AssetDatabase.CreateAsset(m, MeshPath);
            EditorUtility.SetDirty(m);
            AssetDatabase.SaveAssets();
            return m;
        }

        // --------------------------------------------------------------------
        static void ConfigureImporters()
        {
            foreach (string set in new[] { "Vanes", "Glass" })
                foreach (string suffix in new[] { "basecolor", "normal", "metallic_smoothness" })
                {
                    string p = $"{TexDir}/Marble_{set}_{suffix}.png";
                    if (!(AssetImporter.GetAtPath(p) is TextureImporter ti)) { Debug.LogWarning($"[Marble] Falta {p}"); continue; }
                    ti.textureType = suffix == "normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    ti.sRGBTexture = suffix == "basecolor";
                    ti.alphaSource = suffix == "normal" ? TextureImporterAlphaSource.None : TextureImporterAlphaSource.FromInput;
                    ti.alphaIsTransparency = suffix == "basecolor";
                    ti.wrapMode = TextureWrapMode.Clamp;
                    ti.wrapModeU = set == "Glass" ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                    ti.mipmapEnabled = true;
                    ti.filterMode = FilterMode.Trilinear;
                    ti.anisoLevel = 4;
                    ti.maxTextureSize = 2048;
                    ti.textureCompression = TextureImporterCompression.CompressedHQ;
                    ti.SaveAndReimport();
                }
        }

        static Material Textured(string name, string set, out bool urp)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            urp = shader != null;
            if (!urp) shader = Shader.Find("Standard");
            EnsureFolder(MatDir);
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
            else mat.shader = shader;

            Texture2D T(string s) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Marble_{set}_{s}.png");
            var bc = T("basecolor"); var nrm = T("normal"); var ms = T("metallic_smoothness");
            mat.SetTexture(urp ? "_BaseMap" : "_MainTex", bc);
            mat.SetColor(urp ? "_BaseColor" : "_Color", Color.white);
            mat.SetTexture("_BumpMap", nrm);
            mat.SetTexture("_MetallicGlossMap", ms);
            Keyword(mat, "_NORMALMAP", nrm != null);
            if (urp)
            {
                mat.SetFloat("_WorkflowMode", 1f);
                mat.SetFloat("_Smoothness", 1f);
                mat.SetFloat("_SmoothnessTextureChannel", 0f);
                Keyword(mat, "_METALLICSPECGLOSSMAP", ms != null);
            }
            else
            {
                mat.SetFloat("_GlossMapScale", 1f);
                Keyword(mat, "_METALLICGLOSSMAP", ms != null);
            }
            return mat;
        }

        static Material VaneMaterial()
        {
            var mat = Textured("M_MarbleSwirl", "Vanes", out bool urp);
            // Recorte alfa (bordes deshilachados), opaco
            mat.SetFloat(urp ? "_AlphaClip" : "_Mode", urp ? 1f : 1f);
            mat.SetFloat("_Cutoff", 0.45f);
            if (urp) { mat.SetFloat("_Surface", 0f); mat.SetFloat("_Cull", 2f); }
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material GlassMaterial()
        {
            var mat = Textured("M_MarbleGlass", "Glass", out bool urp);
            if (urp)
            {
                mat.SetFloat("_Surface", 1f);                  // Transparent
                mat.SetFloat("_Blend", 0f);                    // Alpha
                mat.SetFloat("_AlphaClip", 0f);
                mat.SetFloat("_BlendModePreserveSpecular", 1f); // el brillo no se vuelve transparente
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_ZWrite", 0f);
                mat.SetFloat("_Cull", 2f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.SetShaderPassEnabled("DepthOnly", false);
                mat.SetShaderPassEnabled("SHADOWCASTER", true);
            }
            else
            {
                mat.SetFloat("_Mode", 3f);                     // Standard: Transparent
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_ZWrite", 0f);
                mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            }
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void Keyword(Material m, string kw, bool on) { if (on) m.EnableKeyword(kw); else m.DisableKeyword(kw); }

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
