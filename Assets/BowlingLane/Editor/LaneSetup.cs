using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BowlingLane.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL de la pista (Pista/Suelo): reemplaza la malla del cubo por una caja 1x1x1
    /// idéntica pero con UV pensadas para la textura de la pista, y asigna el material.
    /// No toca Transform, BoxCollider (ni su material físico) ni ningún otro objeto.
    ///
    /// UV de la cara superior (+Y):  u = 0.5 - x  (u=0 en +X = entrada de la bola, u=1 en -X = pinos)
    ///                               v = 0.5 - z  (fila superior de la imagen = -Z)
    /// Las caras laterales e inferior usan una franja lisa de tablón para que no se vea estirado.
    /// </summary>
    public static class LaneSetup
    {
        const string Root = "Assets/BowlingLane";
        const string TexDir = Root + "/Textures";
        const string MeshPath = Root + "/Mesh/LaneMesh.asset";
        const string MatPath = Root + "/Materials/M_BowlingLane.mat";
        const string ParentName = "Pista";
        const string FloorName = "Suelo";
        static readonly Vector2 SideUV = new Vector2(0.45f, 0.5f);   // zona de tablón sin marcas

        [MenuItem("Tools/Bowling Lane/Apply to Pista (visual only)", priority = 0)]
        public static void Apply()
        {
            ConfigureImporters();
            Mesh mesh = BuildMesh();
            Material mat = BuildMaterial();
            GameObject floor = FindFloor();
            if (floor == null) return;

            var mf = floor.GetComponent<MeshFilter>();
            var mr = floor.GetComponent<MeshRenderer>();
            string before = Snapshot(floor);
            Undo.RecordObjects(new Object[] { mf, mr }, "Pista: malla y material");
            mf.sharedMesh = mesh;
            mr.sharedMaterials = new[] { mat };
            EditorUtility.SetDirty(mf);
            EditorUtility.SetDirty(mr);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(floor.scene);
            string after = Snapshot(floor);

            if (before == after) Debug.Log($"[Lane] Listo. Física sin cambios: {after}", floor);
            else Debug.LogError($"[Lane] ¡La física cambió! Antes: {before} / Después: {after}", floor);

            var s = floor.transform.lossyScale;
            if (Mathf.Abs(s.x / s.z - 5f) > 0.05f)
                Debug.LogWarning($"[Lane] La pista mide {s.x:0.##} x {s.z:0.##}; la textura se hizo para 8 x 1.6. " +
                                 "Regenera la textura con esas medidas para que no se estire.", floor);
        }

        [MenuItem("Tools/Bowling Lane/Revert Pista to plain cube", priority = 1)]
        public static void Revert()
        {
            GameObject floor = FindFloor();
            if (floor == null) return;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cube = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            var mf = floor.GetComponent<MeshFilter>();
            var mr = floor.GetComponent<MeshRenderer>();
            Undo.RecordObjects(new Object[] { mf, mr }, "Pista: revertir");
            mf.sharedMesh = cube;
            var original = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Pisos/M_Piso.mat");
            if (original != null) mr.sharedMaterials = new[] { original };
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(floor.scene);
            Debug.Log("[Lane] Pista revertida al cubo original.", floor);
        }

        // --------------------------------------------------------------------
        static GameObject FindFloor()
        {
            var sel = Selection.activeGameObject;
            if (sel != null && sel.GetComponent<BoxCollider>() != null && sel.GetComponent<MeshFilter>() != null
                && sel.name == FloorName)
                return sel;
            var floor = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => t.name == FloorName && t.parent != null && t.parent.name == ParentName)
                .Select(t => t.gameObject).FirstOrDefault();
            if (floor == null)
                Debug.LogError($"[Lane] No encontré '{ParentName}/{FloorName}'. Selecciona el suelo de la pista.");
            return floor;
        }

        static string Snapshot(GameObject go)
        {
            var t = go.transform;
            var bc = go.GetComponent<BoxCollider>();
            return $"pos {t.localPosition}, escala {t.localScale}, " +
                   $"collider {(bc ? bc.size + " @ " + bc.center : "-")}, " +
                   $"material físico {(bc && bc.sharedMaterial ? bc.sharedMaterial.name : "ninguno")}";
        }

        // --------------------------------------------------------------------
        static Mesh BuildMesh()
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            void Face(Vector3 n, Vector3 right, Vector3 up, System.Func<Vector3, Vector2> uv)
            {
                int b = verts.Count;
                Vector3 c = n * 0.5f;
                Vector3[] corners =
                {
                    c - right * 0.5f - up * 0.5f, c + right * 0.5f - up * 0.5f,
                    c + right * 0.5f + up * 0.5f, c - right * 0.5f + up * 0.5f,
                };
                foreach (var p in corners) { verts.Add(p); uvs.Add(uv(p)); }
                tris.AddRange(new[] { b, b + 3, b + 2, b, b + 2, b + 1 });   // horario visto de frente
            }

            // Cara superior: right = -X, up = -Z (visto desde arriba, sin espejo)
            Face(Vector3.up, Vector3.left, Vector3.back, p => new Vector2(0.5f - p.x, 0.5f - p.z));
            // Resto de caras: color liso de tablón (miden 2 cm de alto, apenas se ven)
            System.Func<Vector3, Vector2> flat = _ => SideUV;
            Face(Vector3.down, Vector3.left, Vector3.forward, flat);
            Face(Vector3.right, Vector3.forward, Vector3.up, flat);
            Face(Vector3.left, Vector3.back, Vector3.up, flat);
            Face(Vector3.forward, Vector3.left, Vector3.up, flat);
            Face(Vector3.back, Vector3.right, Vector3.up, flat);

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            bool isNew = mesh == null;
            if (isNew) mesh = new Mesh();
            mesh.Clear();
            mesh.name = "LaneMesh";
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
            return mesh;
        }

        static void ConfigureImporters()
        {
            void Tex(string file, System.Action<TextureImporter> tweak)
            {
                if (!(AssetImporter.GetAtPath($"{TexDir}/{file}") is TextureImporter ti))
                {
                    Debug.LogWarning($"[Lane] Falta {TexDir}/{file}");
                    return;
                }
                ti.textureType = TextureImporterType.Default;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.mipmapEnabled = true;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 16;                  // la pista se ve en ángulo rasante: anisotrópico alto
                ti.maxTextureSize = 8192;
                ti.npotScale = TextureImporterNPOTScale.None;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                tweak(ti);
                ti.SaveAndReimport();
            }
            Tex("Lane_basecolor.png", ti => ti.sRGBTexture = true);
            Tex("Lane_normal.png", ti => ti.textureType = TextureImporterType.NormalMap);
            Tex("Lane_metallic_smoothness.png", ti =>
            {
                ti.sRGBTexture = false;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = false;
            });
        }

        static Material BuildMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            bool urp = shader != null;
            if (!urp) shader = Shader.Find("Standard");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null)
            {
                EnsureFolder(Path.GetDirectoryName(MatPath));
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, MatPath);
            }
            else mat.shader = shader;

            var baseMap = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Lane_basecolor.png");
            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Lane_normal.png");
            var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Lane_metallic_smoothness.png");
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
            AssetDatabase.SaveAssets();
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
