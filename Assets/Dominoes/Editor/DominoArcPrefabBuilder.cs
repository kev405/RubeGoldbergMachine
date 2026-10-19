using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Dominoes.EditorTools
{
    /// <summary>
    /// Crea 28 prefabs "arc-ready" que copian EXACTAMENTE el cuerpo físico del prefab
    /// Assets/Prefabs/Domino.prefab (escala raíz, BoxCollider y Rigidbody), y le añaden como
    /// hijo "Visual" el modelo envejecido ajustado a esa caja.
    ///
    ///   domino_a_b (escala = la del Domino original, BoxCollider 1x1x1, Rigidbody copiado, DominoInfo)
    ///   └── Visual  (MeshFilter + MeshRenderer, sin collider, escalado a la caja unitaria)
    ///
    /// Así DominoArc sigue funcionando igual: tamaño, pivote centrado, separación y masa no cambian.
    /// </summary>
    public static class DominoArcPrefabBuilder
    {
        const string TemplatePath = "Assets/Prefabs/Domino.prefab";
        const string SourcePrefabDir = "Assets/Dominoes/Prefabs";      // los 28 creados por DominoSetup
        const string OutDir = "Assets/Dominoes/PrefabsArc";

        [MenuItem("Tools/Dominoes/4. Create Arc Prefabs (match Domino.prefab)", priority = 23)]
        public static void Build()
        {
            var template = AssetDatabase.LoadAssetAtPath<GameObject>(TemplatePath);
            if (template == null) { Debug.LogError($"[Dominoes] No encuentro {TemplatePath}"); return; }

            var sources = AssetDatabase.FindAssets("t:Prefab", new[] { SourcePrefabDir })
                .Select(g => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(p => p != null && p.name.StartsWith("domino_"))
                .OrderBy(p => p.name)
                .ToList();
            if (sources.Count == 0)
            {
                Debug.LogError("[Dominoes] No hay prefabs en " + SourcePrefabDir + ". Corre antes 'Run All Steps'.");
                return;
            }

            var tCol = template.GetComponent<BoxCollider>();
            var tRb = template.GetComponent<Rigidbody>();
            if (!Directory.Exists(OutDir)) AssetDatabase.CreateFolder("Assets/Dominoes", "PrefabsArc");

            foreach (var src in sources)
            {
                var srcFilter = src.GetComponent<MeshFilter>();
                var srcRenderer = src.GetComponent<MeshRenderer>();
                if (srcFilter == null || srcFilter.sharedMesh == null) continue;
                Mesh mesh = srcFilter.sharedMesh;

                // --- raíz: idéntica al Domino original ---
                var root = new GameObject(src.name);
                root.transform.localScale = template.transform.localScale;

                var col = root.AddComponent<BoxCollider>();
                if (tCol != null) EditorUtility.CopySerialized(tCol, col);

                var rb = root.AddComponent<Rigidbody>();
                if (tRb != null) EditorUtility.CopySerialized(tRb, rb);

                var info = root.AddComponent<DominoInfo>();
                var srcInfo = src.GetComponent<DominoInfo>();
                if (srcInfo != null) { info.valueA = srcInfo.valueA; info.valueB = srcInfo.valueB; }

                // --- hijo visual: malla real ajustada a la caja unitaria del collider ---
                var visual = new GameObject("Visual");
                visual.transform.SetParent(root.transform, false);
                Vector3 size = mesh.bounds.size;
                var fit = new Vector3(1f / size.x, 1f / size.y, 1f / size.z);
                visual.transform.localScale = fit;
                visual.transform.localPosition = -Vector3.Scale(mesh.bounds.center, fit); // centra la pivote
                visual.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = visual.AddComponent<MeshRenderer>();
                mr.sharedMaterials = srcRenderer.sharedMaterials;

                PrefabUtility.SaveAsPrefabAsset(root, $"{OutDir}/{src.name}.prefab");
                Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[Dominoes] {sources.Count} prefabs listos para DominoArc en {OutDir} " +
                      $"(escala {template.transform.localScale}, mismo collider y rigidbody que Domino.prefab).");
        }
    }
}
