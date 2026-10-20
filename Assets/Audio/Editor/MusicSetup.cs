using UnityEditor;
using UnityEngine;

namespace RubeAudio.EditorTools
{
    /// <summary>Crea (o actualiza) el objeto "MusicaFondo" con el jazz en bucle. No toca nada más de la escena.</summary>
    public static class MusicSetup
    {
        const string ClipPath = "Assets/Audio/Music/jazz_loop.ogg";
        const string ObjectName = "MusicaFondo";

        [MenuItem("Tools/Audio/Add background music (jazz)", priority = 10)]
        public static void Apply()
        {
            if (AssetImporter.GetAtPath(ClipPath) is AudioImporter ai)
            {
                ai.forceToMono = false;
                ai.loadInBackground = true;
                var s = ai.defaultSampleSettings;
                s.loadType = AudioClipLoadType.Streaming;       // 3 min: se lee del disco, no ocupa RAM
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = 0.7f;
                s.preloadAudioData = false;
                ai.defaultSampleSettings = s;
                ai.SaveAndReimport();
            }
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(ClipPath);
            if (clip == null) { Debug.LogError($"[Music] Falta {ClipPath}"); return; }

            var go = GameObject.Find(ObjectName);
            if (go == null)
            {
                go = new GameObject(ObjectName);
                Undo.RegisterCreatedObjectUndo(go, "Música de fondo");
            }
            var bm = go.GetComponent<BackgroundMusic>();
            if (bm == null) bm = Undo.AddComponent<BackgroundMusic>(go);
            Undo.RecordObject(bm, "Música de fondo");
            bm.music = clip;
            EditorUtility.SetDirty(bm);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            Debug.Log($"[Music] '{ObjectName}' listo: {clip.length:0} s de jazz en bucle a volumen {bm.volume}. Ajusta 'Volume' en el Inspector.", go);
        }

        [MenuItem("Tools/Audio/Remove background music", priority = 11)]
        public static void Remove()
        {
            var go = GameObject.Find(ObjectName);
            if (go != null) Undo.DestroyObjectImmediate(go);
        }
    }
}
