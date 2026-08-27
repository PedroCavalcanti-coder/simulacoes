using System.Collections.Generic;
using LabLiquidVR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LabSpillClean.Editor
{
    static class LiquidCompositionSceneBuilder
    {
        const string WaterPath = "Assets/LabSpillClean/Configs/WaterLiquidConfig.json";
        const string AlcoholPath = "Assets/LabSpillClean/Configs/AlcoholLiquidConfig.json";
        const string OilPath = "Assets/LabSpillClean/Configs/OilLiquidConfig.json";

        struct Setup
        {
            public string name;
            public Vector3 offset;
            public TextAsset primary;
            public InitialLiquidPortion[] portions;
        }

        [MenuItem("Lab Spill/Montar 7 Frascos de Composicao")]
        public static void Build()
        {
            TextAsset water = AssetDatabase.LoadAssetAtPath<TextAsset>(WaterPath);
            TextAsset alcohol = AssetDatabase.LoadAssetAtPath<TextAsset>(AlcoholPath);
            TextAsset oil = AssetDatabase.LoadAssetAtPath<TextAsset>(OilPath);
            SpillLiquidContainer[] containers = Object.FindObjectsByType<SpillLiquidContainer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (containers.Length == 0 || water == null || alcohol == null || oil == null)
                return;

            GameObject template = containers[0].transform.root.gameObject;
            Vector3 origin = template.transform.position;
            Quaternion rotation = template.transform.rotation;
            Transform parent = template.transform.parent;
            var oldRoots = new HashSet<GameObject>();
            for (int i = 0; i < containers.Length; i++)
                oldRoots.Add(containers[i].transform.root.gameObject);

            Setup[] setups =
            {
                Single("Frasco - Agua", new Vector3(-0.8f, 0f, 0.42f), water, 120f),
                Single("Frasco - Alcool", new Vector3(0f, 0f, 0.42f), alcohol, 120f),
                Single("Frasco - Oleo", new Vector3(0.8f, 0f, 0.42f), oil, 120f),
                Pair("Mistura - Agua + Alcool", new Vector3(-0.8f, 0f, -0.42f), water, alcohol),
                Pair("Camadas - Agua + Oleo", new Vector3(0f, 0f, -0.42f), water, oil),
                Pair("Camadas - Alcool + Oleo", new Vector3(0.8f, 0f, -0.42f), alcohol, oil),
                new Setup
                {
                    name = "Mistura e Camadas - Agua + Alcool + Oleo",
                    offset = new Vector3(0f, 0f, -1.26f),
                    primary = water,
                    portions = new[]
                    {
                        Portion(water, 50f),
                        Portion(alcohol, 50f),
                        Portion(oil, 50f)
                    }
                }
            };

            for (int i = 0; i < setups.Length; i++)
            {
                Setup setup = setups[i];
                GameObject flask = Object.Instantiate(template, origin + setup.offset, rotation, parent);
                flask.name = setup.name;
                SpillLiquidContainer liquid = flask.GetComponentInChildren<SpillLiquidContainer>(true);
                liquid.liquidConfigFile = setup.primary;
                liquid.initialComposition = new List<InitialLiquidPortion>(setup.portions);
                liquid.currentVolumeML = Total(setup.portions);
                liquid.currentTemperatureC = 22f;
                EditorUtility.SetDirty(liquid);
            }

            foreach (GameObject oldRoot in oldRoots)
            {
                oldRoot.name = oldRoot.name + " - Modelo anterior desativado";
                oldRoot.SetActive(false);
                EditorUtility.SetDirty(oldRoot);
            }
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
        }

        static Setup Single(string name, Vector3 offset, TextAsset config, float ml)
        {
            return new Setup
            {
                name = name,
                offset = offset,
                primary = config,
                portions = new[] { Portion(config, ml) }
            };
        }

        static Setup Pair(string name, Vector3 offset, TextAsset a, TextAsset b)
        {
            return new Setup
            {
                name = name,
                offset = offset,
                primary = a,
                portions = new[] { Portion(a, 60f), Portion(b, 60f) }
            };
        }

        static InitialLiquidPortion Portion(TextAsset config, float ml)
        {
            return new InitialLiquidPortion { configFile = config, milliliters = ml };
        }

        static float Total(InitialLiquidPortion[] portions)
        {
            float total = 0f;
            for (int i = 0; i < portions.Length; i++) total += portions[i].milliliters;
            return total;
        }
    }
}
