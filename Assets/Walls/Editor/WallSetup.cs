using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Walls.EditorTools
{
    /// <summary>
    /// Sala de ladrillo envejecido que encierra toda la escena: 4 paredes (la delantera queda detrás de todas las
    /// cámaras), pilastras, zócalo de madera y suelo de tablones de roble. Medidas automáticas a partir de la escena.
    /// SOLO VISUAL: sin colliders y sin proyectar sombras. El suelo original conserva su collider; solo se oculta
    /// su MeshRenderer (marcado con HiddenByRoom) y "Remove" lo vuelve a mostrar. Todo cuelga de "Paredes".
    /// </summary>
    public static class WallSetup
    {
        const string Root = "Assets/Walls";
        const string TexDir = Root + "/Textures";
        const string MatDir = Root + "/Materials";
        const string MeshPath = Root + "/Mesh/Room.asset";
        const string ObjectName = "Paredes";

        // Ajustes (metros)
        const float Margin = 2.2f;          // espacio libre entre la máquina y las paredes
        const float MinHeight = 10f;
        const float Thick = 0.35f;          // grosor visible en los cantos
        const float PilasterW = 0.7f, PilasterD = 0.3f, PilasterSpacing = 8f;
        const float BaseH = 0.22f, BaseD = 0.035f;
        const float BrickTile = 2.25f;      // metros que cubre un mosaico de ladrillo
        const float GrimeTile = 14f;        // metros que cubre el mapa de suciedad
        const float FloorTile = 4.8f;       // metros que cubre un mosaico de tablones
        const float CameraClearance = 1.5f; // la pared delantera queda así de lejos detrás de la última cámara

        [MenuItem("Tools/Walls/Build brick room (visual only)", priority = 0)]
        public static void Apply()
        {
            var old = GameObject.Find(ObjectName);
            if (old != null) Undo.DestroyObjectImmediate(old);
            RestoreFloors();

            if (!MeasureScene(out Bounds machine, out Bounds floor, out Renderer floorRenderer)) return;
            float y0 = floor.max.y;
            float left = Mathf.Max(machine.min.x - Margin, floor.min.x);
            float right = Mathf.Min(machine.max.x + Margin, floor.max.x);
            float back = Mathf.Min(machine.max.z + Margin, floor.max.z);
            float front = floor.min.z;
            float top = y0 + Mathf.Max(machine.max.y - y0 + 3f, MinHeight);
            // Todas las cámaras (normales y de Cinemachine) deben quedar DENTRO de la sala
            if (CameraBounds(out Bounds cams))
            {
                front = Mathf.Min(front, cams.min.z - CameraClearance);
                back = Mathf.Max(back, cams.max.z + CameraClearance);
                left = Mathf.Min(left, cams.min.x - CameraClearance);
                right = Mathf.Max(right, cams.max.x + CameraClearance);
                top = Mathf.Max(top, cams.max.y + CameraClearance);
            }

            var b = new Builder();
            // Paredes (cara interior + canto superior + canto frontal de las laterales)
            b.Box(0, new Vector3(left - Thick, y0 - 0.05f, back), new Vector3(right + Thick, top, back + Thick), Face.NegZ | Face.PosY);
            b.Box(0, new Vector3(left - Thick, y0 - 0.05f, front - Thick), new Vector3(right + Thick, top, front), Face.PosZ | Face.PosY);
            b.Box(0, new Vector3(left - Thick, y0 - 0.05f, front), new Vector3(left, top, back), Face.PosX | Face.PosY);
            b.Box(0, new Vector3(right, y0 - 0.05f, front), new Vector3(right + Thick, top, back), Face.NegX | Face.PosY);
            b.Box(1, new Vector3(left, y0, back - BaseD), new Vector3(right, y0 + BaseH, back), Face.NegZ | Face.PosY);
            b.Box(1, new Vector3(left, y0, front), new Vector3(right, y0 + BaseH, front + BaseD), Face.PosZ | Face.PosY);
            b.Box(1, new Vector3(left, y0, front), new Vector3(left + BaseD, y0 + BaseH, back), Face.PosX | Face.PosY);
            b.Box(1, new Vector3(right - BaseD, y0, front), new Vector3(right, y0 + BaseH, back), Face.NegX | Face.PosY);
            // Suelo de tablones (submalla 2) sobre todo el recinto
            b.Box(2, new Vector3(left, y0 - 0.02f, front), new Vector3(right, y0, back), Face.PosY);

            // Pilastras en el fondo y en la pared delantera
            int segs = Mathf.Max(1, Mathf.RoundToInt((right - left) / PilasterSpacing));
            for (int i = 1; i < segs; i++)
            {
                float cx = Mathf.Lerp(left, right, i / (float)segs);
                b.Box(0, new Vector3(cx - PilasterW / 2, y0 - 0.05f, back - PilasterD), new Vector3(cx + PilasterW / 2, top, back),
                      Face.NegZ | Face.NegX | Face.PosX | Face.PosY);
                b.Box(1, new Vector3(cx - PilasterW / 2 - BaseD, y0, back - PilasterD - BaseD), new Vector3(cx + PilasterW / 2 + BaseD, y0 + BaseH, back),
                      Face.NegZ | Face.NegX | Face.PosX | Face.PosY);
                b.Box(0, new Vector3(cx - PilasterW / 2, y0 - 0.05f, front), new Vector3(cx + PilasterW / 2, top, front + PilasterD),
                      Face.PosZ | Face.NegX | Face.PosX | Face.PosY);
                b.Box(1, new Vector3(cx - PilasterW / 2 - BaseD, y0, front), new Vector3(cx + PilasterW / 2 + BaseD, y0 + BaseH, front + PilasterD + BaseD),
                      Face.PosZ | Face.NegX | Face.PosX | Face.PosY);
            }
            // Pilastras en los laterales
            int sideSegs = Mathf.Max(1, Mathf.RoundToInt((back - front) / PilasterSpacing));
            for (int i = 1; i < sideSegs; i++)
            {
                float cz = Mathf.Lerp(back, front, i / (float)sideSegs);
                foreach (bool isLeft in new[] { true, false })
                {
                    float wx = isLeft ? left : right, dir = isLeft ? 1f : -1f;
                    var a = new Vector3(wx, y0 - 0.05f, cz - PilasterW / 2);
                    var c = new Vector3(wx + dir * PilasterD, top, cz + PilasterW / 2);
                    b.Box(0, Vector3.Min(a, c), Vector3.Max(a, c), (isLeft ? Face.PosX : Face.NegX) | Face.NegZ | Face.PosZ | Face.PosY);
                    a = new Vector3(wx, y0, cz - PilasterW / 2 - BaseD);
                    c = new Vector3(wx + dir * (PilasterD + BaseD), y0 + BaseH, cz + PilasterW / 2 + BaseD);
                    b.Box(1, Vector3.Min(a, c), Vector3.Max(a, c), (isLeft ? Face.PosX : Face.NegX) | Face.NegZ | Face.PosZ | Face.PosY);
                }
            }

            ConfigureImporters();
            var mesh = Save(b.Build());
            var go = new GameObject(ObjectName);
            Undo.RegisterCreatedObjectUndo(go, "Paredes");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { BrickMaterial(), TrimMaterial(), FloorMaterial() };
            mr.shadowCastingMode = ShadowCastingMode.Off;      // no tapa la luz de la máquina
            mr.receiveShadows = true;
            if (floorRenderer != null)                           // el suelo original queda solo como collider
            {
                Undo.RecordObject(floorRenderer, "Ocultar suelo");
                floorRenderer.enabled = false;
                if (floorRenderer.GetComponent<Walls.HiddenByRoom>() == null) Undo.AddComponent<Walls.HiddenByRoom>(floorRenderer.gameObject);
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            Debug.Log($"[Walls] Sala cerrada: x {left:0.0}..{right:0.0}, z {front:0.0}..{back:0.0}, alto {top - y0:0.0} m. " +
                      $"Suelo de tablones; '{(floorRenderer ? floorRenderer.name : "-")}' conserva su collider. Sin colliders nuevos: la física no cambia.", go);
        }

        [MenuItem("Tools/Walls/Remove brick room", priority = 1)]
        public static void Remove()
        {
            var go = GameObject.Find(ObjectName);
            if (go != null) Undo.DestroyObjectImmediate(go);
            RestoreFloors();
        }

        static void RestoreFloors()
        {
            foreach (var h in Object.FindObjectsByType<Walls.HiddenByRoom>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var r = h.GetComponent<Renderer>();
                if (r != null) { Undo.RecordObject(r, "Mostrar suelo"); r.enabled = true; }
                Undo.DestroyObjectImmediate(h);
            }
        }

        static bool CameraBounds(out Bounds b)
        {
            b = new Bounds();
            bool any = false;
            var pts = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None).Select(c => c.transform.position).ToList();
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string tn = mb.GetType().Name;                        // CinemachineCamera / CinemachineVirtualCamera
                if (tn.StartsWith("Cinemachine") && tn.Contains("Camera") && !tn.Contains("Brain")) pts.Add(mb.transform.position);
            }
            foreach (var p in pts)
            {
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
            return any;
        }

        static bool MeasureScene(out Bounds machine, out Bounds floor, out Renderer floorRenderer)
        {
            machine = new Bounds(); floor = new Bounds(); floorRenderer = null;
            bool hasM = false, hasF = false;
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!(r is MeshRenderer) || r.transform.root.name == ObjectName) continue;
                Bounds bb = r.bounds;
                bool isFloor = bb.size.x > 25f || bb.size.z > 12f;
                if (isFloor)
                {
                    if (!hasF || bb.size.x * bb.size.z > floor.size.x * floor.size.z) { floor = bb; hasF = true; floorRenderer = r; }
                }
                else if (hasM) machine.Encapsulate(bb);
                else { machine = bb; hasM = true; }
            }
            if (!hasM) { Debug.LogError("[Walls] No encontré objetos de la máquina."); return false; }
            if (!hasF)
            {
                floor = new Bounds(new Vector3(machine.center.x, 0f, machine.center.z), new Vector3(machine.size.x + 10f, 0f, machine.size.z + 14f));
                Debug.LogWarning("[Walls] No encontré un suelo grande; uso las medidas de la máquina.");
            }
            return true;
        }

        // ------------------------------------------------------------------------------------------------
        [System.Flags] enum Face { PosX = 1, NegX = 2, PosY = 4, NegY = 8, PosZ = 16, NegZ = 32 }

        class Builder
        {
            readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>();
            readonly List<Vector2> UV = new List<Vector2>();
            readonly List<int>[] S = { new List<int>(), new List<int>(), new List<int>() };

            // UV en metros proyectadas por cara: los ladrillos siguen alineados entre paredes y pilastras
            void Quad(int sub, Vector3 n, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int i0 = V.Count;
                foreach (var p in new[] { a, b, c, d })
                {
                    V.Add(p); N.Add(n);
                    UV.Add(Mathf.Abs(n.z) > 0.5f ? new Vector2(p.x * Mathf.Sign(-n.z), p.y)
                         : Mathf.Abs(n.x) > 0.5f ? new Vector2(p.z * Mathf.Sign(n.x), p.y)
                         : new Vector2(p.x, p.z));
                }
                Tri(sub, i0, i0 + 1, i0 + 2);
                Tri(sub, i0, i0 + 2, i0 + 3);
            }

            void Tri(int sub, int a, int b, int c)
            {
                var n = Vector3.Cross(V[b] - V[a], V[c] - V[a]);
                if (Vector3.Dot(n, N[a]) >= 0f) S[sub].AddRange(new[] { a, b, c });
                else S[sub].AddRange(new[] { a, c, b });
            }

            public void Box(int sub, Vector3 lo, Vector3 hi, Face faces)
            {
                Vector3 P(float x, float y, float z) => new Vector3(x, y, z);
                if ((faces & Face.NegZ) != 0) Quad(sub, Vector3.back, P(lo.x, lo.y, lo.z), P(hi.x, lo.y, lo.z), P(hi.x, hi.y, lo.z), P(lo.x, hi.y, lo.z));
                if ((faces & Face.PosZ) != 0) Quad(sub, Vector3.forward, P(lo.x, lo.y, hi.z), P(hi.x, lo.y, hi.z), P(hi.x, hi.y, hi.z), P(lo.x, hi.y, hi.z));
                if ((faces & Face.NegX) != 0) Quad(sub, Vector3.left, P(lo.x, lo.y, lo.z), P(lo.x, lo.y, hi.z), P(lo.x, hi.y, hi.z), P(lo.x, hi.y, lo.z));
                if ((faces & Face.PosX) != 0) Quad(sub, Vector3.right, P(hi.x, lo.y, lo.z), P(hi.x, lo.y, hi.z), P(hi.x, hi.y, hi.z), P(hi.x, hi.y, lo.z));
                if ((faces & Face.PosY) != 0) Quad(sub, Vector3.up, P(lo.x, hi.y, lo.z), P(hi.x, hi.y, lo.z), P(hi.x, hi.y, hi.z), P(lo.x, hi.y, hi.z));
                if ((faces & Face.NegY) != 0) Quad(sub, Vector3.down, P(lo.x, lo.y, lo.z), P(hi.x, lo.y, lo.z), P(hi.x, lo.y, hi.z), P(lo.x, lo.y, hi.z));
            }

            public Mesh Build()
            {
                var m = new Mesh { name = "Room" };
                m.SetVertices(V); m.SetNormals(N); m.SetUVs(0, UV);
                m.subMeshCount = S.Length;
                for (int i = 0; i < S.Length; i++) m.SetTriangles(S[i], i);
                m.RecalculateTangents(); m.RecalculateBounds();
                return m;
            }
        }

        static Mesh Save(Mesh m)
        {
            EnsureFolder(Path.GetDirectoryName(MeshPath));
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            if (existing != null) { EditorUtility.CopySerialized(m, existing); Object.DestroyImmediate(m); m = existing; }
            else AssetDatabase.CreateAsset(m, MeshPath);
            EditorUtility.SetDirty(m); AssetDatabase.SaveAssets();
            return m;
        }

        // ------------------------------------------------------------------------------------------------
        static void ConfigureImporters()
        {
            foreach (string name in new[] { "Brick_basecolor", "Brick_normal", "Brick_metallic_smoothness", "WallGrime_detail",
                                            "Floor_basecolor", "Floor_normal", "Floor_metallic_smoothness" })
            {
                if (!(AssetImporter.GetAtPath($"{TexDir}/{name}.png") is TextureImporter ti)) { Debug.LogWarning($"[Walls] Falta {name}.png"); continue; }
                ti.textureType = name.EndsWith("normal") ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = name.EndsWith("basecolor");            // el detalle es lineal: 0.5 = neutro
                if (name.EndsWith("smoothness")) { ti.alphaSource = TextureImporterAlphaSource.FromInput; ti.alphaIsTransparency = false; }
                ti.wrapMode = TextureWrapMode.Repeat;
                ti.mipmapEnabled = true;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 8;                                      // paredes vistas en ángulo rasante
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
        }

        static Material Mat(string name, out bool urp)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            urp = sh != null;
            if (!urp) sh = Shader.Find("Standard");
            EnsureFolder(MatDir);
            string path = $"{MatDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); }
            else m.shader = sh;
            return m;
        }

        static Material BrickMaterial() => Textured("M_BrickWall", "Brick", BrickTile, GrimeTile, new Vector2(0.37f, 0.11f));
        static Material FloorMaterial() => Textured("M_WoodFloor", "Floor", FloorTile, 17f, new Vector2(0.61f, 0.29f));

        static Material Textured(string matName, string set, float tile, float grimeTile, Vector2 grimeOffset)
        {
            var m = Mat(matName, out bool urp);
            Texture2D T(string s) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{s}.png");
            var bc = T($"{set}_basecolor"); var nr = T($"{set}_normal"); var ms = T($"{set}_metallic_smoothness"); var gr = T("WallGrime_detail");
            string baseProp = urp ? "_BaseMap" : "_MainTex";
            m.SetTexture(baseProp, bc);
            m.SetTextureScale(baseProp, Vector2.one / tile);
            m.SetColor(urp ? "_BaseColor" : "_Color", Color.white);
            m.SetTexture("_BumpMap", nr); m.SetFloat("_BumpScale", 1f);
            m.SetTexture("_MetallicGlossMap", ms);
            m.EnableKeyword("_NORMALMAP");
            if (urp)
            {
                m.SetFloat("_WorkflowMode", 1f); m.SetFloat("_Smoothness", 1f); m.SetFloat("_SmoothnessTextureChannel", 0f);
                m.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            else { m.SetFloat("_GlossMapScale", 1f); m.EnableKeyword("_METALLICGLOSSMAP"); }
            if (gr != null)                                             // suciedad a gran escala: rompe la repetición
            {
                m.SetTexture("_DetailAlbedoMap", gr);
                m.SetTextureScale("_DetailAlbedoMap", Vector2.one / grimeTile);
                m.SetTextureOffset("_DetailAlbedoMap", grimeOffset);
                m.SetFloat("_DetailAlbedoMapScale", 1f);
                m.EnableKeyword("_DETAIL_MULX2");
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material TrimMaterial()
        {
            var m = Mat("M_WallBaseboard", out bool urp);
            m.SetColor(urp ? "_BaseColor" : "_Color", new Color(0.17f, 0.10f, 0.065f));
            m.SetFloat("_Metallic", 0f);
            m.SetFloat(urp ? "_Smoothness" : "_Glossiness", 0.4f);
            EditorUtility.SetDirty(m);
            return m;
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
