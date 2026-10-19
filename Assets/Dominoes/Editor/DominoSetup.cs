using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_0_OR_NEWER
using PhysMat = UnityEngine.PhysicsMaterial;
using PhysMatCombine = UnityEngine.PhysicsMaterialCombine;
#else
using PhysMat = UnityEngine.PhysicMaterial;
using PhysMatCombine = UnityEngine.PhysicMaterialCombine;
#endif

namespace Dominoes.EditorTools
{
    /// <summary>
    /// Configura el set de dominó envejecido:
    ///   1) ajustes de importación de texturas y del FBX,
    ///   2) material URP/Lit (o Standard si el proyecto es Built-in) con GPU Instancing,
    ///   3) 28 prefabs con collider, rigidbody y material físico,
    ///   4) (opcional) una fila de prueba para verificar la caída en cadena.
    ///
    /// Estructura esperada:
    ///   Assets/Dominoes/Textures/domino_*.png
    ///   Assets/Dominoes/Models/dominoes.fbx
    /// </summary>
    public static class DominoSetup
    {
        const string Root = "Assets/Dominoes";
        const string TexDir = Root + "/Textures";
        const string FbxPath = Root + "/Models/dominoes.fbx";
        const string MatPath = Root + "/Materials/M_Domino_Aged.mat";
        const string PhysMatPath = Root + "/Materials/PM_Domino.asset";
        const string PrefabDir = Root + "/Prefabs";

        // Tamaño real: 48 x 24 x 7,5 mm. Valores físicos razonables para una ficha de ~15 g.
        const float DominoMassKg = 0.015f;
        const float ContactOffset = 0.0008f;   // el default (0.01 m) es mayor que medio grosor de la ficha

        // --------------------------------------------------------------------
        [MenuItem("Tools/Dominoes/Run All Steps", priority = 0)]
        public static void RunAll()
        {
            ConfigureImporters();
            var mat = CreateOrUpdateMaterial();
            CreatePrefabs(mat);
            Debug.Log("[Dominoes] Listo: importadores, material y 28 prefabs.");
        }

        // --------------------------------------------------------------------
        [MenuItem("Tools/Dominoes/1. Configure Importers", priority = 20)]
        public static void ConfigureImporters()
        {
            ConfigureTexture("domino_basecolor.png", ti => { ti.sRGBTexture = true; });
            ConfigureTexture("domino_normal.png", ti => { ti.textureType = TextureImporterType.NormalMap; });
            ConfigureTexture("domino_metallic_smoothness.png", ti =>
            {
                ti.sRGBTexture = false;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;   // A = smoothness
                ti.alphaIsTransparency = false;
            });
            ConfigureTexture("domino_ao.png", ti => { ti.sRGBTexture = false; });

            if (AssetImporter.GetAtPath(FbxPath) is ModelImporter mi)
            {
                mi.globalScale = 1f;
                mi.useFileScale = true;
                mi.importNormals = ModelImporterNormals.Import;            // normales ponderadas de Blender
                mi.importTangents = ModelImporterTangents.CalculateMikk;   // mismo espacio tangente que Blender
                mi.materialImportMode = ModelImporterMaterialImportMode.None;
                mi.importAnimation = false;
                mi.animationType = ModelImporterAnimationType.None;
                mi.addCollider = false;
                mi.isReadable = false;
                mi.SaveAndReimport();
            }
            else
            {
                Debug.LogWarning($"[Dominoes] No encontré el FBX en {FbxPath}");
            }
        }

        static void ConfigureTexture(string file, System.Action<TextureImporter> tweak)
        {
            string path = $"{TexDir}/{file}";
            if (!(AssetImporter.GetAtPath(path) is TextureImporter ti))
            {
                Debug.LogWarning($"[Dominoes] Falta textura: {path}");
                return;
            }
            ti.textureType = TextureImporterType.Default;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Clamp;          // es un atlas, no se repite
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 4;
            ti.maxTextureSize = 4096;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            tweak(ti);
            ti.SaveAndReimport();
        }

        // --------------------------------------------------------------------
        [MenuItem("Tools/Dominoes/2. Create Material", priority = 21)]
        static void CreateOrUpdateMaterialMenu() => CreateOrUpdateMaterial();

        public static Material CreateOrUpdateMaterial()
        {
            EnsureFolder(Path.GetDirectoryName(MatPath));
            var baseMap = Load<Texture2D>("domino_basecolor.png");
            var normal = Load<Texture2D>("domino_normal.png");
            var metalSmooth = Load<Texture2D>("domino_metallic_smoothness.png");
            var ao = Load<Texture2D>("domino_ao.png");

            Shader urp = Shader.Find("Universal Render Pipeline/Lit");
            bool isUrp = urp != null;
            Shader shader = isUrp ? urp : Shader.Find("Standard");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, MatPath);
            }
            else
            {
                mat.shader = shader;
            }

            if (isUrp)
            {
                mat.SetFloat("_WorkflowMode", 1f);                 // Metallic
                mat.SetTexture("_BaseMap", baseMap);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetTexture("_BumpMap", normal);
                mat.SetFloat("_BumpScale", 1f);
                mat.SetTexture("_MetallicGlossMap", metalSmooth);
                mat.SetFloat("_Smoothness", 1f);                   // multiplicador del canal A
                mat.SetFloat("_SmoothnessTextureChannel", 0f);     // 0 = alfa del Metallic Map
                mat.SetTexture("_OcclusionMap", ao);
                mat.SetFloat("_OcclusionStrength", 1f);
                SetKeyword(mat, "_NORMALMAP", normal != null);
                SetKeyword(mat, "_METALLICSPECGLOSSMAP", metalSmooth != null);
                SetKeyword(mat, "_OCCLUSIONMAP", ao != null);
            }
            else
            {
                Debug.LogWarning("[Dominoes] URP no encontrado: uso el shader Standard (Built-in).");
                mat.SetTexture("_MainTex", baseMap);
                mat.SetTexture("_BumpMap", normal);
                mat.SetTexture("_MetallicGlossMap", metalSmooth);
                mat.SetFloat("_GlossMapScale", 1f);
                mat.SetTexture("_OcclusionMap", ao);
                SetKeyword(mat, "_NORMALMAP", normal != null);
                SetKeyword(mat, "_METALLICGLOSSMAP", metalSmooth != null);
            }

            mat.enableInstancing = true;   // con SRP Batcher activo, URP prioriza el batcher; ambos sirven
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        // --------------------------------------------------------------------
        [MenuItem("Tools/Dominoes/3. Create Prefabs", priority = 22)]
        public static void CreatePrefabsMenu() => CreatePrefabs(CreateOrUpdateMaterial());

        public static void CreatePrefabs(Material mat)
        {
            var meshes = AssetDatabase.LoadAllAssetsAtPath(FbxPath)
                .OfType<Mesh>()
                .Where(m => m.name.StartsWith("domino_"))
                .OrderBy(m => m.name)
                .ToList();
            if (meshes.Count == 0)
            {
                Debug.LogError($"[Dominoes] No hay mallas domino_* en {FbxPath}");
                return;
            }

            EnsureFolder(PrefabDir);
            var physMat = CreatePhysicsMaterial();

            foreach (var mesh in meshes)
            {
                var size = mesh.bounds.size;
                if (size.y < size.x || size.y < size.z)
                    Debug.LogWarning($"[Dominoes] {mesh.name}: el alto no está en Y ({size}). " +
                                     "Revisa los ejes del export FBX.");

                var go = new GameObject(mesh.name);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;

                var box = go.AddComponent<BoxCollider>();
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size;
                box.contactOffset = ContactOffset;
                box.sharedMaterial = physMat;

                var rb = go.AddComponent<Rigidbody>();
                rb.mass = DominoMassKg;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;   // suficiente para la cadena
                rb.solverIterations = 12;

                var info = go.AddComponent<DominoInfo>();
                var parts = mesh.name.Split('_');                  // domino_a_b
                info.valueA = int.Parse(parts[1]);
                info.valueB = int.Parse(parts[2]);

                PrefabUtility.SaveAsPrefabAsset(go, $"{PrefabDir}/{mesh.name}.prefab");
                Object.DestroyImmediate(go);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Dominoes] {meshes.Count} prefabs en {PrefabDir}");
        }

        static PhysMat CreatePhysicsMaterial()
        {
            var pm = AssetDatabase.LoadAssetAtPath<PhysMat>(PhysMatPath);
            if (pm == null)
            {
                pm = new PhysMat("PM_Domino");
                AssetDatabase.CreateAsset(pm, PhysMatPath);
            }
            pm.frictionCombine = PhysMatCombine.Average;
            pm.bounceCombine = PhysMatCombine.Minimum;
            pm.staticFriction = 0.45f;
            pm.dynamicFriction = 0.35f;
            pm.bounciness = 0.05f;
            EditorUtility.SetDirty(pm);
            return pm;
        }

        // --------------------------------------------------------------------
        [MenuItem("Tools/Dominoes/Spawn Test Line (20)", priority = 40)]
        public static void SpawnTestLine()
        {
            var prefabs = Directory.Exists(PrefabDir)
                ? AssetDatabase.FindAssets("t:Prefab", new[] { PrefabDir })
                    .Select(g => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g)))
                    .ToArray()
                : new GameObject[0];
            if (prefabs.Length == 0) { Debug.LogError("[Dominoes] Crea primero los prefabs."); return; }

            var parent = new GameObject("DominoLine");
            Undo.RegisterCreatedObjectUndo(parent, "Spawn domino line");
            const int count = 20;
            var bounds = prefabs[0].GetComponent<MeshFilter>().sharedMesh.bounds;
            float height = bounds.size.y;
            float thickness = bounds.size.z;
            float spacing = height * 0.6f;                         // ~60 % del alto: caída fiable
            for (int i = 0; i < count; i++)
            {
                var p = prefabs[i % prefabs.Length];
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(p, parent.transform);
                inst.transform.localPosition = new Vector3(0f, 0f, i * spacing);
                // primera ficha ligeramente inclinada para arrancar la cadena al dar Play
                if (i == 0)
                {
                    const float tilt = 12f;   // gira el borde superior hacia +Z (la siguiente ficha)
                    inst.transform.localRotation = Quaternion.Euler(tilt, 0f, 0f);
                    // al girar sobre el centro de la base, un canto bajaría del suelo: se compensa
                    inst.transform.localPosition += Vector3.up * (thickness * 0.5f * Mathf.Sin(tilt * Mathf.Deg2Rad));
                }
            }
            Selection.activeGameObject = parent;
            Debug.Log($"[Dominoes] Fila de {count} fichas. Recomendado: Fixed Timestep = 0.01.");
        }

        // --------------------------------------------------------------------
        static T Load<T>(string file) where T : Object =>
            AssetDatabase.LoadAssetAtPath<T>($"{TexDir}/{file}");

        static void SetKeyword(Material m, string kw, bool on)
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
