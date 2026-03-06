using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// manages UI for time slot selection and heatmap control
/// providing buttons for time-based CSV switching and heatmap generation
/// </summary>
public class HeatmapUIController : MonoBehaviour
{
    [System.Serializable]
    public struct TimeSlotOption
    {
        public string label;
        public string timeId;
        public string csvPath;
    }

    [SerializeField] private ARHeatmapManager heatmapManager;
    [SerializeField] private TimeSlotOption[] timeSlots = new[]
    {
        new TimeSlotOption 
        { 
            label = "6am",
            timeId = "6am",
            csvPath = "C:\\Users\\denni\\Documents\\csvHeatmaps\\temp1Juni2025_6uhr.csv" // hardcoded
        },
        new TimeSlotOption 
        { 
            label = "4pm",
            timeId = "4pm",
            csvPath = "C:\\Users\\denni\\Documents\\csvHeatmaps\\temp1Juni2025_16uhr.csv" // hardcoded
        }
    };

    [SerializeField] private Canvas uiCanvas;
    [SerializeField] private float latMin = 52.43700f;
    [SerializeField] private float latMax = 52.46400f;
    [SerializeField] private float lonMin = 13.24950f;
    [SerializeField] private float lonMax = 13.30830f;

    private string selectedTimeId = "6am";
    private string selectedCsvPath;
    private Text statusText;
    private System.Collections.Generic.Dictionary<string, Image> timeButtonImages = new System.Collections.Generic.Dictionary<string, Image>();

    private void Start()
    {
        if (heatmapManager == null)
        {
            heatmapManager = GetComponent<ARHeatmapManager>();
        }

        LoadCacheFromPrefs();
        selectedCsvPath = timeSlots[0].csvPath;
        CreateUI();
    }

    private void CreateUI()
    {
        // create main panel
        GameObject panelObj = new GameObject("HeatmapPanel");
        panelObj.transform.SetParent(uiCanvas.transform, false);
        
        Image panelImage = panelObj.AddComponent<Image>();
        panelImage.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);
        
        RectTransform panelRect = panelObj.GetComponent<RectTransform>();
        panelRect.anchoredPosition = new Vector2(10, -10);
        panelRect.sizeDelta = new Vector2(350, 280);
        panelRect.anchorMin = new Vector2(0, 1);
        panelRect.anchorMax = new Vector2(0, 1);

        VerticalLayoutGroup layoutGroup = panelObj.AddComponent<VerticalLayoutGroup>();
        layoutGroup.padding = new RectOffset(10, 10, 10, 10);
        layoutGroup.spacing = 8;

        // title
        GameObject titleObj = new GameObject("Title");
        titleObj.transform.SetParent(panelObj.transform, false);
        Text titleText = titleObj.AddComponent<Text>();
        titleText.text = "Heatmap Control";
        titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        titleText.fontSize = 18;
        titleText.fontStyle = FontStyle.Bold;
        titleText.alignment = TextAnchor.UpperLeft;
        titleText.color = Color.white;
        LayoutElement titleLayout = titleObj.AddComponent<LayoutElement>();
        titleLayout.preferredHeight = 30;

        // time slot buttons
        GameObject timeSlotPanel = new GameObject("TimeSlotPanel");
        timeSlotPanel.transform.SetParent(panelObj.transform, false);
        HorizontalLayoutGroup timeLayout = timeSlotPanel.AddComponent<HorizontalLayoutGroup>();
        timeLayout.spacing = 5;
        LayoutElement timeLayout_elem = timeSlotPanel.AddComponent<LayoutElement>();
        timeLayout_elem.preferredHeight = 35;

        foreach (TimeSlotOption slot in timeSlots)
        {
            CreateTimeButton(slot, timeSlotPanel);
        }

        // status text
        GameObject statusObj = new GameObject("StatusText");
        statusObj.transform.SetParent(panelObj.transform, false);
        statusText = statusObj.AddComponent<Text>();
        statusText.text = "Ready";
        statusText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        statusText.fontSize = 12;
        statusText.alignment = TextAnchor.MiddleCenter;
        statusText.color = Color.yellow;
        LayoutElement statusLayout = statusObj.AddComponent<LayoutElement>();
        statusLayout.preferredHeight = 25;

        // show heatmap button
        GameObject showBtnObj = new GameObject("ShowButton");
        showBtnObj.transform.SetParent(panelObj.transform, false);
        Image showBtnImage = showBtnObj.AddComponent<Image>();
        showBtnImage.color = new Color(0.2f, 0.8f, 0.2f);
        Button showBtn = showBtnObj.AddComponent<Button>();
        showBtn.onClick.AddListener(OnShowClicked);
        RectTransform showBtnRect = showBtnObj.GetComponent<RectTransform>();
        showBtnRect.sizeDelta = new Vector2(0, 40);

        GameObject showTextObj = new GameObject("Text");
        showTextObj.transform.SetParent(showBtnObj.transform, false);
        Text showText = showTextObj.AddComponent<Text>();
        showText.text = "Show Heatmap";
        showText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        showText.alignment = TextAnchor.MiddleCenter;
        showText.color = Color.white;
        showText.raycastTarget = false;
        
        RectTransform showTextRect = showTextObj.GetComponent<RectTransform>();
        showTextRect.anchorMin = Vector2.zero;
        showTextRect.anchorMax = Vector2.one;
        showTextRect.offsetMin = Vector2.zero;
        showTextRect.offsetMax = Vector2.zero;

        // regenerate button
        GameObject genBtnObj = new GameObject("RegenerateButton");
        genBtnObj.transform.SetParent(panelObj.transform, false);
        Image genBtnImage = genBtnObj.AddComponent<Image>();
        genBtnImage.color = new Color(0.2f, 0.6f, 1f);
        Button genBtn = genBtnObj.AddComponent<Button>();
        genBtn.onClick.AddListener(OnGenerateClicked);

        RectTransform genBtnRect = genBtnObj.GetComponent<RectTransform>();
        genBtnRect.sizeDelta = new Vector2(0, 40);

        GameObject genTextObj = new GameObject("Text");
        genTextObj.transform.SetParent(genBtnObj.transform, false);
        Text genText = genTextObj.AddComponent<Text>();
        genText.text = "Regenerate Heatmap";
        genText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        genText.alignment = TextAnchor.MiddleCenter;
        genText.color = Color.white;
        genText.raycastTarget = false; // don't block button clicks
        
        RectTransform genTextRect = genTextObj.GetComponent<RectTransform>();
        genTextRect.anchorMin = Vector2.zero;
        genTextRect.anchorMax = Vector2.one;
        genTextRect.offsetMin = Vector2.zero;
        genTextRect.offsetMax = Vector2.zero;

        Debug.Log("[HeatmapUI] UI created");
    }

    private void UpdateStatus(string message)
    {
        if (statusText != null)
        {
            statusText.text = message;
        }
    }

    private void CreateTimeButton(TimeSlotOption slot, GameObject parent)
    {
        GameObject btnObj = new GameObject($"TimeButton_{slot.timeId}");
        btnObj.transform.SetParent(parent.transform, false);

        Image btnImage = btnObj.AddComponent<Image>();
        btnImage.color = slot.timeId == selectedTimeId ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.4f, 0.4f, 0.4f);

        if (!timeButtonImages.ContainsKey(slot.timeId))
            timeButtonImages[slot.timeId] = btnImage;

        Button btn = btnObj.AddComponent<Button>();
        btn.onClick.AddListener(() => SelectTimeSlot(slot));

        RectTransform btnRect = btnObj.GetComponent<RectTransform>();
        btnRect.sizeDelta = new Vector2(0, 35);

        GameObject textObj = new GameObject("Text");
        textObj.transform.SetParent(btnObj.transform, false);
        Text text = textObj.AddComponent<Text>();
        text.text = slot.label;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;

        RectTransform textRect = textObj.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
    }

    private void CreateModeButton(string label, string mode, GameObject parent)
    {
        // removed
    }

    public void SelectTimeSlot(TimeSlotOption slot)
    {
        selectedTimeId = slot.timeId;
        selectedCsvPath = slot.csvPath;
        PlayerPrefs.SetString("heatmap_time_id", selectedTimeId);
        PlayerPrefs.SetString("heatmap_csv_path", selectedCsvPath);
        Debug.Log($"[HeatmapUI] Time slot selected: {selectedTimeId}");
        foreach (var kv in timeButtonImages)
        {
            try
            {
                kv.Value.color = kv.Key == selectedTimeId ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.4f, 0.4f, 0.4f);
            }
            catch (System.Exception)
            {
            }
        }
    }

    public void SelectTimeSlotById(string timeId)
    {
        foreach (var slot in timeSlots)
        {
            if (slot.timeId == timeId)
            {
                SelectTimeSlot(slot);
                return;
            }
        }
        Debug.LogWarning($"[HeatmapUI] SelectTimeSlotById: unknown timeId {timeId}");
    }

    private void SelectColorMode(string mode)
    {
        // removed
    }

    private void OnShowClicked()
    {
        if (heatmapManager.IsGenerating)
        {
            Debug.LogWarning("[HeatmapUI] Generation already in progress");
            return;
        }

        UpdateStatus("Loading existing texture...");
        heatmapManager.LoadExistingTexture(selectedTimeId, "polygon");
    }

    public void ShowSelectedHeatmap()
    {
        OnShowClicked();
    }

    private void OnGenerateClicked()
    {
        if (heatmapManager.IsGenerating)
        {
            Debug.LogWarning("[HeatmapUI] Generation already in progress");
            return;
        }

        UpdateStatus("Generating heatmap...");

        var settings = new ARHeatmapManager.HeatmapSettings
        {
            latMin = latMin,
            latMax = latMax,
            lonMin = lonMin,
            lonMax = lonMax,
            colorMode = "polygon", // always polygon
            enableGapFill = true,
            scaleMode = "tight",
            tempCsv = selectedCsvPath,
            timeId = selectedTimeId
        };

        Debug.Log($"[HeatmapUI] Regenerating: polygon-{selectedTimeId}");
        heatmapManager.GenerateHeatmap(settings);
        StartCoroutine(WaitForGeneration());
    }

    private System.Collections.IEnumerator WaitForGeneration()
    {
        while (heatmapManager.IsGenerating)
        {
            yield return new WaitForSeconds(0.5f);
        }
        UpdateStatus("Ready");
    }

    private void LoadCacheFromPrefs()
    {
        if (PlayerPrefs.HasKey("heatmap_time_id"))
        {
            selectedTimeId = PlayerPrefs.GetString("heatmap_time_id");
            selectedCsvPath = PlayerPrefs.GetString("heatmap_csv_path");
        }
    }
}
