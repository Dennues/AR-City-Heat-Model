using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// manages heatmap generation and texture application in AR
/// fetches heatmap images from backend server and applies them to plane and building renderers
/// </summary>
public class ARHeatmapManager : MonoBehaviour
{
    [System.Serializable]
    public struct HeatmapSettings
    {
        public float latMin;
        public float latMax;
        public float lonMin;
        public float lonMax;
        public string colorMode; // "polygon" or "smooth"
        public bool enableGapFill;
        public string scaleMode; // "tight", "auto", "percentile"
        public string tempCsv; // path to temperature CSV
        public string timeId; // "6am", "4pm", etc
    }

    [System.Serializable]
    private class GenerateRequest
    {
        public float latMin;
        public float latMax;
        public float lonMin;
        public float lonMax;
        public string colorMode;
        public bool enableGapFill;
        public string scaleMode;
        public string tempCsv;
        public string timeId;
    }

    // Server URL configuration
    [SerializeField] private string serverUrl = "http://localhost:3001";
    [SerializeField] private bool useProductionServer = false; // Set to true for Heroku
    [SerializeField] private string productionServerUrl = "https://heatmap-624a6854fef7.herokuapp.com";
    [SerializeField] private bool useLocalFallbackOnServerFail = false; // try LoadTextureFromDisk fallback when server unreachable
    
    [SerializeField] private Renderer[] targetRenderers;
    [SerializeField] private string materialPropertyName = "_BaseMap";
    [SerializeField] private Transform renderersRoot; // optional root to search under
    [SerializeField] private string projectionReferenceRendererName = "Plane";
    [SerializeField] private Transform projectionReferenceTransform; // optional explicit transform for projection calibration
    
    // building projective mapping offset/scale (plane uses textureOffset/textureScale)
    [SerializeField] private Vector2 projectiveOffset = new Vector2(0.004f, -0.014f); // building offset
    [SerializeField] private Vector2 projectiveScale = new Vector2(0.94f, 0.953f); // building scale
    [SerializeField] private Transform buildingsRoot; // optional root to search for building meshes 
    [SerializeField] private bool applyHeatmapToBuildings = true;
    [SerializeField] private string buildingNameFilter = "default"; // filter for building renderer names
    [SerializeField] private string localGeoJsonDir = ""; // if empty uses Application.streamingAssetsPath/MapAssets/Generated
    [SerializeField] private float detectInterval = 1.0f; // seconds between detection scans
    private float lastDetectTime = 0f;
    
    private readonly List<Material> buildingMaterials = new List<Material>();
    private Vector3 cachedHeatmapOriginWS = Vector3.zero;
    private Vector2 cachedHeatmapSize = Vector2.one;
    private Vector3 cachedPlaneAxisU = Vector3.right;
    private Vector3 cachedPlaneAxisV = Vector3.forward;
    private Renderer cachedProjectionReferenceRenderer;
    
    // plane texture transformation parameters
    [SerializeField] private Vector2 textureOffset = new Vector2(-0.004f, -0.014f); // plane texture offset
    [SerializeField] private Vector2 textureScale = new Vector2(0.94f, 0.953f); // plane texture scale
    [SerializeField] private float textureRotation = 0f; // plane texture rotation in degrees
    private const bool flipZ = true; // flip left-right (Z axis)

    [Header("Labeled Map Overlay")]
    [Tooltip("Optional labeled/map image to overlay on the plane (will be multiply-blended). If empty, the file at StreamingAssets/MapAssets/<LabeledMapFilename> will be used if present.")]
    [SerializeField] private Texture2D labeledMapTexture = null;
    [Tooltip("If no Texture2D is assigned, this filename from StreamingAssets/MapAssets will be loaded (Windows/Editor).")]
    [SerializeField] private string labeledMapFilename = "3815811_SW_2025.png";
    [Tooltip("Offset (UV) for the labeled overlay so you can align it on the plane.")]
    [SerializeField] private Vector2 labeledMapOffset = Vector2.zero; // Labeled Map Offset
    [Tooltip("Scale (UV) for the labeled overlay so you can align it on the plane.")]
    [SerializeField] private Vector2 labeledMapScale = Vector2.one; // Labeled Map Scale
    
    private HeatmapSettings currentSettings;
    private bool isGenerating = false;
    private Texture2D currentTexture;
    private Texture2D currentPlaneCompositeTexture;
    private Material runtimeMaterial;
    private string lastLoadedTextureId = "";
    private string lastRequestId = "";
    // for changing at runtime
    private Vector2 prevLabeledMapOffset = Vector2.zero;
    private Vector2 prevLabeledMapScale = Vector2.one;

    private void Start()
    {        // Auto-configure server URL based on environment
        if (useProductionServer)
        {
            serverUrl = productionServerUrl;
            Debug.Log($"[ARHeatmap] Using production server: {serverUrl}");
        }
        else
        {
            Debug.Log($"[ARHeatmap] Using local server: {serverUrl}");
        }
                RefreshRendererListIfNeeded();
        EnsureCorrectMaterial();
        
        if (targetRenderers != null && targetRenderers.Length > 0)
        {
            Debug.Log($"[ARHeatmap] Start: Found {targetRenderers.Length} renderers");
            foreach (var r in targetRenderers)
            {
                if (r != null)
                    Debug.Log($"[ARHeatmap]   - {r.gameObject.name} (has {r.sharedMaterials.Length} materials)");
            }
        }
        else
        {
            Debug.LogWarning("[ARHeatmap] Start: No renderers found. Ensure renderersRoot is set or assign targetRenderers manually.");
        }
    }

    private void EnsureCorrectMaterial()
    {
        if (targetRenderers == null || targetRenderers.Length == 0) return;

        // create fresh material to avoid keyword space conflicts
        if (runtimeMaterial == null)
        {
            // try URP shaders
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Unlit/Texture");
            if (shader == null) shader = Shader.Find("Standard");

            if (shader != null)
            {

                runtimeMaterial = new Material(shader) { name = "HeatmapMat_Runtime" };
                Debug.Log($"[ARHeatmap] Created fresh runtime material with shader '{shader.name}'");
            }
            else
            {
                Debug.LogError("[ARHeatmap] No suitable shader found for plane material");
                return;
            }
        }


        foreach (var rend in targetRenderers)
        {
            if (rend == null) continue;
            
            // skip "Visuals" objects
            if (rend.gameObject.name == "Visuals")
            {
                Debug.Log($"[ARHeatmap] Skipping Visuals object: {rend.gameObject.name}");
                continue;
            }

            // skip buildings (use projective shader)
            if (buildingsRoot != null && rend.transform.IsChildOf(buildingsRoot.transform))
            {
                Debug.Log($"[ARHeatmap] Skipping building renderer: {rend.gameObject.name}");
                continue;
            }


            int count = rend.sharedMaterials.Length;
            if (count == 0) count = 1;

            Material[] newMaterials = new Material[count];
            for (int i = 0; i < count; i++)
            {
                newMaterials[i] = runtimeMaterial;
            }
            rend.sharedMaterials = newMaterials;
            Debug.Log($"[ARHeatmap] Assigned runtime material to {rend.gameObject.name}");
        }

        Debug.Log($"[ARHeatmap] Assigned runtime material with shader '{runtimeMaterial.shader.name}' to all target renderers");
    }

    private void RefreshRendererListIfNeeded()
    {
        Renderer[] found;
        if (renderersRoot != null)
        {
            found = renderersRoot.GetComponentsInChildren<Renderer>(true);
        }
        else
        {
            found = FindObjectsOfType<Renderer>(true);
            Debug.Log($"[ARHeatmap] Searching entire scene for renderers, found {found.Length} total");
        }
        // filter renderers
        var unique = new System.Collections.Generic.List<Renderer>();
        var seen = new System.Collections.Generic.HashSet<Renderer>();
        foreach (var r in found)
        {
            if (r == null) continue;
            if (r.gameObject.name == "Visuals") continue;
            
            string objName = r.gameObject.name.ToLower();
            if (objName.Contains("default"))
            {
                Debug.Log($"[ARHeatmap] Excluding building '{r.gameObject.name}' from targetRenderers (will use projective mapping)");
                continue;
            }
            
            if (buildingsRoot != null && r.transform.IsChildOf(buildingsRoot.transform))
                continue;
            if (seen.Add(r)) unique.Add(r);
        }

        if (unique.Count > 0)
        {
            targetRenderers = unique.ToArray();
            Debug.Log($"[ARHeatmap] Found {targetRenderers.Length} renderer(s): {string.Join(", ", System.Array.ConvertAll(targetRenderers, r => r.gameObject.name))}");
        }
    }

    /// <summary>
    /// requests heatmap generation from backend server
    /// </summary>
    public void GenerateHeatmap(HeatmapSettings settings)
    {
        if (isGenerating)
        {
            Debug.LogWarning("[ARHeatmap] Generation already in progress");
            return;
        }

        currentSettings = settings;
        StartCoroutine(GenerateHeatmapCoroutine(settings));
    }

    private IEnumerator GenerateHeatmapCoroutine(HeatmapSettings settings)
    {
        isGenerating = true;
        Debug.Log($"[ARHeatmap] Starting generation: mode={settings.colorMode}, time={settings.timeId}");

        GenerateRequest requestData = new GenerateRequest
        {
            latMin = settings.latMin,
            latMax = settings.latMax,
            lonMin = settings.lonMin,
            lonMax = settings.lonMax,
            colorMode = settings.colorMode,
            enableGapFill = settings.enableGapFill,
            scaleMode = settings.scaleMode,
            tempCsv = settings.tempCsv,
            timeId = settings.timeId
        };

        string jsonBody = JsonUtility.ToJson(requestData);
        Debug.Log($"[ARHeatmap] Request JSON: {jsonBody}");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);

        using (UnityWebRequest www = new UnityWebRequest($"{serverUrl}/api/heatmap/generate", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"[ARHeatmap] Generation successful");
                string responseJson = www.downloadHandler.text;
                GenerateResponse response = null;
                try
                {
                    response = JsonUtility.FromJson<GenerateResponse>(responseJson);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ARHeatmap] Failed to parse response: {e.Message}");
                }

                if (response != null)
                {
                    lastRequestId = response.requestId ?? "";
                    if (!string.IsNullOrEmpty(response.pngUrl))
                    {
                        yield return StartCoroutine(LoadAndApplyTexture(response.pngUrl, settings));
                    }
                    else
                    {
                        Debug.LogError("[ARHeatmap] Response missing pngUrl");
                    }
                }
                else
                {
                    Debug.LogError("[ARHeatmap] Response missing pngUrl");
                }
            }
            else
            {
                Debug.LogError($"[ARHeatmap] Generation failed: {www.error}");

                if (useLocalFallbackOnServerFail)
                {
                    Debug.Log("[ARHeatmap] Server request failed, attempting local fallback load ...");
                    // attempt to load local pngs
                    string filename = $"heatmap-{settings.colorMode}-{settings.timeId}.png";
                    string baseDir = System.IO.Path.Combine(Application.streamingAssetsPath, "MapAssets", "Generated");

                    string candidate = System.IO.Path.Combine(baseDir, filename);

                    if (System.IO.File.Exists(candidate) || (Application.platform == RuntimePlatform.Android && candidate.StartsWith(Application.streamingAssetsPath)))
                    {
                        Debug.Log($"[ARHeatmap] Found fallback file: {candidate} loading from disk");
                        yield return StartCoroutine(LoadTextureFromDisk(candidate));
                    }
                    else
                    {
                        Debug.LogError($"[ARHeatmap] Local fallback enabled but no matching heatmap PNG found at: {candidate}");
                    }
                }
            }
        }

        isGenerating = false;
    }

    private IEnumerator LoadAndApplyTexture(string textureUrl, HeatmapSettings settings)
    {
        string resolvedUrl = textureUrl;
        if (!string.IsNullOrEmpty(textureUrl) && textureUrl.StartsWith("/"))
        {
            resolvedUrl = $"{serverUrl}{textureUrl}";
        }

        Debug.Log($"[ARHeatmap] Loading texture from: {resolvedUrl}");

        using (UnityWebRequest www = UnityWebRequestTexture.GetTexture(resolvedUrl))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                currentTexture = DownloadHandlerTexture.GetContent(www);
                ApplyTexture(currentTexture);
                Debug.Log($"[ARHeatmap] Texture applied: {settings.colorMode}-{settings.timeId}");
            }
            else
            {
                Debug.LogError($"[ARHeatmap] Texture load failed: {www.error}");
            }
        }
    }

    public void ApplyTexture(Texture2D texture)
    {
        RefreshRendererListIfNeeded();
        
        EnsureCorrectMaterial();

        if (targetRenderers == null || targetRenderers.Length == 0)
        {
            Debug.LogError("[ARHeatmap] No target renderers assigned");
            return;
        }

        if (runtimeMaterial == null)
        {
            Debug.LogError("[ARHeatmap] Runtime material not initialized");
            return;
        }

        // set base runtime material
        runtimeMaterial.SetTexture(materialPropertyName, texture);
        runtimeMaterial.SetTexture("_BaseMap", texture);
        runtimeMaterial.SetTexture("_MainTex", texture);
        ApplyTextureTransforms();

        // prepare labeled overlay and composite for plane renderers
        EnsureLabeledMapLoaded();
        if (currentPlaneCompositeTexture != null)
        {
            Destroy(currentPlaneCompositeTexture);
            currentPlaneCompositeTexture = null;
        }

        Texture2D planeComposite = null;
        if (labeledMapTexture != null)
        {
            planeComposite = CompositeLabeledMapOnPlane(texture);
            currentPlaneCompositeTexture = planeComposite;
        }

        // assign materials per renderer
        foreach (var rend in targetRenderers)
        {
            if (rend == null) continue;

            // skip "Visuals" objects
            if (rend.gameObject.name == "Visuals")
            {
                Debug.Log($"[ARHeatmap] Skipping Visuals object in ApplyTexture: {rend.gameObject.name}");
                continue;
            }

            bool isBuilding = (buildingsRoot != null && rend.transform.IsChildOf(buildingsRoot.transform));

            if (isBuilding)
            {
                var mats = rend.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    mats[i] = runtimeMaterial;
                    mats[i].SetTexture(materialPropertyName, texture);
                    mats[i].SetTexture("_BaseMap", texture);
                    mats[i].SetTexture("_MainTex", texture);
                }
                rend.sharedMaterials = mats;
            }
            else
            {
                Material inst = new Material(runtimeMaterial) { name = runtimeMaterial.name + "_PlaneInstance" };
                Texture2D toAssign = planeComposite != null ? planeComposite : texture;
                inst.SetTexture(materialPropertyName, toAssign);
                inst.SetTexture("_BaseMap", toAssign);
                inst.SetTexture("_MainTex", toAssign);

                inst.SetTextureOffset("_BaseMap", textureOffset);
                inst.SetTextureScale("_BaseMap", textureScale);
                inst.SetTextureOffset("_MainTex", textureOffset);
                inst.SetTextureScale("_MainTex", textureScale);

                rend.material = inst;
            }
        }

        currentTexture = texture;
        if (!TryGetHeatmapBounds(out cachedHeatmapOriginWS, out cachedHeatmapSize))
        {
            cachedHeatmapOriginWS = Vector3.zero;
            cachedHeatmapSize = Vector2.one;
        }

        if (applyHeatmapToBuildings)
        {
            ApplyHeatmapToBuildings(texture);
            StartCoroutine(DetectAfterDelay(0.25f));
        }

        Debug.Log("[ARHeatmap] Texture applied to plane and buildings simultaneously");
    }

    private IEnumerator DetectAfterDelay(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        Debug.Log("[ARHeatmap] DetectAfterDelay triggered - scanning for cloned buildings");
        DetectAndApplyToNewBuildings();
    }

    private float cachedPlaneRotationY = 0f;

    private bool TryGetHeatmapBounds(out Vector3 originWS, out Vector2 sizeXZ)
    {
        originWS = Vector3.zero;
        sizeXZ = Vector2.one;

        if (targetRenderers == null || targetRenderers.Length == 0) return false;

        if (!ComputeProjectionFrame(out originWS, out sizeXZ, out cachedPlaneAxisU, out cachedPlaneAxisV, out cachedPlaneRotationY))
            return false;

        return true;
    }

    private bool ComputeProjectionFrame(out Vector3 originWS, out Vector2 sizeXZ, out Vector3 axisU, out Vector3 axisV, out float rotationY)
    {
        originWS = Vector3.zero;
        sizeXZ = Vector2.one;
        axisU = Vector3.right;
        axisV = Vector3.forward;
        rotationY = 0f;

        Renderer r = GetProjectionReferenceRenderer();
        if (r == null) return false;

        var b = r.bounds;
        originWS = b.center;
        
        Transform rotationSource = r.transform;
        rotationY = rotationSource.eulerAngles.y;
        axisU = rotationSource.right.normalized;
        axisV = rotationSource.forward.normalized;

        float sizeU, sizeV;
        if (!TryEstimateRendererSizeOnPlaneAxes(r, out sizeU, out sizeV))
        {
            EstimateBoundsSizeOnAxes(b, axisU, axisV, out sizeU, out sizeV);
        }
        sizeXZ = new Vector2(Mathf.Max(0.0001f, sizeU), Mathf.Max(0.0001f, sizeV));

        if (cachedProjectionReferenceRenderer != r)
        {
            cachedProjectionReferenceRenderer = r;
            Debug.Log($"[ARHeatmap] Projection reference renderer: {r.gameObject.name}");
        }
        Debug.Log($"[ARHeatmap] Plane bounds - Center: ({originWS.x:F2}, {originWS.y:F2}, {originWS.z:F2}), Size: ({sizeXZ.x:F2}, {sizeXZ.y:F2})");
        Debug.Log($"[ARHeatmap] Rotation source: {rotationSource.name}, Rotation Y: {rotationY:F2}°, AxisU={axisU}, AxisV={axisV}");
        return true;
    }

    private bool TryEstimateRendererSizeOnPlaneAxes(Renderer renderer, out float sizeU, out float sizeV)
    {
        sizeU = 0f;
        sizeV = 0f;

        if (renderer == null) return false;

        var meshFilter = renderer.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null) return false;

        var localBounds = meshFilter.sharedMesh.bounds;
        var ls = renderer.transform.lossyScale;

        sizeU = Mathf.Abs(localBounds.size.x * ls.x);
        sizeV = Mathf.Abs(localBounds.size.z * ls.z);

        return sizeU > 0.0001f && sizeV > 0.0001f;
    }

    private Renderer GetProjectionReferenceRenderer()
    {
        if (projectionReferenceTransform != null)
        {
            var explicitRenderer = projectionReferenceTransform.GetComponent<Renderer>();
            if (explicitRenderer != null) return explicitRenderer;

            var nested = projectionReferenceTransform.GetComponentsInChildren<Renderer>(true);
            Renderer bestNested = null;
            float bestNestedArea = float.NegativeInfinity;
            for (int i = 0; i < nested.Length; i++)
            {
                var r = nested[i];
                if (r == null) continue;
                if (r.gameObject.name == "Visuals") continue;
                float areaScore = r.bounds.size.sqrMagnitude;
                if (areaScore > bestNestedArea)
                {
                    bestNestedArea = areaScore;
                    bestNested = r;
                }
            }
            if (bestNested != null) return bestNested;
        }

        if (targetRenderers == null || targetRenderers.Length == 0) return null;

        Renderer bestByName = null;
        Renderer bestByArea = null;
        float bestArea = float.NegativeInfinity;

        for (int i = 0; i < targetRenderers.Length; i++)
        {
            var r = targetRenderers[i];
            if (r == null) continue;
            if (r.gameObject.name == "Visuals") continue;
            if (buildingsRoot != null && r.transform.IsChildOf(buildingsRoot.transform)) continue;

            if (!string.IsNullOrEmpty(projectionReferenceRendererName) &&
                string.Equals(r.gameObject.name, projectionReferenceRendererName, StringComparison.OrdinalIgnoreCase))
            {
                bestByName = r;
                break;
            }

            float areaScore = r.bounds.size.sqrMagnitude;
            if (areaScore > bestArea)
            {
                bestArea = areaScore;
                bestByArea = r;
            }
        }

        return bestByName ?? bestByArea;
    }

    private void EstimateBoundsSizeOnAxes(Bounds bounds, Vector3 axisU, Vector3 axisV, out float sizeU, out float sizeV)
    {
        Vector3 c = bounds.center;
        Vector3 e = bounds.extents;

        float minU = float.PositiveInfinity;
        float maxU = float.NegativeInfinity;
        float minV = float.PositiveInfinity;
        float maxV = float.NegativeInfinity;

        for (int ix = -1; ix <= 1; ix += 2)
        {
            for (int iy = -1; iy <= 1; iy += 2)
            {
                for (int iz = -1; iz <= 1; iz += 2)
                {
                    Vector3 p = new Vector3(
                        c.x + ix * e.x,
                        c.y + iy * e.y,
                        c.z + iz * e.z
                    );

                    float u = Vector3.Dot(p - c, axisU);
                    float v = Vector3.Dot(p - c, axisV);
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }
            }
        }

        sizeU = maxU - minU;
        sizeV = maxV - minV;
    }

    private void UpdateBuildingProjectiveParams()
    {
        if (!applyHeatmapToBuildings || buildingMaterials.Count == 0) return;

        Debug.Log($"[ARHeatmap] Updating building projection - Origin: ({cachedHeatmapOriginWS.x:F2}, {cachedHeatmapOriginWS.z:F2}), Size: ({cachedHeatmapSize.x:F2}, {cachedHeatmapSize.y:F2})");
        Debug.Log($"[ARHeatmap] Building transforms - Offset: ({projectiveOffset.x:F2}, {projectiveOffset.y:F2}), Scale: ({projectiveScale.x:F2}, {projectiveScale.y:F2}), Rotation: {textureRotation:F2}°, PlaneRotY: {cachedPlaneRotationY:F2}°");

        foreach (var mat in buildingMaterials)
        {
            if (mat == null) continue;
            mat.SetFloat("_HeatmapOriginX", cachedHeatmapOriginWS.x);
            mat.SetFloat("_HeatmapOriginZ", cachedHeatmapOriginWS.z);
            mat.SetFloat("_HeatmapSizeX", Mathf.Max(0.0001f, cachedHeatmapSize.x));
            mat.SetFloat("_HeatmapSizeZ", Mathf.Max(0.0001f, cachedHeatmapSize.y));
            mat.SetFloat("_HeatmapOffsetX", projectiveOffset.x);
            mat.SetFloat("_HeatmapOffsetY", projectiveOffset.y);
            mat.SetFloat("_HeatmapScaleX", projectiveScale.x);
            mat.SetFloat("_HeatmapScaleY", projectiveScale.y);
            mat.SetFloat("_HeatmapRotation", textureRotation);
            mat.SetFloat("_PlaneRotationY", cachedPlaneRotationY);
            mat.SetFloat("_FlipZ", flipZ ? 1.0f : 0.0f);
            mat.SetVector("_HeatmapOriginWS", new Vector4(cachedHeatmapOriginWS.x, cachedHeatmapOriginWS.y, cachedHeatmapOriginWS.z, 1.0f));
            mat.SetVector("_PlaneAxisU", new Vector4(cachedPlaneAxisU.x, cachedPlaneAxisU.y, cachedPlaneAxisU.z, 0.0f));
            mat.SetVector("_PlaneAxisV", new Vector4(cachedPlaneAxisV.x, cachedPlaneAxisV.y, cachedPlaneAxisV.z, 0.0f));
        }
    }

    private void ApplyTextureTransforms()
    {
        if (runtimeMaterial == null) return;

        runtimeMaterial.SetTextureOffset("_BaseMap", textureOffset);
        runtimeMaterial.SetTextureScale("_BaseMap", textureScale);
        runtimeMaterial.SetTextureOffset("_MainTex", textureOffset);
        runtimeMaterial.SetTextureScale("_MainTex", textureScale);
        
        Debug.Log($"[ARHeatmap] Applied texture transforms: Offset={textureOffset}, Scale={textureScale}, Rotation={textureRotation}°");
    }

    private void EnsureLabeledMapLoaded()
    {
        if (labeledMapTexture != null) return;
        if (string.IsNullOrEmpty(labeledMapFilename)) return;

        string path = System.IO.Path.Combine(Application.streamingAssetsPath, "MapAssets", labeledMapFilename);
        try
        {
            if (System.IO.File.Exists(path))
            {
                byte[] data = System.IO.File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (tex.LoadImage(data))
                {
                    tex.wrapMode = TextureWrapMode.Repeat;
                    labeledMapTexture = tex;
                    Debug.Log($"[ARHeatmap] Loaded labeled map from StreamingAssets: {path}");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ARHeatmap] Failed to load labeled map from {path}: {e.Message}");
        }
    }

    // Composite the labeled map over the heatmap (multiply blend) and return a new Texture2D sized as the heatmap.
    private Texture2D CompositeLabeledMapOnPlane(Texture2D heatmap)
    {
        if (heatmap == null) return null;
        if (labeledMapTexture == null) return heatmap;

        int w = heatmap.width;
        int h = heatmap.height;

        Texture2D result = new Texture2D(w, h, TextureFormat.RGBA32, false);
        result.wrapMode = TextureWrapMode.Repeat;

        Color[] basePixels = heatmap.GetPixels();

        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) / (float)h;
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / (float)w;

                // convert plane UV -> labeled map UV using offset/scale
                float lu = (u - labeledMapOffset.x) / labeledMapScale.x;
                float lv = (v - labeledMapOffset.y) / labeledMapScale.y;

                Color baseC = basePixels[y * w + x];
                Color outC = baseC;

                if (lu >= 0f && lu <= 1f && lv >= 0f && lv <= 1f)
                {
                    Color lab = labeledMapTexture.GetPixelBilinear(lu, lv);
                    float a = lab.a;
                    // multiply blend respecting alpha: out = base * (1-a + lab.rgb * a)
                    Vector3 blend = new Vector3(1f - a + lab.r * a, 1f - a + lab.g * a, 1f - a + lab.b * a);
                    outC.r = baseC.r * blend.x;
                    outC.g = baseC.g * blend.y;
                    outC.b = baseC.b * blend.z;
                }

                result.SetPixel(x, y, outC);
            }
        }

        result.Apply();
        return result;
    }

    public Vector2 LabeledMapOffset
    {
        get => labeledMapOffset;
        set
        {
            labeledMapOffset = value;
            if (currentTexture != null)
            {
                // regenerate plane composite
                if (currentPlaneCompositeTexture != null) Destroy(currentPlaneCompositeTexture);
                currentPlaneCompositeTexture = CompositeLabeledMapOnPlane(currentTexture);
                // re-apply so changes are visible immediately
                ApplyTexture(currentTexture);
            }
            Debug.Log($"[ARHeatmap] LabeledMapOffset changed to {labeledMapOffset}");
        }
    }

    public Vector2 LabeledMapScale
    {
        get => labeledMapScale;
        set
        {
            labeledMapScale = value;
            if (currentTexture != null)
            {
                if (currentPlaneCompositeTexture != null) Destroy(currentPlaneCompositeTexture);
                currentPlaneCompositeTexture = CompositeLabeledMapOnPlane(currentTexture);
                ApplyTexture(currentTexture);
            }
            Debug.Log($"[ARHeatmap] LabeledMapScale changed to {labeledMapScale}");
        }
    }

    /// <summary>
    /// List all available heatmaps (generated and defaults) from server
    /// </summary>
    public void ListAvailableHeatmaps()
    {
        StartCoroutine(ListAvailableHeatmapsCoroutine());
    }

    private IEnumerator ListAvailableHeatmapsCoroutine()
    {
        using (UnityWebRequest www = UnityWebRequest.Get($"{serverUrl}/api/heatmap/list-existing"))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                string jsonResponse = www.downloadHandler.text;
                Debug.Log($"[ARHeatmap] Available heatmaps:\n{jsonResponse}");
                // Parse and use response as needed
            }
            else
            {
                Debug.LogError($"[ARHeatmap] Failed to list heatmaps: {www.error}");
            }
        }
    }

    /// <summary>
    /// loads and applies existing heatmap texture from server (generated or defaults)
    /// </summary>
    public void LoadExistingTexture(string timeId, string colorMode)
    {
        Debug.Log($"[ARHeatmap] LoadExistingTexture: timeId={timeId}, colorMode={colorMode}");
        StartCoroutine(LoadExistingTextureCoroutine(timeId, colorMode));
    }

    private IEnumerator LoadExistingTextureCoroutine(string timeId, string colorMode)
    {
        string selectedUrl = null;

        using (UnityWebRequest www = UnityWebRequest.Get($"{serverUrl}/api/heatmap/list-existing"))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                HeatmapListResponse list = null;
                try
                {
                    list = JsonUtility.FromJson<HeatmapListResponse>(www.downloadHandler.text);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ARHeatmap] Failed to parse list-existing: {e.Message}");
                }

                if (list != null)
                {
                    selectedUrl = FindHeatmapUrl(list.generated, timeId, colorMode);
                    if (string.IsNullOrEmpty(selectedUrl))
                        selectedUrl = FindHeatmapUrl(list.defaults, timeId, colorMode);
                }
            }
            else
            {
                Debug.LogWarning($"[ARHeatmap] list-existing falied: {www.error}");
            }
        }

        if (!string.IsNullOrEmpty(selectedUrl))
        {
            Debug.Log($"[ARHeatmap] Loading existing texture: {selectedUrl}");
            yield return StartCoroutine(LoadAndApplyTexture(selectedUrl, new HeatmapSettings { colorMode = colorMode, timeId = timeId }));
            lastLoadedTextureId = $"{colorMode}-{timeId}";
            yield break;
        }

        Debug.LogWarning($"[ARHeatmap] No texture found for {colorMode}-{timeId} on server");
        // fallback for if none of the servers worked
        if (useLocalFallbackOnServerFail)
        {
            string candidateFilename = $"heatmap-{colorMode}-{timeId}.png";
            string candidate = System.IO.Path.Combine(Application.streamingAssetsPath, "MapAssets", "Generated", candidateFilename);
                    if (System.IO.File.Exists(candidate) || (Application.platform == RuntimePlatform.Android && candidate.StartsWith(Application.streamingAssetsPath)))
                    {
                        Debug.Log($"[ARHeatmap] Local fallback enabled, loading from disk: {candidate}");
                        yield return StartCoroutine(LoadTextureFromDisk(candidate));
                        lastLoadedTextureId = $"{colorMode}-{timeId}";
                        yield break;
                    }
            else
            {
                Debug.LogWarning($"[ARHeatmap] Local fallback enabled but file not found: {candidate}");
            }
        }

        Debug.LogWarning($"[ARHeatmap] No texture found for {colorMode}-{timeId}");
    }

    private string FindHeatmapUrl(HeatmapEntry[] entries, string timeId, string colorMode)
    {
        if (entries == null) return null;
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry != null && entry.timeId == timeId && entry.mode == colorMode)
                return entry.pngUrl;
        }
        return null;
    }


    private System.Collections.IEnumerator LoadTextureFromDisk(string filepath)
    {
        Debug.Log($"[ARHeatmap] Loading texture from disk: {filepath}");
        Debug.Log($"[ARHeatmap] Texture transformations - Offset: {textureOffset}, Scale: {textureScale}");
        byte[] imageData = null;

        if (System.IO.File.Exists(filepath))
        {
            try
            {
                imageData = System.IO.File.ReadAllBytes(filepath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ARHeatmap] Failed to read file directly: {e.Message}");
            }
        }

        if (imageData == null)
        {
            // attempt to load via UnityWebRequest
            string url = filepath;
            if (!url.StartsWith("http://") && !url.StartsWith("https://") && !url.StartsWith("file://") && !url.StartsWith("jar:file://"))
            {
                if (Application.platform == RuntimePlatform.Android && filepath.StartsWith(Application.streamingAssetsPath))
                {
                    url = filepath;
                }
                else
                {
                    url = "file://" + filepath;
                }
            }

            using (UnityWebRequest www = UnityWebRequest.Get(url))
            {
                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    imageData = www.downloadHandler.data;
                }
                else
                {
                    Debug.LogError($"[ARHeatmap] Failed to load texture from disk via UnityWebRequest: {www.error} ({url})");
                    yield break;
                }
            }
        }

        if (imageData == null || imageData.Length == 0)
        {
            Debug.LogError($"[ARHeatmap] No image data available at: {filepath}");
            yield break;
        }

        Texture2D tex = new Texture2D(2, 2);
        tex.LoadImage(imageData);
        tex.name = System.IO.Path.GetFileNameWithoutExtension(filepath);
        ApplyTexture(tex);

        string[] parts = System.IO.Path.GetFileNameWithoutExtension(filepath).Split('-');
        string timeId = (parts.Length >= 3) ? parts[2] : System.IO.Path.GetFileNameWithoutExtension(filepath);
        yield return StartCoroutine(ApplyBuildingColoringCoroutine(timeId));

        Debug.Log($"[ARHeatmap] Texture loaded and applied from disk with transformations");
        yield return null;
    }


    private System.Collections.IEnumerator LoadLatestLocalHeatmapAtStart()
    {
        string baseDir = !string.IsNullOrEmpty(localGeoJsonDir)
            ? localGeoJsonDir
            : System.IO.Path.Combine(Application.streamingAssetsPath, "MapAssets", "Generated");

        if (Application.platform == RuntimePlatform.Android && baseDir.StartsWith(Application.streamingAssetsPath))
        {
            string altBase = System.IO.Path.Combine(Application.persistentDataPath, "MapAssets", "Generated");
            if (System.IO.Directory.Exists(altBase))
            {
                baseDir = altBase;
            }
            else
            {
                Debug.Log($"[ARHeatmap] Startup: cannot enumerate streamingAssets on Android. No persistent data fallback at: {altBase}. Will attempt direct streamingAssets file loads.");
                // fallthrough: we will attempt to load known candidates directly from streamingAssets
            }
        }

        if (!System.IO.Directory.Exists(baseDir))
        {
            Debug.Log($"[ARHeatmap] Startup: local heatmap folder not found: {baseDir}. Attempting direct streamingAssets load of common filenames.");
            // attempt direct load from streamingAssets for some common filenames
            string[] candidates = new[] { "heatmap-smooth-6am.png", "heatmap-smooth-4pm.png", "heatmap-smooth-noon.png", "heatmap-default.png" };
            foreach (var cand in candidates)
            {
                string streamingPath = System.IO.Path.Combine(Application.streamingAssetsPath, "MapAssets", "Generated", cand);
                Debug.Log($"[ARHeatmap] Trying streamingAssets candidate: {streamingPath}");
                yield return StartCoroutine(LoadTextureFromDisk(streamingPath));
                if (currentTexture != null)
                {
                    Debug.Log($"[ARHeatmap] Loaded texture from streamingAssets candidate: {cand}");
                    yield break;
                }
            }
            Debug.Log($"[ARHeatmap] No streamingAssets candidates loaded from {Application.streamingAssetsPath}");
            yield break;
        }

        // 6am heatmap if available
        string[] files = System.IO.Directory.GetFiles(baseDir, "heatmap-*-6am.png");
        if (files == null || files.Length == 0)
        {
            files = System.IO.Directory.GetFiles(baseDir, "heatmap-*.png");
        }
        if (files == null || files.Length == 0)
        {
            Debug.Log($"[ARHeatmap] Startup: no local heatmap files in {baseDir}. Attempting direct streamingAssets load of common filenames.");
            string[] candidates = new[] { "heatmap-smooth-6am.png", "heatmap-smooth-4pm.png", "heatmap-smooth-noon.png", "heatmap-default.png" };
            foreach (var cand in candidates)
            {
                string streamingPath = System.IO.Path.Combine(Application.streamingAssetsPath, "MapAssets", "Generated", cand);
                Debug.Log($"[ARHeatmap] Trying streamingAssets candidate: {streamingPath}");
                yield return StartCoroutine(LoadTextureFromDisk(streamingPath));
                if (currentTexture != null)
                {
                    Debug.Log($"[ARHeatmap] Loaded texture from streamingAssets candidate: {cand}");
                    yield break;
                }
            }
            Debug.Log($"[ARHeatmap] No streamingAssets candidates loaded from {Application.streamingAssetsPath}");
            yield break;
        }

        string latest = null;
        System.DateTime latestTime = System.DateTime.MinValue;
        foreach (var f in files)
        {
            try
            {
                var t = System.IO.File.GetLastWriteTimeUtc(f);
                if (t > latestTime)
                {
                    latestTime = t;
                    latest = f;
                }
            }
            catch (System.Exception)
            {
            }
        }

        if (string.IsNullOrEmpty(latest))
        {
            Debug.Log("[ARHeatmap] Startup: no valid local heatmap file found");
            yield break;
        }

        Debug.Log($"[ARHeatmap] Startup: loading latest local heatmap: {latest}");
        yield return StartCoroutine(LoadTextureFromDisk(latest));
    }

    /// <summary>
    /// Apply areamap colors to individual buildings from GeoJSON data
    /// </summary>
    private System.Collections.IEnumerator LoadTextFromPath(string path, Action<string> onComplete)
    {
        if (System.IO.File.Exists(path))
        {
            try
            {
                string text = System.IO.File.ReadAllText(path);
                onComplete?.Invoke(text);
                yield break;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ARHeatmap] Failed to read text file diretcly: {e.Message}");
            }
        }

        string url = path;
        if (!url.StartsWith("http://") && !url.StartsWith("https://") && !url.StartsWith("file://") && !url.StartsWith("jar:file://"))
        {
            if (Application.platform == RuntimePlatform.Android && path.StartsWith(Application.streamingAssetsPath))
            {
                url = path;
            }
            else
            {
                url = "file://" + path;
            }
        }

        Debug.Log($"[ARHeatmap] LoadTextFromPath attempting URL: {url}");
        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(www.downloadHandler.text);
            }
            else
            {
                Debug.LogWarning($"[ARHeatmap] Failed to load text via UnityWebRequest: {www.error} ({url}). Response code: {www.responseCode}");
                onComplete?.Invoke(null);
            }
        }
    }

    private System.Collections.IEnumerator LoadBytesFromPath(string path, Action<byte[]> onComplete)
    {
        if (System.IO.File.Exists(path))
        {
            try
            {
                byte[] data = System.IO.File.ReadAllBytes(path);
                onComplete?.Invoke(data);
                yield break;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ARHeatmap] Failed to read bytes directly: {e.Message}");
            }
        }

        string url = path;
        if (!url.StartsWith("http://") && !url.StartsWith("https://") && !url.StartsWith("file://") && !url.StartsWith("jar:file://"))
        {
            if (Application.platform == RuntimePlatform.Android && path.StartsWith(Application.streamingAssetsPath))
            {
                url = path;
            }
            else
            {
                url = "file://" + path;
            }
        }

        Debug.Log($"[ARHeatmap] LoadBytesFromPath attempting URL: {url}");
        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(www.downloadHandler.data);
            }
            else
            {
                Debug.LogWarning($"[ARHeatmap] Failed to load bytes via UnityWebRequest: {www.error} ({url}). Response code: {www.responseCode}");
                onComplete?.Invoke(null);
            }
        }
    }

    [Serializable]
    private class GeneratedIndex
    {
        public string[] files;
    }

    private System.Collections.IEnumerator CopyGeneratedFromStreamingAssetsIfMissing()
    {
        string dstBase = System.IO.Path.Combine(Application.persistentDataPath, "MapAssets", "Generated");
        string srcBase = System.IO.Path.Combine(Application.streamingAssetsPath, "MapAssets", "Generated");
        string indexPath = System.IO.Path.Combine(srcBase, "index.json");

        Debug.Log($"[ARHeatmap] CopyGeneratedFromStreamingAssetsIfMissing: srcBase={srcBase}, dstBase={dstBase}, indexPath={indexPath}");

        if (System.IO.Directory.Exists(dstBase) && System.IO.Directory.GetFiles(dstBase).Length > 0)
        {
            Debug.Log($"[ARHeatmap] CopyGeneratedFromStreamingAssetsIfMissing: destination already populated: {dstBase}");
            yield break; // already there
        }
        string indexText = null;
        yield return StartCoroutine(LoadTextFromPath(indexPath, t => indexText = t));

        if (string.IsNullOrEmpty(indexText))
        {
            Debug.LogWarning($"[ARHeatmap] No index.json found in streamingAssets at {indexPath}; cannot copy generated heatmaps to persistentDataPath.");
            yield break;
        }

        GeneratedIndex idx = null;
        try
        {
            idx = JsonUtility.FromJson<GeneratedIndex>(indexText);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ARHeatmap] Failed to parse index.json: {e.Message}");
            yield break;
        }

        if (idx == null || idx.files == null || idx.files.Length == 0)
        {
            Debug.LogWarning("[ARHeatmap] index.json contains no files");
            yield break;
        }

        System.IO.Directory.CreateDirectory(dstBase);
        int copied = 0;
        foreach (var f in idx.files)
        {
            string src = System.IO.Path.Combine(srcBase, f);
            string dst = System.IO.Path.Combine(dstBase, f);

            byte[] data = null;
            yield return StartCoroutine(LoadBytesFromPath(src, d => data = d));
            if (data != null && data.Length > 0)
            {
                try
                {
                    System.IO.File.WriteAllBytes(dst, data);
                    copied++;
                    Debug.Log($"[ARHeatmap] Copied {f} to persistentDataPath");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ARHeatmap] Failed to write file to persistentDataPath: {e.Message}");
                }
            }
            else
            {
                Debug.LogWarning($"[ARHeatmap] Failed to load {f} from streamingAssets");
            }
        }

        Debug.Log($"[ARHeatmap] Copied {copied}/{idx.files.Length} generated files to {dstBase}");
    }

    private System.Collections.IEnumerator ApplyBuildingColoringCoroutine(string timeId)
    {
        string baseDir = !string.IsNullOrEmpty(localGeoJsonDir)
            ? localGeoJsonDir
            : System.IO.Path.Combine(Application.streamingAssetsPath, "MapAssets", "Generated");

        string geojsonPath = System.IO.Path.Combine(baseDir, $"heatmap-polygon-{timeId}.geojson");

        string geojsonText = null;
        yield return StartCoroutine(LoadTextFromPath(geojsonPath, t => geojsonText = t));

        if (string.IsNullOrEmpty(geojsonText))
        {
            Debug.LogWarning($"[ARHeatmap] GeoJSON not found for building coloring: {geojsonPath}");
            yield break;
        }

        try
        {
            Debug.Log($"[ARHeatmap] Building coloring: GeoJSON loaded ({geojsonText.Length} bytes)");
            // TODO: parse and apply building coloring from geojsonText
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ARHeatmap] Error processing GeoJSON for building coloring: {e.Message}");
        }
    }

    /// <summary>
    /// apply heatmap texture to all building meshes with projective mapping with custom shader
    /// filters for objects with name "default"
    /// </summary>
    private void ApplyHeatmapToBuildings(Texture2D heatmapTexture)
    {
        // clear previous building materials
        foreach (var mat in buildingMaterials)
        {
            if (mat != null)
                Destroy(mat);
        }
        buildingMaterials.Clear();

        // find building renderers
        Renderer[] allRenderers;
        if (buildingsRoot != null)
        {
            allRenderers = buildingsRoot.GetComponentsInChildren<Renderer>(true);
        }
        else
        {
            // search entire scene for renderers
            allRenderers = FindObjectsOfType<Renderer>(true);
        }

        Debug.Log($"[ARHeatmap] Found {allRenderers.Length} total renderers, filtering for buildings named 'default'");

        // find or create projective shader
        Shader projShader = Shader.Find("Custom/HeatmapProjective");
        if (projShader == null)
        {
            // try loading from Resources
            projShader = Resources.Load<Shader>("HeatmapProjective") ?? Resources.Load<Shader>("Shaders/HeatmapProjective");
        }

        // fsllbacks for textures
        if (projShader == null)
        {
            string[] fallbackNames = new[] { "Universal Render Pipeline/Unlit", "Unlit/Texture", "Standard" };
            foreach (var name in fallbackNames)
            {
                projShader = Shader.Find(name);
                if (projShader != null) break;
            }
        }

        bool usingRuntimeFallback = false;
        Material resourcesFallbackMaterial = null;

        if (projShader == null)
        {
            // try loading material created by the editor script into Resources
            resourcesFallbackMaterial = Resources.Load<Material>("HeatmapProjective_Mat");

            if (resourcesFallbackMaterial != null)
            {
                Debug.Log("[ARHeatmap] Found Resources/HeatmapProjective_Mat, using its shader.");
                projShader = resourcesFallbackMaterial.shader;
            }
        }

        if (projShader == null)
        {
            Debug.LogError("[ARHeatmap] Custom/HeatmapProjective shader not found. If the shader file is under StreamingAssets it won't be included in builds. Move 'HeatmapProjective.shader' to Assets/Shaders or Assets/Resources and reimport, or run Tools->Ensure Heatmap Shader Included.");
            if (runtimeMaterial != null)
            {
                Debug.LogWarning("[ARHeatmap] Falling back to the runtime material for building projection (no projective mapping).");
                usingRuntimeFallback = true;
            }
            else if (resourcesFallbackMaterial != null)
            {
                // maybe remove
                usingRuntimeFallback = false;
            }
            else
            {
                Debug.LogError("[ARHeatmap] No suitable fallback shader or runtime material available. Aborting building heatmap application.");
                return;
            }
        }

        Debug.Log($"[ARHeatmap] Using shader/material: {(usingRuntimeFallback ? runtimeMaterial.shader.name : (resourcesFallbackMaterial != null ? resourcesFallbackMaterial.shader.name : projShader.name))}");
        Debug.Log($"[ARHeatmap] Projecting from origin ({cachedHeatmapOriginWS.x:F2}, {cachedHeatmapOriginWS.z:F2}) with size ({cachedHeatmapSize.x:F2}, {cachedHeatmapSize.y:F2})");

        int buildingCount = 0;
        foreach (var renderer in allRenderers)
        {
            if (renderer == null)
                continue;
            
            string objName = renderer.gameObject.name.ToLower();
            if (!string.IsNullOrEmpty(buildingNameFilter))
            {
                if (!objName.Contains(buildingNameFilter.ToLower()))
                    continue;
            }

            // skip if already in targetRenderers (like the plane)
            if (targetRenderers != null && System.Array.IndexOf(targetRenderers, renderer) >= 0)
                continue;

            // skip visuals, plane, and other UI objects
            if (renderer.gameObject.name == "Visuals" || renderer.gameObject.name == "Plane")
                continue;

            // create fresh material to prevent keyword space conflicts
            Material buildingMat;
            if (usingRuntimeFallback)
            {
                buildingMat = new Material(runtimeMaterial);
            }
            else if (resourcesFallbackMaterial != null)
            {
                buildingMat = new Material(resourcesFallbackMaterial);
            }
            else
            {
                buildingMat = new Material(projShader);
            }
            buildingMat.name = $"HeatmapMat_Building_{renderer.gameObject.name}_{buildingCount}";

            // set heatmap texture
            buildingMat.SetTexture("_HeatmapTex", heatmapTexture);
            buildingMat.SetColor("_BaseColor", Color.white);

            // use building-specific transformations (projectiveOffset/Scale)
            buildingMat.SetFloat("_HeatmapOriginX", cachedHeatmapOriginWS.x);
            buildingMat.SetFloat("_HeatmapOriginZ", cachedHeatmapOriginWS.z);
            buildingMat.SetFloat("_HeatmapSizeX", Mathf.Max(0.0001f, cachedHeatmapSize.x));
            buildingMat.SetFloat("_HeatmapSizeZ", Mathf.Max(0.0001f, cachedHeatmapSize.y));
            buildingMat.SetFloat("_HeatmapOffsetX", projectiveOffset.x); // building offset
            buildingMat.SetFloat("_HeatmapOffsetY", projectiveOffset.y); // building offset
            buildingMat.SetFloat("_HeatmapScaleX", projectiveScale.x); // building scale
            buildingMat.SetFloat("_HeatmapScaleY", projectiveScale.y); // building scale
            buildingMat.SetFloat("_HeatmapRotation", textureRotation); // keep plane rotation in sync
            buildingMat.SetFloat("_PlaneRotationY", cachedPlaneRotationY); // plane's world rotation
            buildingMat.SetFloat("_FlipZ", flipZ ? 1.0f : 0.0f); // flip Z axis
            buildingMat.SetFloat("_HeatmapOpacity", 1.0f);
            buildingMat.SetFloat("_FlipY", 1.0f); // enable Y flip
            buildingMat.SetVector("_HeatmapOriginWS", new Vector4(cachedHeatmapOriginWS.x, cachedHeatmapOriginWS.y, cachedHeatmapOriginWS.z, 1.0f));
            buildingMat.SetVector("_PlaneAxisU", new Vector4(cachedPlaneAxisU.x, cachedPlaneAxisU.y, cachedPlaneAxisU.z, 0.0f));
            buildingMat.SetVector("_PlaneAxisV", new Vector4(cachedPlaneAxisV.x, cachedPlaneAxisV.y, cachedPlaneAxisV.z, 0.0f));

            int matCount = Mathf.Max(1, renderer.sharedMaterials.Length);
            Material[] mats = new Material[matCount];
            for (int i = 0; i < mats.Length; i++)
            {
                mats[i] = buildingMat;
            }
            renderer.materials = mats;

            buildingMaterials.Add(buildingMat);
            buildingCount++;
            Debug.Log($"[ARHeatmap] Applied projective heatmap to building '{renderer.gameObject.name}'");
        }

        if (buildingCount == 0)
        {
            // fallback
            Debug.LogWarning($"[ARHeatmap] No buildings matched filter '{buildingNameFilter}'. Applying fallback to all renderers (excluding plane/visuals)");
            foreach (var renderer in allRenderers)
            {
                if (renderer == null) continue;
                if (targetRenderers != null && System.Array.IndexOf(targetRenderers, renderer) >= 0) continue;
                string name = renderer.gameObject.name;
                if (name == "Visuals" || name == "Plane") continue;

                Material buildingMat = new Material(Shader.Find("Custom/HeatmapProjective"));
                buildingMat.name = $"HeatmapMat_Building_{renderer.gameObject.name}_fallback";
                buildingMat.SetTexture("_HeatmapTex", heatmapTexture);
                buildingMat.SetColor("_BaseColor", Color.white);
                buildingMat.SetFloat("_HeatmapOriginX", cachedHeatmapOriginWS.x);
                buildingMat.SetFloat("_HeatmapOriginZ", cachedHeatmapOriginWS.z);
                buildingMat.SetFloat("_HeatmapSizeX", Mathf.Max(0.0001f, cachedHeatmapSize.x));
                buildingMat.SetFloat("_HeatmapSizeZ", Mathf.Max(0.0001f, cachedHeatmapSize.y));
                buildingMat.SetFloat("_HeatmapOffsetX", projectiveOffset.x);
                buildingMat.SetFloat("_HeatmapOffsetY", projectiveOffset.y);
                buildingMat.SetFloat("_HeatmapScaleX", projectiveScale.x);
                buildingMat.SetFloat("_HeatmapScaleY", projectiveScale.y);
                buildingMat.SetFloat("_HeatmapRotation", textureRotation);
                buildingMat.SetFloat("_PlaneRotationY", cachedPlaneRotationY);
                buildingMat.SetFloat("_FlipZ", flipZ ? 1.0f : 0.0f);
                buildingMat.SetFloat("_HeatmapOpacity", 1.0f);
                buildingMat.SetFloat("_FlipY", 1.0f);
                buildingMat.SetVector("_HeatmapOriginWS", new Vector4(cachedHeatmapOriginWS.x, cachedHeatmapOriginWS.y, cachedHeatmapOriginWS.z, 1.0f));
                buildingMat.SetVector("_PlaneAxisU", new Vector4(cachedPlaneAxisU.x, cachedPlaneAxisU.y, cachedPlaneAxisU.z, 0.0f));
                buildingMat.SetVector("_PlaneAxisV", new Vector4(cachedPlaneAxisV.x, cachedPlaneAxisV.y, cachedPlaneAxisV.z, 0.0f));

                int matCount = Mathf.Max(1, renderer.sharedMaterials.Length);
                Material[] mats = new Material[matCount];
                for (int i = 0; i < mats.Length; i++) mats[i] = buildingMat;
                renderer.materials = mats;

                buildingMaterials.Add(buildingMat);
                buildingCount++;
            }
        }

        Debug.Log($"[ARHeatmap] Applied heatmap to {buildingCount} buildings using projective mapping");
    }

    /// <summary>
    /// after spawning new map clones to rediscover renderers and apply existing texture
    /// </summary>
    public void RefreshAndApplyTexture()
    {
        Debug.Log("[ARHeatmap] RefreshAndApplyTexture called");
        StartCoroutine(RefreshAndApplyTextureCoroutine());
    }

    private System.Collections.IEnumerator RefreshAndApplyTextureCoroutine()
    {
        RefreshRendererListIfNeeded();
        EnsureCorrectMaterial();
        yield return StartCoroutine(CopyGeneratedFromStreamingAssetsIfMissing());

        // wait a frame
        yield return null;
        yield return null;
        RefreshRendererListIfNeeded();

        if (currentTexture != null)
        {
            ApplyTexture(currentTexture);
            Debug.Log("[ARHeatmap] Existing texture reapplied to newly found renderers");
            yield break;
        }

        Debug.Log("[ARHeatmap] No current texture, attempting to load local 6am heatmap for spawned clones");
        yield return StartCoroutine(LoadLatestLocalHeatmapAtStart());

        // try loading from server
        if (currentTexture == null)
        {
            string defaultMode = "polygon";
            string defaultTime = "6am";
            if (currentSettings.timeId != null) defaultTime = currentSettings.timeId;
            if (!string.IsNullOrEmpty(currentSettings.colorMode)) defaultMode = currentSettings.colorMode;

            Debug.Log($"[ARHeatmap] No local texture found; attempting to load from server: mode={defaultMode}, time={defaultTime}");
            yield return StartCoroutine(LoadExistingTextureCoroutine(defaultTime, defaultMode));
            if (currentTexture != null)
            {
                Debug.Log("[ARHeatmap] Loaded texture from server during spawn refresh");
            }
            else
            {
                Debug.LogWarning("[ARHeatmap] Could not load texture from server during spawn refresh");
            }
        }
    }

    private System.Collections.IEnumerator RunStartupTasks()
    {
        Debug.Log("[ARHeatmap] RunStartupTasks starting");
        yield return StartCoroutine(CopyGeneratedFromStreamingAssetsIfMissing());

        if (currentTexture == null)
        {
            Debug.Log("[ARHeatmap] Startup: attempting to load latest local heatmap after copying streaming assets");
            yield return StartCoroutine(LoadLatestLocalHeatmapAtStart());
        }
    }

    private void LateUpdate()
    {
        // detect runtime changes to labeled overlay offset/scale
        if (currentTexture != null && labeledMapTexture != null)
        {
            if (labeledMapOffset != prevLabeledMapOffset || labeledMapScale != prevLabeledMapScale)
            {
                prevLabeledMapOffset = labeledMapOffset;
                prevLabeledMapScale = labeledMapScale;
                if (currentPlaneCompositeTexture != null)
                {
                    Destroy(currentPlaneCompositeTexture);
                    currentPlaneCompositeTexture = null;
                }
                currentPlaneCompositeTexture = CompositeLabeledMapOnPlane(currentTexture);
                ApplyTexture(currentTexture);
                Debug.Log("[ARHeatmap] Regenerate plane composit after labeled map offset/scale change");
            }
        }

        // if plane/buildings move, move projective mapping too
        if (!applyHeatmapToBuildings || currentTexture == null) return;

        if (TryGetHeatmapBounds(out cachedHeatmapOriginWS, out cachedHeatmapSize))
        {
            // update existing building materials
            UpdateBuildingProjectiveParams();

            // rate-limit detection to once per detectInterval sec
            if (Time.time - lastDetectTime >= detectInterval)
            {
                DetectAndApplyToNewBuildings();
                lastDetectTime = Time.time;
            }
        }
    }

    private void DetectAndApplyToNewBuildings()
    {
        // find all building renderers, search entire scene for clones
        Renderer[] allBuildingRenderers = FindObjectsOfType<Renderer>(true);

        int applied = 0;
        int skipped = 0;

        for (int idx = 0; idx < allBuildingRenderers.Length; idx++)
        {
            var renderer = allBuildingRenderers[idx];
            if (renderer == null) { skipped++; continue; }

            // filter for buildings named "default"
            string objName = renderer.gameObject.name.ToLower();
            if (!string.IsNullOrEmpty(buildingNameFilter) && !objName.Contains(buildingNameFilter.ToLower()))
            {
                skipped++; continue;
            }

            // skip plane and visuals or target renderers
            if (targetRenderers != null && System.Array.IndexOf(targetRenderers, renderer) >= 0) { skipped++; continue; }
            if (renderer.gameObject.name == "Visuals" || renderer.gameObject.name == "Plane") { skipped++; continue; }

            bool alreadyHasHeatmap = false;
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat != null && mat.name.StartsWith("HeatmapMat_Building_")) { alreadyHasHeatmap = true; break; }
            }

            bool hasRuntimeMat = false;
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat != null && mat.name == "HeatmapMat_Runtime") { hasRuntimeMat = true; break; }
            }

            if (!alreadyHasHeatmap || hasRuntimeMat)
            {
                ApplySingleBuildingMaterial(renderer);
                applied++;
            }
            else
            {
                skipped++;
            }
        }

        Debug.Log($"[ARHeatmap] DetectAndApplyToNewBuildings: scanned={allBuildingRenderers.Length}, applied={applied}, skipped={skipped}");
    }

    private void ApplySingleBuildingMaterial(Renderer renderer)
    {
        // find or create projective shader
        Shader projShader = Shader.Find("Custom/HeatmapProjective");
        if (projShader == null)
        {
            projShader = Resources.Load<Shader>("HeatmapProjective") ?? Resources.Load<Shader>("Shaders/HeatmapProjective");
        }

        Material resourcesFallbackMaterial = null;
        if (projShader == null)
        {
            resourcesFallbackMaterial = Resources.Load<Material>("HeatmapProjective_Mat");
            if (resourcesFallbackMaterial != null)
                projShader = resourcesFallbackMaterial.shader;
        }

        bool usingRuntimeFallback = false;
        if (projShader == null)
        {
            if (runtimeMaterial != null)
                usingRuntimeFallback = true;
            else if (resourcesFallbackMaterial == null)
            {
                Debug.LogWarning("[ARHeatmap] No projective shader or fallback material available for ApplySingleBuildingMaterial");
                return;
            }
        }

        // choose material source
        Material buildingMat;
        if (usingRuntimeFallback)
            buildingMat = new Material(runtimeMaterial);
        else if (resourcesFallbackMaterial != null)
            buildingMat = new Material(resourcesFallbackMaterial);
        else
            buildingMat = new Material(projShader);

        buildingMat.name = $"HeatmapMat_Building_{renderer.gameObject.name}";

        Texture2D texToUse = currentTexture != null ? currentTexture : currentPlaneCompositeTexture;
        buildingMat.SetTexture("_HeatmapTex", texToUse);
        buildingMat.SetColor("_BaseColor", Color.white);

        // set projective parameters
        buildingMat.SetFloat("_HeatmapOriginX", cachedHeatmapOriginWS.x);
        buildingMat.SetFloat("_HeatmapOriginZ", cachedHeatmapOriginWS.z);
        buildingMat.SetFloat("_HeatmapSizeX", Mathf.Max(0.0001f, cachedHeatmapSize.x));
        buildingMat.SetFloat("_HeatmapSizeZ", Mathf.Max(0.0001f, cachedHeatmapSize.y));
        buildingMat.SetFloat("_HeatmapOffsetX", projectiveOffset.x);
        buildingMat.SetFloat("_HeatmapOffsetY", projectiveOffset.y);
        buildingMat.SetFloat("_HeatmapScaleX", projectiveScale.x);
        buildingMat.SetFloat("_HeatmapScaleY", projectiveScale.y);
        buildingMat.SetFloat("_HeatmapRotation", textureRotation);
        buildingMat.SetFloat("_PlaneRotationY", cachedPlaneRotationY);
        buildingMat.SetFloat("_FlipZ", flipZ ? 1.0f : 0.0f);
        buildingMat.SetFloat("_HeatmapOpacity", 1.0f);
        buildingMat.SetFloat("_FlipY", 1.0f);
        buildingMat.SetVector("_HeatmapOriginWS", new Vector4(cachedHeatmapOriginWS.x, cachedHeatmapOriginWS.y, cachedHeatmapOriginWS.z, 1.0f));
        buildingMat.SetVector("_PlaneAxisU", new Vector4(cachedPlaneAxisU.x, cachedPlaneAxisU.y, cachedPlaneAxisU.z, 0.0f));
        buildingMat.SetVector("_PlaneAxisV", new Vector4(cachedPlaneAxisV.x, cachedPlaneAxisV.y, cachedPlaneAxisV.z, 0.0f));

        int matCount = Mathf.Max(1, renderer.sharedMaterials.Length);
        Material[] mats = new Material[matCount];
        for (int i = 0; i < mats.Length; i++) mats[i] = buildingMat;
        renderer.materials = mats;

        buildingMaterials.Add(buildingMat);
        Debug.Log($"[ARHeatmap] Applied heatmap to newly spawned building: {renderer.gameObject.name}");
    }

    public bool IsGenerating => isGenerating;
    public Texture2D CurrentTexture => currentTexture;
    

    public Vector2 TextureOffset 
    { 
        get => textureOffset;
        set 
        { 
            textureOffset = value;
            ApplyTextureTransforms();
            Debug.Log($"[ARHeatmap] TextureOffset changed to {textureOffset}");
        }
    }
    
    public Vector2 TextureScale 
    { 
        get => textureScale;
        set 
        { 
            textureScale = value;
            ApplyTextureTransforms();
            Debug.Log($"[ARHeatmap] TextureScale changed to {textureScale}");
        }
    }
    
    public float TextureRotation 
    { 
        get => textureRotation;
        set 
        { 
            textureRotation = value;
            ApplyTextureTransforms();
            Debug.Log($"[ARHeatmap] TextureRotation changed to {textureRotation}°");
        }
    }
    [System.Serializable]
    private class GenerateResponse
    {
        public bool success;
        public string message;
        public string requestId;
        public string colorMode;
        public string timeId;
        public string pngUrl;
        public string geojsonUrl;
        public string benchesUrl;
        public string stdout;
    }

    [System.Serializable]
    private class HeatmapEntry
    {
        public string mode;
        public string timeId;
        public string pngUrl;
        public string geojsonUrl;
        public string benchesUrl;
    }

    [System.Serializable]
    private class HeatmapListResponse
    {
        public HeatmapEntry[] generated;
        public HeatmapEntry[] defaults;
    }
}
