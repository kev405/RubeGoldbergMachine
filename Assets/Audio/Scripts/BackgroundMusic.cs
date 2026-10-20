using UnityEngine;

namespace RubeAudio
{
    /// <summary>
    /// Música de fondo en bucle: 2D (se oye igual en cualquier cámara), volumen bajo para que los efectos
    /// destaquen, fundido de entrada al iniciar. Usa tiempo no escalado: la cámara lenta no la afecta.
    /// </summary>
    [DisallowMultipleComponent]
    public class BackgroundMusic : MonoBehaviour
    {
        public AudioClip music;
        [Tooltip("Volumen de la música. Los efectos suenan a 0.55-1; 0.2 la deja claramente por debajo.")]
        [Range(0f, 1f)] public float volume = 0.2f;
        [Tooltip("Segundos de fundido de entrada al empezar")]
        public float fadeIn = 3f;
        [Tooltip("Empieza en un punto aleatorio de la pieza para que no suene siempre igual")]
        public bool randomStart = false;

        AudioSource src;
        float fade;

        void Awake()
        {
            src = gameObject.AddComponent<AudioSource>();
            src.clip = music;
            src.loop = true;
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.priority = 0;                 // nunca la roban los efectos
            src.bypassReverbZones = true;
            src.volume = 0f;
        }

        void Start()
        {
            if (music == null) return;
            if (randomStart) src.time = Random.Range(0f, music.length * 0.9f);
            src.Play();
        }

        void Update()
        {
            if (!src.isPlaying) return;
            fade = fadeIn > 0f ? Mathf.MoveTowards(fade, 1f, Time.unscaledDeltaTime / fadeIn) : 1f;
            src.volume = volume * fade;
        }
    }
}
