// Clone-stamp occlusion shader.
//
// Renders the object using pixels copied from the camera image at a screen-space
// offset, so the object visually disappears into whatever the AR camera sees next
// to it. Reads URP's _CameraOpaqueTexture, which on an AR Foundation camera holds
// the camera background (plus any opaque virtual geometry) copied after the opaque
// pass. This material renders in the transparent queue so it is never present in
// the texture it samples.
//
// Requires "Opaque Texture" enabled on the active URP asset.
Shader "AR/Clone Stamp Occlusion"
{
    Properties
    {
        _OffsetPixels ("Sample Offset (pixels)", Vector) = (200, 0, 0, 0)
        _Blend ("Clone Blend", Range(0, 1)) = 1
        _DebugColor ("Debug Color", Color) = (1, 0, 1, 1)
        _Alpha ("Alpha", Range(0, 1)) = 1
        _EdgeFeather ("Edge Crossfade (screen fraction)", Range(0.001, 0.2)) = 0.03
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent-100"
        }

        Pass
        {
            Name "CloneStamp"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 screenPos  : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _OffsetPixels;
                half4  _DebugColor;
                half   _Blend;
                half   _Alpha;
                half   _EdgeFeather;
            CBUFFER_END

            // Written by CloneStampPlateFeature. Global, so kept out of UnityPerMaterial.
            TEXTURE2D(_CloneStampPlateTex);
            SAMPLER(sampler_CloneStampPlateTex);
            float _CloneStampPlateScale;
            float _CloneStampPlateValid;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs positions = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = positions.positionCS;
                OUT.screenPos = ComputeScreenPos(positions.positionCS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // Where this pixel is on screen, then shifted by the stamp offset.
                float2 screenUV = IN.screenPos.xy / max(IN.screenPos.w, 1e-5);
                float2 sampleUV = screenUV + _OffsetPixels.xy / max(_ScreenParams.xy, 1.0);

                // Live camera image. Only meaningful while sampleUV is on screen;
                // outside it degenerates to a smeared border texel.
                half3 cloned = SampleSceneColor(clamp(sampleUV, 0.0005, 0.9995));

                // How far off-screen the sample landed, in screen fractions.
                float2 overshoot = max(-sampleUV, sampleUV - 1.0);
                float offScreen = saturate(max(overshoot.x, overshoot.y) / max(_EdgeFeather, 1e-4));

                if (offScreen > 0.0 && _CloneStampPlateValid > 0.5)
                {
                    // The plate covers a wider field of view than the screen, so the
                    // same direction sits closer to the centre in plate space.
                    float2 plateUV = (sampleUV - 0.5) / max(_CloneStampPlateScale, 1e-4) + 0.5;

                    if (all(plateUV > 0.0) && all(plateUV < 1.0))
                    {
                        half4 plate = SAMPLE_TEXTURE2D(_CloneStampPlateTex, sampler_CloneStampPlateTex, plateUV);
                        // plate.a is how much real history that texel holds; where the
                        // camera has never looked, fall back to the clamped live sample.
                        cloned = lerp(cloned, plate.rgb, offScreen * plate.a);
                    }
                }

                half3 rgb = lerp(_DebugColor.rgb, cloned, _Blend);
                return half4(rgb, _Alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
