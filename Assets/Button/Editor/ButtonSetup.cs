using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PushButton.EditorTools
{
    /// <summary>
    /// Cambio SOLO VISUAL del botón: base negra, aro cromado y tapa roja abombada con "PUSH" (emisiva).
    /// Solo cambian MeshFilter y MeshRenderer. El MeshCollider convexo conserva su propia malla (el cilindro)
    /// y el Transform no se toca. Todo cabe dentro del cilindro (radio 0.4 m, alto 0.16 m con la escala actual).
    /// El texto se orienta para leerse derecho desde la Main Camera (vuelve a aplicar si mueves la cámara).
    /// </summary>
    public static class ButtonSetup
    {
        const string Root = "Assets/Button";
        const string TexDir = Root + "/Textures";
        const string MeshPath = Root + "/Mesh/ButtonMesh.asset";
        const string MatDir = Root + "/Materials";
        const string TargetName = "Boton";
        const string OriginalMatGuid = "a29dac8c75f960442a96d0548f501420";
        const float CapR = 0.262f;
        const int Seg = 96;

        [MenuItem("Tools/Button/Apply to Boton (visual only)", priority = 0)]
        public static void Apply()
        {
            GameObject go = FindTarget();
            if (go == null) return;
            ConfigureImporters();
            var mats = new[] { CapMaterial(), Solid("M_ButtonChrome", new Color(0.88f, 0.88f, 0.9f), 1f, 0.93f),
                               Solid("M_ButtonBase", new Color(0.05f, 0.05f, 0.055f), 0.3f, 0.45f) };

            Vector2 up = TextUp(go.transform);
            Mesh mesh = SaveMesh(BuildMesh(go.transform.localScale, up));
            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            string before = Snapshot(go);
            Undo.RecordObjects(new Object[] { mf, mr }, "Botón: aspecto");
            mf.sharedMesh = mesh;
            mr.sharedMaterials = mats;
            EditorUtility.SetDirty(mf);
            EditorUtility.SetDirty(mr);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            string after = Snapshot(go);
            if (before == after) Debug.Log($"[Button] Listo. Física sin cambios: {after}", go);
            else Debug.LogError($"[Button] ¡La física cambió! Antes: {before} / Después: {after}", go);
        }

        [MenuItem("Tools/Button/Revert Boton to cylinder", priority = 1)]
        public static void Revert()
        {
            GameObject go = FindTarget();
            if (go == null) return;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Mesh cyl = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            Undo.RecordObjects(new Object[] { mf, mr }, "Botón: revertir");
            mf.sharedMesh = cyl;
            var original = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(OriginalMatGuid));
            if (original != null) mr.sharedMaterials = new[] { original };
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            Debug.Log("[Button] Revertido al cilindro original.", go);
        }

        static GameObject FindTarget()
        {
            var sel = Selection.activeGameObject;
            if (sel != null && sel.name == TargetName && sel.GetComponent<MeshFilter>() != null) return sel;
            var go = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => t.name == TargetName && t.GetComponent<MeshFilter>() != null).Select(t => t.gameObject).FirstOrDefault();
            if (go == null) Debug.LogError($"[Button] No encontré '{TargetName}'. Selecciónalo y vuelve a intentarlo.");
            return go;
        }

        // "Arriba" del texto (en el plano XZ local): alejándose de la cámara, para que se lea derecho.
        static Vector2 TextUp(Transform button)
        {
            Camera cam = Camera.main;
            if (cam == null) cam = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).FirstOrDefault();
            if (cam == null) return new Vector2(0f, 1f);
            Vector3 away = button.InverseTransformDirection(button.position - cam.transform.position);
            var v = new Vector2(away.x, away.z);
            return v.sqrMagnitude < 1e-6f ? new Vector2(0f, 1f) : v.normalized;
        }

        static string Snapshot(GameObject go)
        {
            var t = go.transform;
            var mc = go.GetComponent<MeshCollider>();
            return $"pos {t.localPosition}, rot {t.localEulerAngles}, escala {t.localScale}, " +
                   $"collider {(mc && mc.sharedMesh ? mc.sharedMesh.name : "-")} convex={(mc ? mc.convex : false)} " +
                   $"trigger={(mc ? mc.isTrigger : false)} material {(mc && mc.sharedMaterial ? mc.sharedMaterial.name : "-")}";
        }

        // --------------------------------------------------------------------
        static Mesh BuildMesh(Vector3 scale, Vector2 up)
        {
            var V = new List<Vector3>(); var N = new List<Vector3>(); var UV = new List<Vector2>();
            var sub = new[] { new List<int>(), new List<int>(), new List<int>() };
            Vector3 inv = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
            Vector2 right = new Vector2(up.y, -up.x);                // derecha del texto (sin espejo, visto desde arriba)

            Vector2 CapUV(Vector3 p) => new Vector2(
                0.5f + (p.x * right.x + p.z * right.y) / (2f * CapR),
                0.5f + (p.x * up.x + p.z * up.y) / (2f * CapR));

            void Lathe(int s, IList<Vector2> prof, bool planar, float side = 1f)
            {
                int start = V.Count, n = prof.Count;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = prof[Mathf.Max(i - 1, 0)], b = prof[Mathf.Min(i + 1, n - 1)];
                    Vector2 tan = (b - a).normalized;
                    Vector2 pn = new Vector2(-tan.y, tan.x) * (Orient(prof) * side);   // side = -1: superficie que mira al eje
                    for (int j = 0; j <= Seg; j++)
                    {
                        float th = j / (float)Seg * Mathf.PI * 2f;
                        float c = Mathf.Cos(th), sn = Mathf.Sin(th);
                        var p = new Vector3(prof[i].x * c, prof[i].y, prof[i].x * sn);
                        V.Add(Vector3.Scale(p, inv));
                        N.Add(Vector3.Scale(new Vector3(pn.x * c, pn.y, pn.x * sn), scale).normalized);
                        UV.Add(planar ? CapUV(p) : new Vector2(j / (float)Seg, i / (float)(n - 1)));
                    }
                }
                int row = Seg + 1;
                for (int i = 0; i < n - 1; i++)
                    for (int j = 0; j < Seg; j++)
                    {
                        int bl = start + i * row + j, br = bl + 1, tl = bl + row, tr = tl + 1;
                        foreach (var tri in new[] { new[] { bl, tl, tr }, new[] { bl, tr, br } })
                        {
                            Vector3 nn = Vector3.Cross(V[tri[1]] - V[tri[0]], V[tri[2]] - V[tri[0]]);
                            if (nn.sqrMagnitude < 1e-16f) continue;
                            if (Vector3.Dot(nn, N[tri[0]] + N[tri[1]] + N[tri[2]]) >= 0f) sub[s].AddRange(tri);
                            else sub[s].AddRange(new[] { tri[0], tri[2], tri[1] });
                        }
                    }
            }

            // Base negra con chaflán (submalla 2)
            Lathe(2, new List<Vector2>
            {
                new Vector2(0f, -0.08f), new Vector2(0.395f, -0.08f), new Vector2(0.4f, -0.076f), new Vector2(0.4f, -0.04f),
                new Vector2(0.393f, -0.03f), new Vector2(0.375f, -0.023f), new Vector2(0.357f, -0.02f),
            }, false);
            // Aro cromado (submalla 1)
            Lathe(1, new List<Vector2>                                    // exterior y parte superior
            {
                new Vector2(0.357f, -0.03f), new Vector2(0.357f, -0.021f), new Vector2(0.356f, -0.012f), new Vector2(0.347f, -0.003f),
                new Vector2(0.33f, 0.003f), new Vector2(0.305f, 0.003f), new Vector2(0.286f, -0.003f), new Vector2(0.276f, -0.012f),
            }, false);
            Lathe(1, new List<Vector2> { new Vector2(0.276f, -0.012f), new Vector2(0.272f, -0.03f) }, false, side: -1f);  // pared interior
            // Tapa roja abombada (submalla 0) con UV planar para el texto
            var cap = new List<Vector2> { new Vector2(CapR, -0.03f), new Vector2(CapR, 0.008f), new Vector2(CapR - 0.002f, 0.022f) };
            for (int k = 0; k <= 16; k++)
            {
                float a = Mathf.Lerp(0.15f, Mathf.PI * 0.5f, k / 16f);
                cap.Add(new Vector2((CapR - 0.008f) * Mathf.Cos(a), 0.03f + 0.045f * Mathf.Sin(a)));
            }
            Lathe(0, cap, true);

            var mesh = new Mesh { name = "ButtonMesh" };
            mesh.SetVertices(V);
            mesh.SetNormals(N);
            mesh.SetUVs(0, UV);
            mesh.subMeshCount = 3;
            for (int i = 0; i < 3; i++) mesh.SetTriangles(sub[i], i);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh SaveMesh(Mesh m)
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
            foreach (string suffix in new[] { "basecolor", "emission", "normal", "metallic_smoothness" })
            {
                if (!(AssetImporter.GetAtPath($"{TexDir}/Button_{suffix}.png") is TextureImporter ti))
                {
                    Debug.LogWarning($"[Button] Falta {TexDir}/Button_{suffix}.png");
                    continue;
                }
                ti.textureType = suffix == "normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = suffix == "basecolor" || suffix == "emission";
                if (suffix == "metallic_smoothness")
                {
                    ti.alphaSource = TextureImporterAlphaSource.FromInput;
                    ti.alphaIsTransparency = false;
                }
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.mipmapEnabled = true;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 8;
                ti.maxTextureSize = 1024;
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

        static Material CapMaterial()
        {
            var mat = LoadOrCreate("M_ButtonCap", out bool urp);
            Texture2D T(string s) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/Button_{s}.png");
            var baseMap = T("basecolor"); var nrm = T("normal"); var ms = T("metallic_smoothness"); var em = T("emission");
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
            // Emisión: mantiene el brillo del material original (M_BotonEmisivo)
            mat.SetTexture("_EmissionMap", em);
            mat.SetColor("_EmissionColor", Color.white * 1.6f);
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
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
