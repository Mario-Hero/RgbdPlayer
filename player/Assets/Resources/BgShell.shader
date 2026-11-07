// VR180 背景球壳 shader: 无深度位移, 固定半径渲染背景纹理
// 用途: 贴在深度球壳后方, 当前景深度差过大的片元被 discard 时, 背景从镂空处露出
// 渲染顺序: Queue=Background (先画), ZWrite Off + ZTest Always (不参与深度竞争, 始终垫底)
Shader "VR180/BgShell"
{
    Properties
    {
        _BgTex ("Background", 2D) = "black" {}
        _EyeIndex ("Eye Index (0=左 1=右 -1=双眼)", Float) = -1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Background" }
        Cull Back
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _BgTex;
            float4 _BgTex_ST;
            float _EyeIndex;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _BgTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // 分眼: 本壳只属于一只眼, 不匹配的眼整壳丢弃
                if (_EyeIndex > -0.5 && (int)unity_StereoEyeIndex != (int)(_EyeIndex + 0.5f))
                    discard;
                return fixed4(tex2D(_BgTex, i.uv).rgb, 1);
            }
            ENDCG
        }
    }
}
