using UnityEngine;

/// <summary>
/// Solo visual: gira las ruedas según cuánto avanza el carrito. Las ruedas no tienen collider ni
/// Rigidbody, así que rotarlas no cambia la física. Ponlo en el objeto que tiene el Rigidbody.
/// </summary>
[DisallowMultipleComponent]
public class WheelSpinner : MonoBehaviour
{
    [Tooltip("Ruedas a girar. Vacío = hijos cuyo nombre empieza por 'Rueda'.")]
    [SerializeField] private Transform[] wheels;
    [Tooltip("Radio visual de la rueda en metros.")]
    [SerializeField, Min(0.001f)] private float wheelRadius = 0.07f;
    [Tooltip("Eje local de la rueda (el del cilindro). Las ruedas del carrito usan Y.")]
    [SerializeField] private Vector3 localAxle = Vector3.up;

    private Vector3 lastPosition;

    private void Awake()
    {
        if (wheels == null || wheels.Length == 0)
        {
            var list = new System.Collections.Generic.List<Transform>();
            foreach (Transform child in transform)
                if (child.name.StartsWith("Rueda")) list.Add(child);
            wheels = list.ToArray();
        }
        lastPosition = transform.position;
    }

    private void LateUpdate()
    {
        // Desplazamiento real del carrito a lo largo de su eje X (funciona con o sin Rigidbody)
        Vector3 delta = transform.position - lastPosition;
        lastPosition = transform.position;
        float along = Vector3.Dot(delta, transform.right);
        if (Mathf.Abs(along) < 1e-6f) return;

        // Rodar sin deslizar: ángulo = distancia / radio. Hacia -X el punto de apoyo se mueve +X.
        float degrees = -along / wheelRadius * Mathf.Rad2Deg;
        foreach (var w in wheels)
            if (w != null) w.Rotate(localAxle, degrees, Space.Self);
    }
}
