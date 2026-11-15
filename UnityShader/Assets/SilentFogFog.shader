// Туман по расстоянию: цвет - из неба/погоды игры, густота - от глубины кадра.
// Ближе _FogStart метров тумана нет вообще, дальше _FogEnd - сплошная стена.
Shader "Hidden/SilentFog/DistanceFog"
{
    Properties
    {
        _MainTex ("Base (RGB)", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float4 _FogColor;
            float _FogStart;    // начало тумана, метры
            float _FogEnd;      // конец, метры
            float _FogStrength; // 0..1 - общая сила
            float _SkyExclude;  // 1 - небо не трогаем

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);

                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                float eye = LinearEyeDepth(raw);

                // Небо рисуется без записи глубины - там расстояние "в бесконечность".
                float far = _ProjectionParams.z;
                float isSky = step(far * 0.999f, eye);

                float span = max(_FogEnd - _FogStart, 0.01);
                float f = saturate((eye - _FogStart) / span);
                f = f * f * (3.0 - 2.0 * f);   // smoothstep

                f *= _FogStrength;
                f *= 1.0 - isSky * _SkyExclude;

                col.rgb = lerp(col.rgb, _FogColor.rgb, f);
                return col;
            }
            ENDCG
        }
    }

    Fallback Off
}
