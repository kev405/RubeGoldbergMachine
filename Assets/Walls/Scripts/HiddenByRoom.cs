using UnityEngine;

namespace Walls
{
    /// <summary>
    /// Marca puesta por Tools/Walls en el suelo original cuyo MeshRenderer se ocultó (lo reemplaza el suelo de
    /// tablones). Su collider sigue intacto. "Remove brick room" vuelve a mostrarlo y quita esta marca.
    /// </summary>
    [DisallowMultipleComponent, AddComponentMenu("")]
    public class HiddenByRoom : MonoBehaviour { }
}
