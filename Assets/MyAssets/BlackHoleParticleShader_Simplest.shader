Shader "BlackHole/ParticleSimplest"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 4.5

            // ========== Manual Matrix Definition ==========
            // Unity が提供しないので、手動で定義
            cbuffer UnityMatrices
            {
                float4x4 unity_ObjectToWorld;
                float4x4 unity_WorldToObject;
                float4x4 unity_MatrixVP;
                float3 _WorldSpaceCameraPos;
            };

            // ========== Particle Structure ==========
            struct Particle
            {
                float3 position;
                float temperature;
                float3 velocity;
                float age;
                uint flags;
                float mass;
                uint _pad0;
                uint _pad1;
            };

            // ========== Vertex/Fragment Input/Output ==========
            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float temperature : TEXCOORD0;
                float age : TEXCOORD1;
                float size : TEXCOORD2;
            };

            // ========== Buffers ==========
            StructuredBuffer<Particle> _ParticleBuffer;
            int _ParticleCount;

            // ========== Helper Functions ==========
            
            float3 TemperatureToColor(float temp)
            {
                // Normalize temperature: 0~2 -> 0~1
                float t = saturate(temp / 2.0f);
                
                // Gradient: Black -> Red -> Orange -> Yellow -> White
                float3 c;
                
                if (t < 0.25f)
                {
                    // Black to Red
                    float blend = t / 0.25f;
                    c = lerp(float3(0.0f, 0.0f, 0.0f), 
                             float3(0.8f, 0.1f, 0.0f), 
                             blend);
                }
                else if (t < 0.5f)
                {
                    // Red to Orange
                    float blend = (t - 0.25f) / 0.25f;
                    c = lerp(float3(0.8f, 0.1f, 0.0f),
                             float3(1.0f, 0.5f, 0.0f),
                             blend);
                }
                else if (t < 0.75f)
                {
                    // Orange to Yellow
                    float blend = (t - 0.5f) / 0.25f;
                    c = lerp(float3(1.0f, 0.5f, 0.0f),
                             float3(1.0f, 0.9f, 0.3f),
                             blend);
                }
                else
                {
                    // Yellow to White
                    float blend = (t - 0.75f) / 0.25f;
                    c = lerp(float3(1.0f, 0.9f, 0.3f),
                             float3(1.0f, 1.0f, 1.0f),
                             blend);
                }
                
                return c;
            }

            float ComputeParticleAlpha(float2 screenPos, float size)
            {
                // Gaussian falloff
                float dist = length(screenPos);
                float falloff = exp(-(dist * dist) / (size * size));
                return falloff;
            }

            // ========== Vertex Shader ==========
            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                uint idx = input.vertexID;
                
                // Safety check
                if (idx >= (uint)_ParticleCount)
                {
                    output.positionCS = float4(0.0f, 0.0f, 0.0f, 0.0f);
                    output.temperature = 0.0f;
                    output.age = 0.0f;
                    output.size = 0.0f;
                    return output;
                }

                // Read particle data
                Particle p = _ParticleBuffer[idx];

                // Skip dead particles
                if ((p.flags & 1u) == 0u)
                {
                    output.positionCS = float4(0.0f, 0.0f, 0.0f, 0.0f);
                    output.temperature = 0.0f;
                    output.age = 0.0f;
                    output.size = 0.0f;
                    return output;
                }

                // World to clip space
                float4 positionCS = mul(float4(p.position, 1.0f), unity_MatrixVP);
                output.positionCS = positionCS;

                // Particle properties
                output.temperature = p.temperature;
                output.age = p.age;
                output.size = 0.1f * max(0.1f, 1.0f - p.age / 30.0f);

                return output;
            }

            // ========== Fragment Shader ==========
            float4 Fragment(Varyings input) : SV_Target
            {
                // Screen-space particle rendering
                float2 screenUV = frac(input.positionCS.xy);
                float2 centerOffset = screenUV * 2.0f - 1.0f;

                // Circular particle with gaussian falloff
                float alpha = ComputeParticleAlpha(centerOffset, input.size * 0.5f);

                // Temperature-based coloring
                float3 color = TemperatureToColor(input.temperature);

                // Glow effect (brighter at higher temperatures)
                float glow = input.temperature / 2.0f;
                color = color * (1.0f + glow * 1.5f);

                // Fade out with age
                float ageFade = max(0.0f, 1.0f - input.age / 30.0f);
                alpha *= ageFade;

                // Output final color
                return float4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/InternalErrorShader"
}
