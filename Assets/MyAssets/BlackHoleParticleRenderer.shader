Shader "BlackHole/ParticleRenderer"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual

        Pass
        {
            Name "ParticleForward"
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            // ========== Structures ==========
            struct Particle
            {
                float3 position;
                float3 velocity;
                float age;
                float temperature;
                float mass;
                uint flags;
            };

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float temperature : TEXCOORD1;
                float age : TEXCOORD2;
                float size : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // ========== Buffers ==========
            StructuredBuffer<Particle> _ParticleBuffer;
            int _ParticleCount;

            // ========== Material Parameters ==========
            float _ParticleSize;
            float _TemperatureScale;
            float _MaxTemperature;
            float _GlowIntensity;

            // ========== Helper: Blackbody Radiation Color ==========
            // 温度 (Kelvin 相当) を RGB に変換
            // 簡略化版：温度に応じて色温度スペクトラムを線形補間
            float3 TemperatureToColor(float temp)
            {
                // temp = normalized value (0~1)
                // 0 = dark red, 0.5 = orange/yellow, 1.0 = white/blue
                
                float3 colors[5] = {
                    float3(0.0f, 0.0f, 0.0f),     // 0.0: black (cold, dead)
                    float3(0.8f, 0.1f, 0.0f),     // 0.25: dark red
                    float3(1.0f, 0.5f, 0.0f),     // 0.5: orange
                    float3(1.0f, 0.9f, 0.4f),     // 0.75: yellow-white
                    float3(1.0f, 1.0f, 1.0f),     // 1.0: white
                };
                
                // 温度を [0, 1] に正規化
                float normalized = saturate(temp / _MaxTemperature);
                
                // 5段階カラーマップで補間
                float index = normalized * 4.0f;
                uint lower = uint(index);
                uint upper = min(lower + 1, 4);
                float blend = frac(index);
                
                float3 color = lerp(colors[lower], colors[upper], blend);
                return color;
            }

            // Gaussian Point Cloud: 中心から放射状に光が落ちる効果
            float PointcloudAlpha(float2 uv, float size)
            {
                // uv: [-size, size] の範囲
                float dist = length(uv);
                float falloff = exp(-(dist * dist) / (size * size));
                return falloff;
            }

            // ========== Vertex Shader ==========
            Varyings Vertex(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output = (Varyings)0;
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                uint particleIdx = input.vertexID;
                if (particleIdx >= _ParticleCount)
                {
                    output.positionCS = float4(0, 0, 0, 0);
                    return output;
                }

                Particle p = _ParticleBuffer[particleIdx];

                // 死んだ粒子はスキップ
                if ((p.flags & 1u) == 0)
                {
                    output.positionCS = float4(0, 0, 0, 0);
                    return output;
                }

                // ワールド座標→クリップ座標
                output.positionWS = p.position;
                output.positionCS = mul(float4(output.positionWS, 1.0f), UNITY_MATRIX_VP);

                // 粒子パラメータ
                output.temperature = p.temperature;
                output.age = p.age;
                output.size = _ParticleSize * (1.0f - p.age / 30.0f);  // 年齢で小さくなる

                return output;
            }

            // ========== Fragment Shader ==========
            float4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // スクリーン空間での粒子サイズ
                float2 uv = (input.positionCS.xy - floor(input.positionCS.xy)) - 0.5f;
                float alpha = PointcloudAlpha(uv, input.size * 0.5f);

                // 温度から色を算出
                float3 color = TemperatureToColor(input.temperature);

                // グロー効果：高温ほど明るく
                float glow = input.temperature / _MaxTemperature;
                color *= (1.0f + glow * _GlowIntensity);

                // 年齢に応じたフェードアウト
                float ageFade = 1.0f - saturate(input.age / 30.0f);
                alpha *= ageFade;

                return float4(color, alpha);
            }
            ENDHLSL
        }

        // ========== Trail Rendering Pass (Optional) ==========
        Pass
        {
            Name "ParticleTrail"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex VertexTrail
            #pragma fragment FragmentTrail
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Particle
            {
                float3 position;
                float3 velocity;
                float age;
                float temperature;
                float mass;
                uint flags;
            };

            struct AttributesTrail
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct VaryingsTrail
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float temperature : TEXCOORD1;
                float alpha : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            StructuredBuffer<Particle> _ParticleBuffer;
            int _ParticleCount;
            float _TrailWidth;
            float _TemperatureScale;
            float _MaxTemperature;

            VaryingsTrail VertexTrail(AttributesTrail input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                VaryingsTrail output = (VaryingsTrail)0;
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // トレイル用：連続した粒子を線でつなぐ
                uint particleIdx = input.vertexID / 2;
                uint vertexInLine = input.vertexID % 2;

                if (particleIdx >= _ParticleCount)
                {
                    output.positionCS = float4(0, 0, 0, 0);
                    return output;
                }

                Particle p = _ParticleBuffer[particleIdx];
                Particle pNext = _ParticleBuffer[(particleIdx + 1) % _ParticleCount];

                if ((p.flags & 1u) == 0)
                {
                    output.positionCS = float4(0, 0, 0, 0);
                    return output;
                }

                // 線を拡張する方向（前フレームの位置から現フレームへ）
                float3 lineDir = normalize(pNext.position - p.position);
                float3 normal = normalize(cross(lineDir, normalize(_WorldSpaceCameraPos - p.position)));

                float offset = (vertexInLine == 0) ? -_TrailWidth : _TrailWidth;
                float3 pos = p.position + normal * offset;

                output.positionWS = pos;
                output.positionCS = mul(float4(pos, 1.0f), UNITY_MATRIX_VP);
                output.temperature = p.temperature;
                output.alpha = 1.0f - saturate(p.age / 30.0f);

                return output;
            }

            float4 FragmentTrail(VaryingsTrail input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float3 color = float3(1.0f, 0.5f, 0.0f);  // オレンジ（簡略版）
                
                // 温度に応じた色調整
                float tempFactor = input.temperature / _MaxTemperature;
                color = lerp(float3(0.5f, 0.1f, 0.0f), float3(1.0f, 1.0f, 1.0f), tempFactor);

                float alpha = input.alpha * 0.5f;  // トレイルは半透明

                return float4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/InternalErrorShader"
}
