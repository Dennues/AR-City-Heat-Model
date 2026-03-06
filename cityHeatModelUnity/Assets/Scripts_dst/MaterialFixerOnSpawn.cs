using UnityEngine;

/// <summary>
/// fixes cached shaders on spawned prefab materials before heatmap manager applies textures
/// </summary>
public class MaterialFixerOnSpawn : MonoBehaviour
{
    private void Awake()
    {
        FixShaders();
    }

    private void FixShaders()
    {
        // find all renderers in this clone, excluding "Visuals"
        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        
        // filter out Visuals renderers
        var filteredRenderers = new System.Collections.Generic.List<Renderer>();
        foreach (var r in renderers)
        {
            if (r.gameObject.name == "Visuals") continue;
            filteredRenderers.Add(r);
        }
        
        // find URP shader
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");

        if (shader == null)
        {
            Debug.LogWarning("[MaterialFixerOnSpawn] No URP shader found");
            return;
        }

        foreach (var renderer in filteredRenderers)
        {
            if (renderer == null) continue;

            var shared = renderer.sharedMaterials;
            if (shared == null || shared.Length == 0) continue;

            Material[] newMats = new Material[shared.Length];
            bool replacedAny = false;
            for (int i = 0; i < shared.Length; i++)
            {
                var src = shared[i];
                if (src == null)
                {
                    newMats[i] = null;
                    continue;
                }
                Material inst = new Material(src) { name = src.name + "_inst" };
                if (inst.shader == null || inst.shader.name != shader.name)
                {
                    inst.shader = shader;
                    replacedAny = true;
                    Debug.Log($"[MaterialFixerOnSpawn] Replaced shader on instance of '{src.name}' to '{shader.name}' for object '{renderer.gameObject.name}'");
                }
                newMats[i] = inst;
            }

            if (replacedAny)
            {
                renderer.materials = newMats;
            }
            else
            {
                for (int i = 0; i < newMats.Length; i++)
                    if (newMats[i] != null) Destroy(newMats[i]);
            }
        }

        Debug.Log($"[MaterialFixerOnSpawn] Shader fixes applied on {gameObject.name}");
    }
}
