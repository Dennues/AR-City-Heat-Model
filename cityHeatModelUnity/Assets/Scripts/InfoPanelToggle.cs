using UnityEngine;

public class InfoPanelToggle : MonoBehaviour
{
    public GameObject infoPanel;

    public void ToggleInfoPanel()
    {
        infoPanel.SetActive(!infoPanel.activeSelf);
    }
}
