using UnityEngine;

namespace RubeAudio
{
    /// <summary>
    /// Crujido de la cuerda en el soporte: suena en cada extremo de la oscilación (cuando la velocidad angular
    /// alrededor del eje de la bisagra cambia de signo), con volumen según lo rápido que iba. Solo lee el Rigidbody.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class PendulumCreak : MonoBehaviour
    {
        public AudioClip[] clips;
        [Range(0f, 1f)] public float volume = 0.5f;
        [Tooltip("Velocidad angular (rad/s) mínima de la media oscilación para que cruja")]
        public float minAngularSpeed = 0.4f;
        public float fullAngularSpeed = 3f;

        Rigidbody rb;
        AudioSource src;
        Vector3 axisLocal = Vector3.forward;
        float lastSign, peak;
        int lastClip = -1;

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            var hj = GetComponent<HingeJoint>();
            if (hj != null && hj.axis.sqrMagnitude > 0f) axisLocal = hj.axis.normalized;
            src = SfxUtil.MakeSource(gameObject, false, 0.8f, 2f, 35f);
        }

        void FixedUpdate()
        {
            if (rb.isKinematic || clips == null || clips.Length == 0) return;
            float w = Vector3.Dot(rb.angularVelocity, transform.TransformDirection(axisLocal));
            peak = Mathf.Max(peak, Mathf.Abs(w));
            float sign = Mathf.Sign(w);
            if (Mathf.Abs(w) > 0.05f && lastSign != 0f && sign != lastSign && peak >= minAngularSpeed)
            {
                float k = Mathf.InverseLerp(minAngularSpeed, fullAngularSpeed, peak);
                src.pitch = Random.Range(0.92f, 1.08f);
                src.PlayOneShot(SfxUtil.Pick(clips, ref lastClip), volume * Mathf.Lerp(0.25f, 1f, k));
                peak = 0f;
            }
            if (Mathf.Abs(w) > 0.05f) lastSign = sign;
        }
    }
}
