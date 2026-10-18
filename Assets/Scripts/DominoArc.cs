using UnityEngine;

public class DominoArc : MonoBehaviour
{
    [SerializeField] private GameObject dominoPrefab;
    [SerializeField] private int count = 24;
    [SerializeField] private float radius = 3f;
    [SerializeField] private float arcDegrees = 180f;
    [Tooltip("Tamaño del primer dominó (1 = tamaño del prefab)")]
    [SerializeField] private float startScale = 0.35f;
    [Tooltip("Tamaño del último dominó")]
    [SerializeField] private float endScale = 1f;

    // Arco: el primero queda en (0, 0, radius) mirando a +X y el último en (0, 0, -radius) mirando a -X.
    [ContextMenu("Generar dominós")]
    private void Generate()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);

        Vector3 baseScale = dominoPrefab.transform.localScale;
        float baseHeight = baseScale.y;
        float arcLength = radius * arcDegrees * Mathf.Deg2Rad;

        float[] heights = new float[count];
        float weight = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = count == 1 ? 0f : (float)i / (count - 1);
            heights[i] = baseHeight * Mathf.Lerp(startScale, endScale, t);
            if (i < count - 1) weight += heights[i];
        }
        // Separación proporcional a la altura, ajustada para llenar el arco exacto.
        float factor = weight > 0f ? arcLength / weight : 0f;

        float dist = 0f;
        for (int i = 0; i < count; i++)
        {
            float s = heights[i] / baseHeight;
            float phi = dist / radius;

            GameObject d = Instantiate(dominoPrefab, transform);
            d.name = $"Domino_{i:00}";
            d.transform.localPosition = new Vector3(radius * Mathf.Sin(phi), heights[i] * 0.5f, radius * Mathf.Cos(phi));
            d.transform.localRotation = Quaternion.LookRotation(new Vector3(Mathf.Cos(phi), 0f, -Mathf.Sin(phi)));
            d.transform.localScale = baseScale * s;

            if (d.TryGetComponent(out Rigidbody rb))
                rb.mass = Mathf.Max(0.03f, s * s * s); // más pequeño = más liviano

            dist += factor * heights[i];
        }

        Debug.Log($"Dominós: {count}. Separación = {factor:0.00} × altura (ideal 0.55–0.65).");
#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
    }
}