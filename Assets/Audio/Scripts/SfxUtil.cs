using UnityEngine;

namespace RubeAudio
{
    /// <summary>Utilidades compartidas: crea AudioSources en tiempo de ejecución y limita voces simultáneas.</summary>
    public static class SfxUtil
    {
        static int frame = -1, voices;
        const int MaxVoicesPerFrame = 8;

        /// <summary>AudioSource propia del componente (no se guarda en la escena, no toca la física).</summary>
        public static AudioSource MakeSource(GameObject go, bool loop, float spatialBlend = 0.85f,
                                             float minDistance = 2.5f, float maxDistance = 45f)
        {
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = loop;
            s.spatialBlend = spatialBlend;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = minDistance;
            s.maxDistance = maxDistance;
            s.dopplerLevel = 0f;
            return s;
        }

        /// <summary>Evita que 28 fichas cayendo a la vez saturen la mezcla.</summary>
        public static bool TryTakeVoice()
        {
            if (frame != Time.frameCount) { frame = Time.frameCount; voices = 0; }
            if (voices >= MaxVoicesPerFrame) return false;
            voices++;
            return true;
        }

        public static AudioClip Pick(AudioClip[] clips, ref int last)
        {
            if (clips == null || clips.Length == 0) return null;
            int i = Random.Range(0, clips.Length);
            if (clips.Length > 1 && i == last) i = (i + 1) % clips.Length;   // nunca el mismo dos veces seguidas
            last = i;
            return clips[i];
        }
    }
}
