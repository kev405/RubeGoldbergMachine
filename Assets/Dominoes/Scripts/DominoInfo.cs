using UnityEngine;

namespace Dominoes
{
    /// <summary>
    /// Valores de la ficha (a = mitad inferior, b = mitad superior estando de pie).
    /// Lo agrega DominoSetup al crear cada prefab.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DominoInfo : MonoBehaviour
    {
        [Range(0, 6)] public int valueA;
        [Range(0, 6)] public int valueB;

        public int Total => valueA + valueB;
        public bool IsDouble => valueA == valueB;

        public override string ToString() => $"[{valueA}|{valueB}]";
    }
}
