using UnityEngine;

// Duerme el Rigidbody al iniciar: no se mueve hasta que algo lo toque.
// Sirve para piezas inclinadas que deben caer solo cuando las golpean.
[RequireComponent(typeof(Rigidbody))]
public class SleepOnStart : MonoBehaviour
{
    private void Start()
    {
        GetComponent<Rigidbody>().Sleep();
    }
}
