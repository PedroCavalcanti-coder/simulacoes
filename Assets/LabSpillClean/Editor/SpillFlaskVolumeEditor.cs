// Copia de Assets/LiquidFX (pasta de exemplo, somente-leitura). Tipos e namespace
// renomeados para conviver com o original no mesmo projeto. Ver PLANO-REFORMA.md, tarefa 2.0.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LabSpill.EditorTools
{
    /// <summary>
    /// Inspector for <see cref="SpillFlaskVolume"/>. The default list drawer for
    /// <c>initialContents</c> works but tells an artist nothing about what they are actually about
    /// to see in the flask - notably not the real stacking order, which is decided by category
    /// density and has nothing to do with the order liquids were dropped into the list. The stacked
    /// bar here renders in that real order, so a mis-set category is visible before entering Play.
    /// </summary>
    [CustomEditor(typeof(SpillFlaskVolume))]
    [CanEditMultipleObjects]
    class SpillFlaskVolumeEditor : Editor
    {
        SerializedProperty capacityML;
        SerializedProperty emptyLevel;
        SerializedProperty fullLevel;
        SerializedProperty initialContents;
        SerializedProperty layeredFullLevel;
        SerializedProperty lip;
        SerializedProperty spillTiltDegrees;
        SerializedProperty fullTiltDegrees;
        SerializedProperty maxFlowMLPerSecond;
        SerializedProperty portRadius;
        SerializedProperty portLocalOffset;

        void OnEnable()
        {
            capacityML = serializedObject.FindProperty("capacityML");
            emptyLevel = serializedObject.FindProperty("emptyLevel");
            fullLevel = serializedObject.FindProperty("fullLevel");
            initialContents = serializedObject.FindProperty("initialContents");
            layeredFullLevel = serializedObject.FindProperty("layeredFullLevel");
            lip = serializedObject.FindProperty("lip");
            spillTiltDegrees = serializedObject.FindProperty("spillTiltDegrees");
            fullTiltDegrees = serializedObject.FindProperty("fullTiltDegrees");
            maxFlowMLPerSecond = serializedObject.FindProperty("maxFlowMLPerSecond");
            portRadius = serializedObject.FindProperty("portRadius");
            portLocalOffset = serializedObject.FindProperty("portLocalOffset");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var flask = (SpillFlaskVolume)target;

            EditorGUILayout.LabelField("Calibration", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(capacityML);
            EditorGUILayout.PropertyField(emptyLevel);
            EditorGUILayout.PropertyField(fullLevel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Layered Contents", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Populated -> this flask bakes into LiquidVolumePro's layer stack and switches to " +
                "Multiple detail automatically. Empty -> single-level flask, unchanged from before.",
                MessageType.None);

            DrawContentsList();
            EditorGUILayout.PropertyField(layeredFullLevel);

            DrawStackPreview(flask);
            DrawTotals(flask);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bake Now"))
                {
                    foreach (Object t in targets)
                        ((SpillFlaskVolume)t).BakeInitialContents();
                }

                if (GUILayout.Button("Empty"))
                {
                    Undo.RecordObject(target, "Empty Flask Contents");
                    initialContents.ClearArray();
                    serializedObject.ApplyModifiedProperties();
                    foreach (Object t in targets)
                        ((SpillFlaskVolume)t).BakeInitialContents();
                }
            }

            if (Application.isPlaying)
                DrawLiveState(flask);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pouring Geometry", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(lip);
            EditorGUILayout.PropertyField(spillTiltDegrees);
            EditorGUILayout.PropertyField(fullTiltDegrees);
            EditorGUILayout.PropertyField(maxFlowMLPerSecond);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Receiving", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(portRadius);
            EditorGUILayout.PropertyField(portLocalOffset);

            serializedObject.ApplyModifiedProperties();
        }

        void DrawContentsList()
        {
            for (int i = 0; i < initialContents.arraySize; i++)
            {
                SerializedProperty element = initialContents.GetArrayElementAtIndex(i);
                SerializedProperty liquidProp = element.FindPropertyRelative("liquid");
                SerializedProperty mlProp = element.FindPropertyRelative("millilitres");
                var liquid = liquidProp.objectReferenceValue as SpillLiquidDefinition;

                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect swatchRect = GUILayoutUtility.GetRect(16f, 16f, GUILayout.Width(16f));
                    EditorGUI.DrawRect(swatchRect, liquid != null ? (Color)liquid.Color : Color.clear);

                    EditorGUILayout.PropertyField(liquidProp, GUIContent.none, GUILayout.MinWidth(120f));
                    EditorGUILayout.PropertyField(mlProp, GUIContent.none, GUILayout.Width(70f));
                    GUILayout.Label("mL", GUILayout.Width(22f));

                    string categoryLabel = liquid == null ? "-"
                        : liquid.Category == null ? "NO CATEGORY" : liquid.Category.DisplayName;
                    var style = liquid != null && liquid.Category == null ? EditorStyles.boldLabel : EditorStyles.miniLabel;
                    Color previousColor = GUI.color;
                    if (liquid != null && liquid.Category == null)
                        GUI.color = Color.red;
                    GUILayout.Label(categoryLabel, style, GUILayout.Width(90f));
                    GUI.color = previousColor;

                    if (GUILayout.Button("x", GUILayout.Width(20f)))
                    {
                        initialContents.DeleteArrayElementAtIndex(i);
                        break;
                    }
                }
            }

            if (GUILayout.Button("+ Add Layer"))
            {
                initialContents.arraySize++;
                SerializedProperty element = initialContents.GetArrayElementAtIndex(initialContents.arraySize - 1);
                element.FindPropertyRelative("liquid").objectReferenceValue = null;
                element.FindPropertyRelative("millilitres").floatValue = 0f;
            }
        }

        void DrawStackPreview(SpillFlaskVolume flask)
        {
            if (initialContents.arraySize == 0)
                return;

            var entries = new List<(SpillLiquidDefinition liquid, float mL)>();
            for (int i = 0; i < initialContents.arraySize; i++)
            {
                SerializedProperty element = initialContents.GetArrayElementAtIndex(i);
                var liquid = element.FindPropertyRelative("liquid").objectReferenceValue as SpillLiquidDefinition;
                float mL = element.FindPropertyRelative("millilitres").floatValue;
                if (liquid != null && mL > 0f)
                    entries.Add((liquid, mL));
            }

            if (entries.Count == 0)
                return;

            // Charges of one mixing family become a single blended layer at bake time
            // (SpillFlaskVolume.AddLayeredCore), so the preview has to fold them the same way or
            // it shows two bars where the flask will show one.
            for (int i = entries.Count - 1; i > 0; i--)
            {
                for (int j = 0; j < i; j++)
                {
                    if (entries[i].liquid.Category != entries[j].liquid.Category)
                        continue;

                    float mergedML = entries[i].mL + entries[j].mL;
                    var dominant = entries[i].mL > entries[j].mL ? entries[i].liquid : entries[j].liquid;
                    entries[j] = (dominant, mergedML);
                    entries.RemoveAt(i);
                    break;
                }
            }

            // Real stacking order: lighter floats on top, matching LiquidVolumePro.
            entries.Sort((a, b) => b.liquid.Density.CompareTo(a.liquid.Density));

            float total = 0f;
            foreach (var e in entries)
                total += e.mL;

            EditorGUILayout.LabelField("Preview (bottom -> top, real stacking order)", EditorStyles.miniLabel);
            Rect barRect = GUILayoutUtility.GetRect(0f, 24f, GUILayout.ExpandWidth(true));
            float x = barRect.x;
            foreach (var e in entries)
            {
                float width = total > 0f ? barRect.width * (e.mL / total) : 0f;
                var segment = new Rect(x, barRect.y, width, barRect.height);
                EditorGUI.DrawRect(segment, e.liquid.Color);
                x += width;
            }
            EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, barRect.width, 1f), Color.black);
            EditorGUI.DrawRect(new Rect(barRect.x, barRect.yMax - 1f, barRect.width, 1f), Color.black);
        }

        void DrawTotals(SpillFlaskVolume flask)
        {
            float total = 0f;
            for (int i = 0; i < initialContents.arraySize; i++)
            {
                SerializedProperty element = initialContents.GetArrayElementAtIndex(i);
                total += element.FindPropertyRelative("millilitres").floatValue;
            }

            float capacity = capacityML.floatValue;
            bool overCapacity = total > capacity + 0.01f;

            var style = new GUIStyle(EditorStyles.label);
            if (overCapacity)
                style.normal.textColor = Color.red;

            EditorGUILayout.LabelField($"Total: {total:0.#} / {capacity:0.#} mL", style);
        }

        void DrawLiveState(SpillFlaskVolume flask)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live (Play Mode)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Layered", flask.IsLayered.ToString());
            EditorGUILayout.LabelField("Contents", $"{flask.ContentsML:0.#} / {flask.CapacityML:0.#} mL");
            EditorGUILayout.LabelField("Top Liquid", flask.TopLiquid != null ? flask.TopLiquid.DisplayName : "-");
            Repaint();
        }
    }
}
