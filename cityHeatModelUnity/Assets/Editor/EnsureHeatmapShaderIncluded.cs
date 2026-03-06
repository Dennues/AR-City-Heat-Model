using System;
using UnityEditor;
using UnityEngine;

// ensures a Material referencing the HeatmapProjective shader exists in Resources
public static class EnsureHeatmapShaderIncluded
{
    [MenuItem("Tools/Ensure Heatmap Shader Included")]
    public static void EnsureIncluded()
    {
        string matPath = "Assets/Resources/HeatmapProjective_Mat.mat";

        var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (existing != null)
        {
            Debug.Log("[EnsureHeatmapShaderIncluded] Resources material already exists.");
            return;
        }

        // try to find shader in project
        Shader s = Shader.Find("Custom/HeatmapProjective");
        if (s == null)
        {
            string[] guids = AssetDatabase.FindAssets("HeatmapProjective t:shader");
            if (guids != null && guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                s = AssetDatabase.LoadAssetAtPath<Shader>(path);
            }
        }

        if (s == null)
        {
            Debug.LogError("[EnsureHeatmapShaderIncluded] Could not find shader 'Custom/HeatmapProjective' in project. Make sure Assets/Shaders/HeatmapProjective.shader exists.");
            return;
        }

        Material mat = new Material(s) { name = "HeatmapProjective_Mat" };
        AssetDatabase.CreateAsset(mat, matPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[EnsureHeatmapShaderIncluded] Created Resources material at {matPath} referencing shader {s.name}. Rebuild your project for changes to take effect.");
    }
}
