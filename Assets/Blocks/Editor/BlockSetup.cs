using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blocks.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL de la torre: cada bloque recibe una malla 1x1x1 (idéntica al cubo, el BoxCollider
    /// sigue igual) con UV hacia el atlas de madera, y uno de los materiales M_Block_XX al azar.
    /// Hay dos mallas: BlockMesh_LongX (bloque largo en X) y BlockMesh_LongZ (largo en Z), para que la veta
    /// siempre siga el lado largo. No toca Transform, BoxCollider ni Rigidbody.
    /// </summary>
    public static class BlockSetup
    {
        const string Root = "Assets/Blocks";
        const string TexDir = Root + "/Textures";
        const string LayoutPath = TexDir + "/block_layout.json";
        const string MeshDir = Root + "/Mesh";
        const string MatDir = Root + "/Materials";
        static readonly string[] Faces = { "top", "bottom", "sideP", "sideN", "endP", "endN" };

        [System.Serializable]
        class Layout { public float[] top, bottom, sideP, sideN, endP, endN; }

        // --------------------------------------------------------------------
        [MenuItem("Tools/Tower Blocks/Apply to towers (visual only)", priority = 0)]
        public static void Apply()
        {
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(LayoutPath);
            if (json == null) { Debug.LogError($"[Blocks] Falta {LayoutPath}"); return; }
            var L = JsonUtility.FromJson<Layout>(json.text);
            Mesh longX = BuildMesh(L, alongX: true);
            Mesh longZ = BuildMesh(L, alongX: false);
            ConfigureImporters();
            List<Material> mats = BuildMaterials();
            if (mats.Count == 0) { Debug.LogError("[Blocks] No encontré texturas Block_XX."); return; }

            var towers = Object.FindObjectsByType<TowerBuilder>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (towers.Length == 0) { Debug.LogError("[Blocks] No hay ningún TowerBuilder en la escena."); return; }

            int changed = 0, mismatches = 0;
            foreach (var tower in towers)
            {
                var so = new SerializedObject(tower);
                so.FindProperty("blockMeshLongX").objectReferenceValue = longX;
                so.FindProperty("blockMeshLongZ").objectReferenceValue = longZ;
                var arr = so.FindProperty("blockMaterials");
                arr.arraySize = mats.Count;
                for (int i = 0; i < mats.Count; i++) arr.GetArrayElementAtIndex(i).objectReferenceValue = mats[i];
                so.ApplyModifiedProperties();

                var rng = new System.Random(tower.name.GetHashCode() & 0x7fffffff);
                var bag = new List<Material>();
                foreach (Transform block in tower.transform)
                {
                    if (block.GetComponent<BoxCollider>() == null || block.GetComponent<MeshFilter>() == null) continue;
                    string before = Snapshot(block.gameObject);
                    if (bag.Count == 0) bag.AddRange(mats);
                    int k = rng.Next(bag.Count);
                    Material m = bag[k];
                    bag.RemoveAt(k);
                    Vector3 s = block.localScale;
                    SetVisual(block, s.x > s.z ? longX : longZ, m);
                    if (Snapshot(block.gameObject) != before) mismatches++;
                    changed++;
                }
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(tower.gameObject.scene);
            }
            if (mismatches == 0) Debug.Log($"[Blocks] {changed} bloques actualizados. Física sin cambios.");
            else Debug.LogError($"[Blocks] {mismatches} bloques cambiaron su física.");
        }

        [MenuItem("Tools/Tower Blocks/Revert towers to plain cubes", priority = 1)]
        public static void Revert()
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cube = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            foreach (var tower in Object.FindObjectsByType<TowerBuilder>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var so = new SerializedObject(tower);
                so.FindProperty("blockMeshLongX").objectReferenceValue = null;
                so.FindProperty("blockMeshLongZ").objectReferenceValue = null;
                so.FindProperty("blockMaterials").arraySize = 0;
                so.ApplyModifiedProperties();
                var original = so.FindProperty("blockMaterial").objectReferenceValue as Material;
                foreach (Transform block in tower.transform)
                    if (block.GetComponent<BoxCollider>() != null) SetVisual(block, cube, original);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(tower.gameObject.scene);
            }
            Debug.Log("[Blocks] Torres revertidas a cubos con su material original.");
        }

        static void SetVisual(Transform block, Mesh mesh, Material mat)
        {
            var mf = block.GetComponent<MeshFilter>();
            var mr = block.GetComponent<MeshRenderer>();
            Undo.RecordObjects(new Object[] { mf, mr }, "Torre: aspecto");
            mf.sharedMesh = mesh;
            if (mat != null) mr.sharedMaterials = new[] { mat };
        }

        static string Snapshot(GameObject go)
        {
            var t = go.transform;
            var c = go.GetComponent<BoxCollider>();
            var rb = go.GetComponent<Rigidbody>();
            return $"{t.localPosition}|{t.localRotation}|{t.localScale}|{c.size}|{c.center}|" +
                   $"{(c.sharedMaterial ? c.sharedMaterial.name : "-")}|{(rb ? rb.mass.ToString() : "-")}";
        }

        // --------------------------------------------------------------------
        // Cubo unidad con UV por cara. Espacio canónico (a = largo, y = alto, b = ancho);
        // mismas fórmulas que generate_block_textures.py (face_points).
        static Vector3 FacePoint(string face, float s, float t)
        {
            switch (face)
            {
                case "top": return new Vector3(s - .5f, .5f, t - .5f);
                case "bottom": return new Vector3(s - .5f, -.5f, .5f - t);
                case "sideP": return new Vector3(.5f - s, t - .5f, .5f);
                case "sideN": return new Vector3(s - .5f, t - .5f, -.5f);
                case "endP": return new Vector3(.5f, t - .5f, s - .5f);
                default: return new Vector3(-.5f, t - .5f, .5f - s);      // endN
            }
        }

        static float[] Rect(Layout L, string face) => face switch
        {
            "top" => L.top, "bottom" => L.bottom, "sideP" => L.sideP,
            "sideN" => L.sideN, "endP" => L.endP, _ => L.endN,
        };

        static Mesh BuildMesh(Layout L, bool alongX)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            Vector2[] st = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };

            // canónico (a, y, b) -> Unity: largo en X => (a, y, b); largo en Z => (b, y, a)
            Vector3 ToUnity(Vector3 c) => alongX ? c : new Vector3(c.z, c.y, c.x);

            foreach (string face in Faces)
            {
                float[] r = Rect(L, face);
                int b0 = verts.Count;
                Vector3 center = Vector3.zero;
                foreach (var p in st)
                {
                    Vector3 v = ToUnity(FacePoint(face, p.x, p.y));
                    verts.Add(v);
                    center += v;
                    uvs.Add(new Vector2(Mathf.Lerp(r[0], r[2], p.x), Mathf.Lerp(r[1], r[3], p.y)));
                }
                Vector3 outward = (center / 4f).normalized;          // centro de la cara = dirección de su normal
                Vector3 n = Vector3.Cross(verts[b0 + 1] - verts[b0], verts[b0 + 2] - verts[b0]);
                if (Vector3.Dot(n, outward) > 0f)
                    tris.AddRange(new[] { b0, b0 + 1, b0 + 2, b0, b0 + 2, b0 + 3 });
                else
                    tris.AddRange(new[] { b0, b0 + 2, b0 + 1, b0, b0 + 3, b0 + 2 });
            }

            string path = $"{MeshDir}/BlockMesh_{(alongX ? "LongX" : "LongZ")}.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool isNew = mesh == null;
            if (isNew) mesh = new Mesh();
            mesh.Clear();
            mesh.name = Path.GetFileNameWithoutExtension(path);
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            if (isNew)
            {
                EnsureFolder(MeshDir);
                AssetDatabase.CreateAsset(mesh, path);
            }
            EditorUtility.SetDirty(mesh);
            AssetDatabase.SaveAssets();
            return mesh;
        }

        // --------------------------------------------------------------------
        static void ConfigureImporters()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TexDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) continue;
                string file = Path.GetFileNameWithoutExtension(path);
                if (!file.StartsWith("Block_")) continue;
                ti.textureType = file.EndsWith("_normal") ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = file.EndsWith("_basecolor");
                if (file.EndsWith("_metallic_smoothness"))
                {
                    ti.alphaSource = TextureImporterAlphaSource.FromInput;
                    ti.alphaIsTransparency = false;
                }
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.mipmapEnabled = true;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 4;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
        }

        static List<Material> BuildMaterials()
        {
            EnsureFolder(MatDir);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            bool urp = shader != null;
            if (!urp) shader = Shader.Find("Standard");
            var result = new List<Material>();
            foreach (string folder in AssetDatabase.GetSubFolders(TexDir).Where(f => Path.GetFileName(f).StartsWith("Block_")).OrderBy(f => f))
            {
                string name = Path.GetFileName(folder);
                Texture2D Tex(string suffix) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{name}_{suffix}.png");
                var baseMap = Tex("basecolor");
                var nrm = Tex("normal");
                var ms = Tex("metallic_smoothness");
                if (baseMap == null) continue;

                string matPath = $"{MatDir}/M_{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, matPath); }
                else mat.shader = shader;
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
                mat.enableInstancing = true;
                EditorUtility.SetDirty(mat);
                result.Add(mat);
            }
            AssetDatabase.SaveAssets();
            return result;
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
