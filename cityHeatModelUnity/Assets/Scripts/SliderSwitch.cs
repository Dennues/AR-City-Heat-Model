using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class SliderSnap : MonoBehaviour, IPointerDownHandler
{
    private Slider slider;
    [SerializeField] private HeatmapUIController heatmapUI;

    void Awake()
    {
        slider = GetComponent<Slider>();
        if (slider != null)
            slider.onValueChanged.AddListener(OnSliderValueChanged);
    }

    // click auf slider
    public void OnPointerDown(PointerEventData eventData)
    {
        Vector2 localPoint;
        RectTransform rt = slider.GetComponent<RectTransform>();

        // mouse to slide coord
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, eventData.position, eventData.pressEventCamera, out localPoint))
        {
            float pct = Mathf.InverseLerp(-rt.rect.width/2, rt.rect.width/2, localPoint.x);

            // in 0 oder 1 umwandeln
            float snapped = pct < 0.5f ? 0f : 1f;
            slider.value = snapped;
            ApplySliderAction(snapped);
        }
    }

    private void OnSliderValueChanged(float value)
    {
        float snapped = value < 0.5f ? 0f : 1f;
        if (Mathf.Approximately(snapped, value))
            ApplySliderAction(snapped);
    }

    private void ApplySliderAction(float snapped)
    {
        if (heatmapUI == null)
        {
            // try to locate one in scene
            heatmapUI = FindObjectOfType<HeatmapUIController>();
            if (heatmapUI == null) return;
        }

        if (snapped <= 0f)
        {
            heatmapUI.SelectTimeSlotById("6am");
            heatmapUI.ShowSelectedHeatmap();
        }
        else
        {
            heatmapUI.SelectTimeSlotById("4pm");
            heatmapUI.ShowSelectedHeatmap();
        }
    }
}