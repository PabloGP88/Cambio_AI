Shader "Custom/ChessShader"
{
    Properties
    {
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        _Scale("Scale", Float) = 10.0
        _SpeedX("Speed X", Float) = 1.0
        _SpeedY("Speed Y", Float) = -1.0
        _SizeX("Increase Size in X",Float) = 0
        _SizeY("Increase Size in Y",Float) = 0
        _ColorA("Color A", Color) = (0.1, 0.1, 0.1, 1)
        _ColorB("Color B", Color) = (0.2, 0.2, 0.2, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
            
                half4 _BaseColor;
                float4 _BaseMap_ST;
                float _Scale;
                float _SpeedX;
                float _SpeedY;
                float _SizeX;
                float _SizeY;
                half4 _ColorA;
                half4 _ColorB;
            
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.uv;

                float aspect = _ScreenParams.x / _ScreenParams.y;
                uv.x *= aspect;
                
                // Diagonal movement
                float2 offset = _Time.y * float2(_SpeedX, _SpeedY);
                float2 movedUV = uv * _Scale + offset;

                // Chess pattern
                float2 grid = floor(movedUV);
                float chess = fmod(grid.x + grid.y, 2.0);
                

                // Combine
                float3 color = lerp(_ColorA, _ColorB, chess);

                return float4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
