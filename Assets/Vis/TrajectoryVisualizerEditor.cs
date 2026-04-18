#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(TrajectoryVisualizer))]
public class TrajectoryVisualizerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        
        TrajectoryVisualizer visualizer = (TrajectoryVisualizer)target;
        
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Instructions:\n" +
            "1. Assign the trajectory log file (TextAsset) in the Log File field\n" +
            "2. Assign the scene Camera\n" +
            "3. Assign the UI Button for toggling\n" +
            "4. Adjust visualization settings as needed\n" +
            "5. Click the button in play mode to toggle trajectory visibility", 
            MessageType.Info);
    }
}
#endif
