using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

namespace UnityEngine.XR.Templates.AR
{
    /// <summary>
    /// Handles buttons and menus.
    /// </summary>
    public class ARTemplateMenuManager : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Button that locks interaction.")]
        Button m_LockButton;

        /// <summary>
        /// Button that locks interaction.
        /// </summary>
        public Button lockButton
        {
            get => m_LockButton;
            set => m_LockButton = value;
        }

        
        [SerializeField]
        [Tooltip("Icon of lock button.")]
        Image m_icon;

        /// <summary>
        /// Button that locks interaction.
        /// </summary>
        public Image lockIcon
        {
            get => m_icon;
            set => m_icon = value;
        }

        [SerializeField]
        [Tooltip("The XR Ray interactor.")]
        XRRayInteractor m_Interactor;

        public XRRayInteractor interactor
        {
            get => m_Interactor;
            set => m_Interactor = value;
        }

        [SerializeField]
        [Tooltip("The slider for activating plane debug visuals.")]
        DebugSlider m_DebugPlaneSlider;

        public DebugSlider debugPlaneSlider
        {
            get => m_DebugPlaneSlider;
            set => m_DebugPlaneSlider = value;
        }

        [SerializeField]
        [Tooltip("The plane manager in the AR demo scene.")]
        ARPlaneManager m_PlaneManager;

        public ARPlaneManager planeManager
        {
            get => m_PlaneManager;
            set => m_PlaneManager = value;
        }

        [SerializeField]
        [Tooltip("Determines whether or not to fade the AR Planes when visualization is toggled.")]
        bool m_UseARPlaneFading = true;

        public bool useARPlaneFading
        {
            get => m_UseARPlaneFading;
            set => m_UseARPlaneFading = value;
        }


        bool m_Locked = false;
        bool m_VisualizePlanes = true;
        Sprite m_LockSprite;
        Sprite m_UnlockSprite;
        readonly List<ARPlane> m_ARPlanes = new List<ARPlane>();
        readonly Dictionary<ARPlane, ARPlaneMeshVisualizer> m_ARPlaneMeshVisualizers = new Dictionary<ARPlane, ARPlaneMeshVisualizer>();
        readonly Dictionary<ARPlane, ARPlaneMeshVisualizerFader> m_ARPlaneMeshVisualizerFaders = new Dictionary<ARPlane, ARPlaneMeshVisualizerFader>();

        /// <summary>
        /// See <see cref="MonoBehaviour"/>.
        /// </summary>
        void OnEnable()
        {
            m_LockButton.onClick.AddListener(ChangeLockState);
            m_PlaneManager.trackablesChanged.AddListener(OnPlaneChanged);
        }

        /// <summary>
        /// See <see cref="MonoBehaviour"/>.
        /// </summary>
        void OnDisable()
        {
            m_LockButton.onClick.RemoveListener(ChangeLockState);
            m_PlaneManager.trackablesChanged.RemoveListener(OnPlaneChanged);
        }

        /// <summary>
        /// See <see cref="MonoBehaviour"/>.
        /// </summary>
        void Start()
        {
            // initialize plane visualization state and slider value
            m_DebugPlaneSlider.value = m_VisualizePlanes ? 1 : 0;

            Sprite[] sprites = Resources.LoadAll<Sprite>("Icons/InlineIcons_edited"); // taken from TMP

            // Check if sprites were loaded successfully
            if (sprites == null || sprites.Length == 0)
            {
                Debug.LogError("No sprites found in the sprite sheet.");
                return;
            }

            // Find the "locked" and "unlocked" sprites by name
            m_LockSprite = System.Array.Find(sprites, s => s.name == "locked");
            m_UnlockSprite = System.Array.Find(sprites, s => s.name == "unlocked");

            // Check if both sprites were found and log errors if not
            if (m_LockSprite == null)
            {
                Debug.LogError("Locked sprite not found");
            }

            if (m_UnlockSprite == null)
            {
                Debug.LogError("Unlocked sprite not found");
            }
        }

        /// <summary>
        /// Change the state of the lock button, which enables/disables interactions with objects via the Interactor and changes the button's icon.
        /// </summary>
        void ChangeLockState()
        {
            // Change button to unlock/lock state and enable/disable interactions with objects via Interactor
            if (m_Locked)
            {
                m_icon.sprite = m_LockSprite;
                m_Interactor.enabled = true;
                Debug.Log("Unlock");
            }
            else
            {
                m_icon.sprite = m_UnlockSprite;
                m_Interactor.enabled = false;
                Debug.Log("Lock");
            }
            m_Locked = !m_Locked;
        }
  
        /// <summary>
        /// Shows or hides the plane debug visuals.
        /// </summary>
        public void ShowHideDebugPlane()
        {
            m_VisualizePlanes = !m_VisualizePlanes;
            m_DebugPlaneSlider.value = m_VisualizePlanes ? 1 : 0;
            ChangePlaneVisibility(m_VisualizePlanes);
        }

        void ChangePlaneVisibility(bool setVisible)
        {
            foreach (var plane in m_ARPlanes)
            {
                if (m_ARPlaneMeshVisualizers.TryGetValue(plane, out var visualizer))
                {
                    visualizer.enabled = m_UseARPlaneFading ? true : setVisible;
                }

                if (m_ARPlaneMeshVisualizerFaders.TryGetValue(plane, out var fader))
                {
                    if (m_UseARPlaneFading)
                        fader.visualizeSurfaces = setVisible;
                    else
                        fader.SetVisualsImmediate(1f);
                }
            }
        }

        void OnPlaneChanged(ARTrackablesChangedEventArgs<ARPlane> eventArgs)
        {
            if (eventArgs.added.Count > 0)
            {
                foreach (var plane in eventArgs.added)
                {
                    m_ARPlanes.Add(plane);
                    if (plane.TryGetComponent<ARPlaneMeshVisualizer>(out var vizualizer))
                    {
                        m_ARPlaneMeshVisualizers.Add(plane, vizualizer);
                        if (!m_UseARPlaneFading)
                        {
                            vizualizer.enabled = m_VisualizePlanes;
                        }
                    }

                    if (!plane.TryGetComponent<ARPlaneMeshVisualizerFader>(out var visualizer))
                    {
                        visualizer = plane.gameObject.AddComponent<ARPlaneMeshVisualizerFader>();
                    }
                    m_ARPlaneMeshVisualizerFaders.Add(plane, visualizer);
                    visualizer.visualizeSurfaces = m_VisualizePlanes;
                }
            }

            if (eventArgs.removed.Count > 0)
            {
                foreach (var plane in eventArgs.removed)
                {
                    var planeGameObject = plane.Value;
                    if (planeGameObject == null)
                        continue;

                    if (m_ARPlanes.Contains(planeGameObject))
                        m_ARPlanes.Remove(planeGameObject);

                    if (m_ARPlaneMeshVisualizers.ContainsKey(planeGameObject))
                        m_ARPlaneMeshVisualizers.Remove(planeGameObject);

                    if (m_ARPlaneMeshVisualizerFaders.ContainsKey(planeGameObject))
                        m_ARPlaneMeshVisualizerFaders.Remove(planeGameObject);
                }
            }

            // Fallback if the counts do not match after an update
            if (m_PlaneManager.trackables.count != m_ARPlanes.Count)
            {
                m_ARPlanes.Clear();
                m_ARPlaneMeshVisualizers.Clear();
                m_ARPlaneMeshVisualizerFaders.Clear();

                foreach (var plane in m_PlaneManager.trackables)
                {
                    m_ARPlanes.Add(plane);
                    if (plane.TryGetComponent<ARPlaneMeshVisualizer>(out var vizualizer))
                    {
                        m_ARPlaneMeshVisualizers.Add(plane, vizualizer);
                        if (!m_UseARPlaneFading)
                        {
                            vizualizer.enabled = m_VisualizePlanes;
                        }
                    }

                    if (!plane.TryGetComponent<ARPlaneMeshVisualizerFader>(out var fader))
                    {
                        fader = plane.gameObject.AddComponent<ARPlaneMeshVisualizerFader>();
                    }
                    m_ARPlaneMeshVisualizerFaders.Add(plane, fader);
                    fader.visualizeSurfaces = m_VisualizePlanes;
                }
            }
        }
    }
}
