using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Books.EditorTools
{
    /// <summary>
    /// Prepara los libros envejecidos para BookRow:
    ///   1) BookMesh.asset: caja 1x1x1 centrada (igual que el cubo de Unity, mismo collider) pero con
    ///      UV por cara hacia el atlas: tapas+lomo continuos, canto delantero, cabeza y pie.
    ///   2) Ajustes de importación de las texturas.
    ///   3) Un material URP/Lit por libro.
    ///   4) Asigna malla y materiales a los BookRow de la escena abierta.
    ///
    /// Orientación (espacio local del libro): grosor en X, alto en Y, fondo en Z.
    ///   Lomo hacia -Z · tapa delantera +X · tapa trasera -X · canto de páginas +Z.
    /// </summary>
    public static class BookSetup
    {
        const string Root = "Assets/Books";
        const string TexDir = Root + "/Textures";
        const string LayoutPath = TexDir + "/book_layout.json";
        const string MeshPath = Root + "/Mesh/BookMesh.asset";
        const string MatDir = Root + "/Materials";

        [System.Serializable]
        class BookLayout
        {
            public int atlasSize;
            public float[] back, spine, front, fore, top, bottom;   // [u0, v0, u1, v1]
        }

        [MenuItem("Tools/Books/Run All Steps", priority = 0)]
        public static void RunAll()
        {
            var mesh = CreateMesh();
            if (mesh == null) return;
            ConfigureImporters();
            var mats = CreateMaterials();
            AssignToBookRows(mesh, mats);
        }

        // --------------------------------------------------------------------
        [MenuItem("Tools/Books/1. Create Book Mesh", priority = 20)]
        static void CreateMeshMenu() => CreateMesh();

        public static Mesh CreateMesh()
        {
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(LayoutPath);
            if (json == null) { Debug.LogError($"[Books] Falta {LayoutPath}"); return null; }
            var L = JsonUtility.FromJson<BookLayout>(json.text);

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            // Cara de la caja unitaria. right/up definen cómo se ve desde fuera; uv(p) da (0-1) dentro del rect.
            void Face(Vector3 n, Vector3 right, Vector3 up, float[] r, System.Func<Vector3, Vector2> uv)
            {
                int b = verts.Count;
                Vector3 c = n * 0.5f;
                Vector3[] corners =
                {
                    c - right * 0.5f - up * 0.5f, c + right * 0.5f - up * 0.5f,
                    c + right * 0.5f + up * 0.5f, c - right * 0.5f + up * 0.5f,
                };
                foreach (var p in corners)
                {
                    verts.Add(p);
                    Vector2 t = uv(p);
                    uvs.Add(new Vector2(Mathf.Lerp(r[0], r[2], t.x), Mathf.Lerp(r[1], r[3], t.y)));
                }
                tris.AddRange(new[] { b, b + 3, b + 2, b, b + 2, b + 1 });   // horario visto de frente
            }

            Face(Vector3.back,    Vector3.right, Vector3.up,      L.spine,  p => new Vector2(p.x + 0.5f, p.y + 0.5f));
            Face(Vector3.right,   Vector3.forward, Vector3.up,    L.front,  p => new Vector2(p.z + 0.5f, p.y + 0.5f));
            Face(Vector3.left,    Vector3.back, Vector3.up,       L.back,   p => new Vector2(0.5f - p.z, p.y + 0.5f));
            Face(Vector3.forward, Vector3.left, Vector3.up,       L.fore,   p => new Vector2(p.y + 0.5f, p.x + 0.5f));
            Face(Vector3.up,      Vector3.right, Vector3.forward, L.top,    p => new Vector2(p.z + 0.5f, 0.5f - p.x));
            Face(Vector3.down,    Vector3.left, Vector3.forward,  L.bottom, p => new Vector2(p.z + 0.5f, p.x + 0.5f));

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            bool isNew = mesh == null;
            if (isNew) mesh = new Mesh();
            mesh.Clear();
            mesh.name = "BookMesh";
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            if (isNew)
            {
                EnsureFolder(Path.GetDirectoryName(MeshPath));
                AssetDatabase.CreateAsset(mesh, MeshPath);
            }
            EditorUtility.SetDirty(mesh);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Books] Malla creada en {MeshPath}");
            return mesh;
        }

        // --------------------------------------------------------------------
        [MenuItem("Tools/Books/2. Configure Texture Importers", priority = 21)]
        public static void ConfigureImporters()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TexDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) continue;
                string file = Path.GetFileNameWithoutExtension(path);
                if (!file.StartsWith("Book_")) continue;

                ti.textureType = file.EndsWith("_normal") ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = file.EndsWith("_basecolor");
                if (file.EndsWith("_metallic_smoothness"))
                {
                    ti.alphaSource = TextureImporterAlphaSource.FromInput;   // A = smoothness
                    ti.alphaIsTransparency = false;
                }
                ti.wrapMode = TextureWrapMode.Clamp;                         // es un atlas
                ti.mipmapEnabled = true;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 4;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
        }

        // --------------------------------------------------------------------
        [MenuItem("Tools/Books/3. Create Materials", priority = 22)]
        static void CreateMaterialsMenu() => CreateMaterials();

        public static List<Material> CreateMaterials()
        {
            EnsureFolder(MatDir);
            Shader urp = Shader.Find("Universal Render Pipeline/Lit");
            Shader shader = urp != null ? urp : Shader.Find("Standard");
            var result = new List<Material>();

            var folders = AssetDatabase.GetSubFolders(TexDir).Where(f => Path.GetFileName(f).StartsWith("Book_")).OrderBy(f => f);
            foreach (string folder in folders)
            {
                string name = Path.GetFileName(folder);
                Texture2D Tex(string suffix) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{name}_{suffix}.png");
                var baseMap = Tex("basecolor");
                var normal = Tex("normal");
                var ms = Tex("metallic_smoothness");
                var ao = Tex("ao");
                if (baseMap == null) { Debug.LogWarning($"[Books] {name}: falta basecolor"); continue; }

                string matPath = $"{MatDir}/M_{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, matPath); }
                else mat.shader = shader;

                if (urp != null)
                {
                    mat.SetFloat("_WorkflowMode", 1f);
                    mat.SetTexture("_BaseMap", baseMap);
                    mat.SetColor("_BaseColor", Color.white);
                    mat.SetTexture("_BumpMap", normal);
                    mat.SetFloat("_BumpScale", 1f);
                    mat.SetTexture("_MetallicGlossMap", ms);
                    mat.SetFloat("_Smoothness", 1f);
                    mat.SetFloat("_SmoothnessTextureChannel", 0f);
                    mat.SetTexture("_OcclusionMap", ao);
                    mat.SetFloat("_OcclusionStrength", 1f);
                    Keyword(mat, "_NORMALMAP", normal != null);
                    Keyword(mat, "_METALLICSPECGLOSSMAP", ms != null);
                    Keyword(mat, "_OCCLUSIONMAP", ao != null);
                }
                else
                {
                    mat.SetTexture("_MainTex", baseMap);
                    mat.SetTexture("_BumpMap", normal);
                    mat.SetTexture("_MetallicGlossMap", ms);
                    mat.SetFloat("_GlossMapScale", 1f);
                    mat.SetTexture("_OcclusionMap", ao);
                    Keyword(mat, "_NORMALMAP", normal != null);
                    Keyword(mat, "_METALLICGLOSSMAP", ms != null);
                }
                mat.enableInstancing = true;
                EditorUtility.SetDirty(mat);
                result.Add(mat);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Books] {result.Count} materiales en {MatDir}");
            return result;
        }

        // --------------------------------------------------------------------
        [MenuItem("Tools/Books/4. Assign to BookRows in Scene", priority = 23)]
        public static void AssignMenu()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            var mats = AssetDatabase.FindAssets("t:Material", new[] { MatDir })
                .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(m => m != null && m.name.StartsWith("M_Book_"))
                .OrderBy(m => m.name).ToList();
            AssignToBookRows(mesh, mats);
        }

        static void AssignToBookRows(Mesh mesh, List<Material> mats)
        {
            var rows = Object.FindObjectsByType<BookRow>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (rows.Length == 0)
            {
                Debug.LogWarning("[Books] No hay ningún BookRow en la escena abierta. Asigna la malla y los materiales a mano.");
                return;
            }
            foreach (var row in rows)
            {
                var so = new SerializedObject(row);
                so.FindProperty("bookMesh").objectReferenceValue = mesh;
                var arr = so.FindProperty("materials");
                arr.arraySize = mats.Count;
                for (int i = 0; i < mats.Count; i++)
                    arr.GetArrayElementAtIndex(i).objectReferenceValue = mats[i];
                so.ApplyModifiedProperties();
                Debug.Log($"[Books] '{row.name}': malla + {mats.Count} materiales asignados. " +
                          "Usa clic derecho en BookRow > 'Generar libros'.", row);
            }
        }

        // --------------------------------------------------------------------
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
