using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BowlingBall.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL de la bola de bolos: reemplaza la malla (esfera con 3 agujeros) y los materiales
    /// del MeshRenderer. No toca Transform, SphereCollider ni Rigidbody. El Rigidbody calcula masa,
    /// centro de masa e inercia a partir del collider, así que la física queda exactamente igual.
    ///
    /// La malla se normaliza a diámetro 1 centrada en el origen = mismo tamaño que la esfera de Unity
    /// y que un SphereCollider de radio 0.5.
    /// </summary>
    public static class BowlingBallSetup
    {
        const string Root = "Assets/BowlingBall";
        const string FbxPath = Root + "/Models/BowlingBall.fbx";
        const string TexDir = Root + "/Textures";
        const string MeshPath = Root + "/Mesh/BowlingBallMesh.asset";
        const string MatPath = Root + "/Materials/M_BowlingBall.mat";
        const string HoleMatPath = Root + "/Materials/M_BowlingBall_Holes.mat";
        const string OriginalMatPath = "Assets/Materials/Genericos/M_BolaBolos.mat";
        const string TargetName = "BolaBolos";

        // --------------------------------------------------------------------
        [MenuItem("Tools/Bowling Ball/Apply to BolaBolos (visual only)", priority = 0)]
        public static void Apply()
        {
            ConfigureImporters();
            Mesh mesh = BuildNormalizedMesh();
            if (mesh == null) return;
            Material[] mats = { CreateBallMaterial(), CreateHoleMaterial() };

            GameObject ball = FindTarget();
            if (ball == null) return;
            var mf = ball.GetComponent<MeshFilter>();
            var mr = ball.GetComponent<MeshRenderer>();
            if (mf == null || mr == null)
            {
                Debug.LogError($"[BowlingBall] '{ball.name}' no tiene MeshFilter/MeshRenderer.", ball);
                return;
            }

            string before = PhysicsSnapshot(ball);
            Undo.RecordObjects(new Object[] { mf, mr }, "Bola de bolos: malla y material");
            mf.sharedMesh = mesh;
            mr.sharedMaterials = mats;                 // 2 submallas: superficie + interior de los agujeros
            EditorUtility.SetDirty(mf);
            EditorUtility.SetDirty(mr);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(ball.scene);
            string after = PhysicsSnapshot(ball);

            if (before == after)
                Debug.Log($"[BowlingBall] Listo. Física sin cambios: {after}", ball);
            else
                Debug.LogError($"[BowlingBall] ¡La física cambió! Antes: {before} / Después: {after}", ball);
        }

        [MenuItem("Tools/Bowling Ball/Revert BolaBolos to plain sphere", priority = 1)]
        public static void Revert()
        {
            GameObject ball = FindTarget();
            if (ball == null) return;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh sphere = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);

            var mf = ball.GetComponent<MeshFilter>();
            var mr = ball.GetComponent<MeshRenderer>();
            Undo.RecordObjects(new Object[] { mf, mr }, "Bola de bolos: revertir");
            mf.sharedMesh = sphere;
            var original = AssetDatabase.LoadAssetAtPath<Material>(OriginalMatPath);
            if (original != null) mr.sharedMaterials = new[] { original };
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(ball.scene);
            Debug.Log("[BowlingBall] Revertida a la esfera original.", ball);
        }

        // --------------------------------------------------------------------
        static GameObject FindTarget()
        {
            // Prioridad: objeto seleccionado con SphereCollider; si no, el que se llame BolaBolos.
            var sel = Selection.activeGameObject;
            if (sel != null && sel.GetComponent<SphereCollider>() != null && sel.GetComponent<MeshFilter>() != null)
                return sel;
            var found = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => t.name == TargetName).Select(t => t.gameObject).FirstOrDefault();
            if (found == null)
                Debug.LogError($"[BowlingBall] No encontré '{TargetName}'. Selecciona la bola y vuelve a intentarlo.");
            return found;
        }

        static string PhysicsSnapshot(GameObject go)
        {
            var t = go.transform;
            var sc = go.GetComponent<SphereCollider>();
            var rb = go.GetComponent<Rigidbody>();
            return $"escala {t.localScale}, collider r={(sc ? sc.radius.ToString("0.###") : "-")} " +
                   $"c={(sc ? sc.center.ToString() : "-")}, masa {(rb ? rb.mass.ToString("0.###") : "-")}, " +
                   $"CoM auto {(rb ? rb.automaticCenterOfMass.ToString() : "-")}, " +
                   $"inercia auto {(rb ? rb.automaticInertiaTensor.ToString() : "-")}";
        }

        // --------------------------------------------------------------------
        static void ConfigureImporters()
        {
            if (AssetImporter.GetAtPath(FbxPath) is ModelImporter mi)
            {
                mi.globalScale = 1f;
                mi.useFileScale = true;
                mi.importNormals = ModelImporterNormals.Import;
                mi.importTangents = ModelImporterTangents.CalculateMikk;
                mi.materialImportMode = ModelImporterMaterialImportMode.None;
                mi.importAnimation = false;
                mi.animationType = ModelImporterAnimationType.None;
                mi.addCollider = false;
                mi.isReadable = true;                    // para copiar los vértices al normalizar
                mi.SaveAndReimport();
            }

            Tex("BowlingBall_basecolor.png", ti =>
            {
                ti.sRGBTexture = true;
                ti.wrapModeU = TextureWrapMode.Repeat;   // la costura en u=0/1 se repite sin corte
                ti.wrapModeV = TextureWrapMode.Clamp;
            });
            Tex("BowlingBall_metallic_smoothness.png", ti =>
            {
                ti.sRGBTexture = false;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = false;
                ti.wrapModeU = TextureWrapMode.Repeat;
                ti.wrapModeV = TextureWrapMode.Clamp;
            });
            Tex("BowlingBall_normal.png", ti =>
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.wrapModeU = TextureWrapMode.Repeat;
                ti.wrapModeV = TextureWrapMode.Clamp;
            });
        }

        static void Tex(string file, System.Action<TextureImporter> tweak)
        {
            if (!(AssetImporter.GetAtPath($"{TexDir}/{file}") is TextureImporter ti))
            {
                Debug.LogWarning($"[BowlingBall] Falta {TexDir}/{file}");
                return;
            }
            ti.textureType = TextureImporterType.Default;
            ti.mipmapEnabled = true;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 8;
            ti.maxTextureSize = 4096;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            tweak(ti);
            ti.SaveAndReimport();
        }

        static Mesh BuildNormalizedMesh()
        {
            Mesh src = AssetDatabase.LoadAllAssetsAtPath(FbxPath).OfType<Mesh>().FirstOrDefault();
            if (src == null) { Debug.LogError($"[BowlingBall] No hay malla en {FbxPath}"); return null; }

            Mesh m = Object.Instantiate(src);
            m.name = "BowlingBallMesh";
            Bounds b = src.bounds;
            float scale = 1f / Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));   // diámetro -> 1
            var verts = src.vertices;
            for (int i = 0; i < verts.Length; i++)
                verts[i] = (verts[i] - b.center) * scale;
            m.vertices = verts;
            m.RecalculateBounds();
            m.RecalculateTangents();

            EnsureFolder(Path.GetDirectoryName(MeshPath));
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(m, existing);
                Object.DestroyImmediate(m);
                m = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(m, MeshPath);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[BowlingBall] Malla normalizada: diámetro {m.bounds.size.x:0.000} " +
                      $"(escala aplicada {scale:0.####}), {m.subMeshCount} submallas, {m.vertexCount} vértices.");
            return m;
        }

        static Material CreateBallMaterial()
        {
            var mat = LoadOrCreate(MatPath);
            var baseMap = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/BowlingBall_basecolor.png");
            var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/BowlingBall_metallic_smoothness.png");
            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/BowlingBall_normal.png");
            if (mat.shader.name.StartsWith("Universal"))
            {
                mat.SetFloat("_WorkflowMode", 1f);
                mat.SetTexture("_BaseMap", baseMap);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetTexture("_MetallicGlossMap", ms);
                mat.SetFloat("_Smoothness", 1f);
                mat.SetFloat("_SmoothnessTextureChannel", 0f);
                mat.SetTexture("_BumpMap", nrm);
                mat.SetFloat("_BumpScale", 0.6f);
                mat.SetFloat("_EnvironmentReflections", 1f);
                mat.SetFloat("_SpecularHighlights", 1f);
                Keyword(mat, "_METALLICSPECGLOSSMAP", ms != null);
                Keyword(mat, "_NORMALMAP", nrm != null);
            }
            else
            {
                mat.SetTexture("_MainTex", baseMap);
                mat.SetTexture("_MetallicGlossMap", ms);
                mat.SetFloat("_GlossMapScale", 1f);
                mat.SetTexture("_BumpMap", nrm);
                Keyword(mat, "_METALLICGLOSSMAP", ms != null);
                Keyword(mat, "_NORMALMAP", nrm != null);
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material CreateHoleMaterial()
        {
            var mat = LoadOrCreate(HoleMatPath);
            var c = new Color(0.012f, 0.010f, 0.014f);
            if (mat.shader.name.StartsWith("Universal"))
            {
                mat.SetColor("_BaseColor", c);
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Smoothness", 0.25f);
            }
            else
            {
                mat.SetColor("_Color", c);
                mat.SetFloat("_Glossiness", 0.25f);
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material LoadOrCreate(string path)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) { mat.shader = shader; return mat; }
            EnsureFolder(Path.GetDirectoryName(path));
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
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

        static void Keyword(Material m, string kw, bool on)
        {
            if (on) m.EnableKeyword(kw); else m.DisableKeyword(kw);
        }
    }
}
