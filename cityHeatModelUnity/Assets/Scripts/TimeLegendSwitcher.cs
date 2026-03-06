using UnityEngine;
using UnityEngine.UI;

public class TimeLegendSwitcher : MonoBehaviour
{
    public Slider timeToggle;
    public GameObject legendPanel06;
    public GameObject legendPanel16;

    void Start()
    {
        UpdateLegend(timeToggle.value);
        timeToggle.onValueChanged.AddListener(UpdateLegend);
    }

    void UpdateLegend(float value)
    {
        bool isMorning = value == 0;

        legendPanel06.SetActive(isMorning);
        legendPanel16.SetActive(!isMorning);
    }
}