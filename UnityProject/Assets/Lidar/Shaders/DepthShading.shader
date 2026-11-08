Shader "Hidden/UnityLidarViewer/DepthShading"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend DstColor Zero
            ColorMask RGB
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float _LidarDepthStrength;

            float NearbyDepth(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(saturate(uv)), _ZBufferParams);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                clip(_LidarDepthStrength - .001);
                float2 uv = GetNormalizedScreenSpaceUV(input.positionCS);
                float depth = NearbyDepth(uv);
                clip(_ProjectionParams.z * .999 - depth);
                float2 pixel = 2.0 / _ScaledScreenParams.xy;
                float4 neighbors = float4(
                    NearbyDepth(uv + float2(pixel.x, 0)),
                    NearbyDepth(uv - float2(pixel.x, 0)),
                    NearbyDepth(uv + float2(0, pixel.y)),
                    NearbyDepth(uv - float2(0, pixel.y)));
                float4 closer = smoothstep(.006, .05, (depth - neighbors) / max(depth, 1));
                // A lone neighbor is often another isolated return rather than a surface edge.
                float response = saturate((dot(closer, float4(1, 1, 1, 1)) - 1) / 3);
                return half4(1 - _LidarDepthStrength * .42 * response.xxx, 1);
            }
            ENDHLSL
        }
    }
}
