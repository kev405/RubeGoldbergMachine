using UnityEngine;
using UnityEngine.InputSystem;

public class MachineStarter : MonoBehaviour
{
    [SerializeField] private Rigidbody[] toRelease;
    [Tooltip("> 0 arranca solo tras N segundos (útil para grabar)")]
    [SerializeField] private float autoStartDelay = 0f;

    private bool started;

    private void Start()
    {
        if (autoStartDelay > 0f) Invoke(nameof(Release), autoStartDelay);
    }

    private void Update()
    {
        if (!started && Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            Release();
    }

    private void Release()
    {
        if (started) return;
        started = true;
        foreach (var rb in toRelease)
        {
            rb.isKinematic = false;
            rb.WakeUp();
        }
    }
}
