#if AR_FOUNDATION_PRESENT
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.AR.Inputs;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace UnityEngine.XR.Interaction.Toolkit.Samples.ARStarterAssets
{
    /// <summary>
    /// Manages object deletion in the AR demo scene,
    /// and also handles the toggling between the object creation menu button and the delete button.
    /// </summary>
    public class ARSampleMenuManager : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Button that locks interaction with objects.")]
        Button m_LockButton;

        /// <summary>
        /// Button that locks interaction.
        /// </summary>
        public Button LockButton
        {
            get => m_LockButton;
            set => m_LockButton = value;
        }

        [SerializeField]
        [Tooltip("Button that deletes a selected object.")]
        Button m_DeleteButton;

        /// <summary>
        /// Button that deletes a selected object.
        /// </summary>
        public Button deleteButton
        {
            get => m_DeleteButton;
            set => m_DeleteButton = value;
        }

        [SerializeField]
        [Tooltip("The interaction group for the AR demo scene.")]
        XRInteractionGroup m_InteractionGroup;

        /// <summary>
        /// The interaction group for the AR demo scene.
        /// </summary>
        public XRInteractionGroup interactionGroup
        {
            get => m_InteractionGroup;
            set => m_InteractionGroup = value;
        }

        [SerializeField]
        XRInputValueReader<Vector2> m_TapStartPositionInput = new XRInputValueReader<Vector2>("Tap Start Position");

        /// <summary>
        /// Input to use for the screen tap start position.
        /// </summary>
        /// <seealso cref="TouchscreenGestureInputController.tapStartPosition"/>
        public XRInputValueReader<Vector2> tapStartPositionInput
        {
            get => m_TapStartPositionInput;
            set => XRInputReaderUtility.SetInputProperty(ref m_TapStartPositionInput, value, this);
        }

        /// <summary>
        /// Calls the methods in its invocation list when the spawned object selection changes.
        /// </summary>
        public UnityEvent<int> spawnedObjectSelectionChanged
        {
            get => m_SpawnedObjectSelectionChanged;
            set => m_SpawnedObjectSelectionChanged = value;
        }

        [Header("Events")]
        [SerializeField]
        [Tooltip("Calls the methods in its invocation list when the spawned object selection changes.")]
        UnityEvent<int> m_SpawnedObjectSelectionChanged = new UnityEvent<int>();

        void OnEnable()
        {
            m_TapStartPositionInput.EnableDirectActionIfModeUsed();
            m_DeleteButton.onClick.AddListener(DeleteFocusedObject);
        }

        void OnDisable()
        {
            m_TapStartPositionInput.DisableDirectActionIfModeUsed();
            m_DeleteButton.onClick.RemoveListener(DeleteFocusedObject);
        }

        void Start()
        {

        }

        void Update()
        {
            if (m_InteractionGroup is not null)
            {
                var currentFocusedObject = m_InteractionGroup.focusInteractable;
                if (currentFocusedObject != null && (!m_DeleteButton.isActiveAndEnabled || m_LockButton.isActiveAndEnabled))
                {
                    m_LockButton.gameObject.SetActive(false);
                    m_DeleteButton.gameObject.SetActive(true);
                }
                else if (currentFocusedObject == null && (!m_LockButton.isActiveAndEnabled || m_DeleteButton.isActiveAndEnabled))
                {
                    m_LockButton.gameObject.SetActive(true);
                    m_DeleteButton.gameObject.SetActive(false);
                }
            }
        }

        public void SetObjectToSpawn(int objectIndex)
        {
            m_SpawnedObjectSelectionChanged.Invoke(objectIndex);
        }

        void DeleteFocusedObject()
        {
            if (m_InteractionGroup == null)
                return;

            var currentFocusedObject = m_InteractionGroup.focusInteractable;
            if (currentFocusedObject != null)
            {
                Destroy(currentFocusedObject.transform.gameObject);
            }
        }
    }
}
#endif
