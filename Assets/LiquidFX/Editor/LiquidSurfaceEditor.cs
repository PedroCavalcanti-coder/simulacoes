using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace LiquidFX.EditorTools
{
    /// <summary>
    /// Gives the basin the same box-with-draggable-handles editing a BoxCollider has, instead of
    /// four separate numbers (floor Y, rim Y, transform position, transform scale) that are easy
    /// to get wrong by hand. "Edit Basin Bounds" toggles it on, same convention as the collider
    /// "Edit Collider" button, so the handles do not clutter the Scene view by default.
    ///
    /// The box edits the real basin: XZ size writes back into the transform's local scale (which
    /// is what the water mesh and the ripple shader already use for width/depth), and the Y range
    /// writes into <see cref="LiquidSurface.SetBasinHeights"/>. There is no shadow copy of the
    /// bounds anywhere — drag the handle, the basin numbers change, that is the whole mechanism.
    /// </summary>
    [CustomEditor(typeof(LiquidSurface))]
    public sealed class LiquidSurfaceEditor : Editor
    {
        static bool editingBounds;

        readonly BoxBoundsHandle handle = new BoxBoundsHandle();

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            bool newValue = GUILayout.Toggle(
                editingBounds,
                editingBounds ? "Editing Basin Bounds (drag handles in Scene view)" : "Edit Basin Bounds",
                "Button");
            if (EditorGUI.EndChangeCheck())
            {
                editingBounds = newValue;
                SceneView.RepaintAll();
            }
        }

        void OnSceneGUI()
        {
            if (!editingBounds)
                return;

            var surface = (LiquidSurface)target;
            Transform basinTransform = surface.transform;

            // Axis aligned by design: sinks and tanks do not tip over, and a rotated handle would
            // make the XZ-size-to-localScale write-back ambiguous.
            using (new Handles.DrawingScope(Matrix4x4.identity))
            {
                Vector3 worldCentre = new Vector3(
                    basinTransform.position.x,
                    (surface.BasinFloorY + surface.BasinRimY) * 0.5f,
                    basinTransform.position.z);

                Vector3 worldSize = new Vector3(
                    Mathf.Abs(basinTransform.lossyScale.x),
                    Mathf.Max(0.001f, surface.BasinRimY - surface.BasinFloorY),
                    Mathf.Abs(basinTransform.lossyScale.z));

                handle.center = worldCentre;
                handle.size = worldSize;
                handle.axes = PrimitiveBoundsHandle.Axes.All;

                Handles.color = new Color(1f, 0.62f, 0.05f, 1f);

                EditorGUI.BeginChangeCheck();
                handle.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(surface, "Adjust Liquid Basin Bounds");
                    Undo.RecordObject(basinTransform, "Adjust Liquid Basin Bounds");

                    Vector3 newCentre = handle.center;
                    Vector3 newSize = handle.size;

                    float newFloorY = newCentre.y - newSize.y * 0.5f;
                    float newRimY = newCentre.y + newSize.y * 0.5f;
                    surface.SetBasinHeights(newFloorY, newRimY);

                    Vector3 position = basinTransform.position;
                    position.x = newCentre.x;
                    position.z = newCentre.z;
                    basinTransform.position = position;

                    // The transform carries width/depth as localScale, but lossyScale (what the
                    // handle just edited) includes any parent scale, so divide that back out.
                    Vector3 parentScale = basinTransform.parent != null
                        ? basinTransform.parent.lossyScale
                        : Vector3.one;

                    Vector3 localScale = basinTransform.localScale;
                    localScale.x = newSize.x / Mathf.Max(0.0001f, parentScale.x);
                    localScale.z = newSize.z / Mathf.Max(0.0001f, parentScale.z);
                    basinTransform.localScale = localScale;

                    EditorUtility.SetDirty(surface);
                    EditorUtility.SetDirty(basinTransform);
                }
            }

            // A quiet outline even outside edit mode would be nice, but the toggle already keeps
            // this from cluttering every basin in the scene at once — mirrors how BoxCollider only
            // shows its wireframe when selected, not a persistent overlay.
        }
    }
}
