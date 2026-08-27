using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace LiquidVolumeFX {

    public class LiquidVolumeDepthPrePassRenderFeature : ScriptableRendererFeature {

        static class ShaderParams {
            public static int RTBackBuffer = Shader.PropertyToID("_VLBackBufferTexture");
            public static int RTFrontBuffer = Shader.PropertyToID("_VLFrontBufferTexture");
            public static int FlaskThickness = Shader.PropertyToID("_FlaskThickness");
            public static int ForcedInvisible = Shader.PropertyToID("_LVForcedInvisible");
            public const string SKW_FP_RENDER_TEXTURE = "LIQUID_VOLUME_FP_RENDER_TEXTURES";
        }

        enum Pass {
            BackBuffer = 0,
            FrontBuffer = 1
        }

        public readonly static List<LiquidVolume> lvBackRenderers = new List<LiquidVolume>();
        public readonly static List<LiquidVolume> lvFrontRenderers = new List<LiquidVolume>();

        public static void AddLiquidToBackRenderers(LiquidVolume lv) {
            if (lv == null || lv.topology != TOPOLOGY.Irregular || lvBackRenderers.Contains(lv)) return;
            lvBackRenderers.Add(lv);
        }

        public static void RemoveLiquidFromBackRenderers(LiquidVolume lv) {
            if (lv == null || !lvBackRenderers.Contains(lv)) return;
            lvBackRenderers.Remove(lv);
        }

        public static void AddLiquidToFrontRenderers(LiquidVolume lv) {
            if (lv == null || lv.topology != TOPOLOGY.Irregular || lvFrontRenderers.Contains(lv)) return;
            lvFrontRenderers.Add(lv);
        }

        public static void RemoveLiquidFromFrontRenderers(LiquidVolume lv) {
            if (lv == null || !lvFrontRenderers.Contains(lv)) return;
            lvFrontRenderers.Remove(lv);
        }

        class DepthPass : ScriptableRenderPass {

            const string profilerTag = "LiquidVolumeDepthPrePass";

            Material mat;
            int targetId;
            int passId;
            List<LiquidVolume> lvRenderers;
            public bool interleavedRendering;

            class DepthPassData {
                public LiquidVolume[] liquids;
                public Material material;
                public int passId;
                public bool useFPRenderTextures;
            }

            class LiquidPassData {
                public LiquidVolume liquid;
                public TextureHandle depthTexture;
                public int targetId;
                public bool hideAfter;
            }

            public DepthPass(Material mat, Pass pass, RenderPassEvent renderPassEvent) {
                this.renderPassEvent = renderPassEvent;
                this.mat = mat;
                switch (pass) {
                    case Pass.BackBuffer:
                        targetId = ShaderParams.RTBackBuffer;
                        passId = (int)Pass.BackBuffer;
                        lvRenderers = lvBackRenderers;
                        break;
                    case Pass.FrontBuffer:
                        targetId = ShaderParams.RTFrontBuffer;
                        passId = (int)Pass.FrontBuffer;
                        lvRenderers = lvFrontRenderers;
                        break;
                }
            }

            public void Setup(LiquidVolumeDepthPrePassRenderFeature feature, ScriptableRenderer renderer) {
                this.interleavedRendering = feature.interleavedRendering;
            }

            static bool IsRenderable(LiquidVolume lv) {
                return lv != null && lv.isActiveAndEnabled && lv.mr != null;
            }

            void CreateDepthTargets(RenderGraph renderGraph, TextureHandle cameraColor, float farClipPlane, int index, out TextureHandle color, out TextureHandle depth) {
                TextureDesc colorDesc = cameraColor.GetDescriptor(renderGraph);
                colorDesc.name = profilerTag + "_" + targetId + "_" + index;
                colorDesc.format = LiquidVolume.useFPRenderTextures ? GraphicsFormat.R16_SFloat : GraphicsFormat.R8G8B8A8_UNorm;
                colorDesc.msaaSamples = MSAASamples.None;
                colorDesc.bindTextureMS = false;
                colorDesc.clearBuffer = true;
                colorDesc.clearColor = LiquidVolume.useFPRenderTextures
                    ? new Color(farClipPlane, 0, 0, 0)
                    : new Color(0.9882353f, 0.4470558f, 0.75f, 0f);
                color = renderGraph.CreateTexture(colorDesc);

                TextureDesc depthDesc = colorDesc;
                depthDesc.name += "_Depth";
                depthDesc.depthBufferBits = DepthBits.Depth16;
                depth = renderGraph.CreateTexture(depthDesc);
            }

            void RecordDepthPass(RenderGraph renderGraph, TextureHandle color, TextureHandle depth, LiquidVolume[] liquids) {
                using (var builder = renderGraph.AddRasterRenderPass<DepthPassData>(profilerTag, out var passData)) {
                    passData.liquids = liquids;
                    passData.material = mat;
                    passData.passId = passId;
                    passData.useFPRenderTextures = LiquidVolume.useFPRenderTextures;

                    builder.SetRenderAttachment(color, 0, AccessFlags.WriteAll);
                    builder.SetRenderAttachmentDepth(depth, AccessFlags.WriteAll);
                    builder.SetGlobalTextureAfterPass(color, targetId);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (DepthPassData data, RasterGraphContext context) => {
                        context.cmd.SetGlobalFloat(ShaderParams.ForcedInvisible, 0);
                        if (data.useFPRenderTextures) {
                            context.cmd.EnableShaderKeyword(ShaderParams.SKW_FP_RENDER_TEXTURE);
                        } else {
                            context.cmd.DisableShaderKeyword(ShaderParams.SKW_FP_RENDER_TEXTURE);
                        }

                        foreach (LiquidVolume lv in data.liquids) {
                            if (!IsRenderable(lv)) continue;
                            context.cmd.SetGlobalFloat(ShaderParams.FlaskThickness, 1.0f - lv.flaskThickness);
                            context.cmd.DrawRenderer(lv.mr, data.material, lv.subMeshIndex >= 0 ? lv.subMeshIndex : 0, data.passId);
                        }
                    });
                }
            }

            void RecordLiquidPass(RenderGraph renderGraph, UniversalResourceData resourceData, TextureHandle depthTexture, LiquidVolume lv, bool hideAfter) {
                using (var builder = renderGraph.AddRasterRenderPass<LiquidPassData>(profilerTag + "_Liquid", out var passData)) {
                    passData.liquid = lv;
                    passData.depthTexture = depthTexture;
                    passData.targetId = targetId;
                    passData.hideAfter = hideAfter;

                    builder.UseTexture(depthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (LiquidPassData data, RasterGraphContext context) => {
                        LiquidVolume lv = data.liquid;
                        context.cmd.SetGlobalTexture(data.targetId, data.depthTexture);
                        context.cmd.SetGlobalFloat(ShaderParams.ForcedInvisible, 0);
                        if (IsRenderable(lv) && lv.liqMat != null) {
                            context.cmd.DrawRenderer(lv.mr, lv.liqMat, lv.subMeshIndex >= 0 ? lv.subMeshIndex : 0, 1);
                        }
                        if (data.hideAfter) {
                            context.cmd.SetGlobalFloat(ShaderParams.ForcedInvisible, 1);
                        }
                    });
                }
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
                if (lvRenderers == null || lvRenderers.Count == 0) return;

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                List<LiquidVolume> activeRenderers = lvRenderers.FindAll(IsRenderable);
                if (activeRenderers.Count == 0) return;

                if (interleavedRendering) {
                    Vector3 cameraPosition = cameraData.camera.transform.position;
                    Vector3 cameraForward = cameraData.camera.transform.forward;
                    activeRenderers.Sort((a, b) => {
                        float depthA = Vector3.Dot(a.mr.bounds.center - cameraPosition, cameraForward);
                        float depthB = Vector3.Dot(b.mr.bounds.center - cameraPosition, cameraForward);
                        return depthB.CompareTo(depthA);
                    });

                    for (int i = 0; i < activeRenderers.Count; i++) {
                        LiquidVolume lv = activeRenderers[i];
                        CreateDepthTargets(renderGraph, resourceData.activeColorTexture, cameraData.camera.farClipPlane, i, out TextureHandle color, out TextureHandle depth);
                        RecordDepthPass(renderGraph, color, depth, new[] { lv });
                        RecordLiquidPass(renderGraph, resourceData, color, lv, i == activeRenderers.Count - 1);
                    }
                } else {
                    CreateDepthTargets(renderGraph, resourceData.activeColorTexture, cameraData.camera.farClipPlane, 0, out TextureHandle color, out TextureHandle depth);
                    RecordDepthPass(renderGraph, color, depth, activeRenderers.ToArray());
                }

            }
        }


        [SerializeField, HideInInspector]
        Shader shader;

        public static bool installed;
        Material mat;
        DepthPass backPass, frontPass;

        [Tooltip("Renders each irregular liquid volume completely before rendering the next one.")]
        public bool interleavedRendering;

        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;

        private void OnDestroy() { 
            Shader.SetGlobalFloat(ShaderParams.ForcedInvisible, 0);
            CoreUtils.Destroy(mat);
        }

        public override void Create() {
            name = "Liquid Volume Depth PrePass";
            shader = Shader.Find("LiquidVolume/DepthPrePass");
            if (shader == null) {
                return;
            }
            mat = CoreUtils.CreateEngineMaterial(shader);
            backPass = new DepthPass(mat, Pass.BackBuffer, renderPassEvent);
            frontPass = new DepthPass(mat, Pass.FrontBuffer, renderPassEvent);
        }

        // This method is called when setting up the renderer once per-camera.
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            installed = true;
            if (backPass != null && lvBackRenderers.Count > 0) {
                backPass.Setup(this, renderer);
                renderer.EnqueuePass(backPass);
            }
            if (frontPass != null && lvFrontRenderers.Count > 0) {
                frontPass.Setup(this, renderer);
                renderer.EnqueuePass(frontPass);
            }
        }
    }
}
