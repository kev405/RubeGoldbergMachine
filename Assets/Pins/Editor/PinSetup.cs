using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pins.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL de los pinos: malla torneada con el perfil reglamentario (15 in de alto,
    /// 4.766 in de diámetro máximo) construida DENTRO del espacio de la cápsula de Unity
    /// (alto 2, diámetro 1, centrada). Los pinos conservan Transform, CapsuleCollider y Rigidbody.
    ///
    /// La franja vieja (hijo "Franja") solo se oculta: su MeshRenderer se desactiva.
    /// PinRack recibe la malla en su campo pinMesh, así "Generar pinos" crea pinos nuevos ya con este aspecto.
    /// </summary>
    public static class PinSetup
    {
        const string Root = "Assets/Pins";
        const string TexDir = Root + "/Textures";
        const string MeshPath = Root + "/Mesh/PinMesh.asset";
        const string MatPath = Root + "/Materials/M_Pin.mat";
        const string OriginalMatPath = "Assets/Materials/Pinos/M_PinoBlanco.mat";
        const float PinHeightIn = 15f, MaxDiameterIn = 4.766f;

        // Perfil reglamentario: (altura desde la base, diámetro) en pulgadas
        static readonly Vector2[] Profile =
        {
            new Vector2(0.000f, 2.031f), new Vector2(0.375f, 2.828f), new Vector2(0.750f, 3.400f),
            new Vector2(1.500f, 3.960f), new Vector2(2.250f, 4.300f), new Vector2(3.375f, 4.650f),
            new Vector2(4.500f, 4.766f), new Vector2(5.875f, 4.510f), new Vector2(7.250f, 3.750f),
            new Vector2(8.625f, 2.720f), new Vector2(9.375f, 2.180f), new Vector2(10.000f, 1.860f),
            new Vector2(10.875f, 1.900f), new Vector2(11.750f, 2.200f), new Vector2(12.625f, 2.450f),
            new Vector2(13.375f, 2.540f), new Vector2(14.000f, 2.420f), new Vector2(14.500f, 1.950f),
            new Vector2(14.800f, 1.300f), new Vector2(14.950f, 0.650f), new Vector2(15.000f, 0.000f),
        };

        // --------------------------------------------------------------------
        [MenuItem("Tools/Pins/Apply to all pins (visual only)", priority = 0)]
        public static void Apply()
        {
            ConfigureImporters();
            Mesh mesh = BuildMesh();
            Material mat = BuildMaterial();

            var racks = Object.FindObjectsByType<PinRack>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (racks.Length == 0) { Debug.LogError("[Pins] No hay ningún PinRack en la escena abierta."); return; }

            int changed = 0, mismatches = 0;
            foreach (var rack in racks)
            {
                var so = new SerializedObject(rack);
                so.FindProperty("pinMesh").objectReferenceValue = mesh;
                so.FindProperty("pinMaterial").objectReferenceValue = mat;
                so.ApplyModifiedProperties();

                foreach (Transform pin in rack.transform)
                {
                    if (pin.GetComponent<CapsuleCollider>() == null || pin.GetComponent<MeshFilter>() == null) continue;
                    string before = Snapshot(pin.gameObject);
                    SetVisual(pin, mesh, mat, stripesVisible: false);
                    if (Snapshot(pin.gameObject) != before) mismatches++;
                    changed++;
                }
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(rack.gameObject.scene);
            }

            if (mismatches == 0) Debug.Log($"[Pins] {changed} pinos actualizados. Física sin cambios (collider, rigidbody y transform intactos).");
            else Debug.LogError($"[Pins] {mismatches} pinos cambiaron su física. Revisa con 'Revert'.");
        }

        [MenuItem("Tools/Pins/Revert pins to capsules", priority = 1)]
        public static void Revert()
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Mesh capsule = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            var original = AssetDatabase.LoadAssetAtPath<Material>(OriginalMatPath);

            foreach (var rack in Object.FindObjectsByType<PinRack>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var so = new SerializedObject(rack);
                so.FindProperty("pinMesh").objectReferenceValue = null;
                if (original != null) so.FindProperty("pinMaterial").objectReferenceValue = original;
                so.ApplyModifiedProperties();
                foreach (Transform pin in rack.transform)
                    if (pin.GetComponent<CapsuleCollider>() != null)
                        SetVisual(pin, capsule, original, stripesVisible: true);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(rack.gameObject.scene);
            }
            Debug.Log("[Pins] Pinos revertidos a cápsulas con su franja.");
        }

        static void SetVisual(Transform pin, Mesh mesh, Material mat, bool stripesVisible)
        {
            var mf = pin.GetComponent<MeshFilter>();
            var mr = pin.GetComponent<MeshRenderer>();
            Undo.RecordObjects(new Object[] { mf, mr }, "Pinos: aspecto");
            mf.sharedMesh = mesh;
            if (mat != null) mr.sharedMaterials = new[] { mat };
            foreach (Transform child in pin)
                if (child.name == "Franja" && child.TryGetComponent(out MeshRenderer stripe))
                {
                    Undo.RecordObject(stripe, "Pinos: franja");
                    stripe.enabled = stripesVisible;
                }
        }

        static string Snapshot(GameObject go)
        {
            var t = go.transform;
            var c = go.GetComponent<CapsuleCollider>();
            var rb = go.GetComponent<Rigidbody>();
            return $"{t.localPosition}|{t.localRotation}|{t.localScale}|{c.radius}|{c.height}|{c.direction}|{c.center}|" +
                   $"{(c.sharedMaterial ? c.sharedMaterial.name : "-")}|{(rb ? rb.mass + "|" + rb.automaticCenterOfMass : "-")}";
        }

        // --------------------------------------------------------------------
        static Mesh BuildMesh()
        {
            // 1) Perfil suave (Catmull-Rom) en unidades de la cápsula: y en [-1, 1], radio máx 0.5
            var prof = new List<Vector2>();                      // (radio, y)
            var heights = new List<float>();
            const int perSegment = 6;
            for (int i = 0; i < Profile.Length - 1; i++)
            {
                Vector2 p0 = Profile[Mathf.Max(i - 1, 0)], p1 = Profile[i];
                Vector2 p2 = Profile[i + 1], p3 = Profile[Mathf.Min(i + 2, Profile.Length - 1)];
                for (int s = 0; s < perSegment; s++)
                {
                    float t = s / (float)perSegment;
                    Vector2 q = CatmullRom(p0, p1, p2, p3, t);
                    prof.Add(new Vector2(Mathf.Clamp(0.5f * q.y / MaxDiameterIn, 0f, 0.5f), -1f + 2f * q.x / PinHeightIn));
                    heights.Add(q.x);
                }
            }
            prof.Add(new Vector2(0f, 1f));
            heights.Add(PinHeightIn);

            // 2) Normales del perfil (en el espacio de la malla, antes de la escala del Transform)
            var pn = new Vector2[prof.Count];
            for (int i = 0; i < prof.Count; i++)
            {
                Vector2 a = prof[Mathf.Max(i - 1, 0)], b = prof[Mathf.Min(i + 1, prof.Count - 1)];
                Vector2 tan = (b - a).normalized;                // (dr, dy)
                pn[i] = new Vector2(tan.y, -tan.x);               // hacia afuera
            }
            pn[prof.Count - 1] = new Vector2(0f, 1f);            // punta

            const int seg = 64;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            for (int i = 0; i < prof.Count; i++)
                for (int j = 0; j <= seg; j++)                       // j = seg duplica la costura (u = 1)
                {
                    float u = j / (float)seg, th = u * Mathf.PI * 2f;
                    float c = Mathf.Cos(th), s = Mathf.Sin(th);
                    verts.Add(new Vector3(prof[i].x * c, prof[i].y, prof[i].x * s));
                    norms.Add(new Vector3(pn[i].x * c, pn[i].y, pn[i].x * s).normalized);
                    uvs.Add(new Vector2(u, heights[i] / PinHeightIn));
                }
            int row = seg + 1;
            for (int i = 0; i < prof.Count - 1; i++)
                for (int j = 0; j < seg; j++)
                {
                    int bl = i * row + j, br = bl + 1, tl = bl + row, tr = tl + 1;
                    tris.AddRange(new[] { bl, tl, tr, bl, tr, br });   // horario visto desde fuera
                }

            // 3) Tapa inferior plana
            int center = verts.Count;
            verts.Add(new Vector3(0f, -1f, 0f)); norms.Add(Vector3.down); uvs.Add(new Vector2(0.5f, 0.002f));
            for (int j = 0; j <= seg; j++)
            {
                float u = j / (float)seg, th = u * Mathf.PI * 2f;
                verts.Add(new Vector3(prof[0].x * Mathf.Cos(th), -1f, prof[0].x * Mathf.Sin(th)));
                norms.Add(Vector3.down);
                uvs.Add(new Vector2(u, 0.002f));
            }
            for (int j = 0; j < seg; j++)
                tris.AddRange(new[] { center, center + 1 + j, center + 2 + j });

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            bool isNew = mesh == null;
            if (isNew) mesh = new Mesh();
            mesh.Clear();
            mesh.name = "PinMesh";
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            if (isNew)
            {
                EnsureFolder(Path.GetDirectoryName(MeshPath));
                AssetDatabase.CreateAsset(mesh, MeshPath);
            }
            EditorUtility.SetDirty(mesh);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Pins] Malla: {verts.Count} vértices, límites {mesh.bounds.size} (cápsula = 1 x 2 x 1).");
            return mesh;
        }

        static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        // --------------------------------------------------------------------
        static void ConfigureImporters()
        {
            void Tex(string file, System.Action<TextureImporter> tweak)
            {
                if (!(AssetImporter.GetAtPath($"{TexDir}/{file}") is TextureImporter ti))
                {
                    Debug.LogWarning($"[Pins] Falta {TexDir}/{file}");
                    return;
                }
                ti.textureType = TextureImporterType.Default;
                ti.wrapModeU = TextureWrapMode.Repeat;           // alrededor del pino
                ti.wrapModeV = TextureWrapMode.Clamp;
                ti.mipmapEnabled = true;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 4;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                tweak(ti);
                ti.SaveAndReimport();
            }
            Tex("Pin_basecolor.png", ti => ti.sRGBTexture = true);
            Tex("Pin_normal.png", ti => ti.textureType = TextureImporterType.NormalMap);
            Tex("Pin_metallic_smoothness.png", ti =>
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

            var baseMap = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Pin_basecolor.png");
            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Pin_normal.png");
            var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Pin_metallic_smoothness.png");
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
