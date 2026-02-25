using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SceneChange))]
public class SceneChangeEditor : Editor
{
    private SerializedProperty sceneNamesProp;
    private List<SceneAsset> cachedAssets = new List<SceneAsset>();

    private static readonly Color HeaderColor    = new Color(0.18f, 0.18f, 0.22f);
    private static readonly Color DropZoneColor  = new Color(0.22f, 0.35f, 0.22f);
    private static readonly Color DropHoverColor = new Color(0.28f, 0.55f, 0.28f);
    private static readonly Color RowEven        = new Color(0.20f, 0.20f, 0.25f);
    private static readonly Color RowOdd         = new Color(0.23f, 0.23f, 0.28f);
    private static readonly Color ActiveRow      = new Color(0.20f, 0.40f, 0.20f);

    private bool isDragHovering = false;

    private void OnEnable()
    {
        sceneNamesProp = serializedObject.FindProperty("sceneNames");
        SyncAssetsFromNames();
    }

    private void SyncAssetsFromNames()
    {
        cachedAssets.Clear();
        for (int i = 0; i < sceneNamesProp.arraySize; i++)
        {
            string name = sceneNamesProp.GetArrayElementAtIndex(i).stringValue;
            cachedAssets.Add(FindSceneAssetByName(name));
        }
    }

    private static SceneAsset FindSceneAssetByName(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return null;
        foreach (string guid in AssetDatabase.FindAssets($"t:SceneAsset {sceneName}"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            SceneAsset asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            if (asset != null && asset.name == sceneName)
                return asset;
        }
        return null;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawHeader();
        EditorGUILayout.Space(4);
        DrawDropZone();
        EditorGUILayout.Space(4);
        DrawSceneList();
        EditorGUILayout.Space(4);
        DrawAddButton();
        EditorGUILayout.Space(8);
        DrawPlayModeControls();

        serializedObject.ApplyModifiedProperties();
    }

    // ── Header ───────────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        Rect rect = GUILayoutUtility.GetRect(0, 28, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, HeaderColor);

        GUIStyle style = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Color.white }
        };
        EditorGUI.LabelField(new Rect(rect.x + 8, rect.y, rect.width - 8, rect.height),
            $"Scene Sequence  ({sceneNamesProp.arraySize} scenes)", style);
    }

    // ── Drop zone ────────────────────────────────────────────────────────────

    private void DrawDropZone()
    {
        Rect dropArea = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
        HandleDragAndDrop(dropArea);
    }

    private void HandleDragAndDrop(Rect area)
    {
        Event evt = Event.current;
        if (!area.Contains(evt.mousePosition))
        {
            isDragHovering = false;
            return;
        }

        switch (evt.type)
        {
            case EventType.DragUpdated:
                isDragHovering = ContainsSceneAsset(DragAndDrop.objectReferences);
                DragAndDrop.visualMode = isDragHovering
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.Rejected;
                evt.Use();
                Repaint();
                break;

            case EventType.DragPerform:
                DragAndDrop.AcceptDrag();
                foreach (Object obj in DragAndDrop.objectReferences)
                {
                    if (obj is SceneAsset sceneAsset && !IsDuplicate(sceneAsset))
                        AppendScene(sceneAsset);
                }
                isDragHovering = false;
                evt.Use();
                Repaint();
                break;

            case EventType.DragExited:
                isDragHovering = false;
                Repaint();
                break;
        }
    }

    private static bool ContainsSceneAsset(Object[] objects)
    {
        foreach (Object obj in objects)
            if (obj is SceneAsset) return true;
        return false;
    }

    private bool IsDuplicate(SceneAsset asset)
    {
        foreach (SceneAsset existing in cachedAssets)
            if (existing == asset) return true;
        return false;
    }

    // ── Scene list ───────────────────────────────────────────────────────────

    private void DrawSceneList()
    {
        int activeIndex = Application.isPlaying
            ? ((SceneChange)target).CurrentSceneIndex
            : -1;

        int toDelete  = -1;
        int moveUp    = -1;
        int moveDown  = -1;
        int jumpTo    = -1;

        for (int i = 0; i < cachedAssets.Count; i++)
        {
            bool isActive = (i == activeIndex);
            Color rowColor = isActive ? ActiveRow : (i % 2 == 0 ? RowEven : RowOdd);

            Rect rowRect = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rowRect, rowColor);

            // Index badge
            GUIStyle badge = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = isActive ? Color.green : new Color(0.6f, 0.6f, 0.7f) }
            };
            Rect badgeRect = new Rect(rowRect.x + 4, rowRect.y, 24, rowRect.height);
            GUI.Label(badgeRect, i.ToString(), badge);

            // Scene asset field
            Rect fieldRect = new Rect(rowRect.x + 32, rowRect.y + 1, rowRect.width - 150, rowRect.height - 2);
            SceneAsset newAsset = (SceneAsset)EditorGUI.ObjectField(fieldRect, cachedAssets[i], typeof(SceneAsset), false);
            if (newAsset != cachedAssets[i])
            {
                cachedAssets[i] = newAsset;
                UpdateNameAt(i);
            }

            float btnX = rowRect.x + rowRect.width - 118;
            float btnY = rowRect.y + 1;
            float btnH = rowRect.height - 2;

            if (Application.isPlaying)
            {
                if (GUI.Button(new Rect(btnX, btnY, 40, btnH), "▶ Go"))
                    jumpTo = i;
                btnX += 44;
            }

            if (GUI.Button(new Rect(btnX,      btnY, 22, btnH), "↑")) moveUp   = i;
            if (GUI.Button(new Rect(btnX + 24, btnY, 22, btnH), "↓")) moveDown = i;
            if (GUI.Button(new Rect(btnX + 48, btnY, 22, btnH), "✕")) toDelete  = i;
        }

        // Apply deferred actions
        if (jumpTo >= 0)
            ((SceneChange)target).LoadSceneAtIndex(jumpTo);

        if (moveUp > 0)
        {
            (cachedAssets[moveUp - 1], cachedAssets[moveUp]) = (cachedAssets[moveUp], cachedAssets[moveUp - 1]);
            RebuildNamesFromAssets();
        }

        if (moveDown >= 0 && moveDown < cachedAssets.Count - 1)
        {
            (cachedAssets[moveDown], cachedAssets[moveDown + 1]) = (cachedAssets[moveDown + 1], cachedAssets[moveDown]);
            RebuildNamesFromAssets();
        }

        if (toDelete >= 0)
        {
            cachedAssets.RemoveAt(toDelete);
            sceneNamesProp.DeleteArrayElementAtIndex(toDelete);
            serializedObject.ApplyModifiedProperties();
        }
    }

    // ── Add button ───────────────────────────────────────────────────────────

    private void DrawAddButton()
    {
        if (GUILayout.Button("+ Add Empty Slot", GUILayout.Height(22)))
        {
            cachedAssets.Add(null);
            sceneNamesProp.InsertArrayElementAtIndex(sceneNamesProp.arraySize);
            sceneNamesProp.GetArrayElementAtIndex(sceneNamesProp.arraySize - 1).stringValue = string.Empty;
            serializedObject.ApplyModifiedProperties();
        }
    }

    // ── Play mode controls ───────────────────────────────────────────────────

    private void DrawPlayModeControls()
    {
        Rect rect = GUILayoutUtility.GetRect(0, 24, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, HeaderColor);
        GUIStyle style = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 11,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Color.white }
        };
        EditorGUI.LabelField(new Rect(rect.x + 8, rect.y, rect.width, rect.height),
            "Navigation  (Ctrl+P / Ctrl+N in-game)", style);

        EditorGUILayout.Space(2);

        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            EditorGUILayout.BeginHorizontal();
            SceneChange sc = (SceneChange)target;

            if (GUILayout.Button("◀  Previous", GUILayout.Height(26)))
                sc.GoToPreviousScene();

            if (GUILayout.Button("Next  ▶", GUILayout.Height(26)))
                sc.GoToNextScene();

            EditorGUILayout.EndHorizontal();
        }

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode to use the navigation buttons.", MessageType.None);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void AppendScene(SceneAsset asset)
    {
        cachedAssets.Add(asset);
        int idx = sceneNamesProp.arraySize;
        sceneNamesProp.InsertArrayElementAtIndex(idx);
        sceneNamesProp.GetArrayElementAtIndex(idx).stringValue = asset.name;
        serializedObject.ApplyModifiedProperties();
    }

    private void UpdateNameAt(int index)
    {
        while (sceneNamesProp.arraySize <= index)
            sceneNamesProp.InsertArrayElementAtIndex(sceneNamesProp.arraySize);

        sceneNamesProp.GetArrayElementAtIndex(index).stringValue =
            cachedAssets[index] != null ? cachedAssets[index].name : string.Empty;

        serializedObject.ApplyModifiedProperties();
    }

    private void RebuildNamesFromAssets()
    {
        sceneNamesProp.ClearArray();
        for (int i = 0; i < cachedAssets.Count; i++)
        {
            sceneNamesProp.InsertArrayElementAtIndex(i);
            sceneNamesProp.GetArrayElementAtIndex(i).stringValue =
                cachedAssets[i] != null ? cachedAssets[i].name : string.Empty;
        }
        serializedObject.ApplyModifiedProperties();
    }
}
