// Туман по расстоянию. Цвет - из неба/погоды игры, густота - от глубины кадра.
// Ближе _FogStart метров тумана нет вообще, дальше _FogEnd - сплошная стена.
// Динамика: мировые клочья по FBM-шуму в плоскости XZ, плывут по ветру.
// Частицы рисуются отдельным проходом ПОСЛЕ этого эффекта (см. SilentFogParticle).
Shader "Hidden/SilentFog/DistanceFog"
{
    Properties
    {
        _MainTex ("Base (RGB)", 2D) = "white" {}
        _NoiseTex ("Noise (RGB)", 3D) = "" {}
        _DynamicAmount ("Dynamic Amount", Float) = 0
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_fog
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            sampler3D _NoiseTex;

            float4 _FogColor;
            float _FogStart;
            float _FogEnd;
            float _FogStrength;
            float _SkyExclude;

            // Динамика. Базис камеры передаём из C# (cam.transform), а не через
            // unity_CameraToWorld: в этой игре он иногда давал NaN и дыры.
            float _DynamicAmount;
            float4 _CamPos;
            float4 _CamRight;
            float4 _CamUp;
            float4 _CamForward;
            float _TanHalfFovX;
            float _TanHalfFovY;
            float4 _Wind;

            // Мировой центр тумана (xy = XZ), высота земли и «стекание» шума.
            float4 _FogCenter;
            float _BaseY;
            float _NoiseSink;

            // Единое поле: до 6 боксов-препятствий. Внутри них туман не рисуется,
            // но воздух перед домом/за забором продолжает давать туман.
            #define FOG_BOXES 6
            float4 _FogBoxMin[FOG_BOXES]; // xyz = min, w = вес 0..1
            float4 _FogBoxMax[FOG_BOXES]; // xyz = max, w = полуширина кромки, м
            float _FogBoxCount;
            float4 _FogEdge;              // x=amp, y=vk, z=k кривой, w=toe
            float _FogDebug;
            float4 _MainTex_TexelSize;

            // Побитовая проверка: isnan/isinf компилятор может «оптимизировать» прочь.
            bool IsBad3(float3 c)
            {
                uint3 u = asuint(c);
                return any((u & 0x7f800000u) == 0x7f800000u);
            }

            float Fin(float x)
            {
                return ((asuint(x) & 0x7f800000u) == 0x7f800000u) ? 0.0 : x;
            }

            // Сумма «хороших» соседей по кольцу; ok = их количество.
            float3 RingSum(float2 uv, float px, out float ok)
            {
                float2 o = _MainTex_TexelSize.xy * px;
                float3 acc = float3(0, 0, 0);
                ok = 0.0;
                [unroll] for (int k = 0; k < 8; k++)
                {
                    float a = k * 0.7853982;
                    float2 d = float2(cos(a), sin(a)) * o;
                    float3 s = tex2Dlod(_MainTex, float4(uv + d, 0, 0)).rgb;
                    float good = IsBad3(s) ? 0.0 : 1.0;
                    acc += good > 0.5 ? s : float3(0, 0, 0);
                    ok += good;
                }
                return acc;
            }

            // Доля отрезка [0,1] камера->поверхность внутри AABB.
            // Координаты бокса передаются относительно камеры.
            float ChordFrac(float3 bmin, float3 bmax, float3 invD)
            {
                float3 t0 = bmin * invD;
                float3 t1 = bmax * invD;
                float3 tmn = min(t0, t1);
                float3 tmx = max(t0, t1);
                float tin = max(max(tmn.x, tmn.y), max(tmn.z, 0.0));
                float tout = min(min(tmx.x, tmx.y), min(tmx.z, 1.0));
                return max(tout - tin, 0.0);
            }

            // Мягкий бокс: среднее хорд внешнего и внутреннего боксов.
            // shift двигает кромку волной, edge — полуширина кромки.
            float SoftChord(float3 bmin, float3 bmax, float edge, float shift, float3 invD)
            {
                float3 k = float3(1.0, _FogEdge.y, 1.0);
                float eo = max(edge + shift, 0.25);
                float ei = max(edge - shift, 0.25);
                float3 half3 = (bmax - bmin) * 0.5;
                float3 eoV = eo * k;
                float3 eiV = min(ei * k, half3 * 0.45);
                float a = ChordFrac(bmin - eoV, bmax + eoV, invD);
                float b = ChordFrac(bmin + eiV, bmax - eiV, invD);
                return 0.5 * (a + b);
            }

            // Кривая тумана: мягкий старт (toe) + сохранение плотности в середине.
            float FogCurve(float d)
            {
                float span = max(_FogEnd - _FogStart, 0.01);
                float s = saturate((d - _FogStart) / span);
                float ss = s * s * (3.0 - 2.0 * s);
                float toe = smoothstep(0.0, max(_FogEdge.w, 1e-3), s);
                return pow(ss, _FogEdge.z) * toe;
            }

            struct v2f_fog
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 screen : TEXCOORD1;
            };

            v2f_fog vert_fog(appdata_img v)
            {
                v2f_fog o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                // NDC берём из clip-space, а не из uv: так ось Y не зависит
                // от того, перевёрнут ли кадр на D3D.
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            fixed4 frag(v2f_fog i) : SV_Target
            {
                float4 col = tex2D(_MainTex, i.uv);
                bool wasBad = IsBad3(col.rgb);

                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                float eye = LinearEyeDepth(raw);

                float far = _ProjectionParams.z;
                float isSky = step(far * 0.999f, eye);

                float2 ndc = (i.screen.xy / i.screen.w) * 2.0 - 1.0;
                float3 ray = _CamForward.xyz
                           + _CamRight.xyz * (ndc.x * _TanHalfFovX)
                           + _CamUp.xyz * (ndc.y * _TanHalfFovY);

                // Безопасный normalize: в полюсе ray может быть (0,0,0).
                float3 rd = ray * rsqrt(max(dot(ray, ray), 1e-12));

                // Санитайзинг источника: Inf/NaN и тёмная точка полюса неба.
                bool inPole = (isSky > 0.5) && (rd.y > 0.97);
                bool darkPole = false;
                [branch] if (wasBad || inPole)
                {
                    float ok;
                    float3 sum = RingSum(i.uv, 28.0, ok);
                    float3 ring = ok > 0.5 ? sum / ok : _FogColor.rgb;
                    float lumR = dot(ring, float3(0.299, 0.587, 0.114));
                    float lumC = wasBad ? 0.0 : dot(col.rgb, float3(0.299, 0.587, 0.114));
                    darkPole = !wasBad && (lumR > 0.002) && (lumC < 0.90 * lumR);
                    if (wasBad || darkPole)
                        col.rgb = ring;
                }

                float3 D = ray * eye;
                float3 wpos = _CamPos.xyz + D;

                // Туман живёт в мире: базовая дистанция считается от плавающего
                // центра, чтобы на улице сохранялось отставание FollowLag.
                float2 delta = wpos.xz - _FogCenter.xy;
                float worldDist = length(delta);
                float Dh = length(D.xz);

                // Шум один раз на пиксель: кромки боксов + динамические клочья.
                float sampleEye = min(eye, 60.0);
                float3 npos = _CamPos.xyz + ray * sampleEye;
                npos.y += _NoiseSink;

                float edgeA = 0.0;
                float edgeB = 0.0;
                [branch] if (_FogBoxCount > 0.5 || _DynamicAmount > 0.0001)
                {
                    float3 windA = float3(_Wind.x, 0.0, _Wind.y) * 0.45;
                    edgeA = tex3Dlod(_NoiseTex,
                        float4((npos + windA) * 0.022 + float3(0.71, 0.19, 0.83), 0)).r * 2.0 - 1.0;
                    edgeB = tex3Dlod(_NoiseTex,
                        float4((npos + windA) * 0.037 + float3(0.33, 0.58, 0.12), 0)).r * 2.0 - 1.0;
                }

                // Доля пути внутри чистых объёмов (боксы зданий/торговца/базы).
                float cleanFrac = 0.0;
                [branch] if (_FogBoxCount > 0.5 && isSky < 0.5)
                {
                    float3 sD = D + lerp(-1e-4, 1e-4, step(0.0, D));
                    float3 invD = 1.0 / sD;
                    float3 rel = -_CamPos.xyz;

                    [unroll] for (int b = 0; b < FOG_BOXES; b++)
                    {
                        [branch] if (b < _FogBoxCount)
                        {
                            float4 bmn = _FogBoxMin[b];
                            float4 bmx = _FogBoxMax[b];
                            float ni = lerp(edgeA, edgeB, b * 0.2);
                            cleanFrac += bmn.w * SoftChord(
                                bmn.xyz + rel, bmx.xyz + rel, bmx.w,
                                ni * _FogEdge.x, invD);
                        }
                    }
                    cleanFrac = saturate(cleanFrac);
                }

                // Ключевая формула: вычитаем только пройденный внутри боксов
                // путь. Воздух перед домом остаётся туманным, интерьер — чистым.
                float fogDist = max(worldDist - cleanFrac * Dh, 0.0);
                float f = FogCurve(fogDist);

                // Туман объёмный: почти не редеет вверх, чтобы не превращаться
                // в плоскую полосу у земли.
                float height = wpos.y - _BaseY;
                float heightFactor = saturate(1.0 - height * 0.006);
                heightFactor = 0.9 + 0.1 * heightFactor;
                f *= heightFactor;

                float fogShade = 1.0;

                if (_DynamicAmount > 0.0001 && isSky < 0.5)
                {
                    float3 windA = float3(_Wind.x, 0.0, _Wind.y) * 0.45;
                    float3 windB = float3(_Wind.x, 0.0, _Wind.y) * 0.75;
                    float3 windC = float3(_Wind.x, 0.0, _Wind.y) * 1.10;

                    float warp = tex3D(
                        _NoiseTex,
                        (npos + windA) * 0.0052 + float3(0.17, 0.31, 0.47)).r - 0.5;

                    float3 wq = npos + float3(warp, warp * 0.55, warp * 0.25) * 28.0;

                    float nA = tex3D(_NoiseTex, (wq + windA) * 0.0045).r;
                    float nB = tex3D(_NoiseTex,
                        (wq + windB) * 0.0110 + float3(0.21, 0.37, 0.53)).r;
                    float nC = tex3D(_NoiseTex,
                        (wq + windC) * 0.0280 + float3(0.61, 0.13, 0.29)).r;

                    float n = nA * 0.48 + nB * 0.34 + nC * 0.18;
                    n = (n - 0.5) * 2.0;
                    n *= 1.0 - 0.25 * abs(n);

                    f *= saturate(1.0 + n * _DynamicAmount * 0.35);
                    fogShade = 1.0 + n * _DynamicAmount * 0.40;
                }

                // «Молоко»: дожимаем плотность к 1, чтобы дальние силуэты
                // не просвечивали через 5–20% остатка.
                f = 1.0 - pow(1.0 - saturate(f), 2.2);

                // Дырка в зените: цилиндр по XZ даёт почти 0 на вертикали
                // над центром, хотя пиксель далеко (skybox/дальний объект).
                // Для далёких пикселей включаем полную плотность по глубине.
                float farFog = saturate(
                    (eye - _FogEnd) / max(_FogEnd * 0.5, 1.0));
                // Не перебиваем маску торговца/POI/базы: если луч уже
                // прошёл внутри бокса, дальняя плотность не нужна.
                farFog *= 1.0 - saturate(cleanFrac * 4.0);
                f = max(f, farFog);

                f *= _FogStrength;
                f *= 1.0 - isSky * _SkyExclude;
                f = saturate(Fin(f));                 // NaN/Inf guard

                float3 fogCol = _FogColor.rgb * saturate(Fin(fogShade));
                col.rgb = lerp(col.rgb, fogCol, f);

                // Диагностика: 1 = чистая доля пути/туман.
                if (_FogDebug > 0.5 && _FogDebug < 1.5)
                    col.rgb = lerp(col.rgb,
                        float3(1.0 - cleanFrac, cleanFrac, 0.0),
                        0.55 * (1.0 - isSky));

                // 2 = пурпурный там, где источник был NaN/Inf; жёлтый — тёмная точка в зените.
                if (_FogDebug > 1.5)
                {
                    if (wasBad)        col.rgb = float3(1, 0, 1);
                    else if (darkPole) col.rgb = float3(1, 1, 0);
                }

                if (IsBad3(col.rgb)) col.rgb = _FogColor.rgb; // финальная страховка
                return col;
            }
            ENDCG
        }
    }

    Fallback Off
}
