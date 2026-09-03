#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Imports base game ScriptableObjects into the mod project so modders can
/// browse, inspect, and duplicate them to create overrides.
///
/// Usage:
///   Star Vortex Mod > Import Game Content
///   Point it at your Star Vortex installation folder.
///
/// This creates a read-only "GameContent" folder containing all base game
/// ScriptableObjects with their values and cross-SO references intact.
/// To override a base game asset, duplicate it into your own folder,
/// edit it, and assign it to your AssetBundle with the same name and resourcePath.
///
/// Non-SO references (prefabs, sprites, materials, audio) show as Missing —
/// this is expected. At runtime, the game inherits these from the base original
/// so you only need to change the values you care about.
/// </summary>
public static class GameContentImporter
{
    private const string GameContentFolder = "Assets/GameContent";
    private const string ManifestFileName = "StarVortexGameContent.json";

    [MenuItem("Star Vortex Mod/Import Game Content", false, 50)]
    public static void ImportGameContent()
    {
        // Try to auto-detect game path from ThunderKit settings
        string gamePath = DetectGamePath();

        if (string.IsNullOrEmpty(gamePath))
        {
            gamePath = EditorUtility.OpenFolderPanel(
                "Select Star Vortex Installation Folder",
                "",
                "");

            if (string.IsNullOrEmpty(gamePath))
            {
                return;
            }
        }
        else
        {
            // Confirm auto-detected path
            if (!EditorUtility.DisplayDialog(
                "Import Game Content",
                "Detected Star Vortex at:\n" + gamePath + "\n\nImport game content from this location?",
                "Import",
                "Browse..."))
            {
                gamePath = EditorUtility.OpenFolderPanel(
                    "Select Star Vortex Installation Folder",
                    "",
                    "");

                if (string.IsNullOrEmpty(gamePath))
                {
                    return;
                }
            }
        }

        // Find the manifest file
        string manifestPath = FindManifest(gamePath);
        if (manifestPath == null)
        {
            EditorUtility.DisplayDialog(
                "Content Manifest Not Found",
                "Could not find " + ManifestFileName + " in the Star Vortex installation.\n\n" +
                "Make sure you selected the correct folder (the one containing Star Vortex.exe) " +
                "and that the game is up to date.",
                "OK");
            return;
        }

        string json = File.ReadAllText(manifestPath);
        ContentManifest manifest = JsonUtility.FromJson<ContentManifest>(json);

        if (manifest == null || manifest.assets == null || manifest.assets.Length == 0)
        {
            EditorUtility.DisplayDialog("Empty Manifest",
                "The content manifest contains no assets.", "OK");
            return;
        }

        // Clean existing GameContent folder
        if (Directory.Exists(GameContentFolder))
        {
            AssetDatabase.DeleteAsset(GameContentFolder);
        }
        Directory.CreateDirectory(GameContentFolder);

        // Write a readme so modders know this is read-only reference content
        File.WriteAllText(
            Path.Combine(GameContentFolder, "README.txt"),
            "=== GAME CONTENT (READ ONLY) ===\n\n" +
            "These are the base game ScriptableObjects imported for reference.\n" +
            "DO NOT edit these directly - changes here have no effect.\n\n" +
            "To override a base game asset:\n" +
            "  1. Right-click the asset you want to change\n" +
            "  2. Select Edit > Duplicate (Ctrl+D)\n" +
            "  3. Move the copy to your own mod folder\n" +
            "  4. Edit the copy\n" +
            "  5. Assign it to your AssetBundle\n" +
            "  6. The mod version replaces the original at runtime (matched by name)\n\n" +
            "Game version: " + manifest.gameVersion + "\n");

        // --- Pass 1: Create all ScriptableObject instances and populate value fields ---
        // Key = "TypeFullName|ResourcePath|AssetName", Value = created instance
        Dictionary<string, ScriptableObject> createdAssets = new Dictionary<string, ScriptableObject>();
        // Also index by type+name (without resourcePath) as a fallback for reference resolution
        Dictionary<string, ScriptableObject> assetsByTypeName = new Dictionary<string, ScriptableObject>();
        // Also index by just name for cross-SO reference resolution
        // (multiple types may share a name, so store lists)
        Dictionary<string, List<ScriptableObject>> assetsByName = new Dictionary<string, List<ScriptableObject>>();
        List<KeyValuePair<ContentEntry, ScriptableObject>> allEntries = new List<KeyValuePair<ContentEntry, ScriptableObject>>();

        int created = 0;
        int typeErrors = 0;

        foreach (ContentEntry entry in manifest.assets)
        {
            Type soType = FindType(entry.type);
            if (soType == null)
            {
                typeErrors++;
                Debug.LogWarning("[GameContentImporter] Type not found: " + entry.type +
                    " (asset: " + entry.name + "). Is ThunderKit import up to date?");
                continue;
            }

            ScriptableObject instance = ScriptableObject.CreateInstance(soType);
            JsonUtility.FromJsonOverwrite(entry.json, instance);
            instance.name = entry.name;

            string rp = entry.resourcePath ?? "";
            string key = entry.type + "|" + rp + "|" + entry.name;
            createdAssets[key] = instance;

            // Fallback index by type+name (for references without resourcePath)
            string typeNameKey = entry.type + "|" + entry.name;
            if (!assetsByTypeName.ContainsKey(typeNameKey))
            {
                assetsByTypeName[typeNameKey] = instance;
            }

            if (!assetsByName.ContainsKey(entry.name))
            {
                assetsByName[entry.name] = new List<ScriptableObject>();
            }
            assetsByName[entry.name].Add(instance);

            allEntries.Add(new KeyValuePair<ContentEntry, ScriptableObject>(entry, instance));
            created++;
        }

        // --- Pass 2: Save all as .asset files (must exist on disk before references can be set) ---
        Dictionary<ScriptableObject, string> assetPaths = new Dictionary<ScriptableObject, string>();

        foreach (KeyValuePair<ContentEntry, ScriptableObject> pair in allEntries)
        {
            ContentEntry entry = pair.Key;
            ScriptableObject instance = pair.Value;

            string folder = GameContentFolder;
            if (!string.IsNullOrEmpty(entry.resourcePath))
            {
                folder = Path.Combine(GameContentFolder, entry.resourcePath).Replace('\\', '/');
            }

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // Sanitize filename for filesystem
            string safeName = entry.name;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(c, '_');
            }

            string assetPath = folder + "/" + safeName + ".asset";
            AssetDatabase.CreateAsset(instance, assetPath);
            assetPaths[instance] = assetPath;
        }

        AssetDatabase.SaveAssets();

        // --- Pass 3: Resolve cross-SO references ---
        int resolvedRefs = 0;
        int unresolvedRefs = 0;

        foreach (KeyValuePair<ContentEntry, ScriptableObject> pair in allEntries)
        {
            ContentEntry entry = pair.Key;
            ScriptableObject instance = pair.Value;

            if (entry.references == null || entry.references.Length == 0)
            {
                continue;
            }

            SerializedObject serializedObj = new SerializedObject(instance);
            bool modified = false;

            foreach (ContentReference contentRef in entry.references)
            {
                // Try to find the referenced asset among our created SOs
                ScriptableObject target = null;

                // First try exact type + resourcePath + name match (disambiguates same-name assets)
                string refRp = contentRef.resourcePath ?? "";
                string refKey = contentRef.assetType + "|" + refRp + "|" + contentRef.assetName;
                if (createdAssets.TryGetValue(refKey, out target))
                {
                    // Found exact match with resourcePath
                }
                // Fall back to type + name (for references from old manifests without resourcePath)
                else if (assetsByTypeName.TryGetValue(contentRef.assetType + "|" + contentRef.assetName, out target))
                {
                    // Found by type + name
                }
                else if (assetsByName.ContainsKey(contentRef.assetName))
                {
                    // Try to find by name + compatible type
                    Type targetType = FindType(contentRef.assetType);
                    if (targetType != null)
                    {
                        foreach (ScriptableObject candidate in assetsByName[contentRef.assetName])
                        {
                            if (targetType.IsAssignableFrom(candidate.GetType()))
                            {
                                target = candidate;
                                break;
                            }
                        }
                    }
                }

                if (target == null)
                {
                    // Not a SO we imported (prefab, sprite, etc.) - skip silently
                    unresolvedRefs++;
                    continue;
                }

                SerializedProperty prop = serializedObj.FindProperty(contentRef.propertyPath);
                if (prop != null && prop.propertyType == SerializedPropertyType.ObjectReference)
                {
                    prop.objectReferenceValue = target;
                    modified = true;
                    resolvedRefs++;
                }
            }

            if (modified)
            {
                serializedObj.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string message = "Imported " + created + " game assets (v" + manifest.gameVersion + ")\n" +
            "SO references resolved: " + resolvedRefs + "\n" +
            "Non-SO references (prefabs/sprites): " + unresolvedRefs;

        if (typeErrors > 0)
        {
            message += "\nType errors: " + typeErrors + " (check console)";
        }

        Debug.Log("[GameContentImporter] " + message);
        EditorUtility.DisplayDialog("Import Complete", message, "OK");
    }

    private static string FindManifest(string gamePath)
    {
        // Try common locations:
        // <gamePath>/Star Vortex_Data/StreamingAssets/StarVortexGameContent.json
        // <gamePath>/StarVortex_Data/StreamingAssets/StarVortexGameContent.json
        // <gamePath>/StreamingAssets/StarVortexGameContent.json (if they pointed at _Data)

        string[] candidates = new string[]
        {
            Path.Combine(gamePath, "Star Vortex_Data", "Mods", ManifestFileName),
            Path.Combine(gamePath, "StarVortex_Data", "Mods", ManifestFileName),
            Path.Combine(gamePath, "Star Vortex_Data", "StreamingAssets", ManifestFileName),
            Path.Combine(gamePath, "StarVortex_Data", "StreamingAssets", ManifestFileName),
            Path.Combine(gamePath, "Mods", ManifestFileName),
            Path.Combine(gamePath, "StreamingAssets", ManifestFileName),
            Path.Combine(gamePath, ManifestFileName),
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string DetectGamePath()
    {
        // Try to find ThunderKit's configured game path
        string[] settingsGuids = AssetDatabase.FindAssets("t:ThunderKitSettings");
        if (settingsGuids.Length > 0)
        {
            string settingsPath = AssetDatabase.GUIDToAssetPath(settingsGuids[0]);
            ScriptableObject settings = AssetDatabase.LoadAssetAtPath<ScriptableObject>(settingsPath);
            if (settings != null)
            {
                SerializedObject so = new SerializedObject(settings);
                SerializedProperty gamePath = so.FindProperty("GamePath");
                if (gamePath == null)
                {
                    gamePath = so.FindProperty("gamePath");
                }
                if (gamePath == null)
                {
                    gamePath = so.FindProperty("GameExecutable");
                }
                if (gamePath != null && gamePath.propertyType == SerializedPropertyType.String
                    && !string.IsNullOrEmpty(gamePath.stringValue))
                {
                    string path = gamePath.stringValue;
                    // If it points to the exe, get the parent directory
                    if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        path = Path.GetDirectoryName(path);
                    }
                    if (Directory.Exists(path))
                    {
                        return path;
                    }
                }
            }
        }

        return null;
    }

    private static Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

    private static Type FindType(string fullTypeName)
    {
        if (string.IsNullOrEmpty(fullTypeName))
        {
            return null;
        }

        Type cached;
        if (typeCache.TryGetValue(fullTypeName, out cached))
        {
            return cached;
        }

        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (Assembly assembly in assemblies)
        {
            Type type = assembly.GetType(fullTypeName);
            if (type != null)
            {
                typeCache[fullTypeName] = type;
                return type;
            }
        }

        return null;
    }

    [Serializable]
    private class ContentManifest
    {
        public string gameVersion;
        public ContentEntry[] assets;
    }

    [Serializable]
    private class ContentEntry
    {
        public string name;
        public string type;
        public string resourcePath;
        public string json;
        public ContentReference[] references;
    }

    [Serializable]
    private class ContentReference
    {
        public string propertyPath;
        public string assetName;
        public string assetType;
        public string resourcePath;
    }
}
#endif
