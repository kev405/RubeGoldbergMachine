using System.Collections.Generic;
using System.Linq;
using System.Text;
using RubeAudio;
using UnityEditor;
using UnityEngine;

namespace RubeAudio.EditorTools
{
    /// <summary>
    /// Asigna los sonidos a la escena de la máquina. Solo AÑADE componentes de audio (ImpactSound, RollingSound,
    /// FanSound, PendulumCreak) y el clip del AudioSource de cada sensor HitEvent. No toca Rigidbody, Collider,
    /// Joint ni Transform: compara una huella de toda la física antes y después.
    /// </summary>
    public static class AudioSetup
    {
        const string SfxDir = "Assets/Audio/SFX";

        [MenuItem("Tools/Audio/Apply sounds to machine (no physics change)", priority = 0)]
        public static void Apply()
        {
            ConfigureImporters();
            string before = PhysicsFingerprint();
            int n = 0;
            Undo.SetCurrentGroupName("Sonidos de la máquina");

            // --- Grupos generados (re-ejecuta este menú si los regeneras) ---------------------------------
            n += Group("Dominos", "Dominoes/domino_click", 0.55f, 0.15f, 2.5f);
            n += Group("Torre", "Blocks/wood_block", 0.7f, 0.15f, 3f);
            n += Group("Libros", "Books/book_thud", 0.8f, 0.2f, 3f);
            n += Group("Pinos", "Pins/pin_clack", 0.9f, 0.2f, 4f);

            // --- Objetos sueltos ---------------------------------------------------------------------------
            n += One("BolaBolos", "BowlingBall/bowling_ball_hit", 1f, 0.3f, 4f, roll: "BowlingBall/bowling_roll_loop", rollVol: 0.9f, rollFull: 6f, pMin: 0.7f, pMax: 1.15f);
            foreach (string ball in new[] { "Bola1", "Bola2", "BolaFinal" })
                n += One(ball, "SteelBall/steel_ball_hit", 0.8f, 0.2f, 4f, roll: "SteelBall/steel_roll_loop", rollVol: 0.6f, rollFull: 5f);
            n += One("Canica", "Marble/marble_click", 0.7f, 0.1f, 2.5f, roll: "Marble/marble_roll_loop", rollVol: 0.55f, rollFull: 3f, pMin: 0.85f, pMax: 1.35f);
            n += One("Carrito", "Car/car_bump", 0.8f, 0.2f, 3f, roll: "Car/car_wheels_loop", rollVol: 0.5f, rollFull: 4f);
            n += One("Palanca", "Lever/lever_knock", 0.85f, 0.2f, 3f);
            n += One("Balancin", "Lever/lever_knock", 0.85f, 0.2f, 3f);
            n += One("Campana", "Bell/bell_ring", 1f, 0.2f, 3f, interval: 0.3f, jitter: 0.01f);
            n += One("Trampolin", "Trampoline/trampoline_bounce", 0.9f, 0.5f, 5f, interval: 0.15f);
            n += One("Boton", "Button/button_click", 0.9f, 0.1f, 2f, interval: 0.25f, jitter: 0.03f);

            // --- Péndulo: golpe de la bola de acero + crujido de la cuerda en los extremos -------------------
            var pend = FindByName("Pendulo");
            if (pend != null)
            {
                Impact(pend.gameObject, "SteelBall/steel_ball_hit", 0.8f, 0.2f, 4f, 0.06f, 0.06f);
                var pc = Get<PendulumCreak>(pend.gameObject);
                Undo.RecordObject(pc, "Creak");
                pc.clips = Clips("Pendulum/rope_creak");
                EditorUtility.SetDirty(pc);
                n += 2;
            }

            // --- Ventilador ----------------------------------------------------------------------------------
            var fanZone = Object.FindObjectsByType<FanZone>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            if (fanZone != null)
            {
                var fs = Get<FanSound>(fanZone.gameObject);
                Undo.RecordObject(fs, "Fan");
                fs.blades = new SerializedObject(fanZone).FindProperty("blades").objectReferenceValue as Transform;
                fs.startClip = Clips("Fan/fan_start").FirstOrDefault();
                fs.loopClip = Clips("Fan/fan_loop").FirstOrDefault();
                EditorUtility.SetDirty(fs);
                if (fs.blades == null) Debug.LogWarning("[Audio] FanZone no tiene 'blades' asignado: el ventilador no sabrá cuándo arranca.", fanZone);
                n++;
            }

            // --- Sensores: campanita suave de "etapa superada" (2D, como la interfaz del contador) ----------
            var chime = Clips("Sensor/sensor_chime").FirstOrDefault();
            foreach (var he in Object.FindObjectsByType<HitEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var so = new SerializedObject(he);
                var prop = so.FindProperty("sfx");
                var src = prop.objectReferenceValue as AudioSource;
                if (src == null)
                {
                    src = Undo.AddComponent<AudioSource>(he.gameObject);
                    prop.objectReferenceValue = src;
                    so.ApplyModifiedProperties();
                }
                Undo.RecordObject(src, "Sensor sfx");
                src.clip = chime;
                src.playOnAwake = false;
                src.volume = 0.4f;
                src.spatialBlend = 0f;
                EditorUtility.SetDirty(src);
                n++;
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            string after = PhysicsFingerprint();
            if (before == after) Debug.Log($"[Audio] {n} objetos con sonido. Física sin cambios ({after.Length} car. de huella comparados).");
            else Debug.LogError("[Audio] ¡La huella de física cambió! Usa Undo (Ctrl+Z).\nAntes:\n" + before + "\nDespués:\n" + after);
        }

        [MenuItem("Tools/Audio/Remove machine sounds", priority = 1)]
        public static void Remove()
        {
            int n = 0;
            foreach (var t in new[] { typeof(ImpactSound), typeof(RollingSound), typeof(FanSound), typeof(PendulumCreak) })
                foreach (var c in Object.FindObjectsByType(t, FindObjectsInactive.Include, FindObjectsSortMode.None))
                { Undo.DestroyObjectImmediate(c); n++; }
            foreach (var he in Object.FindObjectsByType<HitEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var src = new SerializedObject(he).FindProperty("sfx").objectReferenceValue as AudioSource;
                if (src != null) { Undo.RecordObject(src, "Sensor sfx"); src.clip = null; }
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Debug.Log($"[Audio] Quitados {n} componentes de sonido.");
        }

        // ------------------------------------------------------------------------------------------------
        static int Group(string parentName, string prefix, float vol, float min, float full)
        {
            var parent = FindByName(parentName);
            if (parent == null) { Debug.LogWarning($"[Audio] No encontré '{parentName}'."); return 0; }
            int n = 0;
            foreach (var rb in parent.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb.transform == parent) continue;
                Impact(rb.gameObject, prefix, vol, min, full, 0.06f, 0.07f);
                n++;
            }
            if (n == 0) Debug.LogWarning($"[Audio] '{parentName}' no tiene piezas con Rigidbody (¿falta generarlas?).");
            return n;
        }

        static int One(string name, string prefix, float vol, float min, float full, float interval = 0.06f, float jitter = 0.06f,
                       string roll = null, float rollVol = 0.6f, float rollFull = 5f, float pMin = 0.8f, float pMax = 1.3f)
        {
            var t = FindByName(name);
            if (t == null) { Debug.LogWarning($"[Audio] No encontré '{name}'."); return 0; }
            var rb = t.GetComponentInParent<Rigidbody>();
            GameObject target = rb != null ? rb.gameObject : t.gameObject;   // los choques llegan al Rigidbody
            Impact(target, prefix, vol, min, full, interval, jitter);
            if (roll != null && rb != null)
            {
                var rs = Get<RollingSound>(target);
                Undo.RecordObject(rs, "Roll");
                rs.loop = Clips(roll).FirstOrDefault();
                rs.volume = rollVol; rs.fullSpeed = rollFull; rs.pitchMin = pMin; rs.pitchMax = pMax;
                EditorUtility.SetDirty(rs);
            }
            return 1;
        }

        static void Impact(GameObject go, string prefix, float vol, float min, float full, float interval, float jitter)
        {
            var c = Get<ImpactSound>(go);
            Undo.RecordObject(c, "Impact");
            c.clips = Clips(prefix);
            c.volume = vol; c.minSpeed = min; c.fullSpeed = full; c.minInterval = interval; c.pitchJitter = jitter;
            EditorUtility.SetDirty(c);
            if (c.clips.Length == 0) Debug.LogWarning($"[Audio] Sin clips para {prefix}", go);
        }

        static T Get<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : Undo.AddComponent<T>(go);
        }

        static Transform FindByName(string name) =>
            Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                  .Where(t => t.name == name && t.gameObject.scene.IsValid())
                  .OrderBy(t => t.GetComponentInParent<Rigidbody>() == null)   // si hay homónimos, el que tiene física
                  .FirstOrDefault();

        static AudioClip[] Clips(string prefix)
        {
            string folder = $"{SfxDir}/{System.IO.Path.GetDirectoryName(prefix).Replace('\\', '/')}";
            string stem = System.IO.Path.GetFileName(prefix);
            if (!AssetDatabase.IsValidFolder(folder)) return new AudioClip[0];
            return AssetDatabase.FindAssets("t:AudioClip", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => System.IO.Path.GetFileNameWithoutExtension(p).StartsWith(stem))
                .OrderBy(p => p)
                .Select(AssetDatabase.LoadAssetAtPath<AudioClip>)
                .ToArray();
        }

        static void ConfigureImporters()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { SfxDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!(AssetImporter.GetAtPath(path) is AudioImporter ai)) continue;
                bool longClip = path.Contains("_loop") || path.Contains("fan_start") || path.Contains("bell_");
                ai.forceToMono = true;
                ai.loadInBackground = false;
                var s = ai.defaultSampleSettings;
                s.loadType = longClip ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = 0.85f;
                s.preloadAudioData = true;
                ai.defaultSampleSettings = s;
                ai.SaveAndReimport();
            }
        }

        static string PhysicsFingerprint()
        {
            var sb = new StringBuilder();
            foreach (var rb in Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
                sb.Append($"{rb.name}:{rb.mass}/{rb.isKinematic}/{rb.useGravity}/{rb.constraints}/{rb.interpolation}/{rb.collisionDetectionMode}/{rb.transform.position}/{rb.transform.rotation};");
            foreach (var c in Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
                sb.Append($"{c.name}:{c.GetType().Name}/{c.enabled}/{c.isTrigger}/{(c.sharedMaterial ? c.sharedMaterial.name : "-")}/{c.bounds.size};");
            foreach (var j in Object.FindObjectsByType<Joint>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
                sb.Append($"{j.name}:{j.GetType().Name}/{j.anchor}/{j.axis};");
            return sb.ToString();
        }
    }
}
