using UnityEngine;

namespace RubeAudio
{
    /// <summary>
    /// Ventilador: detecta que las aspas empezaron a girar (FanZone.TurnOn) observando su rotación, reproduce
    /// el arranque y enlaza con el bucle mediante un fundido cruzado. No modifica FanZone.
    /// </summary>
    [DisallowMultipleComponent]
    public class FanSound : MonoBehaviour
    {
        public Transform blades;
        public AudioClip startClip;
        public AudioClip loopClip;
        [Range(0f, 1f)] public float volume = 0.6f;
        public float crossfade = 0.3f;

        AudioSource a, b;
        Quaternion prev;
        bool on;

        void Awake()
        {
            a = SfxUtil.MakeSource(gameObject, false, 0.8f, 3f, 40f);
            b = SfxUtil.MakeSource(gameObject, true, 0.8f, 3f, 40f);
            b.clip = loopClip;
            if (blades != null) prev = blades.localRotation;
        }

        void Update()
        {
            if (blades == null) return;
            if (!on && Quaternion.Angle(prev, blades.localRotation) > 0.01f)
            {
                on = true;
                if (startClip != null) { a.clip = startClip; a.volume = volume; a.Play(); }
                else { b.volume = volume; b.Play(); }
            }
            prev = blades.localRotation;
            if (!on || startClip == null) return;

            float remaining = startClip.length - a.time;
            if (a.isPlaying && remaining <= crossfade)
            {
                if (!b.isPlaying) b.Play();
                float t = 1f - Mathf.Clamp01(remaining / crossfade);
                a.volume = volume * (1f - t);
                b.volume = volume * t;
            }
            else if (!a.isPlaying)
            {
                if (!b.isPlaying) b.Play();
                b.volume = volume;
            }
        }
    }
}
