using System.Collections;
using UnityEngine;

// Genera una fila de bombillas (esfera + luz) y las enciende una por una con Encender().
public class BulbRow : MonoBehaviour
{
    [Header("Fila")]
    [SerializeField, Min(1)] private int count = 6;
    [Tooltip("Distancia entre bombillas, hacia -X local.")]
    [SerializeField] private float spacing = 0.8f;
    [SerializeField] private float bulbDiameter = 0.25f;

    [Header("Aspecto")]
    [Tooltip("Material con Emission (por ejemplo M_BombillaEmisiva).")]
    [SerializeField] private Material bulbMaterial;
    [SerializeField] private Color lightColor = new Color(1f, 0.78f, 0.44f);
    [SerializeField] private float lightRange = 3f;
    [SerializeField] private float lightIntensity = 2f;

    [Header("Encendido")]
    [Tooltip("Segundos entre una bombilla y la siguiente.")]
    [SerializeField] private float delayBetween = 0.15f;

    private void Awake()
    {
        // Al iniciar todas apagadas: se encienden solo con Encender().
        foreach (Transform bulb in transform)
            bulb.gameObject.SetActive(false);
    }

    // Llamar desde el On Hit () del sensor 13: BulbRow › Encender ().
    public void Encender()
    {
        StopAllCoroutines();
        StartCoroutine(EncenderEnSecuencia());
    }

    private IEnumerator EncenderEnSecuencia()
    {
        foreach (Transform bulb in transform)
        {
            bulb.gameObject.SetActive(true);
            yield return new WaitForSecondsRealtime(delayBetween); // no lo frena la cámara lenta
        }
    }

    [ContextMenu("Generar bombillas")]
    private void Generate()
    {
        ClearChildren();

        for (int i = 0; i < count; i++)
        {
            GameObject bulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bulb.name = $"Bombilla_{i + 1}";
            DestroyImmediate(bulb.GetComponent<Collider>()); // decorativa: sin física
            bulb.transform.SetParent(transform, false);
            bulb.transform.localPosition = new Vector3(-spacing * i, 0f, 0f);
            bulb.transform.localScale = Vector3.one * bulbDiameter;
            if (bulbMaterial != null)
                bulb.GetComponent<MeshRenderer>().sharedMaterial = bulbMaterial;

            GameObject lightObject = new GameObject("Luz");
            lightObject.transform.SetParent(bulb.transform, false);
            Light pointLight = lightObject.AddComponent<Light>();
            pointLight.type = LightType.Point;
            pointLight.color = lightColor;
            pointLight.range = lightRange;
            pointLight.intensity = lightIntensity;
        }

        Debug.Log($"BulbRow: {count} bombillas, de X local 0 a {-spacing * (count - 1):0.00}.", this);
#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
    }

    [ContextMenu("Borrar bombillas")]
    private void ClearChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
    }
}
