using UnityEditor;
using UnityEngine;
using LiquidVolumeFX;

namespace LiquidFX.EditorTools
{
    /// <summary>
    /// Wires the LiquidVolumePro "SceneAssorted" demo scene up as the LiquidFX showcase: every
    /// container gets catalogued layered contents, a working pour rig, and a mouth the transfer
    /// system can aim at.
    ///
    /// Kept as a re-runnable menu item rather than a one-off because the scene is a third-party
    /// demo we are adopting - if it is ever reimported or reverted, this rebuilds the whole setup
    /// instead of leaving someone to reconstruct it by hand.
    ///
    /// Tools > LiquidFX > Setup Showcase Scene
    /// </summary>
    static class ShowcaseSceneSetup
    {
        const string GlassMaterialPath = LiquidFXPaths.Materials + "/M_ShowcaseGlass.mat";
        const string SourceGlassMaterialPath = "Assets/LiquidVolumePro/Resources/Materials/Flask.mat";

        [MenuItem("Tools/LiquidFX/Setup Showcase Scene", priority = 95)]
        static void Setup()
        {
            Material glass = EnsureGlassMaterial();

            LiquidDefinition water = Liquid("Liq_Water");
            LiquidDefinition saltWater = Liquid("Liq_SaltWater");
            LiquidDefinition oil = Liquid("Liq_VegetableOil");
            LiquidDefinition ethanol = Liquid("Liq_Ethanol");
            LiquidDefinition syrup = Liquid("Liq_Syrup");
            LiquidDefinition acid = Liquid("Liq_SulfuricAcid");
            LiquidDefinition mercury = Liquid("Liq_Mercury");

            if (water == null || mercury == null)
            {
                Debug.LogError("LiquidFX: liquid library missing. Run Tools/LiquidFX/Build Liquid Library first.");
                return;
            }

            EnsureSpillManager();

            Configure("Beaker/Liquid", 280f, glass, (mercury, 20f), (syrup, 60f), (saltWater, 80f), (oil, 60f));
            Configure("Flask/Liquid", 65f, glass, (saltWater, 20f), (water, 15f), (ethanol, 15f));
            Configure("Glass", 200f, glass, (acid, 40f), (water, 120f));
            Configure("Potion", 260f, glass, (oil, 60f), (ethanol, 60f), (saltWater, 90f));
            Configure("Potion4", 38f, glass, (mercury, 10f), (ethanol, 20f));

            EditorUtility.SetDirty(glass);
            AssetDatabase.SaveAssets();
            Debug.Log("LiquidFX: showcase scene setup complete.");
        }

        /// <summary>
        /// A copy of LiquidVolumePro's own Flask material with the mirror taken out of it.
        ///
        /// The stock material is metallic 0.75 / smoothness 0.8, which on a large curved container
        /// under a bright sky turns the whole glass surface into a mirror of that sky and buries
        /// the liquid behind it - the round-bottomed "Potion" flask in this scene was washed out to
        /// near-white by exactly that, while the small containers got away with it because so
        /// little of their surface faces up. Glass is a dielectric anyway, so metallic belongs near
        /// zero; this keeps the specular highlight without the mirror.
        ///
        /// It also has to be a real asset we own: LiquidVolume instantiates a runtime clone of its
        /// Resources material otherwise, so any tuning would be thrown away on the next reload.
        /// </summary>
        static Material EnsureGlassMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(GlassMaterialPath);
            if (material == null)
            {
                LiquidFXPaths.EnsureFolder(LiquidFXPaths.Materials);
                // Copied rather than authored from scratch so it keeps the exact property set
                // LiquidVolume writes into it (_CullMode / _ZTestMode, which URP's stock Lit
                // material does not have).
                if (!AssetDatabase.CopyAsset(SourceGlassMaterialPath, GlassMaterialPath))
                {
                    Debug.LogError($"LiquidFX: could not copy {SourceGlassMaterialPath}.");
                    return null;
                }
                material = AssetDatabase.LoadAssetAtPath<Material>(GlassMaterialPath);
            }

            material.SetFloat("_Metallic", 0.0f);
            material.SetFloat("_Smoothness", 0.75f);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0.10f));
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", new Color(1f, 1f, 1f, 0.10f));
            return material;
        }

        static void EnsureSpillManager()
        {
            if (Object.FindAnyObjectByType<LiquidSpillManager>() != null)
                return;

            var puddlePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LiquidFXPaths.Prefabs + "/P_LiquidPuddle.prefab");
            var go = new GameObject("Liquid Spill Manager");
            var manager = go.AddComponent<LiquidSpillManager>();
            var so = new SerializedObject(manager);
            so.FindProperty("puddlePrefab").objectReferenceValue = puddlePrefab.GetComponent<LiquidSpillPuddle>();
            so.FindProperty("poolSize").intValue = 6;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static LiquidDefinition Liquid(string fileName)
        {
            return AssetDatabase.LoadAssetAtPath<LiquidDefinition>(
                LiquidFXPaths.Library + "/Liquids/" + fileName + ".asset");
        }

        static void Configure(string path, float capacityML, Material glass,
            params (LiquidDefinition liquid, float millilitres)[] charges)
        {
            GameObject go = GameObject.Find(path);
            if (go == null)
            {
                Debug.LogError($"LiquidFX: '{path}' not found in the open scene.");
                return;
            }

            var volume = go.GetComponent<LiquidVolume>();
            var flask = go.GetComponent<FlaskVolume>() ?? go.AddComponent<FlaskVolume>();

            // The whole piece of glassware, not just the liquid sub-object: on the Beaker and the
            // Flask the LiquidVolume sits on a "Liquid" child while the glass is a sibling, so the
            // rim we want to pour over belongs to the parent.
            Transform root = go.transform.parent != null && go.name == "Liquid" ? go.transform.parent : go.transform;
            Bounds worldBounds = CombinedWorldBounds(root.gameObject);

            // ---- contents -----------------------------------------------------------------
            var serialized = new SerializedObject(flask);
            serialized.FindProperty("capacityML").floatValue = capacityML;

            SerializedProperty list = serialized.FindProperty("initialContents");
            list.ClearArray();
            for (int i = 0; i < charges.Length; i++)
            {
                list.InsertArrayElementAtIndex(i);
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("liquid").objectReferenceValue = charges[i].liquid;
                element.FindPropertyRelative("millilitres").floatValue = charges[i].millilitres;
            }

            // ---- mouth, so a stream aimed here counts as going in --------------------------
            // Left at defaults these are (0, 0.12, 0) with a 4.5 cm radius, which for meshes
            // authored at a lossyScale of 0.03 sits far outside the container - the reason
            // nothing could pour from one flask into another.
            Vector3 mouthWorld = new Vector3(worldBounds.center.x, worldBounds.max.y, worldBounds.center.z);
            float mouthWorldRadius = Mathf.Max(worldBounds.extents.x, worldBounds.extents.z) * 0.75f;
            float scale = Mathf.Max(0.0001f, Mathf.Max(
                Mathf.Abs(go.transform.lossyScale.x), Mathf.Abs(go.transform.lossyScale.z)));

            serialized.FindProperty("portLocalOffset").vector3Value = go.transform.InverseTransformPoint(mouthWorld);
            serialized.FindProperty("portRadius").floatValue = mouthWorldRadius / scale;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            flask.BakeInitialContents();

            // ---- glass shell ---------------------------------------------------------------
            if (glass != null && volume.detail.usesFlask())
            {
                volume.flaskMaterial = glass;
                StripStaleGlassMaterials(go, glass);
            }

            // ---- lip ------------------------------------------------------------------------
            // Derived from world bounds, not the mesh's local bounds: several of these meshes are
            // rotated or off-centre in local space, which put the lip below the rim and off to one
            // side (the falling stream then started from inside the glassware, out of sight).
            Transform lip = go.transform.Find("Lip");
            if (lip == null)
            {
                var lipObject = new GameObject("Lip");
                lipObject.transform.SetParent(go.transform, true);
                lip = lipObject.transform;
            }
            lip.position = new Vector3(
                worldBounds.center.x + worldBounds.extents.x * 0.85f,
                worldBounds.max.y,
                worldBounds.center.z);

            var flaskSerialized = new SerializedObject(flask);
            flaskSerialized.FindProperty("lip").objectReferenceValue = lip;
            flaskSerialized.ApplyModifiedPropertiesWithoutUndo();

            WirePourRig(root, flask, lip);

            EditorUtility.SetDirty(flask);
            EditorUtility.SetDirty(volume);
        }

        /// <summary>
        /// Drops the runtime "Flask(Clone)" LiquidVolume made for itself before we handed it our
        /// own glass material.
        ///
        /// LiquidVolume only ever removes the material it is currently tracking in
        /// <c>_flaskMaterial</c> (see UpdateMaterialPropertiesNow), so pointing that field at a new
        /// material leaves the previous clone sitting in the renderer forever - the mesh then draws
        /// its glass shell twice, which is worse than the single mirror-like shell we set out to
        /// fix. Nothing in LiquidVolume cleans this up, so it has to happen here.
        /// </summary>
        static void StripStaleGlassMaterials(GameObject go, Material keep)
        {
            var meshRenderer = go.GetComponent<MeshRenderer>();
            var current = meshRenderer.sharedMaterials;
            var kept = new System.Collections.Generic.List<Material>(current.Length);

            foreach (Material material in current)
            {
                if (material != null && material != keep && material.name.StartsWith("Flask"))
                    continue;
                kept.Add(material);
            }

            if (kept.Count != current.Length)
                meshRenderer.sharedMaterials = kept.ToArray();
        }

        static void WirePourRig(Transform root, FlaskVolume flask, Transform lip)
        {
            var streamPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LiquidFXPaths.Prefabs + "/P_LiquidStream.prefab");
            var impactPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LiquidFXPaths.Prefabs + "/P_LiquidImpactFX.prefab");

            Transform streamTransform = root.Find("Pour Stream");
            if (streamTransform == null)
            {
                var streamObject = (GameObject)PrefabUtility.InstantiatePrefab(streamPrefab, root);
                streamObject.name = "Pour Stream";
                streamTransform = streamObject.transform;
            }

            Transform impactTransform = root.Find("Pour Impact FX");
            if (impactTransform == null)
            {
                var impactObject = (GameObject)PrefabUtility.InstantiatePrefab(impactPrefab, root);
                impactObject.name = "Pour Impact FX";
                impactTransform = impactObject.transform;
            }

            Transform controllerTransform = root.Find("Pour Controller");
            if (controllerTransform == null)
            {
                var controllerObject = new GameObject("Pour Controller");
                controllerObject.transform.SetParent(root, false);
                controllerTransform = controllerObject.transform;
            }

            var controller = controllerTransform.GetComponent<LiquidPourController>()
                ?? controllerTransform.gameObject.AddComponent<LiquidPourController>();

            var so = new SerializedObject(controller);
            so.FindProperty("sourceMode").enumValueIndex = (int)LiquidPourController.SourceMode.FlaskTilt;
            so.FindProperty("sourceFlask").objectReferenceValue = flask;
            so.FindProperty("lip").objectReferenceValue = lip;
            so.FindProperty("ribbon").objectReferenceValue = streamTransform.GetComponent<LiquidStreamRibbon>();
            so.FindProperty("impactFX").objectReferenceValue = impactTransform.GetComponent<LiquidImpactFX>();
            so.FindProperty("minimumExitSpeed").floatValue = 0.08f;
            so.FindProperty("maximumExitSpeed").floatValue = 0.5f;
            // Left empty on purpose: the receiver is resolved every frame from whatever container
            // the stream is actually falling into, which is what makes flask-to-flask transfer work
            // by simply tipping one over another.
            so.FindProperty("explicitReceiver").objectReferenceValue = null;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(controller);
        }

        static Bounds CombinedWorldBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return new Bounds(root.transform.position, Vector3.one * 0.1f);

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                // Skip the pour effects themselves - the stream ribbon is authored in world space
                // and would otherwise drag the "container" bounds across the whole scene.
                if (renderers[i].GetComponentInParent<LiquidStreamRibbon>() != null)
                    continue;
                if (renderers[i] is ParticleSystemRenderer)
                    continue;
                bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }
    }
}
