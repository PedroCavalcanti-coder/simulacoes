using LiquidVolumeFX;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace LiquidFX.EditorTools
{
    /// <summary>
    /// Builds every asset the liquid system needs and assembles the two test scenes.
    ///
    /// Everything is generated from code on purpose: the whole package can be regenerated after a
    /// tuning change with one menu click, and there are no binary assets to merge.
    ///
    /// Tools > LiquidFX > Build Everything
    /// </summary>
    public static class LiquidFXBuilder
    {
        // ------------------------------------------------------------------ scene dimensions
        // Real sink dimensions in metres. The basin is 50 x 40 cm and 18 cm deep, which is what
        // makes the fill rate believable: 300 mL/s raises the level about 1.5 mm per second.

        const float BasinWidth = 0.5f;
        const float BasinDepth = 0.4f;
        const float BasinFloorY = 0.85f;
        const float CounterTopY = 1.03f;
        const float LipY = 1.34f;
        const float LipZ = 0.02f;

        [MenuItem("Tools/LiquidFX/Build Everything", priority = 0)]
        public static void BuildEverything()
        {
            EnsureFolders();
            LiquidFXTextureFactory.CreateAll();
            LiquidLibraryBuilder.BuildLibrary();

            Assets assets = CreateAssets();
            CreatePrefabs(assets);

            BuildSinkScene(assets);
            BuildFlaskPourScene(assets);
            BuildFlaskFloorSpillScene(assets);
            BuildLayeredPourScene(assets);
            BuildTwoFlasksNoMixScene(assets);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("LiquidFX: assets, prefabs and all test scenes rebuilt under " + LiquidFXPaths.Root + ".");
        }

        [MenuItem("Tools/LiquidFX/Rebuild Materials Only", priority = 20)]
        public static void RebuildMaterialsOnly()
        {
            EnsureFolders();
            CreateAssets();
            AssetDatabase.SaveAssets();
            Debug.Log("LiquidFX: materials rebuilt.");
        }

        static void EnsureFolders()
        {
            LiquidFXPaths.EnsureFolder(LiquidFXPaths.Materials);
            LiquidFXPaths.EnsureFolder(LiquidFXPaths.Meshes);
            LiquidFXPaths.EnsureFolder(LiquidFXPaths.Prefabs);
            LiquidFXPaths.EnsureFolder(LiquidFXPaths.Scenes);
        }

        // ================================================================== assets

        class Assets
        {
            public Mesh SurfaceGrid;
            public Mesh PuddleQuad;

            public Material Surface;
            public Material Stream;
            public Material Puddle;
            public Material Droplet;
            public Material Sheet;
            public Material Ring;
            public Material Bubble;
            public Material Sparkle;
            public Material Splash;

            public Material Ceramic;
            public Material Metal;
            public Material Counter;
            public Material Floor;
            public Material Table;

            public GameObject ImpactPrefab;
            public GameObject PuddlePrefab;
            public GameObject StreamPrefab;
        }

        static Assets CreateAssets()
        {
            var assets = new Assets
            {
                SurfaceGrid = CreateGridMesh("SurfaceGrid", 24),
                PuddleQuad = CreateDiscMesh("PuddleDisc", 28, 6)
            };

            Texture2D microNormal = Load<Texture2D>(LiquidFXTextureFactory.TextureFolder + "/WaterMicroNormal.png");
            Texture2D streamFlow = Load<Texture2D>(LiquidFXTextureFactory.TextureFolder + "/StreamFlow.png");
            Texture2D puddleNoise = Load<Texture2D>(LiquidFXTextureFactory.TextureFolder + "/PuddleNoise.png");
            Texture2D droplet = Load<Texture2D>(LiquidFXTextureFactory.TextureFolder + "/SoftDroplet.png");
            Texture2D sheet = Load<Texture2D>(LiquidFXTextureFactory.TextureFolder + "/SplashSheet.png");
            Texture2D ring = Load<Texture2D>(LiquidFXTextureFactory.TextureFolder + "/ImpactRing.png");

            assets.Surface = CreateMaterial("M_LiquidSurface", "LiquidFX/Liquid Surface", material =>
            {
                material.SetTexture("_NormalMap", microNormal);
                material.SetColor("_LiquidTint", new Color(0.28f, 0.62f, 0.74f, 1f));
                // Water absorbs red first, which is why depth reads as blue-green.
                material.SetVector("_AbsorptionPerMetre", new Vector4(3.2f, 1.1f, 0.7f, 0f));
                material.SetFloat("_FoamDepth", 0.022f);
                material.SetFloat("_EdgeFadeDepth", 0.012f);
                material.SetFloat("_NormalTiling", 5.5f);
                material.SetFloat("_MicroStrength", 0.14f);
                material.SetFloat("_RippleNormalStrength", 3.4f);
                material.SetColor("_FoamColor", new Color(0.95f, 0.99f, 1f, 1f));
                // Pushed past "physically correct" on purpose: a bright travelling crest and a
                // faint additive shimmer are what read as lively/stylised water instead of a flat
                // realistic puddle.
                material.SetFloat("_CrestFoam", 1.05f);
                material.SetColor("_RippleGlowColor", new Color(0.7f, 0.96f, 1f, 1f));
                material.SetFloat("_RippleGlowStrength", 1.1f);
                material.SetFloat("_ReflectionStrength", 1.2f);
                material.SetFloat("_SpecularStrength", 1.7f);
                EnableRefraction(material, true);
            });

            assets.Stream = CreateMaterial("M_LiquidStream", "LiquidFX/Liquid Stream", material =>
            {
                material.SetTexture("_FlowMap", streamFlow);
                material.SetTextureScale("_FlowMap", new Vector2(1f, 1f));
                material.SetColor("_BaseColor", new Color(0.78f, 0.93f, 1f, 1f));
                material.SetFloat("_Opacity", 0.78f);
                material.SetFloat("_SoftFade", 0.035f);
                EnableRefraction(material, true);
            });

            assets.Puddle = CreateMaterial("M_LiquidPuddle", "LiquidFX/Liquid Puddle", material =>
            {
                material.SetTexture("_NoiseMap", puddleNoise);
                material.SetColor("_BaseColor", new Color(0.78f, 0.86f, 0.9f, 0.85f));
                // A puddle is read as a darker, glossier patch of the surface underneath. The rim
                // is a thin bead, not a glowing outline, so keep it subtle and barely irregular.
                material.SetFloat("_EdgeIrregularity", 0.09f);
                material.SetFloat("_RimWidth", 0.035f);
                material.SetFloat("_RimBrightness", 0.35f);
                material.SetFloat("_Darkening", 0.55f);
                material.SetFloat("_NoiseScale", 1.8f);
                // Toned down from the flat-disc days: the new bulge normal already breaks the
                // highlight up into something that reads as a lens, so it does not also need to be
                // this bright to sell "wet".
                material.SetFloat("_SpecularStrength", 1.1f);
                material.SetFloat("_PuddleDepth", 0.006f);
                material.SetFloat("_BulgeNormalStrength", 1.4f);
            });

            assets.Droplet = CreateParticleMaterial("M_ParticleDroplet", droplet, false, 1.1f);
            assets.Sheet = CreateParticleMaterial("M_ParticleSheet", sheet, false, 1.0f);
            assets.Ring = CreateParticleMaterial("M_ParticleRing", ring, false, 1.25f);
            assets.Bubble = CreateParticleMaterial("M_ParticleBubble", droplet, true, 0.9f);
            assets.Sparkle = CreateParticleMaterial("M_ParticleSparkle", droplet, true, 1.9f);
            // Additive on purpose: a quick bright flash at first contact sells "impact" far better
            // than another translucent alpha-blended disc quietly fading in on top of the others.
            assets.Splash = CreateParticleMaterial("M_ParticleSplash", ring, true, 2.6f);

            assets.Ceramic = CreateLitMaterial("M_Ceramic", new Color(0.88f, 0.89f, 0.9f), 0.25f, 0.0f);
            assets.Metal = CreateLitMaterial("M_Metal", new Color(0.62f, 0.64f, 0.68f), 0.18f, 0.95f);
            assets.Counter = CreateLitMaterial("M_Counter", new Color(0.22f, 0.23f, 0.26f), 0.55f, 0.05f);
            assets.Floor = CreateLitMaterial("M_Floor", new Color(0.14f, 0.15f, 0.17f), 0.8f, 0f);
            assets.Table = CreateLitMaterial("M_Table", new Color(0.32f, 0.26f, 0.21f), 0.65f, 0f);

            return assets;
        }

        static void EnableRefraction(Material material, bool enabled)
        {
            material.SetFloat("_Refraction", enabled ? 1f : 0f);
            if (enabled)
                material.EnableKeyword("_REFRACTION_ON");
            else
                material.DisableKeyword("_REFRACTION_ON");
        }

        static Material CreateParticleMaterial(string name, Texture2D texture, bool additive, float brightness)
        {
            return CreateMaterial(name, "LiquidFX/Liquid Particle", material =>
            {
                material.SetTexture("_BaseMap", texture);
                material.SetFloat("_Brightness", brightness);
                material.SetFloat("_SoftFade", 0.03f);
                material.SetFloat("_Additive", additive ? 1f : 0f);
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
                if (additive)
                    material.EnableKeyword("_ADDITIVE_ON");
                else
                    material.DisableKeyword("_ADDITIVE_ON");
            });
        }

        static Material CreateLitMaterial(string name, Color color, float smoothnessInverse, float metallic)
        {
            return CreateMaterial(name, "Universal Render Pipeline/Lit", material =>
            {
                material.SetColor("_BaseColor", color);
                material.SetFloat("_Smoothness", 1f - smoothnessInverse);
                material.SetFloat("_Metallic", metallic);
            });
        }

        static Material CreateMaterial(string name, string shaderName, System.Action<Material> configure)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError($"LiquidFX: shader '{shaderName}' not found. Let Unity finish importing and run the builder again.");
                return null;
            }

            string path = LiquidFXPaths.Materials + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            configure?.Invoke(material);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// Flat grid in the XZ plane spanning -0.5..0.5. Resolution 1 gives a plain quad for the
        /// puddle; 24 gives the sink surface enough vertices for the ripple displacement to show.
        /// </summary>
        static Mesh CreateGridMesh(string name, int resolution)
        {
            string path = LiquidFXPaths.Meshes + "/" + name + ".asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool isNew = mesh == null;
            if (isNew)
                mesh = new Mesh();

            mesh.name = name;
            mesh.Clear();

            int lineVertices = resolution + 1;
            var vertices = new Vector3[lineVertices * lineVertices];
            var uvs = new Vector2[vertices.Length];
            var normals = new Vector3[vertices.Length];

            for (int z = 0; z < lineVertices; z++)
            {
                for (int x = 0; x < lineVertices; x++)
                {
                    int index = z * lineVertices + x;
                    float u = x / (float)resolution;
                    float v = z / (float)resolution;
                    vertices[index] = new Vector3(u - 0.5f, 0f, v - 0.5f);
                    uvs[index] = new Vector2(u, v);
                    normals[index] = Vector3.up;
                }
            }

            var triangles = new int[resolution * resolution * 6];
            int triangle = 0;
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int bottomLeft = z * lineVertices + x;
                    int bottomRight = bottomLeft + 1;
                    int topLeft = bottomLeft + lineVertices;
                    int topRight = topLeft + 1;

                    triangles[triangle++] = bottomLeft;
                    triangles[triangle++] = topLeft;
                    triangles[triangle++] = bottomRight;
                    triangles[triangle++] = bottomRight;
                    triangles[triangle++] = topLeft;
                    triangles[triangle++] = topRight;
                }
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = triangles;
            // Generous bounds so ripple displacement never gets culled at a grazing angle.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1f, 0.4f, 1f));

            if (isNew)
                AssetDatabase.CreateAsset(mesh, path);
            else
                EditorUtility.SetDirty(mesh);

            return mesh;
        }

        /// <summary>
        /// A filled circular disc, radius 0.5 (diameter 1, matching the convention every puddle
        /// caller already uses for transform.localScale). UV is centred at (0.5, 0.5) with the
        /// rim exactly at UV-radius 1, so the puddle shader's radial shape math has a true
        /// circular boundary to work with instead of trying to hide the corners of a flat quad
        /// with a discard threshold — that mismatch was what carried through the original 4-vertex
        /// PuddleQuad as a puddle that read as a hard-edged square. The extra rings also give the
        /// vertex shader enough resolution to bulge into a real dome instead of a paper-flat decal.
        /// </summary>
        static Mesh CreateDiscMesh(string name, int segments, int rings)
        {
            string path = LiquidFXPaths.Meshes + "/" + name + ".asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool isNew = mesh == null;
            if (isNew)
                mesh = new Mesh();

            mesh.name = name;
            mesh.Clear();

            int vertexCount = 1 + segments * rings;
            var vertices = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var normals = new Vector3[vertexCount];

            vertices[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);
            normals[0] = Vector3.up;

            for (int ring = 1; ring <= rings; ring++)
            {
                float radiusFraction = ring / (float)rings;
                for (int segment = 0; segment < segments; segment++)
                {
                    float angle = segment / (float)segments * Mathf.PI * 2f;
                    float cos = Mathf.Cos(angle);
                    float sin = Mathf.Sin(angle);
                    int index = 1 + (ring - 1) * segments + segment;
                    vertices[index] = new Vector3(cos * radiusFraction * 0.5f, 0f, sin * radiusFraction * 0.5f);
                    uvs[index] = new Vector2(0.5f + cos * radiusFraction * 0.5f, 0.5f + sin * radiusFraction * 0.5f);
                    normals[index] = Vector3.up;
                }
            }

            int centreFanTriangles = segments * 3;
            int ringQuadTriangles = (rings - 1) * segments * 6;
            var triangles = new int[centreFanTriangles + ringQuadTriangles];
            int triangle = 0;

            for (int segment = 0; segment < segments; segment++)
            {
                int a = 1 + segment;
                int b = 1 + (segment + 1) % segments;
                triangles[triangle++] = 0;
                triangles[triangle++] = a;
                triangles[triangle++] = b;
            }

            for (int ring = 1; ring < rings; ring++)
            {
                int innerStart = 1 + (ring - 1) * segments;
                int outerStart = 1 + ring * segments;
                for (int segment = 0; segment < segments; segment++)
                {
                    int innerA = innerStart + segment;
                    int innerB = innerStart + (segment + 1) % segments;
                    int outerA = outerStart + segment;
                    int outerB = outerStart + (segment + 1) % segments;

                    triangles[triangle++] = innerA;
                    triangles[triangle++] = outerA;
                    triangles[triangle++] = innerB;
                    triangles[triangle++] = innerB;
                    triangles[triangle++] = outerA;
                    triangles[triangle++] = outerB;
                }
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = triangles;
            // Generous vertical bounds so vertex-shader bulge displacement never gets frustum culled.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1.05f, 0.06f, 1.05f));

            if (isNew)
                AssetDatabase.CreateAsset(mesh, path);
            else
                EditorUtility.SetDirty(mesh);

            return mesh;
        }

        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

        // ================================================================== prefabs

        static void CreatePrefabs(Assets assets)
        {
            assets.ImpactPrefab = SavePrefab(BuildImpactRig(assets), "P_LiquidImpactFX");
            assets.PuddlePrefab = SavePrefab(BuildPuddle(assets), "P_LiquidPuddle");
            assets.StreamPrefab = SavePrefab(BuildStream(assets), "P_LiquidStream");
        }

        static GameObject SavePrefab(GameObject instance, string name)
        {
            string path = LiquidFXPaths.Prefabs + "/" + name + ".prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
            Object.DestroyImmediate(instance);
            return prefab;
        }

        static GameObject BuildImpactRig(Assets assets)
        {
            var root = new GameObject("Liquid Impact FX");
            var fx = root.AddComponent<LiquidImpactFX>();

            ParticleSystem sheets = BuildCrownSheets(root.transform, assets.Sheet);
            ParticleSystem droplets = BuildDroplets(root.transform, assets.Droplet);
            ParticleSystem ring = BuildSurfaceRing(root.transform, assets.Ring);
            ParticleSystem bubbles = BuildBubbles(root.transform, assets.Bubble);
            ParticleSystem splash = BuildSplashBurst(root.transform, assets.Splash);

            SetPrivate(fx, "crownSheets", sheets);
            SetPrivate(fx, "droplets", droplets);
            SetPrivate(fx, "surfaceRing", ring);
            SetPrivate(fx, "bubbles", bubbles);
            SetPrivate(fx, "splashBurst", splash);
            return root;
        }

        static GameObject BuildPuddle(Assets assets)
        {
            var root = new GameObject("Liquid Puddle");
            root.AddComponent<MeshFilter>().sharedMesh = assets.PuddleQuad;
            MeshRenderer meshRenderer = root.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = assets.Puddle;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            root.AddComponent<LiquidSpillPuddle>();
            return root;
        }

        static GameObject BuildStream(Assets assets)
        {
            var root = new GameObject("Liquid Stream");
            root.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = root.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = assets.Stream;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            root.AddComponent<LiquidStreamRibbon>();
            return root;
        }

        // ------------------------------------------------------------------ particle systems

        static ParticleSystem BuildParticleSystem(string name, Transform parent, Material material, int maxParticles)
        {
            var gameObject = new GameObject(name);
            gameObject.transform.SetParent(parent, false);
            ParticleSystem system = gameObject.AddComponent<ParticleSystem>();

            var main = system.main;
            main.loop = true;
            main.playOnAwake = false;
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = system.emission;
            emission.enabled = false;
            emission.rateOverTime = 0f;

            ParticleSystemRenderer particleRenderer = system.GetComponent<ParticleSystemRenderer>();
            particleRenderer.sharedMaterial = material;
            particleRenderer.shadowCastingMode = ShadowCastingMode.Off;
            particleRenderer.receiveShadows = false;
            particleRenderer.lightProbeUsage = LightProbeUsage.Off;
            particleRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            particleRenderer.sortMode = ParticleSystemSortMode.None;
            return system;
        }

        /// <summary>The thin sheets thrown up around an impact, before they break into drops.</summary>
        static ParticleSystem BuildCrownSheets(Transform parent, Material material)
        {
            ParticleSystem system = BuildParticleSystem("Crown Sheets", parent, material, 20);

            var main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.22f, 0.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.85f, 1.9f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.018f, 0.045f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.85f, 0.95f, 1f, 0.55f));
            main.gravityModifier = 1.1f;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 62f;
            shape.radius = 0.008f;
            shape.radiusThickness = 1f;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, Curve(0.5f, 1f, 0.55f));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(FadeOutGradient(0.15f));

            ParticleSystemRenderer particleRenderer = system.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            particleRenderer.velocityScale = 0.06f;
            particleRenderer.lengthScale = 1.6f;
            return system;
        }

        static ParticleSystem BuildDroplets(Transform parent, Material material)
        {
            ParticleSystem system = BuildParticleSystem("Droplets", parent, material, 24);

            var main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.75f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.1f, 2.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.005f, 0.013f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.88f, 0.96f, 1f, 0.9f));
            main.gravityModifier = 1.25f;

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 45f;
            shape.radius = 0.006f;
            shape.radiusThickness = 1f;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(FadeOutGradient(0.6f));

            ParticleSystemRenderer particleRenderer = system.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            particleRenderer.velocityScale = 0.035f;
            particleRenderer.lengthScale = 2.2f;
            return system;
        }

        /// <summary>The flat ring that races outward on the surface where the stream lands.</summary>
        static ParticleSystem BuildSurfaceRing(Transform parent, Material material)
        {
            ParticleSystem system = BuildParticleSystem("Surface Ring", parent, material, 8);

            var main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.8f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.85f, 0.97f, 1f, 0.5f));
            main.gravityModifier = 0f;

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.004f;

            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            // A x7 multiplier on a 0.03 start size makes a 21 cm flat billboard - fine tucked
            // under the sink's own shader ripples, but nakedly oversized and glitchy-looking on
            // an open floor with nothing else to blend it into. x3 keeps a visible expanding ring
            // without it reading as a stray quad.
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(3f, Curve(0.15f, 1f, 1f));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(FadeOutGradient(0.05f));

            ParticleSystemRenderer particleRenderer = system.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
            return system;
        }

        /// <summary>Air dragged under the surface by a hard impact, floating back up.</summary>
        static ParticleSystem BuildBubbles(Transform parent, Material material)
        {
            ParticleSystem system = BuildParticleSystem("Entrained Bubbles", parent, material, 16);

            var main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.15f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.22f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.003f, 0.009f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.8f, 0.94f, 1f, 0.5f));
            main.gravityModifier = -0.35f;

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.02f;

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(FadeOutGradient(0.35f));
            return system;
        }

        /// <summary>
        /// One-shot bright flash at the exact instant a stream first lands - fired once by
        /// <see cref="LiquidImpactFX"/> on the idle-to-landed edge, never by a continuous rate.
        /// This is what turns a splash from "particles slowly ramping up" into an actual impact.
        /// </summary>
        static ParticleSystem BuildSplashBurst(Transform parent, Material material)
        {
            ParticleSystem system = BuildParticleSystem("Splash Burst", parent, material, 6);

            var main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.16f, 0.24f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.05f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.9f, 0.98f, 1f, 0.75f));
            main.gravityModifier = 0f;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.012f;

            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            // Snaps open fast (a real splash flash pops, it doesn't ease in) and holds near full
            // size while it fades, rather than shrinking back down like a bubble would.
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(2.6f, Curve(0.15f, 1f, 0.9f));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(FadeOutGradient(0.05f));

            ParticleSystemRenderer particleRenderer = system.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
            return system;
        }

        static ParticleSystem BuildLipDrips(Transform parent, Material material)
        {
            ParticleSystem system = BuildParticleSystem("Lip Drips", parent, material, 10);

            var main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.004f, 0.009f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.85f, 0.95f, 1f, 0.95f));
            main.gravityModifier = 1f;

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.004f;

            ParticleSystemRenderer particleRenderer = system.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            particleRenderer.velocityScale = 0.03f;
            particleRenderer.lengthScale = 2.4f;
            return system;
        }

        static AnimationCurve Curve(float start, float peak, float end)
        {
            return new AnimationCurve(
                new Keyframe(0f, start),
                new Keyframe(0.35f, peak),
                new Keyframe(1f, end));
        }

        static Gradient FadeOutGradient(float holdUntil)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, Mathf.Clamp01(holdUntil)),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
        }

        /// <summary>Eases in, holds, eases out. Used for motes that pop in, glint, and fade.</summary>
        static Gradient FadeInOutGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.25f),
                    new GradientAlphaKey(1f, 0.7f),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
        }

        /// <summary>
        /// Ambient sparkle motes scattered across the water surface. Parented to the water so it
        /// inherits the basin's world-space width and depth through its own transform scale, and
        /// rises with the level for free as the water fills.
        /// </summary>
        static ParticleSystem BuildSurfaceSparkles(Transform waterTransform, Material material)
        {
            var gameObject = new GameObject("Surface Sparkles");
            gameObject.transform.SetParent(waterTransform, false);
            gameObject.transform.localPosition = new Vector3(0f, 0.002f, 0f);

            ParticleSystem system = gameObject.AddComponent<ParticleSystem>();

            var main = system.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.4f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.006f, 0.016f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.75f, 0.97f, 1f, 0.55f),
                new Color(1f, 1f, 1f, 0.85f));
            main.gravityModifier = 0f;
            main.maxParticles = 40;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            // Scaling mode Local means the shape below multiplies by the water's own transform
            // scale, so the emission box always covers exactly the basin footprint without needing
            // the basin size passed in separately. (This lives on MainModule, not ShapeModule.)
            main.scalingMode = ParticleSystemScalingMode.Local;

            var emission = system.emission;
            emission.enabled = false;
            emission.rateOverTime = 0f;

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(1f, 0.01f, 1f);

            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, Curve(0f, 1f, 0.3f));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(FadeInOutGradient());

            // A gentle drift and wobble so the motes feel alive without any real current beneath
            // them; this is the whole "fantastical" trick, they never sit perfectly still.
            var velocity = system.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.y = new ParticleSystem.MinMaxCurve(0.006f, 0.018f);

            var noise = system.noise;
            noise.enabled = true;
            noise.strength = 0.01f;
            noise.frequency = 0.3f;
            noise.scrollSpeed = 0.2f;
            noise.quality = ParticleSystemNoiseQuality.Low;

            ParticleSystemRenderer particleRenderer = system.GetComponent<ParticleSystemRenderer>();
            particleRenderer.sharedMaterial = material;
            particleRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            particleRenderer.shadowCastingMode = ShadowCastingMode.Off;
            particleRenderer.receiveShadows = false;
            particleRenderer.lightProbeUsage = LightProbeUsage.Off;
            particleRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            particleRenderer.sortMode = ParticleSystemSortMode.None;

            return system;
        }

        // ================================================================== sink scene

        static void BuildSinkScene(Assets assets)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("Sink Faucet");
            ApplyEnvironment();

            // Everything below is grouped under a handful of top-level folders instead of sitting
            // loose under the scene root: Scene Setup (camera/light/probe), Environment (the room
            // and the basin shell), Water System (the liquid itself plus what reacts to it), Faucet
            // (the geometry and the pour logic that drives it), and Props (test objects only).
            Transform sceneSetup = CreateGroup("Scene Setup", root.transform);
            Transform environment = CreateGroup("Environment", root.transform);
            Transform waterSystem = CreateGroup("Water System", root.transform);
            Transform faucet = CreateGroup("Faucet", root.transform);
            Transform props = CreateGroup("Props", root.transform);

            Camera camera = CreateCamera(sceneSetup, new Vector3(0.62f, 1.62f, -0.78f), new Vector3(0f, 1f, 0.06f), 44f);
            CreateLight(sceneSetup);
            CreateReflectionProbe(sceneSetup, new Vector3(0f, 1.05f, 0f), new Vector3(1.6f, 1.1f, 1.2f));

            // ---- room and counter ------------------------------------------------------
            CreateBox("Floor", environment, new Vector3(0f, -0.05f, 0f), new Vector3(6f, 0.1f, 5f), assets.Floor);
            CreateBox("Back Wall", environment, new Vector3(0f, 1.2f, 0.95f), new Vector3(6f, 2.4f, 0.1f), assets.Floor);

            const float slabCentreY = CounterTopY * 0.5f;
            float halfWidth = BasinWidth * 0.5f;
            float halfDepth = BasinDepth * 0.5f;

            CreateBox("Counter Left", environment,
                new Vector3(-halfWidth - 0.275f, slabCentreY, 0f),
                new Vector3(0.55f, CounterTopY, 0.7f), assets.Counter);
            CreateBox("Counter Right", environment,
                new Vector3(halfWidth + 0.275f, slabCentreY, 0f),
                new Vector3(0.55f, CounterTopY, 0.7f), assets.Counter);
            CreateBox("Counter Back", environment,
                new Vector3(0f, slabCentreY, halfDepth + 0.075f),
                new Vector3(BasinWidth, CounterTopY, 0.15f), assets.Counter);
            CreateBox("Counter Front", environment,
                new Vector3(0f, slabCentreY, -halfDepth - 0.075f),
                new Vector3(BasinWidth, CounterTopY, 0.15f), assets.Counter);

            // The ceramic liner sits INSIDE the counter opening. Putting it outside would make it
            // share space with the counter slabs, and two coplanar boxes z-fight into a striped
            // mess exactly where the water meets the wall.
            const float linerThickness = 0.012f;
            const float linerHalf = linerThickness * 0.5f;
            float wallCentreY = (BasinFloorY + CounterTopY) * 0.5f;
            float wallHeight = CounterTopY - BasinFloorY;

            Transform basin = CreateGroup("Basin", environment);
            CreateBox("Basin Floor", basin,
                new Vector3(0f, BasinFloorY - 0.015f, 0f),
                new Vector3(BasinWidth, 0.03f, BasinDepth), assets.Ceramic);
            CreateBox("Basin Wall Left", basin,
                new Vector3(-halfWidth + linerHalf, wallCentreY, 0f),
                new Vector3(linerThickness, wallHeight, BasinDepth), assets.Ceramic);
            CreateBox("Basin Wall Right", basin,
                new Vector3(halfWidth - linerHalf, wallCentreY, 0f),
                new Vector3(linerThickness, wallHeight, BasinDepth), assets.Ceramic);
            CreateBox("Basin Wall Back", basin,
                new Vector3(0f, wallCentreY, halfDepth - linerHalf),
                new Vector3(BasinWidth - linerThickness * 2f, wallHeight, linerThickness), assets.Ceramic);
            CreateBox("Basin Wall Front", basin,
                new Vector3(0f, wallCentreY, -halfDepth + linerHalf),
                new Vector3(BasinWidth - linerThickness * 2f, wallHeight, linerThickness), assets.Ceramic);

            CreateCylinder("Drain", basin,
                new Vector3(0.14f, BasinFloorY + 0.003f, -0.1f),
                new Vector3(0.075f, 0.004f, 0.075f), assets.Metal);

            // ---- water -----------------------------------------------------------------
            GameObject water = CreateMeshObject("Sink Water", waterSystem, assets.SurfaceGrid, assets.Surface);
            water.transform.position = new Vector3(0f, 0.88f, 0f);
            water.transform.localScale = new Vector3(
                BasinWidth - linerThickness * 2f, 1f, BasinDepth - linerThickness * 2f);
            var surface = water.AddComponent<LiquidSurface>();
            SetPrivate(surface, "basinFloorY", BasinFloorY);
            SetPrivate(surface, "basinRimY", CounterTopY - 0.01f);
            // The sink starts bone dry: filling it up is the point of the demo.
            SetPrivate(surface, "startingContentsML", 0f);
            SetPrivate(surface, "drainMLPerSecond", 2200f);
            SetPrivate(surface, "liquidTint", new Color(0.33f, 0.68f, 0.74f, 1f));
            // Wider, slower rings than the defaults: on a 50 cm basin the default 5.5 cm
            // wavelength packs in nine rings and reads as frantic. Fewer, bigger, longer-lived
            // rings look calmer and match the reference's broad travelling crests.
            SetPrivate(surface, "rippleSpeed", 0.42f);
            SetPrivate(surface, "rippleWavelength", 0.09f);
            SetPrivate(surface, "rippleSpatialDecay", 4.5f);
            SetPrivate(surface, "rippleTimeDecay", 0.9f);
            SetPrivate(surface, "rippleLifetime", 3.2f);
            SetPrivate(surface, "rippleAmplitude", 0.0055f);

            ParticleSystem sparkles = BuildSurfaceSparkles(water.transform, assets.Sparkle);
            var surfaceSparkles = water.AddComponent<LiquidSurfaceSparkles>();
            SetPrivate(surfaceSparkles, "surface", surface);
            SetPrivate(surfaceSparkles, "sparkles", sparkles);

            // ---- impacts ---------------------------------------------------------------
            var faucetImpact = (GameObject)PrefabUtility.InstantiatePrefab(assets.ImpactPrefab, waterSystem);
            faucetImpact.name = "Faucet Impact FX";
            var faucetImpactFX = faucetImpact.GetComponent<LiquidImpactFX>();

            var objectImpact = (GameObject)PrefabUtility.InstantiatePrefab(assets.ImpactPrefab, waterSystem);
            objectImpact.name = "Object Impact FX";
            var objectImpactFX = objectImpact.GetComponent<LiquidImpactFX>();

            var triggerObject = new GameObject("Water Contact Trigger");
            triggerObject.transform.SetParent(waterSystem);
            triggerObject.transform.position = new Vector3(0f, (BasinFloorY + CounterTopY) * 0.5f, 0f);
            BoxCollider trigger = triggerObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(BasinWidth, CounterTopY - BasinFloorY + 0.2f, BasinDepth);
            var impacts = triggerObject.AddComponent<LiquidSurfaceImpacts>();
            SetPrivate(impacts, "surface", surface);
            SetPrivate(impacts, "impactFX", objectImpactFX);

            CreateSpillManager(waterSystem, assets);

            // ---- faucet ----------------------------------------------------------------
            CreateCylinder("Faucet Column", faucet,
                new Vector3(0f, (CounterTopY + LipY + 0.08f) * 0.5f, 0.3f),
                new Vector3(0.045f, (LipY + 0.08f - CounterTopY) * 0.5f, 0.045f), assets.Metal);
            CreateCylinderBetween("Faucet Arm", faucet,
                new Vector3(0f, LipY + 0.08f, 0.3f),
                new Vector3(0f, LipY + 0.08f, LipZ), 0.022f, assets.Metal);
            CreateCylinderBetween("Faucet Spout", faucet,
                new Vector3(0f, LipY + 0.08f, LipZ),
                new Vector3(0f, LipY, LipZ), 0.02f, assets.Metal);

            var lip = new GameObject("Faucet Lip");
            lip.transform.SetParent(faucet);
            lip.transform.position = new Vector3(0f, LipY - 0.005f, LipZ);

            var stream = (GameObject)PrefabUtility.InstantiatePrefab(assets.StreamPrefab, faucet);
            stream.name = "Faucet Stream";
            var ribbon = stream.GetComponent<LiquidStreamRibbon>();

            ParticleSystem drips = BuildLipDrips(faucet, assets.Droplet);
            drips.name = "Faucet Lip Drips";

            var controllerObject = new GameObject("Faucet Pour Controller");
            controllerObject.transform.SetParent(faucet);
            var controller = controllerObject.AddComponent<LiquidPourController>();
            SetPrivate(controller, "sourceMode", (int)LiquidPourController.SourceMode.Valve);
            SetPrivate(controller, "lip", lip.transform);
            SetPrivate(controller, "valveMaxFlowMLPerSecond", 300f);
            SetPrivate(controller, "intensity", 0f);
            SetPrivate(controller, "valveLiquidColor", new Color(0.72f, 0.9f, 0.98f, 1f));
            SetPrivate(controller, "minimumExitSpeed", 0.2f);
            SetPrivate(controller, "maximumExitSpeed", 0.9f);
            SetPrivate(controller, "ribbon", ribbon);
            SetPrivate(controller, "impactFX", faucetImpactFX);
            SetPrivate(controller, "lipDrips", drips);
            SetPrivate(controller, "explicitReceiver", surface);

            // ---- test props ------------------------------------------------------------
            // Three densities side by side so the effect is legible at a glance once the basin
            // fills: dense sinks and stays down, near-water bobs at the surface, light floats
            // high and, if pushed to the floor by hand, launches back out.
            BuildBuoyantSphere("Dense Sphere (sinks)", props, new Vector3(-0.16f, 1.1f, -0.06f), 0.055f, assets.Metal, 3500f, surface);
            BuildBuoyantSphere("Neutral Sphere (bobs near surface)", props, new Vector3(-0.14f, 0.9f, 0.08f), 0.07f, assets.Metal, 950f, surface);
            BuildBuoyantSphere("Light Sphere (floats, pops if pushed under)", props, new Vector3(0.16f, 1.28f, 0.1f), 0.055f, assets.Metal, 180f, surface);

            // ---- demo input ------------------------------------------------------------
            var rig = camera.gameObject.AddComponent<LiquidFXDemoRig>();
            SetPrivate(rig, "pour", controller);
            SetPrivate(rig, "surface", surface);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, LiquidFXPaths.Scenes + "/SinkFaucet.unity");
        }

        // ================================================================== flask scene

        static void BuildFlaskPourScene(Assets assets)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("Flask Pour");
            ApplyEnvironment();

            const float tableTop = 0.75f;
            Camera camera = CreateCamera(root.transform, new Vector3(0.26f, 1.02f, -0.42f), new Vector3(0f, 0.85f, 0f), 40f);
            CreateLight(root.transform);
            CreateReflectionProbe(root.transform, new Vector3(0f, 0.95f, 0f), new Vector3(1.4f, 0.9f, 1f));

            CreateBox("Floor", root.transform, new Vector3(0f, -0.05f, 0f), new Vector3(6f, 0.1f, 5f), assets.Floor);
            CreateBox("Bench", root.transform, new Vector3(0f, tableTop * 0.5f, 0f), new Vector3(1.4f, tableTop, 0.7f), assets.Table);

            GameObject sourcePrefab = Load<GameObject>(
                "Assets/LiquidVolumePro/Prefabs/ChemistryFlasks/250ml_Erlenmeyer/Prefabs/Erlenmeyer_250ml.prefab");
            GameObject targetPrefab = Load<GameObject>(
                "Assets/LiquidVolumePro/Prefabs/ChemistryFlasks/250ml_Beaker/Prefabs/Beaker_250ml.prefab");

            if (sourcePrefab == null || targetPrefab == null)
            {
                Debug.LogWarning("LiquidFX: LiquidVolumePro chemistry flask prefabs not found; the pour scene will be empty.");
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, LiquidFXPaths.Scenes + "/FlaskPour.unity");
                return;
            }

            // ---- receiving beaker -------------------------------------------------------
            var target = (GameObject)PrefabUtility.InstantiatePrefab(targetPrefab, root.transform);
            target.name = "Target Beaker 250 mL";
            target.transform.position = new Vector3(0.08f, tableTop, 0f);
            FlaskVolume targetVolume = ConfigureFlask(target, 250f, 0f, out Bounds targetBounds);
            DropToSurface(target.transform, targetBounds, tableTop);

            // ---- pouring erlenmeyer ------------------------------------------------------
            // The flask is tipped around its own lip, the way a hand does it, so the spout stays
            // put over the beaker while the body rotates.
            var source = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab, root.transform);
            source.name = "Source Erlenmeyer 250 mL";
            source.transform.position = new Vector3(-0.09f, tableTop, 0f);
            FlaskVolume sourceVolume = ConfigureFlask(source, 250f, 210f, out Bounds sourceBounds);
            DropToSurface(source.transform, sourceBounds, tableTop);

            // Recompute after the drop so the lip sits on the real rim.
            sourceBounds = WorldBounds(source);
            Vector3 lipWorld = new Vector3(
                sourceBounds.center.x + sourceBounds.extents.x * 0.55f,
                sourceBounds.max.y,
                sourceBounds.center.z);

            var lip = new GameObject("Lip");
            lip.transform.SetParent(source.transform, true);
            lip.transform.position = lipWorld;
            SetPrivate(sourceVolume, "lip", lip.transform);

            // The pivot is created at the lip and only then adopts the flask, so parenting does
            // not drag the glassware off the bench.
            var pivot = new GameObject("Pour Pivot");
            pivot.transform.SetParent(root.transform);
            pivot.transform.position = lipWorld;
            source.transform.SetParent(pivot.transform, true);

            // Nobody pours by tipping a flask that is still standing on the bench: the lip has to
            // be over the mouth of the beaker. Lift the whole rig by the offset that puts it there,
            // which also means the tilt pivot stays exactly on the lip.
            Vector3 pourPosition = targetVolume.PortCentreWorld + Vector3.up * 0.1f;
            pivot.transform.position += pourPosition - lipWorld;
            lipWorld = pourPosition;

            // ---- pour rig -----------------------------------------------------------------
            var stream = (GameObject)PrefabUtility.InstantiatePrefab(assets.StreamPrefab, root.transform);
            stream.name = "Pour Stream";
            var ribbon = stream.GetComponent<LiquidStreamRibbon>();

            var impact = (GameObject)PrefabUtility.InstantiatePrefab(assets.ImpactPrefab, root.transform);
            impact.name = "Pour Impact FX";
            var impactFX = impact.GetComponent<LiquidImpactFX>();

            ParticleSystem drips = BuildLipDrips(root.transform, assets.Droplet);
            drips.name = "Flask Lip Drips";

            var controllerObject = new GameObject("Flask Pour Controller");
            controllerObject.transform.SetParent(root.transform);
            var controller = controllerObject.AddComponent<LiquidPourController>();
            SetPrivate(controller, "sourceMode", (int)LiquidPourController.SourceMode.FlaskTilt);
            SetPrivate(controller, "sourceFlask", sourceVolume);
            SetPrivate(controller, "lip", lip.transform);
            SetPrivate(controller, "ribbon", ribbon);
            SetPrivate(controller, "impactFX", impactFX);
            SetPrivate(controller, "lipDrips", drips);
            SetPrivate(controller, "minimumExitSpeed", 0.08f);
            SetPrivate(controller, "maximumExitSpeed", 0.5f);
            // Left empty so the receiver is picked from whatever is under the stream: move the
            // beaker aside in play mode and the liquid puddles on the bench instead.
            SetPrivate(controller, "explicitReceiver", null);

            CreateSpillManager(root.transform, assets);

            var rig = camera.gameObject.AddComponent<LiquidFXDemoRig>();
            SetPrivate(rig, "pour", controller);
            SetPrivate(rig, "tiltTarget", pivot.transform);
            SetPrivate(rig, "tiltFlask", sourceVolume);
            // The lip sits on the +X side of the flask and the beaker is at +X, so the flask has to
            // tip that way. Rotating about +Z would swing the lip away from the target.
            SetPrivate(rig, "tiltAxis", Vector3.back);
            SetPrivate(rig, "maximumTilt", 118f);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, LiquidFXPaths.Scenes + "/FlaskPour.unity");
        }

        // ================================================================== flask-to-floor scene

        /// <summary>
        /// Isolated review scene: a flask tips, the stream falls, and whatever lands becomes a
        /// puddle on the floor that grows, sits, dries from the rim inward, and switches itself
        /// off. There is no receiving vessel anywhere in the scene on purpose — every drop always
        /// misses, so this is nothing but the two effects the floor spill is actually about.
        /// </summary>
        static void BuildFlaskFloorSpillScene(Assets assets)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("Flask Floor Spill");
            ApplyEnvironment();

            // Lower and closer than the flask-pour review camera: the whole point of this scene is
            // the fall and the splash, so the framing has to hold the floor around the landing
            // point in view, not just the flask that's doing the pouring.
            Camera camera = CreateCamera(root.transform, new Vector3(0.5f, 0.8f, -0.78f), new Vector3(0f, 0.1f, 0f), 46f);
            CreateLight(root.transform);
            CreateReflectionProbe(root.transform, new Vector3(0f, 0.55f, 0f), new Vector3(2.6f, 1.6f, 2.6f));

            // Open floor with room for the puddle to spread, no bench or basin in the way.
            CreateBox("Floor", root.transform, new Vector3(0f, -0.05f, 0f), new Vector3(3f, 0.1f, 3f), assets.Floor);

            // A simple stand: enough to read as "the flask is mounted up here", without pretending
            // to be a real clamp stand. Fall height is what sells the effect, not the furniture.
            // The base is kept small on purpose - a wide foot sat exactly under the pour used to
            // hide the puddle underneath it for the first several seconds of pouring.
            const float standHeight = 0.55f;
            CreateCylinder("Flask Stand", root.transform,
                new Vector3(0f, standHeight * 0.5f, 0f), new Vector3(0.03f, standHeight * 0.5f, 0.03f), assets.Metal);
            CreateCylinder("Flask Stand Base", root.transform,
                new Vector3(0f, 0.01f, 0f), new Vector3(0.05f, 0.01f, 0.05f), assets.Metal);

            GameObject sourcePrefab = Load<GameObject>(
                "Assets/LiquidVolumePro/Prefabs/ChemistryFlasks/250ml_Erlenmeyer/Prefabs/Erlenmeyer_250ml.prefab");
            if (sourcePrefab == null)
            {
                Debug.LogWarning("LiquidFX: LiquidVolumePro Erlenmeyer prefab not found; the floor spill scene will be empty.");
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, LiquidFXPaths.Scenes + "/FlaskFloorSpill.unity");
                return;
            }

            var source = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab, root.transform);
            source.name = "Source Erlenmeyer 250 mL";
            source.transform.position = new Vector3(0f, standHeight, 0f);
            FlaskVolume sourceVolume = ConfigureFlask(source, 250f, 210f, out Bounds sourceBounds);
            DropToSurface(source.transform, sourceBounds, standHeight);

            // Recompute after the drop so the lip sits on the real rim, then tip around the lip
            // (not the flask centre) so the spout stays put while the body rotates through it.
            sourceBounds = WorldBounds(source);
            Vector3 lipWorld = new Vector3(
                sourceBounds.center.x + sourceBounds.extents.x * 0.55f,
                sourceBounds.max.y,
                sourceBounds.center.z);

            var lip = new GameObject("Lip");
            lip.transform.SetParent(source.transform, true);
            lip.transform.position = lipWorld;
            SetPrivate(sourceVolume, "lip", lip.transform);

            var pivot = new GameObject("Pour Pivot");
            pivot.transform.SetParent(root.transform);
            pivot.transform.position = lipWorld;
            source.transform.SetParent(pivot.transform, true);

            var stream = (GameObject)PrefabUtility.InstantiatePrefab(assets.StreamPrefab, root.transform);
            stream.name = "Pour Stream";
            var ribbon = stream.GetComponent<LiquidStreamRibbon>();
            // A full-height fall onto open floor reads thin at this camera distance with the
            // default width tuned for the much shorter faucet/flask-into-container drops, so this
            // scene gets a visibly heavier jet.
            SetPrivate(ribbon, "widthMultiplier", 2f);

            var impact = (GameObject)PrefabUtility.InstantiatePrefab(assets.ImpactPrefab, root.transform);
            impact.name = "Pour Impact FX";
            var impactFX = impact.GetComponent<LiquidImpactFX>();
            // No liquid body on a dry floor for air to entrain into.
            SetPrivate(impactFX, "includeBubbles", false);

            ParticleSystem drips = BuildLipDrips(root.transform, assets.Droplet);
            drips.name = "Flask Lip Drips";

            var controllerObject = new GameObject("Flask Pour Controller");
            controllerObject.transform.SetParent(root.transform);
            var controller = controllerObject.AddComponent<LiquidPourController>();
            SetPrivate(controller, "sourceMode", (int)LiquidPourController.SourceMode.FlaskTilt);
            SetPrivate(controller, "sourceFlask", sourceVolume);
            SetPrivate(controller, "lip", lip.transform);
            SetPrivate(controller, "ribbon", ribbon);
            SetPrivate(controller, "impactFX", impactFX);
            SetPrivate(controller, "lipDrips", drips);
            SetPrivate(controller, "minimumExitSpeed", 0.08f);
            SetPrivate(controller, "maximumExitSpeed", 0.5f);
            // No receiver exists in this scene at all: every drop overflows straight to the spill
            // manager, which is the entire point of an effects-only review scene.
            SetPrivate(controller, "explicitReceiver", null);

            CreateSpillManager(root.transform, assets);

            var rig = camera.gameObject.AddComponent<LiquidFXDemoRig>();
            SetPrivate(rig, "pour", controller);
            SetPrivate(rig, "tiltTarget", pivot.transform);
            SetPrivate(rig, "tiltFlask", sourceVolume);
            SetPrivate(rig, "tiltAxis", Vector3.back);
            SetPrivate(rig, "maximumTilt", 118f);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, LiquidFXPaths.Scenes + "/FlaskFloorSpill.unity");
        }

        // ================================================================== layered pour scene

        /// <summary>
        /// Review scene for the liquid-category/layer system (see SPEC-Camadas.md): a flask
        /// stratified with three immiscible liquids tips into a beaker that already has water in
        /// it. Expected result once poured - oil stacks on top of the beaker's water (different
        /// category, does not mix), more water poured in merges straight into the existing water
        /// layer (same category), and the stream/splash colour visibly changes as the source
        /// empties from its top liquid down to the next.
        /// </summary>
        static void BuildLayeredPourScene(Assets assets)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("Layered Pour");
            ApplyEnvironment();

            const float tableTop = 0.75f;
            Camera camera = CreateCamera(root.transform, new Vector3(0.26f, 1.02f, -0.42f), new Vector3(0f, 0.85f, 0f), 40f);
            CreateLight(root.transform);
            CreateReflectionProbe(root.transform, new Vector3(0f, 0.95f, 0f), new Vector3(1.4f, 0.9f, 1f));

            CreateBox("Floor", root.transform, new Vector3(0f, -0.05f, 0f), new Vector3(6f, 0.1f, 5f), assets.Floor);
            CreateBox("Bench", root.transform, new Vector3(0f, tableTop * 0.5f, 0f), new Vector3(1.4f, tableTop, 0.7f), assets.Table);

            LiquidDefinition water = Load<LiquidDefinition>(LiquidFXPaths.Library + "/Liquids/Liq_Water.asset");
            LiquidDefinition oil = Load<LiquidDefinition>(LiquidFXPaths.Library + "/Liquids/Liq_VegetableOil.asset");
            LiquidDefinition syrup = Load<LiquidDefinition>(LiquidFXPaths.Library + "/Liquids/Liq_Syrup.asset");
            if (water == null || oil == null || syrup == null)
            {
                Debug.LogWarning("LiquidFX: liquid library not found; run Tools/LiquidFX/Build Liquid Library first. The layered pour scene will be empty.");
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, LiquidFXPaths.Scenes + "/LayeredPour.unity");
                return;
            }

            GameObject sourcePrefab = Load<GameObject>(
                "Assets/LiquidVolumePro/Prefabs/ChemistryFlasks/250ml_Erlenmeyer/Prefabs/Erlenmeyer_250ml.prefab");
            GameObject targetPrefab = Load<GameObject>(
                "Assets/LiquidVolumePro/Prefabs/ChemistryFlasks/250ml_Beaker/Prefabs/Beaker_250ml.prefab");

            if (sourcePrefab == null || targetPrefab == null)
            {
                Debug.LogWarning("LiquidFX: LiquidVolumePro chemistry flask prefabs not found; the layered pour scene will be empty.");
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, LiquidFXPaths.Scenes + "/LayeredPour.unity");
                return;
            }

            // ---- receiving beaker: starts with 50 mL of water already in it -----------------
            var target = (GameObject)PrefabUtility.InstantiatePrefab(targetPrefab, root.transform);
            target.name = "Target Beaker 250 mL";
            target.transform.position = new Vector3(0.08f, tableTop, 0f);
            FlaskVolume targetVolume = ConfigureFlask(target, 250f, 0f, out Bounds targetBounds);
            DropToSurface(target.transform, targetBounds, tableTop);
            SetLayerCharges(targetVolume, (water, 50f));

            // ---- pouring erlenmeyer: three stratified liquids, syrup at the bottom -----------
            var source = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab, root.transform);
            source.name = "Source Erlenmeyer 250 mL";
            source.transform.position = new Vector3(-0.09f, tableTop, 0f);
            FlaskVolume sourceVolume = ConfigureFlask(source, 250f, 0f, out Bounds sourceBounds);
            DropToSurface(source.transform, sourceBounds, tableTop);
            SetLayerCharges(sourceVolume, (syrup, 40f), (water, 90f), (oil, 60f));

            // Recompute after the drop so the lip sits on the real rim.
            sourceBounds = WorldBounds(source);
            Vector3 lipWorld = new Vector3(
                sourceBounds.center.x + sourceBounds.extents.x * 0.55f,
                sourceBounds.max.y,
                sourceBounds.center.z);

            var lip = new GameObject("Lip");
            lip.transform.SetParent(source.transform, true);
            lip.transform.position = lipWorld;
            SetPrivate(sourceVolume, "lip", lip.transform);

            var pivot = new GameObject("Pour Pivot");
            pivot.transform.SetParent(root.transform);
            pivot.transform.position = lipWorld;
            source.transform.SetParent(pivot.transform, true);

            Vector3 pourPosition = targetVolume.PortCentreWorld + Vector3.up * 0.1f;
            pivot.transform.position += pourPosition - lipWorld;
            lipWorld = pourPosition;

            // ---- pour rig -----------------------------------------------------------------
            var stream = (GameObject)PrefabUtility.InstantiatePrefab(assets.StreamPrefab, root.transform);
            stream.name = "Pour Stream";
            var ribbon = stream.GetComponent<LiquidStreamRibbon>();

            var impact = (GameObject)PrefabUtility.InstantiatePrefab(assets.ImpactPrefab, root.transform);
            impact.name = "Pour Impact FX";
            var impactFX = impact.GetComponent<LiquidImpactFX>();

            ParticleSystem drips = BuildLipDrips(root.transform, assets.Droplet);
            drips.name = "Flask Lip Drips";

            var controllerObject = new GameObject("Flask Pour Controller");
            controllerObject.transform.SetParent(root.transform);
            var controller = controllerObject.AddComponent<LiquidPourController>();
            SetPrivate(controller, "sourceMode", (int)LiquidPourController.SourceMode.FlaskTilt);
            SetPrivate(controller, "sourceFlask", sourceVolume);
            SetPrivate(controller, "lip", lip.transform);
            SetPrivate(controller, "ribbon", ribbon);
            SetPrivate(controller, "impactFX", impactFX);
            SetPrivate(controller, "lipDrips", drips);
            SetPrivate(controller, "minimumExitSpeed", 0.08f);
            SetPrivate(controller, "maximumExitSpeed", 0.5f);
            SetPrivate(controller, "explicitReceiver", targetVolume);

            CreateSpillManager(root.transform, assets);

            var rig = camera.gameObject.AddComponent<LiquidFXDemoRig>();
            SetPrivate(rig, "pour", controller);
            SetPrivate(rig, "tiltTarget", pivot.transform);
            SetPrivate(rig, "tiltFlask", sourceVolume);
            SetPrivate(rig, "tiltAxis", Vector3.back);
            SetPrivate(rig, "maximumTilt", 118f);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, LiquidFXPaths.Scenes + "/LayeredPour.unity");
        }

        // ================================================================== two flasks, no mix

        /// <summary>
        /// The simplest possible demonstration of the category system: one flask of water, one
        /// flask of oil, nothing else going on. Tip the source into the target and the two
        /// liquids stack into two clean bands instead of blending - because their categories carry
        /// different stacking densities (see LiquidCategory), not because of any special-case code
        /// for "these two happen not to mix". Meant as the reference scene to hand another
        /// programmer: no test props, no multi-layer stack, just the one behaviour that matters.
        /// </summary>
        static void BuildTwoFlasksNoMixScene(Assets assets)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("Two Flasks No Mix");
            ApplyEnvironment();

            const float tableTop = 0.75f;
            Camera camera = CreateCamera(root.transform, new Vector3(0.26f, 1.02f, -0.42f), new Vector3(0f, 0.85f, 0f), 40f);
            CreateLight(root.transform);
            CreateReflectionProbe(root.transform, new Vector3(0f, 0.95f, 0f), new Vector3(1.4f, 0.9f, 1f));

            CreateBox("Floor", root.transform, new Vector3(0f, -0.05f, 0f), new Vector3(6f, 0.1f, 5f), assets.Floor);
            CreateBox("Bench", root.transform, new Vector3(0f, tableTop * 0.5f, 0f), new Vector3(1.4f, tableTop, 0.7f), assets.Table);

            LiquidDefinition water = Load<LiquidDefinition>(LiquidFXPaths.Library + "/Liquids/Liq_Water.asset");
            LiquidDefinition oil = Load<LiquidDefinition>(LiquidFXPaths.Library + "/Liquids/Liq_VegetableOil.asset");
            if (water == null || oil == null)
            {
                Debug.LogWarning("LiquidFX: liquid library not found; run Tools/LiquidFX/Build Liquid Library first. The two-flasks scene will be empty.");
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, LiquidFXPaths.Scenes + "/TwoFlasksNoMix.unity");
                return;
            }

            GameObject sourcePrefab = Load<GameObject>(
                "Assets/LiquidVolumePro/Prefabs/ChemistryFlasks/250ml_Erlenmeyer/Prefabs/Erlenmeyer_250ml.prefab");
            GameObject targetPrefab = Load<GameObject>(
                "Assets/LiquidVolumePro/Prefabs/ChemistryFlasks/250ml_Beaker/Prefabs/Beaker_250ml.prefab");

            if (sourcePrefab == null || targetPrefab == null)
            {
                Debug.LogWarning("LiquidFX: LiquidVolumePro chemistry flask prefabs not found; the two-flasks scene will be empty.");
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, LiquidFXPaths.Scenes + "/TwoFlasksNoMix.unity");
                return;
            }

            // ---- target: starts with water --------------------------------------------------
            var target = (GameObject)PrefabUtility.InstantiatePrefab(targetPrefab, root.transform);
            target.name = "Water Beaker 250 mL";
            target.transform.position = new Vector3(0.08f, tableTop, 0f);
            FlaskVolume targetVolume = ConfigureFlask(target, 250f, 0f, out Bounds targetBounds);
            DropToSurface(target.transform, targetBounds, tableTop);
            SetLayerCharges(targetVolume, (water, 120f));

            // ---- source: pure oil, poured on top ---------------------------------------------
            var source = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab, root.transform);
            source.name = "Oil Erlenmeyer 250 mL";
            source.transform.position = new Vector3(-0.09f, tableTop, 0f);
            FlaskVolume sourceVolume = ConfigureFlask(source, 250f, 0f, out Bounds sourceBounds);
            DropToSurface(source.transform, sourceBounds, tableTop);
            SetLayerCharges(sourceVolume, (oil, 120f));

            // Recompute after the drop so the lip sits on the real rim.
            sourceBounds = WorldBounds(source);
            Vector3 lipWorld = new Vector3(
                sourceBounds.center.x + sourceBounds.extents.x * 0.55f,
                sourceBounds.max.y,
                sourceBounds.center.z);

            var lip = new GameObject("Lip");
            lip.transform.SetParent(source.transform, true);
            lip.transform.position = lipWorld;
            SetPrivate(sourceVolume, "lip", lip.transform);

            var pivot = new GameObject("Pour Pivot");
            pivot.transform.SetParent(root.transform);
            pivot.transform.position = lipWorld;
            source.transform.SetParent(pivot.transform, true);

            Vector3 pourPosition = targetVolume.PortCentreWorld + Vector3.up * 0.1f;
            pivot.transform.position += pourPosition - lipWorld;
            lipWorld = pourPosition;

            // ---- pour rig -----------------------------------------------------------------
            var stream = (GameObject)PrefabUtility.InstantiatePrefab(assets.StreamPrefab, root.transform);
            stream.name = "Pour Stream";
            var ribbon = stream.GetComponent<LiquidStreamRibbon>();

            var impact = (GameObject)PrefabUtility.InstantiatePrefab(assets.ImpactPrefab, root.transform);
            impact.name = "Pour Impact FX";
            var impactFX = impact.GetComponent<LiquidImpactFX>();

            ParticleSystem drips = BuildLipDrips(root.transform, assets.Droplet);
            drips.name = "Flask Lip Drips";

            var controllerObject = new GameObject("Flask Pour Controller");
            controllerObject.transform.SetParent(root.transform);
            var controller = controllerObject.AddComponent<LiquidPourController>();
            SetPrivate(controller, "sourceMode", (int)LiquidPourController.SourceMode.FlaskTilt);
            SetPrivate(controller, "sourceFlask", sourceVolume);
            SetPrivate(controller, "lip", lip.transform);
            SetPrivate(controller, "ribbon", ribbon);
            SetPrivate(controller, "impactFX", impactFX);
            SetPrivate(controller, "lipDrips", drips);
            SetPrivate(controller, "minimumExitSpeed", 0.08f);
            SetPrivate(controller, "maximumExitSpeed", 0.5f);
            SetPrivate(controller, "explicitReceiver", targetVolume);

            CreateSpillManager(root.transform, assets);

            var rig = camera.gameObject.AddComponent<LiquidFXDemoRig>();
            SetPrivate(rig, "pour", controller);
            SetPrivate(rig, "tiltTarget", pivot.transform);
            SetPrivate(rig, "tiltFlask", sourceVolume);
            SetPrivate(rig, "tiltAxis", Vector3.back);
            SetPrivate(rig, "maximumTilt", 118f);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, LiquidFXPaths.Scenes + "/TwoFlasksNoMix.unity");
        }

        /// <summary>
        /// Writes a set of (liquid, millilitres) charges into a FlaskVolume's initialContents list
        /// and bakes them immediately, so the scene shows the real stacked result rather than an
        /// empty flask that only populates once played.
        /// </summary>
        static void SetLayerCharges(FlaskVolume flask, params (LiquidDefinition liquid, float millilitres)[] charges)
        {
            var serialized = new SerializedObject(flask);
            SerializedProperty list = serialized.FindProperty("initialContents");
            list.ClearArray();
            for (int i = 0; i < charges.Length; i++)
            {
                list.InsertArrayElementAtIndex(i);
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("liquid").objectReferenceValue = charges[i].liquid;
                element.FindPropertyRelative("millilitres").floatValue = charges[i].millilitres;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();

            flask.BakeInitialContents();
            EditorUtility.SetDirty(flask);
        }

        static FlaskVolume ConfigureFlask(GameObject flask, float capacityML, float contentsML, out Bounds bounds)
        {
            bounds = WorldBounds(flask);

            var liquidVolume = flask.GetComponentInChildren<LiquidVolume>();
            GameObject host = liquidVolume != null ? liquidVolume.gameObject : flask;

            var volume = host.GetComponent<FlaskVolume>();
            if (volume == null)
                volume = host.AddComponent<FlaskVolume>();

            SetPrivate(volume, "capacityML", capacityML);

            // The mouth sits at the top of the mesh; the radius is a bit under the rim so a stream
            // grazing the edge counts as a miss and spills, which is the behaviour we want.
            Bounds localBounds = LocalBounds(host);
            SetPrivate(volume, "portLocalOffset", new Vector3(localBounds.center.x, localBounds.max.y, localBounds.center.z));
            SetPrivate(volume, "portRadius", Mathf.Max(localBounds.extents.x, localBounds.extents.z) * 0.7f);

            volume.SetContentsML(contentsML);
            EditorUtility.SetDirty(volume);
            return volume;
        }

        static Bounds WorldBounds(GameObject gameObject)
        {
            var renderers = gameObject.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return new Bounds(gameObject.transform.position, Vector3.one * 0.1f);

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        static Bounds LocalBounds(GameObject gameObject)
        {
            var filter = gameObject.GetComponentInChildren<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
                return filter.sharedMesh.bounds;

            return new Bounds(Vector3.zero, Vector3.one * 0.1f);
        }

        static void DropToSurface(Transform transform, Bounds bounds, float surfaceY)
        {
            float offset = surfaceY - bounds.min.y;
            transform.position += new Vector3(0f, offset, 0f);
        }

        static void CreateSpillManager(Transform parent, Assets assets)
        {
            var spillObject = new GameObject("Liquid Spill Manager");
            spillObject.transform.SetParent(parent);
            var manager = spillObject.AddComponent<LiquidSpillManager>();
            SetPrivate(manager, "puddlePrefab", assets.PuddlePrefab.GetComponent<LiquidSpillPuddle>());
            SetPrivate(manager, "poolSize", 4);
        }

        // ================================================================== scene helpers

        static Camera CreateCamera(Transform parent, Vector3 position, Vector3 target, float fieldOfView)
        {
            var gameObject = new GameObject("Main Camera") { tag = "MainCamera" };
            gameObject.transform.SetParent(parent);
            gameObject.transform.position = position;
            gameObject.transform.LookAt(target);

            Camera camera = gameObject.AddComponent<Camera>();
            camera.fieldOfView = fieldOfView;
            camera.nearClipPlane = 0.02f;
            camera.farClipPlane = 60f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.02f, 0.03f, 0.045f, 1f);
            camera.allowHDR = false;

            // The liquid shaders read both the opaque colour and the depth of the scene. Forcing
            // them on per camera means these scenes work even in a project whose URP asset has
            // them switched off.
            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            data.requiresColorOption = CameraOverrideOption.On;
            data.requiresDepthOption = CameraOverrideOption.On;

            gameObject.AddComponent<AudioListener>();
            return camera;
        }

        /// <summary>
        /// Empty scenes come with no skybox at all, which leaves the reflection probe and the
        /// ambient term black and makes every liquid look like ink.
        /// </summary>
        static void ApplyEnvironment()
        {
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.32f, 0.36f, 0.42f);
            RenderSettings.ambientEquatorColor = new Color(0.2f, 0.22f, 0.26f);
            RenderSettings.ambientGroundColor = new Color(0.08f, 0.08f, 0.09f);
            RenderSettings.fog = false;
        }

        static void CreateLight(Transform parent)
        {
            var gameObject = new GameObject("Directional Light");
            gameObject.transform.SetParent(parent);
            gameObject.transform.rotation = Quaternion.Euler(52f, -35f, 0f);
            Light light = gameObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.35f;
            light.color = new Color(1f, 0.97f, 0.92f);
            // A tabletop scene inside URP's default 50 m shadow distance gets huge cascade texels,
            // which shows up as striped shadow acne all over the ceramic. Ambient plus the
            // reflection probe carry the lighting here, so the directional light casts nothing.
            light.shadows = LightShadows.None;
        }

        static void CreateReflectionProbe(Transform parent, Vector3 position, Vector3 size)
        {
            var gameObject = new GameObject("Reflection Probe (64 px, On Awake)");
            gameObject.transform.SetParent(parent);
            gameObject.transform.position = position;

            ReflectionProbe probe = gameObject.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.OnAwake;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            probe.resolution = 64;
            probe.size = size;
            probe.boxProjection = true;
            probe.intensity = 1f;
            probe.hdr = false;
            probe.renderDynamicObjects = false;
            probe.nearClipPlane = 0.05f;
            probe.farClipPlane = 8f;
        }

        /// <summary>An empty transform used purely to keep the hierarchy panel organised.</summary>
        static Transform CreateGroup(string name, Transform parent)
        {
            var gameObject = new GameObject(name);
            gameObject.transform.SetParent(parent);
            gameObject.transform.localPosition = Vector3.zero;
            gameObject.transform.localRotation = Quaternion.identity;
            gameObject.transform.localScale = Vector3.one;
            return gameObject.transform;
        }

        static GameObject CreateBox(string name, Transform parent, Vector3 position, Vector3 size, Material material)
        {
            GameObject gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gameObject.name = name;
            gameObject.transform.SetParent(parent);
            gameObject.transform.position = position;
            gameObject.transform.localScale = size;
            gameObject.GetComponent<Renderer>().sharedMaterial = material;
            return gameObject;
        }

        static GameObject CreateCylinder(string name, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            GameObject gameObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            gameObject.name = name;
            gameObject.transform.SetParent(parent);
            gameObject.transform.position = position;
            gameObject.transform.localScale = scale;
            gameObject.GetComponent<Renderer>().sharedMaterial = material;
            Object.DestroyImmediate(gameObject.GetComponent<Collider>());
            return gameObject;
        }

        static GameObject CreateCylinderBetween(string name, Transform parent, Vector3 from, Vector3 to, float radius, Material material)
        {
            Vector3 delta = to - from;
            GameObject cylinder = CreateCylinder(name, parent, (from + to) * 0.5f,
                new Vector3(radius, Mathf.Max(0.001f, delta.magnitude * 0.5f), radius), material);
            if (delta.sqrMagnitude > 0.0000001f)
                cylinder.transform.up = delta.normalized;
            return cylinder;
        }

        static GameObject CreateSphere(string name, Transform parent, Vector3 position, float radius, Material material, bool keepCollider)
        {
            GameObject gameObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            gameObject.name = name;
            gameObject.transform.SetParent(parent);
            gameObject.transform.position = position;
            gameObject.transform.localScale = Vector3.one * (radius * 2f);
            gameObject.GetComponent<Renderer>().sharedMaterial = material;
            if (!keepCollider)
                Object.DestroyImmediate(gameObject.GetComponent<Collider>());
            return gameObject;
        }

        /// <summary>A physics sphere wired to <see cref="LiquidBuoyancy"/> at a given density.</summary>
        static GameObject BuildBuoyantSphere(
            string name, Transform parent, Vector3 position, float radius, Material material,
            float density, LiquidSurface surface)
        {
            GameObject sphere = CreateSphere(name, parent, position, radius, material, true);

            Rigidbody body = sphere.AddComponent<Rigidbody>();
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            LiquidBuoyancy buoyancy = sphere.AddComponent<LiquidBuoyancy>();
            SetPrivate(buoyancy, "surface", surface);
            SetPrivate(buoyancy, "volumeCollider", sphere.GetComponent<Collider>());
            SetPrivate(buoyancy, "density", density);

            return sphere;
        }

        static GameObject CreateMeshObject(string name, Transform parent, Mesh mesh, Material material)
        {
            var gameObject = new GameObject(name);
            gameObject.transform.SetParent(parent);
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            return gameObject;
        }

        /// <summary>
        /// Writes a private serialised field. The runtime components keep their fields private on
        /// purpose; this is the builder reaching in, not an API anyone else should use.
        /// </summary>
        static void SetPrivate(Object target, string fieldName, object value)
        {
            if (target == null)
                return;

            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(fieldName);
            if (property == null)
            {
                Debug.LogWarning($"LiquidFX: field '{fieldName}' not found on {target.GetType().Name}.");
                return;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Float:
                    property.floatValue = System.Convert.ToSingle(value);
                    break;
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Enum:
                    property.intValue = System.Convert.ToInt32(value);
                    break;
                case SerializedPropertyType.Boolean:
                    property.boolValue = System.Convert.ToBoolean(value);
                    break;
                case SerializedPropertyType.Color:
                    property.colorValue = (Color)value;
                    break;
                case SerializedPropertyType.Vector3:
                    property.vector3Value = (Vector3)value;
                    break;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = value as Object;
                    break;
                default:
                    Debug.LogWarning($"LiquidFX: unsupported field type for '{fieldName}'.");
                    return;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
