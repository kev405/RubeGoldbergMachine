using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lever.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL de la palanca: TablaPalanca recibe una caja 1x1x1 (igual que el cubo, el BoxCollider no
    /// cambia) con UV hacia el atlas de madera con cinta de peligro, más un perno de acero con arandelas
    /// exactamente en el eje de la bisagra. Rigidbody, HingeJoint, SleepOnStart, BoxCollider y Transforms intactos.
    /// </summary>
    public static class LeverSetup
    {
        const string Root = "Assets/Lever";
        const string TexDir = Root + "/Textures";
        const string MeshPath = Root + "/Mesh/LeverBoard.asset";
        const string MatDir = Root + "/Materials";
        const string OriginalMatGuid = "4f8b69ec57eeae64ea7eb31e741a9182";
        static readonly string[] Faces = { "top", "bottom", "sideP", "sideN", "endP", "endN" };

        [System.Serializable]
        class Layout { public float[] top, bottom, sideP, sideN, endP, endN; }

        [MenuItem("Tools/Lever/Apply to Palanca (visual only)", priority = 0)]
        public static void Apply()
        {
            Transform lever = FindLever();
            if (lever == null) return;
            Transform board = lever.Find("TablaPalanca");
            if (board == null) { Debug.LogError("[Lever] No encontré 'TablaPalanca'."); return; }
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>($"{TexDir}/lever_layout.json");
            if (json == null) { Debug.LogError($"[Lever] Falta {TexDir}/lever_layout.json"); return; }
            var L = JsonUtility.FromJson<Layout>(json.text);

            ConfigureImporters();
            var mats = new[] { BoardMaterial(), Solid("M_LeverSteel", new Color(0.55f, 0.56f, 0.58f), 0.95f, 0.55f) };
            string before = Snapshot(lever);
            Mesh mesh = Save(BuildBoard(L, board, lever));
            var mf = board.GetComponent<MeshFilter>();
            var mr = board.GetComponent<MeshRenderer>();
            Undo.RecordObjects(new Object[] { mf, mr }, "Palanca: aspecto");
            mf.sharedMesh = mesh;
            mr.sharedMaterials = mats;
            EditorUtility.SetDirty(mf);
            EditorUtility.SetDirty(mr);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(lever.gameObject.scene);
            string after = Snapshot(lever);
            if (before == after) Debug.Log($"[Lever] Listo. Física sin cambios: {after}", lever);
            else Debug.LogError($"[Lever] ¡La física cambió! Antes: {before} / Después: {after}", lever);
        }

        [MenuItem("Tools/Lever/Revert Palanca to cube", priority = 1)]
        public static void Revert()
        {
            Transform lever = FindLever();
            Transform board = lever != null ? lever.Find("TablaPalanca") : null;
            if (board == null) return;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cube = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            var mf = board.GetComponent<MeshFilter>();
            var mr = board.GetComponent<MeshRenderer>();
            Undo.RecordObjects(new Object[] { mf, mr }, "Palanca: revertir");
            mf.sharedMesh = cube;
            var original = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(OriginalMatGuid));
            if (original != null) mr.sharedMaterials = new[] { original };
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(lever.gameObject.scene);
            Debug.Log("[Lever] Palanca revertida al cubo original.", lever);
        }

        static Transform FindLever()
        {
            var sel = Selection.activeTransform;
            if (sel != null && sel.Find("TablaPalanca") != null) return sel;
            if (sel != null && sel.name == "TablaPalanca") return sel.parent;
            var t = Object.FindObjectsByType<HingeJoint>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(h => h.transform).FirstOrDefault(x => x.Find("TablaPalanca") != null);
            if (t == null) Debug.LogError("[Lever] No encontré la palanca (HingeJoint con hijo 'TablaPalanca').");
            return t;
        }

        static string Snapshot(Transform lever)
        {
            var rb = lever.GetComponent<Rigidbody>();
            var hj = lever.GetComponent<HingeJoint>();
            var sb = new System.Text.StringBuilder($"masa {(rb ? rb.mass : 0)}, bisagra {(hj ? hj.anchor + "/" + hj.axis : "-")}; ");
            foreach (var c in lever.GetComponentsInChildren<Collider>(true))
            {
                sb.Append($"{c.name}:{c.GetType().Name}");
                if (c is BoxCollider bc) sb.Append($"[{bc.size} @ {bc.center}]");
                sb.Append($" {c.transform.localPosition} x {c.transform.localScale}; ");
            }
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

            // Torneado alrededor de un eje; las normales salen hacia fuera según el sentido del perfil
            public void Lathe(int sub, Vector3 origin, Vector3 axis, IList<Vector2> rh, int seg)
            {
                axis = axis.normalized;
                Vector3 e1 = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 e2 = Vector3.Cross(axis, e1);
                float orient = Orient(rh);
                int n = rh.Count, start = V.Count;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = rh[Mathf.Max(i - 1, 0)], b = rh[Mathf.Min(i + 1, n - 1)];
                    Vector2 tan = (b - a).normalized;
                    Vector2 pn = new Vector2(-tan.y, tan.x) * orient;
                    for (int j = 0; j <= seg; j++)
                    {
                        float th = j / (float)seg * Mathf.PI * 2f;
                        Vector3 rad = e1 * Mathf.Cos(th) + e2 * Mathf.Sin(th);
                        Add(origin + axis * rh[i].y + rad * rh[i].x, rad * pn.x + axis * pn.y, new Vector2(0.5f, 0.5f));
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

            public Mesh Build(string name)
            {
                var mesh = new Mesh { name = name };
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

        // Cara del cubo en espacio canónico (a = largo, y = grosor, b = ancho); mismas fórmulas que el generador.
        static Vector3 FacePoint(string face, float s, float t)
        {
            switch (face)
            {
                case "top": return new Vector3(s - .5f, .5f, t - .5f);
                case "bottom": return new Vector3(s - .5f, -.5f, .5f - t);
                case "sideP": return new Vector3(.5f - s, t - .5f, .5f);
                case "sideN": return new Vector3(s - .5f, t - .5f, -.5f);
                case "endP": return new Vector3(.5f, t - .5f, s - .5f);
                default: return new Vector3(-.5f, t - .5f, .5f - s);
            }
        }

        static float[] Rect(Layout L, string f) => f switch
        {
            "top" => L.top, "bottom" => L.bottom, "sideP" => L.sideP, "sideN" => L.sideN, "endP" => L.endP, _ => L.endN,
        };

        static Mesh BuildBoard(Layout L, Transform board, Transform lever)
        {
            // Se modela en el espacio de la Palanca (el origen es el eje de la bisagra) y se pasa al de la tabla
            var b = new Builder(board.worldToLocalMatrix * lever.localToWorldMatrix);
            Vector3 c = board.localPosition;
            float T = board.localScale.x, Len = board.localScale.y, W = board.localScale.z;
            Vector3 ToLever(Vector3 k) => c + Vector3.Scale(board.localRotation * new Vector3(k.y * T, k.x * Len, k.z * W), Vector3.one);

            Vector2[] st = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            foreach (string face in Faces)
            {
                float[] r = Rect(L, face);
                Vector3 kc = FacePoint(face, 0.5f, 0.5f) * 2f;               // normal canónica de la cara
                Vector3 n = board.localRotation * new Vector3(kc.y, kc.x, kc.z);
                int b0 = b.V.Count;
                foreach (var p in st)
                    b.Add(ToLever(FacePoint(face, p.x, p.y)), n,
                          new Vector2(Mathf.Lerp(r[0], r[2], p.x), Mathf.Lerp(r[1], r[3], p.y)));
                b.Quad(0, b0, b0 + 1, b0 + 3, b0 + 2);
            }

            // Perno del pivote (submalla 1): eje Z en el origen de la Palanca (eje de la HingeJoint)
            var hj = lever.GetComponent<HingeJoint>();
            Vector3 pivot = hj != null ? hj.anchor : Vector3.zero;
            Vector3 axis = hj != null && hj.axis.sqrMagnitude > 0f ? hj.axis.normalized : Vector3.forward;
            float half = W * 0.5f;
            b.Lathe(1, pivot, axis, new List<Vector2>
            {
                new Vector2(0f, half + 0.032f), new Vector2(0.014f, half + 0.032f), new Vector2(0.016f, half + 0.026f),
                new Vector2(0.016f, -half - 0.026f), new Vector2(0.014f, -half - 0.032f), new Vector2(0f, -half - 0.032f),
            }, 16);
            foreach (float sgn in new[] { 1f, -1f })
            {
                // arandela + cabeza hexagonal (6 lados)
                b.Lathe(1, pivot, axis * sgn, new List<Vector2>
                {
                    new Vector2(0.012f, half + 0.0045f), new Vector2(0.045f, half + 0.0045f), new Vector2(0.046f, half + 0.002f),
                    new Vector2(0.045f, half - 0.0005f), new Vector2(0.012f, half - 0.0005f),
                }, 32);
                b.Lathe(1, pivot, axis * sgn, new List<Vector2>
                {
                    new Vector2(0f, half + 0.022f), new Vector2(0.028f, half + 0.022f), new Vector2(0.032f, half + 0.018f),
                    new Vector2(0.032f, half + 0.005f), new Vector2(0f, half + 0.005f),
                }, 6);
            }
            return b.Build("LeverBoard");
        }

        static Mesh Save(Mesh m)
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
            return m;
        }

        // --------------------------------------------------------------------
        static void ConfigureImporters()
        {
            foreach (string suffix in new[] { "basecolor", "normal", "metallic_smoothness" })
            {
                if (!(AssetImporter.GetAtPath($"{TexDir}/Lever_{suffix}.png") is TextureImporter ti))
                {
                    Debug.LogWarning($"[Lever] Falta {TexDir}/Lever_{suffix}.png");
                    continue;
                }
                ti.textureType = suffix == "normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = suffix == "basecolor";
                if (suffix == "metallic_smoothness")
                {
                    ti.alphaSource = TextureImporterAlphaSource.FromInput;
                    ti.alphaIsTransparency = false;
                }
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.mipmapEnabled = true;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 8;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
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

        static Material BoardMaterial()
        {
            var mat = LoadOrCreate("M_LeverBoard", out bool urp);
            Texture2D T(string s) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Lever_{s}.png");
            var baseMap = T("basecolor"); var nrm = T("normal"); var ms = T("metallic_smoothness");
            mat.SetTexture(urp ? "_BaseMap" : "_MainTex", baseMap);
            mat.SetColor(urp ? "_BaseColor" : "_Color", Color.white);
            mat.SetTexture("_BumpMap", nrm);
            mat.SetTexture("_MetallicGlossMap", ms);
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
            Keyword(mat, "_NORMALMAP", nrm != null);
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
