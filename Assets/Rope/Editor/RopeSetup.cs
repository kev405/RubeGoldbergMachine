using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Rope.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL de la Cuerda del péndulo: cilindro con las mismas medidas que el de Unity (radio 0.5,
    /// alto 2 en unidades locales) pero con UV proporcionales: u = una vuelta, v = largo / contorno. Así la textura
    /// trenzada se repite sin estirarse a lo largo de los 3 m de cuerda. La Cuerda no tiene collider; el Rigidbody,
    /// la HingeJoint y los colliders del Pendulo no se tocan (el script lo comprueba antes y después).
    /// </summary>
    public static class RopeSetup
    {
        const string Root = "Assets/Rope";
        const string TexDir = Root + "/Textures";
        const string MatPath = Root + "/Materials/M_RopeBraided.mat";
        const string MeshPath = Root + "/Mesh/RopeBraided.asset";
        const string OriginalMatGuid = "31321ba15b8f8eb4c954353edc038b1d";
        const int Sides = 24;

        [MenuItem("Tools/Rope/Apply to Cuerda (visual only)", priority = 0)]
        public static void Apply()
        {
            Transform rope = FindRope();
            if (rope == null) return;
            Transform body = rope.parent != null ? rope.parent : rope;

            ConfigureImporters();
            Material mat = RopeMaterial();
            string before = Snapshot(body);
            Mesh mesh = Save(BuildMesh(rope));
            var mf = rope.GetComponent<MeshFilter>();
            var mr = rope.GetComponent<MeshRenderer>();
            Undo.RecordObjects(new Object[] { mf, mr }, "Cuerda: aspecto");
            mf.sharedMesh = mesh;
            mr.sharedMaterials = new[] { mat };
            EditorUtility.SetDirty(mf);
            EditorUtility.SetDirty(mr);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(rope.gameObject.scene);
            string after = Snapshot(body);
            if (before == after) Debug.Log($"[Rope] Listo. Física sin cambios: {after}", rope);
            else Debug.LogError($"[Rope] ¡La física cambió! Antes: {before} / Después: {after}", rope);
        }

        [MenuItem("Tools/Rope/Revert Cuerda to cylinder", priority = 1)]
        public static void Revert()
        {
            Transform rope = FindRope();
            if (rope == null) return;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Mesh cyl = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            var mf = rope.GetComponent<MeshFilter>();
            var mr = rope.GetComponent<MeshRenderer>();
            Undo.RecordObjects(new Object[] { mf, mr }, "Cuerda: revertir");
            mf.sharedMesh = cyl;
            var original = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(OriginalMatGuid));
            if (original != null) mr.sharedMaterials = new[] { original };
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(rope.gameObject.scene);
            Debug.Log("[Rope] Cuerda revertida al cilindro original.", rope);
        }

        static Transform FindRope()
        {
            var sel = Selection.activeTransform;
            if (sel != null && sel.GetComponent<MeshFilter>() && sel.name.StartsWith("Cuerda")) return sel;
            foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (mf.name == "Cuerda") return mf.transform;
            Debug.LogError("[Rope] No encontré el objeto 'Cuerda' (selecciónalo).");
            return null;
        }

        static string Snapshot(Transform body)
        {
            var rb = body.GetComponent<Rigidbody>();
            var hj = body.GetComponent<HingeJoint>();
            var sb = new System.Text.StringBuilder(
                $"masa {(rb ? rb.mass : 0)}, bisagra {(hj ? hj.anchor + "/" + hj.axis : "-")}; ");
            foreach (var c in body.GetComponentsInChildren<Collider>(true))
                sb.Append($"{c.name}:{c.GetType().Name} {c.transform.localPosition} x {c.transform.localScale}; ");
            foreach (Transform t in body)
                sb.Append($"{t.name} {t.localPosition} {t.localRotation.eulerAngles} {t.localScale}; ");
            return sb.ToString();
        }

        // --------------------------------------------------------------------
        static Mesh BuildMesh(Transform rope)
        {
            Vector3 s = rope.lossyScale;
            float diameter = (Mathf.Abs(s.x) + Mathf.Abs(s.z)) * 0.5f;     // radio local 0.5 -> diámetro = escala
            float length = Mathf.Abs(s.y) * 2f;                            // alto local 2
            float vMax = length / (Mathf.PI * Mathf.Max(diameter, 1e-4f)); // repeticiones a lo largo

            var V = new List<Vector3>(); var N = new List<Vector3>(); var UV = new List<Vector2>(); var I = new List<int>();
            void Tri(int a, int b, int c)
            {
                // Comparación con la normal en espacio escalado (la escala no uniforme cambia la orientación aparente)
                Vector3 n = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
                if (Vector3.Dot(n, N[a] + N[b] + N[c]) >= 0f) I.AddRange(new[] { a, b, c });
                else I.AddRange(new[] { a, c, b });
            }

            // Lateral: 2 anillos bastan (la textura se repite por UV)
            for (int ring = 0; ring <= 1; ring++)
                for (int j = 0; j <= Sides; j++)
                {
                    float u = j / (float)Sides, a = u * Mathf.PI * 2f;
                    var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    V.Add(d * 0.5f + Vector3.up * (ring == 0 ? -1f : 1f));
                    N.Add(d);
                    UV.Add(new Vector2(u, ring == 0 ? 0f : vMax));
                }
            for (int j = 0; j < Sides; j++)
            {
                int bl = j, br = j + 1, tl = j + Sides + 1, tr = j + Sides + 2;
                Tri(bl, tl, tr); Tri(bl, tr, br);
            }

            // Tapas (extremos cortados de la cuerda: núcleo de fibra)
            foreach (float y in new[] { -1f, 1f })
            {
                int c0 = V.Count;
                V.Add(new Vector3(0f, y, 0f)); N.Add(Vector3.up * y); UV.Add(new Vector2(0.5f, 0.5f));
                for (int j = 0; j <= Sides; j++)
                {
                    float a = j / (float)Sides * Mathf.PI * 2f;
                    V.Add(new Vector3(Mathf.Cos(a) * 0.5f, y, Mathf.Sin(a) * 0.5f));
                    N.Add(Vector3.up * y);
                    UV.Add(new Vector2(0.5f + Mathf.Cos(a) * 0.12f, 0.5f + Mathf.Sin(a) * 0.12f));
                }
                for (int j = 0; j < Sides; j++) Tri(c0, c0 + 1 + j, c0 + 2 + j);
            }

            var mesh = new Mesh { name = "RopeBraided" };
            mesh.SetVertices(V); mesh.SetNormals(N); mesh.SetUVs(0, UV); mesh.SetTriangles(I, 0);
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
            foreach (string suffix in new[] { "basecolor", "normal", "metallic_smoothness" })
            {
                string p = $"{TexDir}/Rope_{suffix}.png";
                if (!(AssetImporter.GetAtPath(p) is TextureImporter ti)) { Debug.LogWarning($"[Rope] Falta {p}"); continue; }
                ti.textureType = suffix == "normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = suffix == "basecolor";
                if (suffix == "metallic_smoothness") { ti.alphaSource = TextureImporterAlphaSource.FromInput; ti.alphaIsTransparency = false; }
                ti.wrapMode = TextureWrapMode.Repeat;               // mosaico: se repite a lo largo de la cuerda
                ti.mipmapEnabled = true;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 8;                                  // la cuerda se ve muy de lado: evita borrones
                ti.maxTextureSize = 1024;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
        }

        static Material RopeMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            bool urp = shader != null;
            if (!urp) shader = Shader.Find("Standard");
            EnsureFolder(Path.GetDirectoryName(MatPath));
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, MatPath); }
            else mat.shader = shader;

            Texture2D T(string s) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Rope_{s}.png");
            var bc = T("basecolor"); var nrm = T("normal"); var ms = T("metallic_smoothness");
            mat.SetTexture(urp ? "_BaseMap" : "_MainTex", bc);
            mat.SetColor(urp ? "_BaseColor" : "_Color", Color.white);
            mat.SetTexture("_BumpMap", nrm);
            mat.SetFloat("_BumpScale", 1.2f);
            mat.SetTexture("_MetallicGlossMap", ms);
            if (nrm != null) mat.EnableKeyword("_NORMALMAP");
            if (urp)
            {
                mat.SetFloat("_WorkflowMode", 1f);
                mat.SetFloat("_Smoothness", 1f);
                mat.SetFloat("_SmoothnessTextureChannel", 0f);
                if (ms != null) mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            else
            {
                mat.SetFloat("_GlossMapScale", 1f);
                if (ms != null) mat.EnableKeyword("_METALLICGLOSSMAP");
            }
            EditorUtility.SetDirty(mat);
            return mat;
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
