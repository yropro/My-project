using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
 
public class HueShiftMaterialInspector : MaterialEditor
{
	private string[] keywordIDs = new string[2] {
		"PS_HSV_ALPHAMASK_ON",
		"PS_HSV_HUERANGE_ON"
	};

	private string[] keywordNames = new string[2] {
		"Use Alpha as HSV mask",
		"Use Hue Range to limit HSV"
	};

	Material targetMat;
	List<string> keywords;
	bool isDirty;

    public override void OnInspectorGUI ()
    {
        base.OnInspectorGUI ();

		targetMat = target as Material;

		if (!isVisible || targetMat == null)
			return;

		isDirty = false;
		keywords = new List<string>(targetMat.shaderKeywords);

		EditorGUILayout.LabelField("HSV Options", EditorStyles.boldLabel);

		for (int i = 0; i < keywordIDs.Length; i++) 
			ShowKeywordButton(keywordIDs[i], keywordNames[i]);

		if (keywords.Contains("PS_HSV_HUERANGE_ON")) {
			ShowPropertySlider(targetMat, "_HueRangeMin", "Min Hue Range");
			ShowPropertySlider(targetMat, "_HueRangeMax", "Max Hue Range");
		}

		if (isDirty) {
			PropertiesChanged();
			targetMat.shaderKeywords = keywords.ToArray();
			EditorUtility.SetDirty(target);
			isDirty = false;
        }

        ShowSpriteTintWarning();

        EditorGUILayout.HelpBox("Red value changes the source texture hue.", MessageType.None);
        EditorGUILayout.HelpBox("Green value changes the source texture saturation.", MessageType.None);
        EditorGUILayout.HelpBox("Blue value changes the source texture lightness.", MessageType.None);
    }

    void ShowSpriteTintWarning() {
        // Sprite tint color
        var allProperties = GetMaterialProperties(targets);

        for (int i = 0; i < allProperties.Length; i++) {
            if (allProperties[i].name != "_Color")
                continue;
            else if (allProperties[i].type != MaterialProperty.PropType.Color || allProperties[i].colorValue == Color.white)
                continue;

            EditorGUILayout.HelpBox("Tint Color should generally remain White (255, 255, 255, 255). Use sprite/image color instead!", MessageType.Warning);
            return;
        }

        EditorGUILayout.HelpBox("To change nothing in the source texture, set the sprite/ui color to 128, 128, 128, 256.", MessageType.Info);
    }

	void ShowKeywordButton(string keyword, string keywordName) {
		bool isOn = IsKeywordOn(keyword);

		EditorGUI.BeginChangeCheck();
		isOn = EditorGUILayout.Toggle (keywordName, isOn);

		if (EditorGUI.EndChangeCheck()) {
			if (isOn)
				keywords.Add(keyword);
			else
				keywords.Remove(keyword);

			isDirty = true;
		}
	}

	void ShowPropertySlider(Material mat, string propertyName, string label) {
		MaterialProperty property = GetMaterialProperty( new Object[] { mat }, propertyName );

		if( property == null )
			return;

		EditorGUI.BeginChangeCheck( );

		RangeProperty( property, label );

		if( EditorGUI.EndChangeCheck( ) )
			isDirty = true;
	}

	bool IsKeywordOn(string keyword) {
		for (int i = 0; i < targetMat.shaderKeywords.Length; i++) 
			if (targetMat.shaderKeywords[i] == keyword)
				return true;

		return false;
	}
}