using UnityEngine;

namespace RubeAudio
{
    /// <summary>
    /// Bucle de rodadura/ruedas: volumen y tono siguen la velocidad mientras el objeto está en contacto con algo.
    /// Solo LEE la velocidad del Rigidbody (nunca la modifica).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class RollingSound : MonoBehaviour
    {
        public AudioClip loop;
        [Range(0f, 1f)] public float volume = 0.7f;
        public float minSpeed = 0.15f;
        public float fullSpeed = 5f;
        public float pitchMin = 0.8f, pitchMax = 1.3f;
        [Tooltip("Rapidez de subida/bajada del volumen")]
        public float response = 8f;

        Rigidbody rb;
        AudioSource src;
        float lastContact = -10f, current;

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            src = SfxUtil.MakeSource(gameObject, true);
            src.clip = loop;
            src.volume = 0f;
        }

        void OnCollisionStay(Collision c) => lastContact = Time.time;
        void OnCollisionEnter(Collision c) => lastContact = Time.time;

        void Update()
        {
            if (loop == null) return;
            bool touching = Time.time - lastContact < 0.12f && !rb.isKinematic;
            float speed = rb.linearVelocity.magnitude;
            float k = touching ? Mathf.InverseLerp(minSpeed, fullSpeed, speed) : 0f;
            current = Mathf.MoveTowards(current, Mathf.Sqrt(k), response * Time.deltaTime);

            if (current > 0.001f)
            {
                if (!src.isPlaying) { src.time = Random.Range(0f, loop.length * 0.9f); src.Play(); }
                src.volume = volume * current;
                src.pitch = Mathf.Lerp(pitchMin, pitchMax, k);
            }
            else if (src.isPlaying) src.Stop();
        }
    }
}
