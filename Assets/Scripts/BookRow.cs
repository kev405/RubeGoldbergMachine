using System.Collections.Generic;
using UnityEngine;

public class BookRow : MonoBehaviour
{
    [Header("Fila")]
    [SerializeField, Min(1)] private int count = 8;
    [Tooltip("Distancia entre centros de libros vecinos. Ideal: 50–65% del alto.")]
    [SerializeField, Min(0.05f)] private float spacing = 0.71f;

    [Header("Medidas de cada libro (metros)")]
    [SerializeField] private float thickness = 0.25f; // X: grosor (tapa a tapa)
    [SerializeField] private float height = 1.2f;     // Y
    [SerializeField] private float depth = 0.9f;      // Z
    [Tooltip("Variación aleatoria del alto (± metros). 0 = todos iguales.")]
    [SerializeField, Min(0f)] private float heightVariation = 0f;
    [SerializeField] private int randomSeed = 7;

    [Header("Física y aspecto")]
    [SerializeField, Min(0.01f)] private float mass = 0.5f;
    [Tooltip("Malla de libro con UV (Assets/Books/Mesh/BookMesh.asset). Vacío = cubo normal. " +
             "Mide 1x1x1 como el cubo, así que el collider y la física no cambian.")]
    [SerializeField] private Mesh bookMesh;
    [Tooltip("Materiales de los libros (M_Book_XX).")]
    [SerializeField] private Material[] materials;
    [Tooltip("Reparte los materiales al azar sin repetir hasta agotarlos. Apagado = en orden y en ciclo.")]
    [SerializeField] private bool randomizeMaterials = true;
    [Tooltip("El lomo mira hacia -Z local. Actívalo si tu cámara ve el otro lado (gira cada libro 180°).")]
    [SerializeField] private bool spineTowardPositiveZ = false;
    [SerializeField] private PhysicsMaterial physicsMaterial;
    [Header("Último libro (el que empuja la bola de bolos)")]
    [Tooltip("Masa del último libro. 0 = igual a los demás.")]
    [SerializeField, Min(0f)] private float lastBookMass = 1.5f;
    [Tooltip("Material físico solo para el último libro. Usa Resbaloso: así no frena el giro de la bola al apoyarse en ella.")]
    [SerializeField] private PhysicsMaterial lastBookPhysicsMaterial;

    [Tooltip("Los libros empiezan dormidos: no se mueven hasta que algo los golpea.")]
    [SerializeField] private bool startAsleep = true;

    private void Start()
    {
        if (!startAsleep) return;
        foreach (Rigidbody rb in GetComponentsInChildren<Rigidbody>())
            rb.Sleep();
    }

    // El primer libro queda en la posición del Empty y los demás hacia -X local.
    // El Empty debe estar en el piso o la superficie donde se apoyan los libros.
    [ContextMenu("Generar libros")]
    private void Generate()
    {
        ClearChildren();

        var random = new System.Random(randomSeed);
        var materialRandom = new System.Random(randomSeed * 31 + 1); // independiente: no cambia las alturas
        var bag = new List<Material>();
        float minHeight = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            float h = height;
            if (heightVariation > 0f)
                h += (float)(random.NextDouble() * 2.0 - 1.0) * heightVariation;
            minHeight = Mathf.Min(minHeight, h);

            GameObject book = GameObject.CreatePrimitive(PrimitiveType.Cube);
            book.name = $"Libro_{i + 1}";
            book.transform.SetParent(transform, false);
            book.transform.localPosition = new Vector3(-spacing * i, h * 0.5f, 0f);
            book.transform.localScale = new Vector3(thickness, h, depth);
            if (spineTowardPositiveZ)
                book.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // caja simétrica: misma física

            if (bookMesh != null)
                book.GetComponent<MeshFilter>().sharedMesh = bookMesh;    // el BoxCollider sigue siendo 1x1x1

            Material mat = PickMaterial(i, bag, materialRandom);
            if (mat != null)
                book.GetComponent<MeshRenderer>().sharedMaterial = mat;

            bool isLast = i == count - 1;
            PhysicsMaterial physics = isLast && lastBookPhysicsMaterial != null ? lastBookPhysicsMaterial : physicsMaterial;
            if (physics != null)
                book.GetComponent<BoxCollider>().sharedMaterial = physics;

            Rigidbody rb = book.AddComponent<Rigidbody>();
            rb.mass = isLast && lastBookMass > 0f ? lastBookMass : mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        float ratio = spacing / minHeight;
        if (spacing <= thickness)
            Debug.LogWarning("BookRow: los libros se tocan o se atraviesan. Sube Spacing.", this);
        else if (ratio > 0.75f)
            Debug.LogWarning($"BookRow: separación {ratio:P0} del alto del libro más bajo. Puede que no alcancen a tocarse; baja de 75%.", this);
        else
            Debug.Log($"BookRow: {count} libros. Separación {ratio:P0} del alto (ideal 50–65%). Último libro en X local {-spacing * (count - 1):0.00}.", this);

#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
    }

    private Material PickMaterial(int i, List<Material> bag, System.Random rng)
    {
        if (materials == null || materials.Length == 0) return null;
        if (!randomizeMaterials) return materials[i % materials.Length];
        if (bag.Count == 0)
            foreach (var m in materials)
                if (m != null) bag.Add(m);
        if (bag.Count == 0) return null;
        int k = rng.Next(bag.Count);
        Material picked = bag[k];
        bag.RemoveAt(k);
        return picked;
    }

    [ContextMenu("Borrar libros")]
    private void ClearChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
    }
}
