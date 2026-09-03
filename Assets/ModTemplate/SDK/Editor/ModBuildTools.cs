#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Build tools for Star Vortex mod projects.
/// Use alongside ThunderKit which handles importing game DLLs.
/// </summary>
public static class ModBuildTools
{
    private const string OutputFolder = "ModBuild";

    [MenuItem("Star Vortex Mod/Build AssetBundles", false, 1)]
    public static void BuildAssetBundles()
    {
        string outputPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, OutputFolder);
        if (!Directory.Exists(outputPath))
        {
            Directory.CreateDirectory(outputPath);
        }

        GenerateComponentManifest(outputPath);

        BuildPipeline.BuildAssetBundles(
            outputPath,
            BuildAssetBundleOptions.None,
            EditorUserBuildSettings.activeBuildTarget
        );

        Debug.Log("[ModBuildTools] AssetBundles built to: " + outputPath);
        EditorUtility.RevealInFinder(outputPath);
    }

    [MenuItem("Star Vortex Mod/Package Mod", false, 20)]
    public static void PackageMod()
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string buildPath = Path.Combine(projectRoot, OutputFolder);
        string modJsonPath = Path.Combine(Application.dataPath, "mod.json");

        if (!File.Exists(modJsonPath))
        {
            EditorUtility.DisplayDialog("Missing mod.json",
                "No mod.json found in your Assets folder.\n\n" +
                "The template mod.json should be in Assets/. Fill in your mod details.",
                "OK");
            return;
        }

        string[] bundleFiles = Directory.Exists(buildPath)
            ? Directory.GetFiles(buildPath, "*", SearchOption.TopDirectoryOnly)
            : new string[0];

        string modJson = File.ReadAllText(modJsonPath);
        string modName = "MyMod";

        int nameIndex = modJson.IndexOf("\"name\"");
        if (nameIndex >= 0)
        {
            int colonIndex = modJson.IndexOf(':', nameIndex);
            int firstQuote = modJson.IndexOf('"', colonIndex + 1);
            int secondQuote = modJson.IndexOf('"', firstQuote + 1);
            if (firstQuote >= 0 && secondQuote > firstQuote)
            {
                modName = modJson.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
            }
        }

        string modOutputPath = Path.Combine(projectRoot, "ModPackage", modName);
        if (Directory.Exists(modOutputPath))
        {
            Directory.Delete(modOutputPath, true);
        }
        Directory.CreateDirectory(modOutputPath);

        File.Copy(modJsonPath, Path.Combine(modOutputPath, "mod.json"));

        string iconPath = FindFileInAssets("icon.png");
        if (iconPath != null)
        {
            File.Copy(iconPath, Path.Combine(modOutputPath, "icon.png"));
        }

        int bundleCount = 0;
        foreach (string file in bundleFiles)
        {
            string fileName = Path.GetFileName(file);

            if (fileName == OutputFolder || fileName == OutputFolder + ".manifest")
            {
                continue;
            }
            if (fileName.EndsWith(".manifest") || fileName.EndsWith(".json"))
            {
                continue;
            }

            string destName = fileName.EndsWith(".bundle") ? fileName : fileName + ".bundle";
            File.Copy(file, Path.Combine(modOutputPath, destName));
            bundleCount++;
        }

        string dllsPath = Path.Combine(Application.dataPath, "ModDLLs");
        if (Directory.Exists(dllsPath))
        {
            string[] modDlls = Directory.GetFiles(dllsPath, "*.dll");
            foreach (string dll in modDlls)
            {
                File.Copy(dll, Path.Combine(modOutputPath, Path.GetFileName(dll)));
            }
        }

        // Copy compiled DLLs from non-Editor assembly definitions
        string scriptAssembliesPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Library", "ScriptAssemblies");
        string[] asmdefGuids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset", new[] { "Assets" });
        foreach (string guid in asmdefGuids)
        {
            string asmdefPath = AssetDatabase.GUIDToAssetPath(guid);
            string asmdefJson = File.ReadAllText(asmdefPath);

            // Skip Editor-only assemblies
            int includeIdx = asmdefJson.IndexOf("\"includePlatforms\"");
            if (includeIdx >= 0)
            {
                int bracketStart = asmdefJson.IndexOf('[', includeIdx);
                int bracketEnd = asmdefJson.IndexOf(']', bracketStart);
                if (bracketStart >= 0 && bracketEnd > bracketStart)
                {
                    string platformsBlock = asmdefJson.Substring(bracketStart, bracketEnd - bracketStart + 1);
                    if (platformsBlock.Contains("\"Editor\""))
                    {
                        continue;
                    }
                }
            }

            string asmdefName = Path.GetFileNameWithoutExtension(asmdefPath);
            string compiledDll = Path.Combine(scriptAssembliesPath, asmdefName + ".dll");
            if (File.Exists(compiledDll))
            {
                File.Copy(compiledDll, Path.Combine(modOutputPath, asmdefName + ".dll"));
                Debug.Log("[ModBuildTools] Copied compiled assembly: " + asmdefName + ".dll");
            }
        }

        // Copy component manifest
        string componentsManifestPath = Path.Combine(buildPath, "components.json");
        if (File.Exists(componentsManifestPath))
        {
            File.Copy(componentsManifestPath, Path.Combine(modOutputPath, "components.json"));
        }

        // Copy language files
        string languagesPath = Path.Combine(Application.dataPath, "Languages");
        int langCount = 0;
        if (Directory.Exists(languagesPath))
        {
            string[] langFiles = Directory.GetFiles(languagesPath, "lang_*.json");
            foreach (string langFile in langFiles)
            {
                File.Copy(langFile, Path.Combine(modOutputPath, Path.GetFileName(langFile)));
                langCount++;
            }
        }

        Debug.Log("[ModBuildTools] Mod packaged to: " + modOutputPath +
            " (" + bundleCount + " bundle(s), " + langCount + " language file(s))");
        EditorUtility.RevealInFinder(modOutputPath);
    }

    [MenuItem("Star Vortex Mod/Generate Language File", false, 30)]
    public static void GenerateLanguageFile()
    {
        SortedDictionary<string, string> entries = new SortedDictionary<string, string>();

        // Scan ScriptableObjects assigned to an AssetBundle for languageKey fields.
        // Assets not in a bundle aren't shipped with the mod, so they're skipped —
        // this keeps imported reference content (GameContent/) out of the lang file.
        string[] guids = AssetDatabase.FindAssets("t:ScriptableObject");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!IsInAssetBundle(path))
            {
                continue;
            }
            ScriptableObject asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null)
            {
                continue;
            }

            SerializedObject so = new SerializedObject(asset);
            SerializedProperty languageKeyProp = so.FindProperty("languageKey");
            if (languageKeyProp == null || string.IsNullOrEmpty(languageKeyProp.stringValue))
            {
                continue;
            }

            string languageKey = languageKeyProp.stringValue;

            // Name: use asset name (matches FasterScriptableObject.filename)
            entries[languageKey + ".name"] = asset.name;

            // Description (ItemBase, Legendary, PlayerBase, MissionBase)
            SerializedProperty descProp = so.FindProperty("description");
            if (descProp != null && descProp.propertyType == SerializedPropertyType.String
                && !string.IsNullOrEmpty(descProp.stringValue))
            {
                entries[languageKey + ".desc"] = descProp.stringValue;
            }

            // DisplayName (SquadronBase, MissionBase)
            SerializedProperty displayNameProp = so.FindProperty("displayName");
            if (displayNameProp != null && displayNameProp.propertyType == SerializedPropertyType.String
                && !string.IsNullOrEmpty(displayNameProp.stringValue))
            {
                entries[languageKey + ".displayName"] = displayNameProp.stringValue;
            }

            // MissionBase: steps[].initialDialog[] + steps[].autoPilotDialog[]
            SerializedProperty stepsProp = so.FindProperty("steps");
            if (stepsProp != null && stepsProp.isArray)
            {
                for (int s = 0; s < stepsProp.arraySize; s++)
                {
                    SerializedProperty step = stepsProp.GetArrayElementAtIndex(s);
                    int dialogCount = 0;

                    SerializedProperty initialDialog = step.FindPropertyRelative("initialDialog");
                    if (initialDialog != null && initialDialog.isArray)
                    {
                        for (int d = 0; d < initialDialog.arraySize; d++)
                        {
                            AddDialogEntry(entries, languageKey + ".dialog." + s + "." + dialogCount, initialDialog.GetArrayElementAtIndex(d));
                            dialogCount++;
                        }
                    }

                    SerializedProperty autoPilotDialog = step.FindPropertyRelative("autoPilotDialog");
                    if (autoPilotDialog != null && autoPilotDialog.isArray)
                    {
                        for (int d = 0; d < autoPilotDialog.arraySize; d++)
                        {
                            SerializedProperty dialogElement = autoPilotDialog.GetArrayElementAtIndex(d);
                            SerializedProperty nameProp = dialogElement.FindPropertyRelative("name");
                            // AutoPilotBreak (9999) has no dialog text but still increments the index
                            if (nameProp != null && nameProp.intValue != 9999)
                            {
                                AddDialogEntry(entries, languageKey + ".dialog." + s + "." + dialogCount, dialogElement);
                            }
                            dialogCount++;
                        }
                    }
                }
            }

            // FixedStar: arrivalAutoPilotDialog[], fleeDialog[], completeAutoPilotDialog[]
            AddDialogArray(entries, so, "arrivalAutoPilotDialog", languageKey + ".arrivalDialog");
            AddDialogArray(entries, so, "fleeDialog", languageKey + ".fleeDialog");
            AddDialogArray(entries, so, "completeAutoPilotDialog", languageKey + ".completeDialog");

            // NPCBase: echoPartDialog[]
            AddDialogArray(entries, so, "echoPartDialog", languageKey + ".echoPartDialog");

            // ShipPart alternate sprites (alternateSprites[].languageKey + ".key")
            SerializedProperty altSpritesProp = so.FindProperty("alternateSprites");
            if (altSpritesProp != null && altSpritesProp.isArray)
            {
                for (int i = 0; i < altSpritesProp.arraySize; i++)
                {
                    SerializedProperty element = altSpritesProp.GetArrayElementAtIndex(i);
                    SerializedProperty altLangKey = element.FindPropertyRelative("languageKey");
                    SerializedProperty altKey = element.FindPropertyRelative("key");
                    if (altLangKey != null && !string.IsNullOrEmpty(altLangKey.stringValue)
                        && altKey != null && !string.IsNullOrEmpty(altKey.stringValue))
                    {
                        entries[altLangKey.stringValue + ".key"] = altKey.stringValue;
                    }
                }
            }
        }

        // Scan prefabs assigned to an AssetBundle for MonoBehaviours with languageKey fields
        // (ShipPart and any other game MonoBehaviour the modder ships).
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        foreach (string prefabGuid in prefabGuids)
        {
            string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuid);
            if (!IsInAssetBundle(prefabPath))
            {
                continue;
            }
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                continue;
            }

            MonoBehaviour[] behaviours = prefab.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (MonoBehaviour mb in behaviours)
            {
                if (mb == null)
                {
                    continue;
                }
                SerializedObject mbSo = new SerializedObject(mb);
                SerializedProperty mbKeyProp = mbSo.FindProperty("languageKey");
                if (mbKeyProp == null || mbKeyProp.propertyType != SerializedPropertyType.String
                    || string.IsNullOrEmpty(mbKeyProp.stringValue))
                {
                    continue;
                }

                string mbKey = mbKeyProp.stringValue;

                // Name: derive from resourceName (e.g. "Conclave/Pixel" → "Pixel") if present,
                // otherwise fall back to the GameObject name.
                string displayName = mb.gameObject.name;
                SerializedProperty resourceNameProp = mbSo.FindProperty("resourceName");
                if (resourceNameProp != null && resourceNameProp.propertyType == SerializedPropertyType.String
                    && !string.IsNullOrEmpty(resourceNameProp.stringValue))
                {
                    string rn = resourceNameProp.stringValue;
                    int slashIndex = rn.LastIndexOf('/');
                    displayName = slashIndex >= 0 ? rn.Substring(slashIndex + 1) : rn;
                }
                entries[mbKey + ".name"] = displayName;

                // ShipPart alternateSprites (same shape as the SO loop above).
                SerializedProperty mbAltSprites = mbSo.FindProperty("alternateSprites");
                if (mbAltSprites != null && mbAltSprites.isArray)
                {
                    for (int i = 0; i < mbAltSprites.arraySize; i++)
                    {
                        SerializedProperty element = mbAltSprites.GetArrayElementAtIndex(i);
                        SerializedProperty altLangKey = element.FindPropertyRelative("languageKey");
                        SerializedProperty altKey = element.FindPropertyRelative("key");
                        if (altLangKey != null && !string.IsNullOrEmpty(altLangKey.stringValue)
                            && altKey != null && !string.IsNullOrEmpty(altKey.stringValue))
                        {
                            entries[altLangKey.stringValue + ".key"] = altKey.stringValue;
                        }
                    }
                }
            }
        }

        if (entries.Count == 0)
        {
            EditorUtility.DisplayDialog("No Language Entries",
                "No assets with language keys were found in the project.",
                "OK");
            return;
        }

        // Build JSON
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        int index = 0;
        foreach (KeyValuePair<string, string> entry in entries)
        {
            string escapedValue = entry.Value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n");

            sb.Append("  \"" + entry.Key + "\": \"" + escapedValue + "\"");
            if (index < entries.Count - 1)
            {
                sb.AppendLine(",");
            }
            else
            {
                sb.AppendLine();
            }
            index++;
        }
        sb.AppendLine("}");

        // Write to Languages folder
        string languagesFolder = Path.Combine(Application.dataPath, "Languages");
        if (!Directory.Exists(languagesFolder))
        {
            Directory.CreateDirectory(languagesFolder);
        }

        string outputPath = Path.Combine(languagesFolder, "lang_enUS.json");
        File.WriteAllText(outputPath, sb.ToString());
        AssetDatabase.Refresh();

        Debug.Log("[ModBuildTools] Generated lang_enUS.json with " + entries.Count + " entries at " + outputPath);
        EditorUtility.RevealInFinder(outputPath);
    }

    private static void AddDialogEntry(SortedDictionary<string, string> entries, string key, SerializedProperty dialogElement)
    {
        SerializedProperty dialogProp = dialogElement.FindPropertyRelative("dialog");
        if (dialogProp != null && !string.IsNullOrEmpty(dialogProp.stringValue))
        {
            entries[key] = dialogProp.stringValue;
        }
    }

    private static void AddDialogArray(SortedDictionary<string, string> entries, SerializedObject so, string propertyName, string keyPrefix)
    {
        SerializedProperty arrayProp = so.FindProperty(propertyName);
        if (arrayProp == null || !arrayProp.isArray)
        {
            return;
        }

        for (int i = 0; i < arrayProp.arraySize; i++)
        {
            SerializedProperty element = arrayProp.GetArrayElementAtIndex(i);
            SerializedProperty nameProp = element.FindPropertyRelative("name");
            // Skip AutoPilotBreak (9999) entries
            if (nameProp != null && nameProp.intValue == 9999)
            {
                continue;
            }
            AddDialogEntry(entries, keyPrefix + "." + i, element);
        }
    }

    [MenuItem("Star Vortex Mod/Open Build Folder", false, 40)]
    public static void OpenBuildFolder()
    {
        string outputPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, OutputFolder);
        if (!Directory.Exists(outputPath))
        {
            Directory.CreateDirectory(outputPath);
        }
        EditorUtility.RevealInFinder(outputPath);
    }

    private static bool IsInAssetBundle(string assetPath)
    {
        AssetImporter importer = AssetImporter.GetAtPath(assetPath);
        if (importer == null)
        {
            return false;
        }
        return !string.IsNullOrEmpty(importer.assetBundleName);
    }

    private static string FindFileInAssets(string fileName)
    {
        string[] files = Directory.GetFiles(Application.dataPath, fileName, SearchOption.AllDirectories);
        if (files.Length > 0)
        {
            return files[0];
        }
        return null;
    }

    private static void GenerateComponentManifest(string outputPath)
    {
        List<ComponentEntry> componentEntries = new List<ComponentEntry>();

        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
        foreach (string guid in prefabGuids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (assetPath.Contains("/Editor/") || assetPath.Contains("/ModTemplate/"))
            {
                continue;
            }
            // Only ship manifest entries for assets assigned to an AssetBundle —
            // unbundled strays would override base game content at runtime.
            if (!IsInAssetBundle(assetPath))
            {
                continue;
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                continue;
            }

            Transform[] allTransforms = prefab.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in allTransforms)
            {
                Component[] components = t.GetComponents<Component>();
                foreach (Component comp in components)
                {
                    if (comp == null)
                    {
                        continue;
                    }

                    System.Type compType = comp.GetType();
                    if (compType.Assembly.GetName().Name != "Assembly-CSharp")
                    {
                        continue;
                    }

                    ComponentEntry entry = new ComponentEntry();
                    entry.prefab = prefab.name;
                    entry.path = GetRelativePath(t, prefab.transform);
                    entry.type = compType.FullName;
                    entry.fields = SerializeFields(comp);
                    componentEntries.Add(entry);
                }
            }
        }

        // Scan ScriptableObjects from Assembly-CSharp
        List<ScriptableObjectEntry> soEntries = new List<ScriptableObjectEntry>();

        string[] soGuids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets" });
        foreach (string soGuid in soGuids)
        {
            string soAssetPath = AssetDatabase.GUIDToAssetPath(soGuid);
            if (soAssetPath.Contains("/Editor/") || soAssetPath.Contains("/ModTemplate/") || soAssetPath.Contains("/GameContent/"))
            {
                continue;
            }
            // Only ship manifest entries for assets assigned to an AssetBundle —
            // unbundled strays would override base game content at runtime
            // (RestoreModScriptableObjects applies these over base assets).
            if (!IsInAssetBundle(soAssetPath))
            {
                continue;
            }

            ScriptableObject so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(soAssetPath);
            if (so == null)
            {
                continue;
            }

            System.Type soType = so.GetType();
            if (soType.Assembly.GetName().Name != "Assembly-CSharp")
            {
                continue;
            }

            ScriptableObjectEntry soEntry = new ScriptableObjectEntry();
            soEntry.assetName = so.name;
            soEntry.type = soType.FullName;
            soEntry.json = JsonUtility.ToJson(so);
            soEntry.assetReferences = CollectAssetReferences(so);
            soEntries.Add(soEntry);
        }

        string manifestPath = Path.Combine(outputPath, "components.json");
        if (componentEntries.Count == 0 && soEntries.Count == 0)
        {
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }
            return;
        }

        ComponentManifest manifest = new ComponentManifest();
        manifest.gameVersion = ParseModJsonField("gameVersion");
        manifest.components = componentEntries.ToArray();
        manifest.scriptableObjects = soEntries.ToArray();
        string json = JsonUtility.ToJson(manifest, true);
        File.WriteAllText(manifestPath, json);
        Debug.Log("[ModBuildTools] Generated manifest with " + componentEntries.Count + " component(s) and " + soEntries.Count + " ScriptableObject(s).");
    }

    private static AssetReference[] CollectAssetReferences(ScriptableObject so)
    {
        List<AssetReference> refs = new List<AssetReference>();
        SerializedObject serializedObj = new SerializedObject(so);
        SerializedProperty prop = serializedObj.GetIterator();

        while (prop.Next(true))
        {
            if (prop.propertyType != SerializedPropertyType.ObjectReference)
            {
                continue;
            }
            if (prop.name == "m_Script")
            {
                continue;
            }

            Object obj = prop.objectReferenceValue;
            if (obj == null)
            {
                continue;
            }

            AssetReference assetRef = new AssetReference();
            assetRef.fieldPath = prop.propertyPath.Replace(".Array.data[", ".").Replace("]", "");
            assetRef.assetName = obj.name;
            assetRef.assetType = obj.GetType().FullName;
            refs.Add(assetRef);
        }

        return refs.ToArray();
    }

    private static string GetRelativePath(Transform child, Transform root)
    {
        if (child == root)
        {
            return "";
        }

        List<string> parts = new List<string>();
        Transform current = child;
        while (current != null && current != root)
        {
            parts.Add(current.name);
            current = current.parent;
        }
        parts.Reverse();
        return string.Join("/", parts.ToArray());
    }

    private static FieldEntry[] SerializeFields(Component comp)
    {
        SerializedObject so = new SerializedObject(comp);
        SerializedProperty prop = so.GetIterator();
        List<FieldEntry> fields = new List<FieldEntry>();

        bool enterChildren = true;
        while (prop.NextVisible(enterChildren))
        {
            enterChildren = false;

            if (prop.name == "m_Script" || prop.name == "m_ObjectHideFlags"
                || prop.name == "m_Enabled" || prop.name == "m_EditorHideFlags"
                || prop.name == "m_EditorClassIdentifier")
            {
                continue;
            }

            FieldEntry field = SerializeField(prop);
            if (field != null)
            {
                fields.Add(field);
            }
        }

        return fields.ToArray();
    }

    private static FieldEntry SerializeField(SerializedProperty prop)
    {
        // Handle arrays (String is flagged as array internally but is a single value)
        if (prop.isArray && prop.propertyType != SerializedPropertyType.String)
        {
            return SerializeArrayField(prop);
        }

        FieldEntry entry = new FieldEntry();
        entry.name = prop.name;
        entry.value = SerializePropertyValue(prop);

        if (entry.value == null)
        {
            return null;
        }

        switch (prop.propertyType)
        {
            case SerializedPropertyType.Integer:
            case SerializedPropertyType.LayerMask:
                entry.type = "int";
                return entry;
            case SerializedPropertyType.Float:
                entry.type = "float";
                return entry;
            case SerializedPropertyType.Boolean:
                entry.type = "bool";
                return entry;
            case SerializedPropertyType.String:
                entry.type = "string";
                return entry;
            case SerializedPropertyType.Enum:
                entry.type = "enum";
                return entry;
            case SerializedPropertyType.Color:
                entry.type = "color";
                return entry;
            case SerializedPropertyType.Vector2:
                entry.type = "vector2";
                return entry;
            case SerializedPropertyType.Vector3:
                entry.type = "vector3";
                return entry;
            case SerializedPropertyType.Vector4:
                entry.type = "vector4";
                return entry;
            case SerializedPropertyType.Quaternion:
                entry.type = "quaternion";
                return entry;
            case SerializedPropertyType.Rect:
                entry.type = "rect";
                return entry;
            case SerializedPropertyType.Bounds:
                entry.type = "bounds";
                return entry;
            case SerializedPropertyType.ObjectReference:
                Object obj = prop.objectReferenceValue;
                if (obj == null)
                {
                    return null;
                }
                entry.type = "asset";
                entry.assetType = obj.GetType().FullName;
                return entry;
            default:
                return null;
        }
    }

    private static string SerializePropertyValue(SerializedProperty prop)
    {
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;

        switch (prop.propertyType)
        {
            case SerializedPropertyType.Integer:
            case SerializedPropertyType.LayerMask:
                return prop.intValue.ToString();
            case SerializedPropertyType.Float:
                return prop.floatValue.ToString(inv);
            case SerializedPropertyType.Boolean:
                return prop.boolValue ? "true" : "false";
            case SerializedPropertyType.String:
                return prop.stringValue;
            case SerializedPropertyType.Enum:
                // intValue is the raw underlying enum value; enumValueIndex is an
                // index into enumNames, which differs for non-sequential enums and
                // would deserialize to the wrong member via Enum.ToObject.
                return prop.intValue.ToString();
            case SerializedPropertyType.Color:
                Color c = prop.colorValue;
                return c.r.ToString(inv) + "," + c.g.ToString(inv) + "," + c.b.ToString(inv) + "," + c.a.ToString(inv);
            case SerializedPropertyType.Vector2:
                Vector2 v2 = prop.vector2Value;
                return v2.x.ToString(inv) + "," + v2.y.ToString(inv);
            case SerializedPropertyType.Vector3:
                Vector3 v3 = prop.vector3Value;
                return v3.x.ToString(inv) + "," + v3.y.ToString(inv) + "," + v3.z.ToString(inv);
            case SerializedPropertyType.Vector4:
                Vector4 v4 = prop.vector4Value;
                return v4.x.ToString(inv) + "," + v4.y.ToString(inv) + "," + v4.z.ToString(inv) + "," + v4.w.ToString(inv);
            case SerializedPropertyType.Quaternion:
                Quaternion q = prop.quaternionValue;
                return q.x.ToString(inv) + "," + q.y.ToString(inv) + "," + q.z.ToString(inv) + "," + q.w.ToString(inv);
            case SerializedPropertyType.Rect:
                Rect r = prop.rectValue;
                return r.x.ToString(inv) + "," + r.y.ToString(inv) + "," + r.width.ToString(inv) + "," + r.height.ToString(inv);
            case SerializedPropertyType.Bounds:
                Bounds b = prop.boundsValue;
                return b.center.x.ToString(inv) + "," + b.center.y.ToString(inv) + "," + b.center.z.ToString(inv)
                    + "," + b.size.x.ToString(inv) + "," + b.size.y.ToString(inv) + "," + b.size.z.ToString(inv);
            case SerializedPropertyType.ObjectReference:
                Object obj = prop.objectReferenceValue;
                return obj != null ? obj.name : null;
            default:
                return null;
        }
    }

    private static FieldEntry SerializeArrayField(SerializedProperty prop)
    {
        if (prop.arraySize == 0)
        {
            return null;
        }

        SerializedProperty firstElement = prop.GetArrayElementAtIndex(0);
        string elementType;
        string assetType = "";

        switch (firstElement.propertyType)
        {
            case SerializedPropertyType.Integer:
            case SerializedPropertyType.LayerMask:
                elementType = "int"; break;
            case SerializedPropertyType.Float:
                elementType = "float"; break;
            case SerializedPropertyType.Boolean:
                elementType = "bool"; break;
            case SerializedPropertyType.String:
                elementType = "string"; break;
            case SerializedPropertyType.Enum:
                elementType = "enum"; break;
            case SerializedPropertyType.Color:
                elementType = "color"; break;
            case SerializedPropertyType.Vector2:
                elementType = "vector2"; break;
            case SerializedPropertyType.Vector3:
                elementType = "vector3"; break;
            case SerializedPropertyType.Vector4:
                elementType = "vector4"; break;
            case SerializedPropertyType.Quaternion:
                elementType = "quaternion"; break;
            case SerializedPropertyType.Rect:
                elementType = "rect"; break;
            case SerializedPropertyType.Bounds:
                elementType = "bounds"; break;
            case SerializedPropertyType.ObjectReference:
                elementType = "asset";
                for (int i = 0; i < prop.arraySize; i++)
                {
                    Object obj = prop.GetArrayElementAtIndex(i).objectReferenceValue;
                    if (obj != null)
                    {
                        assetType = obj.GetType().FullName;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(assetType))
                {
                    return null;
                }
                break;
            default:
                return null;
        }

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < prop.arraySize; i++)
        {
            if (i > 0)
            {
                sb.Append("|");
            }
            string val = SerializePropertyValue(prop.GetArrayElementAtIndex(i));
            sb.Append(val != null ? val : "");
        }

        FieldEntry entry = new FieldEntry();
        entry.name = prop.name;
        entry.type = elementType + "[]";
        entry.value = sb.ToString();
        entry.assetType = assetType;
        return entry;
    }

    private static string ParseModJsonField(string fieldName)
    {
        string modJsonPath = Path.Combine(Application.dataPath, "mod.json");
        if (!File.Exists(modJsonPath))
        {
            return "";
        }

        string json = File.ReadAllText(modJsonPath);
        int fieldIndex = json.IndexOf("\"" + fieldName + "\"");
        if (fieldIndex < 0)
        {
            return "";
        }

        int colonIndex = json.IndexOf(':', fieldIndex);
        int firstQuote = json.IndexOf('"', colonIndex + 1);
        int secondQuote = json.IndexOf('"', firstQuote + 1);
        if (firstQuote >= 0 && secondQuote > firstQuote)
        {
            return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }

        return "";
    }

    [System.Serializable]
    private class ComponentManifest
    {
        public string gameVersion;
        public ComponentEntry[] components;
        public ScriptableObjectEntry[] scriptableObjects;
    }

    [System.Serializable]
    private class ScriptableObjectEntry
    {
        public string assetName;
        public string type;
        public string json;
        public AssetReference[] assetReferences;
    }

    [System.Serializable]
    private class AssetReference
    {
        public string fieldPath;
        public string assetName;
        public string assetType;
    }

    [System.Serializable]
    private class ComponentEntry
    {
        public string prefab;
        public string path;
        public string type;
        public FieldEntry[] fields;
    }

    [System.Serializable]
    private class FieldEntry
    {
        public string name;
        public string type;
        public string value;
        public string assetType;
    }
}
#endif
