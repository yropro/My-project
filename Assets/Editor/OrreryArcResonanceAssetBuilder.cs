using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Editor-only preparation; lives outside the shipped Leviathan assembly.
/// Creates an owned, script-free copy of the actual blue Zap prefab. The vendor
/// original and already-assigned dependency bundle names are never modified.
/// </summary>
public static class OrreryArcResonanceAssetBuilder
{
    private const string Source =
        "Assets/Vefects/Zap VFX URP/VFX/Zap/Particles/VFX_Zap_02_Blue.prefab";
    private const string Destination =
        "Assets/Leviathan/Content/Scripts/Orrery/ArcResonanceVFX/OrreryArcResonanceZap.prefab";
    private const string Bundle = "leviathanarcvfx.bundle";

    [MenuItem("Star Vortex Mod/Prepare Arc Resonance VFX")]
    public static void Prepare()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Prepare Arc Resonance outside Play Mode.");
        if (AssetDatabase.LoadAssetAtPath<GameObject>(Source) == null)
            throw new FileNotFoundException("The blue Zap source prefab is missing.", Source);
        string folder = Path.GetDirectoryName(Destination).Replace('\\', '/');
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
        }
        GameObject root = PrefabUtility.LoadPrefabContents(Source);
        try
        {
            root.name = "OrreryArcResonanceZap";
            // Strip scripts FIRST so RequireComponent cannot retain their physics.
            MonoBehaviour[] scripts = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = scripts.Length - 1; i >= 0; i--)
                if (scripts[i] != null) Object.DestroyImmediate(scripts[i]);
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transforms[i].gameObject);
            RemoveAll<AudioSource>(root);
            RemoveAll<Animator>(root);
            RemoveAll<Animation>(root);
            RemoveAll<ParticleSystemForceField>(root);
            RemoveAll<Joint2D>(root);
            RemoveAll<Joint>(root);
            RemoveAll<Collider2D>(root);
            RemoveAll<Collider>(root);
            RemoveAll<Rigidbody2D>(root);
            RemoveAll<Rigidbody>(root);
            RemoveAll<Light>(root);
            ParticleSystem[] particles = root.GetComponentsInChildren<ParticleSystem>(true);
            if (particles.Length == 0 || particles.Length > 32)
                throw new InvalidOperationException("Blue Zap must have 1-32 particle layers.");
            for (int i = 0; i < particles.Length; i++)
            {
                var main = particles[i].main;
                main.playOnAwake = false;
                main.stopAction = ParticleSystemStopAction.None;
            }
            if (root.GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
                throw new InvalidOperationException("Blue Zap still contains a script; no prepared copy was saved.");
            bool success;
            PrefabUtility.SaveAsPrefabAsset(root, Destination, out success);
            if (!success) throw new InvalidOperationException("Could not save prepared Arc Resonance prefab.");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }

        AssetImporter prefabImporter = AssetImporter.GetAtPath(Destination);
        if (prefabImporter == null) throw new InvalidOperationException("Prepared prefab importer missing.");
        prefabImporter.SetAssetBundleNameAndVariant(Bundle, string.Empty);
        string[] dependencies = AssetDatabase.GetDependencies(Destination, true);
        int assigned = 0;
        for (int i = 0; i < dependencies.Length; i++)
        {
            string path = dependencies[i];
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || AssetDatabase.IsValidFolder(path)) continue;
            Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            // Never bundle scripts/asmdefs/editor tooling. Preserve existing
            // shader/texture/material bundles used by Plasma or other effects.
            if (!(asset is GameObject) && !(asset is Material) && !(asset is Texture) &&
                !(asset is Shader) && !(asset is Mesh) && !(asset is AudioClip)) continue;
            AssetImporter importer = AssetImporter.GetAtPath(path);
            if (importer == null || !string.IsNullOrEmpty(importer.assetBundleName)) continue;
            importer.SetAssetBundleNameAndVariant(Bundle, string.Empty);
            assigned++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[Orrery] Prepared script-free blue Arc Resonance Zap; assigned " + assigned +
            " previously unassigned dependencies. Next run Build AssetBundles, then Package Mod. " +
            "Visual appearance still requires in-game testing.");
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(Destination);
    }

    private static void RemoveAll<T>(GameObject root) where T : Component
    {
        T[] components = root.GetComponentsInChildren<T>(true);
        for (int i = components.Length - 1; i >= 0; i--)
            if (components[i] != null) Object.DestroyImmediate(components[i]);
    }
}
