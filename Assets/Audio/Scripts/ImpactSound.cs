using UnityEngine;

namespace RubeAudio
{
    /// <summary>
    /// Golpe con volumen según la velocidad del choque (componente normal). Solo escucha OnCollisionEnter:
    /// no aplica fuerzas ni modifica Rigidbody/Collider. Funciona también en objetos estáticos (botón, trampolín).
    /// </summary>
    [DisallowMultipleComponent]
    public class ImpactSound : MonoBehaviour
    {
        public AudioClip[] clips;
        [Range(0f, 1f)] public float volume = 0.8f;
        [Tooltip("Velocidad de choque (m/s) por debajo de la cual no suena")]
        public float minSpeed = 0.2f;
        [Tooltip("Velocidad de choque (m/s) a la que suena a volumen completo")]
        public float fullSpeed = 4f;
        [Range(0f, 0.3f)] public float pitchJitter = 0.06f;
        [Tooltip("Tiempo mínimo entre dos golpes de este objeto")]
        public float minInterval = 0.06f;
        [Tooltip("Ignora los choques de asentamiento al iniciar la escena")]
        public float startupMute = 0.4f;

        AudioSource src;
        float lastTime = -10f;
        int lastClip = -1;

        void Awake() => src = SfxUtil.MakeSource(gameObject, false);

        void OnCollisionEnter(Collision c)
        {
            if (clips == null || clips.Length == 0 || Time.timeSinceLevelLoad < startupMute) return;
            if (Time.time - lastTime < minInterval) return;

            float speed = c.relativeVelocity.magnitude;
            if (c.contactCount > 0)
                speed = Mathf.Max(Mathf.Abs(Vector3.Dot(c.relativeVelocity, c.GetContact(0).normal)), speed * 0.35f);
            if (speed < minSpeed || !SfxUtil.TryTakeVoice()) return;

            float k = Mathf.InverseLerp(minSpeed, fullSpeed, speed);
            var clip = SfxUtil.Pick(clips, ref lastClip);
            src.pitch = 1f + Random.Range(-pitchJitter, pitchJitter) - 0.04f * (1f - k);   // golpes suaves: un poco más graves
            src.PlayOneShot(clip, volume * Mathf.Lerp(0.12f, 1f, k * k));
            lastTime = Time.time;
        }
    }
}
