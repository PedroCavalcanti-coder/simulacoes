using UnityEditor;

namespace LiquidFX.EditorTools
{
    /// <summary>Single place that knows where the generated assets live.</summary>
    public static class LiquidFXPaths
    {
        public const string Root = "Assets/LiquidFX";
        public const string Generated = Root + "/Generated";
        public const string Materials = Generated + "/Materials";
        public const string Meshes = Generated + "/Meshes";
        public const string Prefabs = Generated + "/Prefabs";
        public const string Library = Generated + "/Library";
        public const string Scenes = Root + "/Scenes";
        public const string Shaders = Root + "/Shaders";

        public static void EnsureFolder(string path)
        {
            string[] segments = path.Split('/');
            string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }
    }
}
