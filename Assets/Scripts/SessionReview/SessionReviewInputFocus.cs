using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SessionReview
{
    internal static class SessionReviewInputFocus
    {
        public static bool IsTextEntryActive()
        {
            if (IsUguiInputFieldActive())
                return true;

            // IMGUI keeps keyboardControl latched after some controls are clicked. Only
            // treat that as typing while a screen that actually has text entry is open;
            // otherwise agent possession can stay "blocked" after an old panel closes.
            return GUIUtility.keyboardControl != 0 && IsImguiTextEntrySurfaceOpen();
        }

        private static bool IsUguiInputFieldActive()
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null || eventSystem.currentSelectedGameObject == null)
                return false;

            GameObject selected = eventSystem.currentSelectedGameObject;
            InputField input = selected.GetComponentInParent<InputField>();
            if (input != null)
                return input.isActiveAndEnabled && input.isFocused;

            Component[] components = selected.GetComponentsInParent<Component>();
            foreach (Component component in components)
            {
                if (component == null) continue;
                if (component is Behaviour behaviour && !behaviour.isActiveAndEnabled)
                    continue;
                string typeName = component.GetType().Name;
                if (typeName.IndexOf("InputField", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                System.Reflection.PropertyInfo focusedProperty = component.GetType().GetProperty("isFocused");
                if (focusedProperty != null && focusedProperty.PropertyType == typeof(bool))
                    return (bool)focusedProperty.GetValue(component, null);

                return true;
            }

            return false;
        }

        private static bool IsImguiTextEntrySurfaceOpen()
        {
            SessionReviewManager manager = SessionReviewManager.Instance;
            if (manager != null && (manager.IsOnboardingActive || manager.IsWorldBuildingModeActive))
                return true;

            TestSceneFlowManager flow = UnityEngine.Object.FindObjectOfType<TestSceneFlowManager>();
            return flow != null && flow.IsCharacterSelectOpen;
        }
    }
}
