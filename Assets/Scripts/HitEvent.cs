using System.Collections;
using UnityEngine;
using UnityEngine.Events;

[RequireComponent(typeof(Collider))]
public class HitEvent : MonoBehaviour
{
    [Header("Identificación")]
    [SerializeField] private string hitName = "Golpe";
    [Tooltip("Rigidbody que debe entrar al sensor. Vacío = cualquiera")]
    [SerializeField] private Rigidbody expectedHitter;

    [Header("Efectos")]
    [SerializeField] private AudioSource sfx;
    [SerializeField] private ParticleSystem vfx;

    [Header("Cámara lenta (1 = desactivada)")]
    [SerializeField, Range(0.05f, 1f)] private float slowMoScale = 1f;
    [SerializeField] private float slowMoSeconds = 0f;

    [Header("Acciones al golpear")]
    public UnityEvent onHit;

    private bool fired;
    private static float baseFixedDelta = -1f;

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        if (baseFixedDelta < 0f) baseFixedDelta = Time.fixedDeltaTime;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (fired) return;
        if (expectedHitter != null && other.attachedRigidbody != expectedHitter) return;
        fired = true;

        if (vfx != null)
        {
            vfx.transform.position = other.ClosestPoint(transform.position);
            vfx.Play();
        }
        if (sfx != null) sfx.Play();
        if (HitCounter.Instance != null) HitCounter.Instance.Register(hitName);
        onHit.Invoke();

        if (slowMoScale < 1f && slowMoSeconds > 0f) StartCoroutine(SlowMo());
    }

    private IEnumerator SlowMo()
    {
        Time.timeScale = slowMoScale;
        Time.fixedDeltaTime = baseFixedDelta * slowMoScale; // física fluida en cámara lenta
        yield return new WaitForSecondsRealtime(slowMoSeconds);
        Time.timeScale = 1f;
        Time.fixedDeltaTime = baseFixedDelta;
    }
}