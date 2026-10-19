using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class DominoArc : MonoBehaviour
{
    [Header("Prefabs (se elige uno al azar por ficha)")]
    [Tooltip("Variantes a usar. Si está vacío se usa 'Domino Prefab'. Clic derecho en el componente > " +
             "'Cargar prefabs envejecidos' para llenarlo con Assets/Dominoes/PrefabsArc.")]
    [SerializeField] private GameObject[] dominoPrefabs = new GameObject[0];
    [Tooltip("Prefab de respaldo (el cubo original).")]
    [SerializeField] private GameObject dominoPrefab;

    [Header("Aleatoriedad")]
    [Tooltip("Misma semilla = mismo orden cada vez que generas. 0 = distinto cada vez.")]
    [SerializeField] private int seed = 1234;
    [Tooltip("No repetir una ficha hasta haber usado todas (como sacar del saco).")]
    [SerializeField] private bool avoidRepeats = true;
    [Tooltip("Gira 180° algunas fichas para que también se vea el dorso. No afecta la física.")]
    [SerializeField] private bool randomFlip = false;

    [Header("Forma del arco")]
    [SerializeField] private int count = 24;
    [SerializeField] private float radius = 3f;
    [SerializeField] private float arcDegrees = 180f;

    [Header("Crecimiento (1 = tamaño del prefab)")]
    [SerializeField] private float startScale = 0.35f;
    [SerializeField] private float endScale = 1f;

    [Header("Último dominó")]
    [Tooltip("Alto en metros del último dominó. 0 = usar el alto normal del crecimiento.")]
    [SerializeField, Min(0f)] private float lastDominoHeight = 0.75f;

    // Lista efectiva de prefabs (variantes no nulas, o el de respaldo).
    private List<GameObject> GetPool()
    {
        var pool = new List<GameObject>();
        if (dominoPrefabs != null)
            foreach (var p in dominoPrefabs)
                if (p != null) pool.Add(p);
        if (pool.Count == 0 && dominoPrefab != null) pool.Add(dominoPrefab);
        return pool;
    }

    // Arco: el primero queda en (0, 0, radius) mirando a +X y el último en (0, 0, -radius) mirando a -X.
    [ContextMenu("Generar dominós")]
    private void Generate()
    {
        List<GameObject> pool = GetPool();
        if (pool.Count == 0)
        {
            Debug.LogError("DominoArc: asigna al menos un prefab antes de generar.", this);
            return;
        }

        ClearChildren();

        // Todas las variantes comparten escala raíz; el tamaño base sale de la primera.
        Vector3 baseScale = pool[0].transform.localScale;
        float baseVolume = baseScale.x * baseScale.y * baseScale.z;
        float arcLength = radius * arcDegrees * Mathf.Deg2Rad;

        var rng = seed == 0 ? new System.Random() : new System.Random(seed);
        var bag = new List<GameObject>();

        // 1) Alturas del crecimiento. La separación se calcula con estas alturas,
        //    así cambiar el último dominó no mueve a ninguno de los demás.
        float[] heights = new float[count];
        float weight = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = count == 1 ? 0f : (float)i / (count - 1);
            heights[i] = baseScale.y * Mathf.Lerp(startScale, endScale, t);
            if (i < count - 1) weight += heights[i];
        }
        float factor = weight > 0f ? arcLength / weight : 0f; // separación = factor × altura

        // 2) Colocar cada dominó.
        float dist = 0f;
        for (int i = 0; i < count; i++)
        {
            float s = heights[i] / baseScale.y;
            Vector3 scale = baseScale * s;

            bool isLast = i == count - 1;
            if (isLast && lastDominoHeight > 0f)
                scale.y = lastDominoHeight; // solo cambia el alto; ancho y grosor siguen el crecimiento

            GameObject prefab = PickPrefab(pool, bag, rng);
            float phi = dist / radius;
            GameObject d = Spawn(prefab);
            d.name = $"Domino_{i:00}";
            d.transform.localPosition = new Vector3(radius * Mathf.Sin(phi), scale.y * 0.5f, radius * Mathf.Cos(phi));
            Vector3 forward = new Vector3(Mathf.Cos(phi), 0f, -Mathf.Sin(phi));
            if (randomFlip && rng.NextDouble() < 0.5) forward = -forward; // caja simétrica: misma física
            d.transform.localRotation = Quaternion.LookRotation(forward);
            d.transform.localScale = scale;

            // Masa proporcional al volumen (los pequeños pesan menos).
            if (d.TryGetComponent(out Rigidbody rb))
                rb.mass = Mathf.Max(0.03f, (scale.x * scale.y * scale.z) / baseVolume);

            dist += factor * heights[i];
        }

        Debug.Log($"DominoArc: {count} dominós con {pool.Count} variante(s). Separación = {factor:0.00} × altura " +
                  $"(ideal 0.55–0.65). Último dominó: alto {(lastDominoHeight > 0f ? lastDominoHeight : heights[count - 1]):0.00} m.", this);

#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
    }

    private GameObject PickPrefab(List<GameObject> pool, List<GameObject> bag, System.Random rng)
    {
        if (!avoidRepeats) return pool[rng.Next(pool.Count)];
        if (bag.Count == 0) bag.AddRange(pool);          // se rellena el saco cuando se vacía
        int k = rng.Next(bag.Count);
        GameObject p = bag[k];
        bag.RemoveAt(k);
        return p;
    }

    private GameObject Spawn(GameObject prefab)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            // En el editor conserva el vínculo con el prefab (cambios al prefab se propagan).
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, transform);
            if (go != null) return go;
        }
#endif
        return Instantiate(prefab, transform);
    }

#if UNITY_EDITOR
    [ContextMenu("Cargar prefabs envejecidos (Assets/Dominoes/PrefabsArc)")]
    private void LoadAgedPrefabs()
    {
        const string dir = "Assets/Dominoes/PrefabsArc";
        if (!AssetDatabase.IsValidFolder(dir))
        {
            Debug.LogError($"DominoArc: no existe {dir}. Usa Tools > Dominoes > 4. Create Arc Prefabs.", this);
            return;
        }
        var list = new List<GameObject>();
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { dir }))
        {
            var p = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (p != null) list.Add(p);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        Undo.RecordObject(this, "Cargar prefabs de dominó");
        dominoPrefabs = list.ToArray();
        EditorUtility.SetDirty(this);
        Debug.Log($"DominoArc: {dominoPrefabs.Length} prefabs cargados. Ahora usa 'Generar dominós'.", this);
    }
#endif

    [ContextMenu("Borrar dominós")]
    private void ClearChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
    }
}
