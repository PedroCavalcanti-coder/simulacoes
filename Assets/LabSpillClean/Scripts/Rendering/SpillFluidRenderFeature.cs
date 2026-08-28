using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace LabSpill.Rendering
{
    /// <summary>
    /// Reconstrucao screen-space da superficie do PBD (Screen-Space Fluid).
    ///
    /// Este renderer le somente os buffers publicados no FluidRenderBridge;
    /// ele nunca altera a simulacao. E, deliberadamente, ele NAO decide como
    /// o liquido aparece: nao ha cor, luz, sombra ou transparencia aqui.
    ///
    /// O que esta feature faz e' puramente geometrico:
    ///   1) rasteriza as particulas como esferoides e captura a profundidade
    ///      mais proxima por pixel (splat de profundidade);
    ///   2) suaviza essa profundidade (curvature flow separavel, com peso
    ///      por diferenca de profundidade) ate parecer uma superficie continua;
    ///   3) reconstroi a normal por pixel a partir da profundidade suavizada
    ///      e resolve oclusao contra a profundidade real da cena;
    ///   4) publica tudo (_PBDFluidSurfaceDepth, _PBDFluidSurfaceNormal e as
    ///      matrizes de reconstrucao) como globals para o material real do
    ///      liquido consumir.
    ///
    /// Quem pinta o liquido -- cor rasa/funda, absorcao, fresnel, especular,
    /// blend/transparencia, sombra recebida e projetada -- e' o material
    /// atribuido a malha (ou shader graph) do liquido, desenhado normalmente
    /// pelo pipeline do URP com luz e sombra de verdade (Forward+, shadow
    /// maps, light probes). Esta feature apenas fornece a geometria.
    /// </summary>
    public sealed class SpillFluidRenderFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public class Settings
        {
            [Tooltip("Unica fonte de raio visual, blur e normal.")]
            public SpillVisualSettings visual;
            public RenderPassEvent passEvent = RenderPassEvent.BeforeRenderingTransparents;

        }

        public Settings settings = new Settings();

        Material m_depthMat;
        Material m_blurXMat;
        Material m_blurYMat;
        Material m_normalMat;
        Mesh m_splatMesh;
        SSFPass m_pass;
        GraphicsFormat m_eyeThicknessFormat = GraphicsFormat.None;
        readonly List<SpillRenderBridge.Entry> m_entriesToRender = new List<SpillRenderBridge.Entry>();
        readonly Dictionary<SpillRenderBridge.Entry, SurfaceTargets> m_targets =
            new Dictionary<SpillRenderBridge.Entry, SurfaceTargets>();
        readonly List<SpillRenderBridge.Entry> m_staleTargets = new List<SpillRenderBridge.Entry>();

        sealed class SurfaceTargets
        {
            public RTHandle depth;
            public RTHandle normal;
            public RTHandle substance;
            public ComputeBuffer splatArgs;
            public int width;
            public int height;
            int m_argsCount = -1;
            readonly uint[] m_argsData = new uint[5];

            public void EnsureSplatArgs(Mesh mesh, int count)
            {
                if (mesh == null || count <= 0) return;
                if (splatArgs == null)
                    splatArgs = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
                if (m_argsCount == count) return;

                m_argsData[0] = mesh.GetIndexCount(0);
                m_argsData[1] = (uint)count;
                m_argsData[2] = mesh.GetIndexStart(0);
                m_argsData[3] = (uint)mesh.GetBaseVertex(0);
                m_argsData[4] = 0;
                splatArgs.SetData(m_argsData);
                m_argsCount = count;
            }

            public void Release()
            {
                depth?.Release();
                normal?.Release();
                substance?.Release();
                splatArgs?.Release();
                splatArgs = null;
                m_argsCount = -1;
                depth = normal = substance = null;
            }
        }

        SpillVisualSettings Visual => settings.visual;
        float EffectiveResolutionScale => Visual != null ? Visual.resolutionScale : 0.66f;
        float EffectiveParticleScale => Visual != null ? Visual.visualRadiusScale : 3.5f;
        float EffectiveBlurRadius => Visual != null ? Visual.blurRadius : 3.2f;
        int EffectiveBlurIterations => Visual != null ? Visual.blurIterations : 1;
        float EffectiveNormalRadius => Visual != null ? Visual.normalRadius : 1.5f;
        float EffectiveDepthFalloff => Visual != null ? Visual.depthFalloff : 0.45f;
        float EffectiveSurfaceTension => Visual != null ? Visual.surfaceTension : 0.55f;

        public override void Create()
        {
            ReleaseTargets();
            m_eyeThicknessFormat = ChooseEyeThicknessFormat();
            if (m_eyeThicknessFormat == GraphicsFormat.None)
                Debug.LogError("[LabSpill] A GPU nao suporta um formato float filtravel e blendavel para depth + thickness SSF.", this);
            CoreUtils.Destroy(m_splatMesh);
            m_splatMesh = CreateSplatMesh();
            Shader depthShader = Shader.Find("Hidden/PBDFluid/SSFDepth");
            Shader blurShader = Shader.Find("Hidden/PBDFluid/SSFBlur");
            Shader normalShader = Shader.Find("Hidden/PBDFluid/SSFNormal");

            CoreUtils.Destroy(m_depthMat);
            CoreUtils.Destroy(m_blurXMat);
            CoreUtils.Destroy(m_blurYMat);
            CoreUtils.Destroy(m_normalMat);

            if (depthShader != null)
            {
                m_depthMat = CoreUtils.CreateEngineMaterial(depthShader);
                m_depthMat.enableInstancing = true;
                // O attachment privado e' limpo em profundidade de hardware. Em
                // reversed-Z, o fragmento mais proximo tem valor maior.
                m_depthMat.SetInt(ShaderIDs.FluidZTest, (int)(SystemInfo.usesReversedZBuffer
                    ? CompareFunction.GreaterEqual
                    : CompareFunction.LessEqual));
            }
            if (blurShader != null)
            {
                m_blurXMat = CoreUtils.CreateEngineMaterial(blurShader);
                m_blurYMat = CoreUtils.CreateEngineMaterial(blurShader);
                m_blurXMat.SetVector(ShaderIDs.Direction, Vector2.right);
                m_blurYMat.SetVector(ShaderIDs.Direction, Vector2.up);
            }
            if (normalShader != null) m_normalMat = CoreUtils.CreateEngineMaterial(normalShader);

            m_pass = new SSFPass(this, m_depthMat, m_blurXMat, m_blurYMat, m_normalMat)
            {
                renderPassEvent = settings.passEvent
            };
        }

        static bool SupportsEyeThicknessFormat(GraphicsFormat format)
        {
            return SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render) &&
                   SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample) &&
                   SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Blend);
        }

        // Quatro canais, nao dois: R profundidade, G espessura, B substancia. A
        // substancia precisa atravessar o blur junto da profundidade - num alvo
        // separado, a borda que o filtro espalha ficaria com o valor de limpeza e
        // seria pintada com a aparencia do liquido 0.
        static GraphicsFormat ChooseEyeThicknessFormat()
        {
            if (SupportsEyeThicknessFormat(GraphicsFormat.R32G32B32A32_SFloat))
                return GraphicsFormat.R32G32B32A32_SFloat;
            return GraphicsFormat.None;
        }

        static Mesh CreateSplatMesh()
        {
            var mesh = new Mesh
            {
                name = "PBD SSF Analytic Splat",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(-1f, -1f, 0f),
                    new Vector3(-1f,  1f, 0f),
                    new Vector3( 1f,  1f, 0f),
                    new Vector3( 1f, -1f, 0f)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
                bounds = new Bounds(Vector3.zero, Vector3.one * 2f)
            };
            mesh.UploadMeshData(true);
            return mesh;
        }

        static bool IsValidEntry(SpillRenderBridge.Entry entry)
        {
            return entry != null && entry.Positions != null && entry.Count > 0;
        }

        void CollectEntries()
        {
            m_entriesToRender.Clear();
            var entries = SpillRenderBridge.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!IsValidEntry(entry)) continue;
                m_entriesToRender.Add(entry);
            }
        }

        SurfaceTargets GetTargets(SpillRenderBridge.Entry entry, int width, int height)
        {
            if (!m_targets.TryGetValue(entry, out var targets))
            {
                targets = new SurfaceTargets();
                m_targets.Add(entry, targets);
            }
            if (targets.depth != null && targets.width == width && targets.height == height)
            {
                targets.EnsureSplatArgs(m_splatMesh, entry.Count);
                return targets;
            }

            targets.Release();
            targets.width = width;
            targets.height = height;
            int id = entry.GetHashCode();
            targets.depth = RTHandles.Alloc(width, height,
                colorFormat: GraphicsFormat.R32_SFloat,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Clamp,
                name: $"_PBDFluidSurfaceDepth_{id}");
            targets.normal = RTHandles.Alloc(width, height,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Clamp,
                name: $"_PBDFluidSurfaceNormal_{id}");
            // Um canal de 8 bits comporta as 8 substancias com folga. Point filter:
            // um indice interpolado misturaria identidades e pintaria a fronteira
            // entre dois liquidos com a aparencia de um terceiro.
            targets.substance = RTHandles.Alloc(width, height,
                colorFormat: GraphicsFormat.R8_UNorm,
                filterMode: FilterMode.Point,
                wrapMode: TextureWrapMode.Clamp,
                name: $"_PBDFluidSurfaceSubstance_{id}");
            targets.EnsureSplatArgs(m_splatMesh, entry.Count);
            return targets;
        }

        void BindTargets(SpillRenderBridge.Entry entry, SurfaceTargets targets)
        {
            entry.SurfaceDepth = targets.depth.rt;
            entry.SurfaceNormal = targets.normal.rt;
            entry.SurfaceSubstance = targets.substance.rt;
            if (entry.SurfaceRenderer == null) return;
            if (entry.SurfaceProperties == null) entry.SurfaceProperties = new MaterialPropertyBlock();
            entry.SurfaceRenderer.GetPropertyBlock(entry.SurfaceProperties);
            entry.SurfaceProperties.SetTexture(ShaderIDs.SurfaceDepth, entry.SurfaceDepth);
            entry.SurfaceProperties.SetTexture(ShaderIDs.SurfaceNormal, entry.SurfaceNormal);
            entry.SurfaceProperties.SetTexture(ShaderIDs.SurfaceSubstance, entry.SurfaceSubstance);
            entry.SurfaceRenderer.SetPropertyBlock(entry.SurfaceProperties);
        }

        void PruneTargets()
        {
            m_staleTargets.Clear();
            foreach (var pair in m_targets)
                if (!SpillRenderBridge.Entries.Contains(pair.Key)) m_staleTargets.Add(pair.Key);
            for (int i = 0; i < m_staleTargets.Count; i++)
            {
                var entry = m_staleTargets[i];
                m_targets[entry].Release();
                m_targets.Remove(entry);
            }
        }

        void ReleaseTargets()
        {
            foreach (var pair in m_targets) pair.Value.Release();
            m_targets.Clear();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            PruneTargets();
            if (m_depthMat == null || m_blurXMat == null || m_blurYMat == null ||
                m_normalMat == null || m_splatMesh == null || m_depthMat.passCount < 2 ||
                m_eyeThicknessFormat == GraphicsFormat.None) return;

            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType != CameraType.Game && cameraType != CameraType.SceneView) return;
            // Em Play, a Game Camera e' a autoridade. Evita realocar os alvos
            // persistentes entre resolucoes diferentes da Game e Scene View.
            if (cameraType == CameraType.SceneView && Application.isPlaying) return;

            CollectEntries();
            if (m_entriesToRender.Count == 0) return;

            // Sempre precisamos da profundidade real da cena para resolver
            // oclusao (liquido atras de geometria opaca). So' pedimos a cor
            // da camera Ã¢â‚¬â€ e o intermediate texture que isso exige Ã¢â‚¬â€ quando a
            // visualizacao de diagnostico esta ligada. Em producao esta
            // feature nunca escreve na imagem final, entao nao ha motivo
            // para pagar esse custo.
            m_pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            m_pass.requiresIntermediateTexture = false;

            m_pass.renderPassEvent = settings.passEvent;
            m_pass.SetEntries(m_entriesToRender);
            renderer.EnqueuePass(m_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(m_depthMat);
            CoreUtils.Destroy(m_blurXMat);
            CoreUtils.Destroy(m_blurYMat);
            CoreUtils.Destroy(m_normalMat);
            CoreUtils.Destroy(m_splatMesh);
            m_splatMesh = null;
            ReleaseTargets();
        }

        static class ShaderIDs
        {
            public static readonly int FluidZTest = Shader.PropertyToID("_FluidZTest");
            public static readonly int Direction = Shader.PropertyToID("_Direction");
            public static readonly int BlurRadius = Shader.PropertyToID("_Radius");
            public static readonly int DepthFalloff = Shader.PropertyToID("_DepthFalloff");
            public static readonly int SurfaceTension = Shader.PropertyToID("_SurfaceTension");
            public static readonly int WorldRadius = Shader.PropertyToID("_PBDFluidWorldRadius");
            public static readonly int ProjectionScaleY = Shader.PropertyToID("_PBDFluidProjectionScaleY");
            public static readonly int Orthographic = Shader.PropertyToID("_PBDFluidOrthographic");
            // O blur existente usa o nome historico _TexelSize. A reconstrucao
            // e o material publico usam _PBDFluidTexelSize. Nao unificar estes
            // IDs: isso transformaria todos os taps do blur no pixel central.
            public static readonly int BlurTexelSize = Shader.PropertyToID("_TexelSize");
            public static readonly int SurfaceTexelSize = Shader.PropertyToID("_PBDFluidTexelSize");
            public static readonly int InvProjection = Shader.PropertyToID("_PBDFluidInvProjection");
            public static readonly int CameraToWorld = Shader.PropertyToID("_PBDFluidCameraToWorld");
            public static readonly int RenderCameraPosition =
                Shader.PropertyToID("_PBDFluidRenderCameraPosition");
            public static readonly int SurfaceDepth = Shader.PropertyToID("_PBDFluidSurfaceDepth");
            public static readonly int SurfaceNormal = Shader.PropertyToID("_PBDFluidSurfaceNormal");
            public static readonly int SceneDepth = Shader.PropertyToID("_PBDFluidSceneDepth");
            public static readonly int HasSceneDepth = Shader.PropertyToID("_PBDFluidHasSceneDepth");
            public static readonly int NormalRadius = Shader.PropertyToID("_PBDFluidNormalRadius");
            public static readonly int Positions = Shader.PropertyToID("_Positions");
            public static readonly int SubstanceIds = Shader.PropertyToID("_SubstanceIds");
            public static readonly int SurfaceSubstance =
                Shader.PropertyToID("_PBDFluidSurfaceSubstance");
            public static readonly int Scale = Shader.PropertyToID("_Scale");
        }

        class SSFPass : ScriptableRenderPass
        {
            readonly SpillFluidRenderFeature m_owner;
            readonly Material m_depthMat;
            readonly Material m_blurXMat;
            readonly Material m_blurYMat;
            readonly Material m_normalMat;
            readonly List<SpillRenderBridge.Entry> m_entries = new List<SpillRenderBridge.Entry>();

            public SSFPass(SpillFluidRenderFeature owner, Material depth, Material blurX,
                Material blurY, Material normal)
            {
                m_owner = owner;
                m_depthMat = depth;
                m_blurXMat = blurX;
                m_blurYMat = blurY;
                m_normalMat = normal;
            }

            public void SetEntries(List<SpillRenderBridge.Entry> entries)
            {
                m_entries.Clear();
                m_entries.AddRange(entries);
            }

            class DepthData
            {
                public Material mat;
                public Mesh mesh;
                public ComputeBuffer args;
                public ComputeBuffer positions;
                public ComputeBuffer substanceIds;
                public float scale;
            }

            class NormalData
            {
                public Material mat;
                public TextureHandle smoothedEye;
                public bool hasSceneDepth;
                public TextureHandle sceneDepth;
                public Matrix4x4 invProjection;
                public Matrix4x4 cameraToWorld;
                public Vector3 cameraPosition;
                public Vector4 texelSize;
                public float normalRadius;
                public float worldRadius;
                public float projectionScaleY;
                public float orthographic;
            }

            static TextureDesc EyeTextureDesc(int width, int height, string name,
                GraphicsFormat format)
            {
                return new TextureDesc(width, height)
                {
                    format = format,
                    filterMode = FilterMode.Bilinear,
                    msaaSamples = MSAASamples.None,
                    name = name,
                    clearBuffer = true,
                    // Zero e' o sentinela de "sem liquido neste pixel". Alem de
                    // ser a limpeza nativa mais barata, evita que backends
                    // restrinjam um clear muito alto e o confundam com uma
                    // profundidade valida.
                    clearColor = Color.clear
                };
            }

            static TextureDesc NormalTextureDesc(int width, int height, string name)
            {
                return new TextureDesc(width, height)
                {
                    format = GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode = FilterMode.Bilinear,
                    msaaSamples = MSAASamples.None,
                    name = name,
                    clearBuffer = true,
                    clearColor = Color.clear
                };
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (m_entries.Count == 0) return;
                var resources = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var cameraDesc = cameraData.cameraTargetDescriptor;
                float resolutionScale = Mathf.Clamp(
                    m_owner.EffectiveResolutionScale, 0.5f, 1f);
                int width = Mathf.Max(8, Mathf.RoundToInt(cameraDesc.width * resolutionScale));
                int height = Mathf.Max(8, Mathf.RoundToInt(cameraDesc.height * resolutionScale));
                Vector4 texelSize = new Vector4(1f / width, 1f / height, width, height);
                for (int i = 0; i < m_entries.Count; i++)
                {
                    var entry = m_entries[i];
                    if (entry == null) continue;
                    SurfaceTargets targets = m_owner.GetTargets(entry, width, height);
                    m_owner.BindTargets(entry, targets);
                    RecordEntry(renderGraph, resources, cameraData, entry, targets, i,
                        width, height, texelSize);
                }
            }

            void RecordEntry(RenderGraph renderGraph, UniversalResourceData resources,
                UniversalCameraData cameraData, SpillRenderBridge.Entry entry, SurfaceTargets targets,
                int entryIndex, int width, int height, Vector4 texelSize)
            {
                string suffix = entryIndex.ToString();
                TextureHandle eye = renderGraph.CreateTexture(EyeTextureDesc(width, height,
                    "_PBDFluidEyeRaw_" + suffix, m_owner.m_eyeThicknessFormat));
                TextureHandle privateDepth = renderGraph.CreateTexture(new TextureDesc(width, height)
                {
                    depthBufferBits = DepthBits.Depth32,
                    msaaSamples = MSAASamples.None,
                    name = "_PBDFluidDepth_" + suffix,
                    clearBuffer = true
                });


                using (var builder = renderGraph.AddRasterRenderPass<DepthData>(
                    "PBD SSF Depth " + suffix, out var passData))
                {
                    passData.mat = m_depthMat;
                    passData.mesh = m_owner.m_splatMesh;
                    passData.args = targets.splatArgs;
                    passData.positions = entry.Positions;
                    passData.substanceIds = entry.SubstanceIds;
                    passData.scale = entry.Radius * 2f * m_owner.EffectiveParticleScale;
                    builder.SetRenderAttachment(eye, 0, AccessFlags.WriteAll);
                    builder.SetRenderAttachmentDepth(privateDepth, AccessFlags.WriteAll);
                    builder.SetRenderFunc((DepthData data, RasterGraphContext context) =>
                    {
                        context.cmd.ClearRenderTarget(RTClearFlags.All, Color.clear,
                            SystemInfo.usesReversedZBuffer ? 0f : 1f, 0);
                        data.mat.SetBuffer(ShaderIDs.Positions, data.positions);
                        if (data.substanceIds != null)
                            data.mat.SetBuffer(ShaderIDs.SubstanceIds, data.substanceIds);
                        data.mat.SetFloat(ShaderIDs.Scale, data.scale);
                        context.cmd.DrawMeshInstancedIndirect(data.mesh, 0, data.mat, 0, data.args);
                    });
                }

                // O segundo pass preserva R (eye depth) e soma, em G, a corda
                // atravessada em todas as particulas deste liquido. Manter o
                // mesmo depth attachment permite que o RenderGraph funda os
                // raster passes quando o backend suportar native render passes.
                using (var builder = renderGraph.AddRasterRenderPass<DepthData>(
                    "PBD SSF Thickness " + suffix, out var passData))
                {
                    passData.mat = m_depthMat;
                    passData.mesh = m_owner.m_splatMesh;
                    passData.args = targets.splatArgs;
                    passData.positions = entry.Positions;
                    passData.substanceIds = entry.SubstanceIds;
                    passData.scale = entry.Radius * 2f * m_owner.EffectiveParticleScale;
                    builder.SetRenderAttachment(eye, 0, AccessFlags.ReadWrite);
                    builder.SetRenderAttachmentDepth(privateDepth, AccessFlags.Read);
                    builder.SetRenderFunc((DepthData data, RasterGraphContext context) =>
                    {
                        data.mat.SetBuffer(ShaderIDs.Positions, data.positions);
                        if (data.substanceIds != null)
                            data.mat.SetBuffer(ShaderIDs.SubstanceIds, data.substanceIds);
                        data.mat.SetFloat(ShaderIDs.Scale, data.scale);
                        context.cmd.DrawMeshInstancedIndirect(data.mesh, 0, data.mat, 1, data.args);
                    });
                }

                Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(cameraData.camera.projectionMatrix, true);
                float worldRadius = Mathf.Max(1e-4f,
                    entry.Radius * m_owner.EffectiveParticleScale);
                float projectionScaleY = Mathf.Abs(gpuProjection.m11);
                float orthographic = cameraData.camera.orthographic ? 1f : 0f;

                m_blurXMat.SetVector(ShaderIDs.BlurTexelSize, texelSize);
                m_blurXMat.SetFloat(ShaderIDs.BlurRadius, m_owner.EffectiveBlurRadius);
                m_blurXMat.SetFloat(ShaderIDs.DepthFalloff, m_owner.EffectiveDepthFalloff);
                m_blurXMat.SetFloat(ShaderIDs.SurfaceTension, m_owner.EffectiveSurfaceTension);
                m_blurXMat.SetFloat(ShaderIDs.WorldRadius, worldRadius);
                m_blurXMat.SetFloat(ShaderIDs.ProjectionScaleY, projectionScaleY);
                m_blurXMat.SetFloat(ShaderIDs.Orthographic, orthographic);
                m_blurYMat.SetVector(ShaderIDs.BlurTexelSize, texelSize);
                m_blurYMat.SetFloat(ShaderIDs.BlurRadius, m_owner.EffectiveBlurRadius);
                m_blurYMat.SetFloat(ShaderIDs.DepthFalloff, m_owner.EffectiveDepthFalloff);
                m_blurYMat.SetFloat(ShaderIDs.SurfaceTension, m_owner.EffectiveSurfaceTension);
                m_blurYMat.SetFloat(ShaderIDs.WorldRadius, worldRadius);
                m_blurYMat.SetFloat(ShaderIDs.ProjectionScaleY, projectionScaleY);
                m_blurYMat.SetFloat(ShaderIDs.Orthographic, orthographic);

                TextureHandle smoothedEye = eye;
                int blurIterations = Mathf.Clamp(m_owner.EffectiveBlurIterations, 1, 3);
                for (int i = 0; i < blurIterations; i++)
                {
                    TextureHandle blurX = renderGraph.CreateTexture(EyeTextureDesc(width, height,
                        $"_PBDFluidEyeBlurX{suffix}_{i}", m_owner.m_eyeThicknessFormat));
                    TextureHandle blurY = renderGraph.CreateTexture(EyeTextureDesc(width, height,
                        $"_PBDFluidEyeBlurY{suffix}_{i}", m_owner.m_eyeThicknessFormat));
                    renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(
                        smoothedEye, blurX, m_blurXMat, 0), $"PBD SSF Blur X {suffix}.{i + 1}");
                    renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(
                        blurX, blurY, m_blurYMat, 0), $"PBD SSF Blur Y {suffix}.{i + 1}");
                    smoothedEye = blurY;
                }

                var importParams = new ImportResourceParams
                {
                    clearOnFirstUse = true,
                    clearColor = Color.clear,
                    discardOnLastUse = false
                };
                TextureHandle surfaceDepth = renderGraph.ImportTexture(targets.depth, importParams);
                TextureHandle surfaceNormal = renderGraph.ImportTexture(targets.normal, importParams);
                TextureHandle surfaceSubstance =
                    renderGraph.ImportTexture(targets.substance, importParams);
                using (var builder = renderGraph.AddRasterRenderPass<NormalData>(
                    "PBD SSF Normal Reconstruct " + suffix, out var passData))
                {
                    passData.mat = m_normalMat;
                    passData.smoothedEye = smoothedEye;
                    passData.hasSceneDepth = resources.cameraDepthTexture.IsValid();
                    passData.sceneDepth = resources.cameraDepthTexture;
                    passData.invProjection = gpuProjection.inverse;
                    passData.cameraToWorld = cameraData.camera.cameraToWorldMatrix;
                    passData.cameraPosition = cameraData.camera.transform.position;
                    passData.texelSize = texelSize;
                    passData.normalRadius = m_owner.EffectiveNormalRadius;
                    passData.worldRadius = worldRadius;
                    passData.projectionScaleY = projectionScaleY;
                    passData.orthographic = orthographic;
                    builder.SetRenderAttachment(surfaceDepth, 0, AccessFlags.WriteAll);
                    builder.SetRenderAttachment(surfaceNormal, 1, AccessFlags.WriteAll);
                    builder.SetRenderAttachment(surfaceSubstance, 2, AccessFlags.WriteAll);
                    builder.UseTexture(smoothedEye, AccessFlags.Read);
                    if (passData.hasSceneDepth) builder.UseTexture(passData.sceneDepth, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);

                    // Compatibilidade com cenas antigas. Proxies novos usam as
                    // texturas locais do MaterialPropertyBlock, nao estes globals.
                    if (entryIndex == 0)
                    {
                        builder.SetGlobalTextureAfterPass(surfaceDepth, ShaderIDs.SurfaceDepth);
                        builder.SetGlobalTextureAfterPass(surfaceNormal, ShaderIDs.SurfaceNormal);
                    }

                    builder.SetRenderFunc((NormalData data, RasterGraphContext context) =>
                    {
                        context.cmd.SetGlobalMatrix(ShaderIDs.InvProjection, data.invProjection);
                        context.cmd.SetGlobalMatrix(ShaderIDs.CameraToWorld, data.cameraToWorld);
                        context.cmd.SetGlobalVector(ShaderIDs.RenderCameraPosition,
                            data.cameraPosition);
                        context.cmd.SetGlobalVector(ShaderIDs.SurfaceTexelSize, data.texelSize);
                        context.cmd.SetGlobalFloat(ShaderIDs.HasSceneDepth, data.hasSceneDepth ? 1f : 0f);
                        context.cmd.SetGlobalFloat(ShaderIDs.NormalRadius, data.normalRadius);
                        context.cmd.SetGlobalFloat(ShaderIDs.WorldRadius, data.worldRadius);
                        context.cmd.SetGlobalFloat(ShaderIDs.ProjectionScaleY, data.projectionScaleY);
                        context.cmd.SetGlobalFloat(ShaderIDs.Orthographic, data.orthographic);
                        if (data.hasSceneDepth) context.cmd.SetGlobalTexture(ShaderIDs.SceneDepth, data.sceneDepth);
                        Blitter.BlitTexture(context.cmd, data.smoothedEye,
                            new Vector4(1f, 1f, 0f, 0f), data.mat, 0);
                    });
                }
            }
        }
    }
}
