using UnityEngine;
using System.Runtime.InteropServices;
using Unity.Collections;  // ← 追加

/// <summary>
/// ブラックホール粒子システム - 動作確認版（修正済み）
/// 最小限の実装で速く動かす
/// </summary>
public class BlackHoleParticleManager : MonoBehaviour
{
    [System.Serializable]
    public struct ParticleData
    {
        public Vector3 position;
        public float temperature;

        public Vector3 velocity;
        public float age;

        public uint flags;
        public float mass;
        public uint _pad0;
        public uint _pad1;
    }

    [Header("Particle System")]
    [SerializeField] private int particleCount = 10000;
    [SerializeField] private int dispatchGroupSize = 64;

    [Header("Black Hole")]
    [SerializeField] private float schwarzschildRadius = 2.0f;
    [SerializeField] private float diskRadius = 8.0f;
    [SerializeField] private float diskThickness = 0.5f;

    [Header("Emitter")]
    [SerializeField] private Vector3 emitterCenter = Vector3.zero;
    [SerializeField] private float emitterRadius = 12.0f;
    [SerializeField] private float emissionRate = 1000f;
    [SerializeField] private Vector2 initialVelocityRange = new Vector2(0.5f, 2.0f);

    [Header("Shaders & Materials")]
    [SerializeField] private ComputeShader computeShader;
    [SerializeField] private Material particleMaterial;
    [SerializeField] private Mesh particleMesh;

    // ========== Internal ==========
    private ComputeBuffer particleBuffer;
    private ComputeBuffer argsBuffer;

    private int updateKernelID;
    private int emitKernelID;

    private float timeSinceLastEmission = 0f;
    private const int PARTICLE_STRIDE = 48;

    private void OnEnable()
    {
        InitializeBuffers();
        SetupComputeShader();
    }

    private void OnDisable()
    {
        ReleaseBuffers();
    }

    private void InitializeBuffers()
    {
        Debug.Log("[BlackHole] Initializing particle buffers...");

        // Particle Buffer
        particleBuffer = new ComputeBuffer(particleCount, PARTICLE_STRIDE);

        ParticleData[] particles = new ParticleData[particleCount];
        for (int i = 0; i < particleCount; i++)
        {
            particles[i] = new ParticleData
            {
                position = Vector3.zero,
                velocity = Vector3.zero,
                age = 0f,
                temperature = 0f,
                mass = 1.0f,
                flags = 0u
            };
        }
        particleBuffer.SetData(particles);

        // Indirect Draw Args
        uint[] args = new uint[5] { 0, (uint)particleCount, 0, 0, 0 };
        argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        argsBuffer.SetData(args);

        Debug.Log($"[BlackHole] Buffers initialized: {particleCount} particles");
    }

    private void SetupComputeShader()
    {
        if (computeShader == null)
        {
            Debug.LogError("[BlackHole] Compute Shader が設定されていません");
            return;
        }

        updateKernelID = computeShader.FindKernel("UpdateParticles");
        emitKernelID = computeShader.FindKernel("EmitParticles");

        // Constants
        computeShader.SetFloat("_SchwarzchildRadius", schwarzschildRadius);
        computeShader.SetFloat("_DiskRadius", diskRadius);
        computeShader.SetFloat("_DiskThickness", diskThickness);
        computeShader.SetVector("_EmitterCenter", emitterCenter);
        computeShader.SetFloat("_EmitterRadius", emitterRadius);
        computeShader.SetVector("_InitialVelocityRange", initialVelocityRange);
        computeShader.SetInt("_ParticleCount", particleCount);

        Debug.Log("[BlackHole] Compute Shader setup complete");
    }

    private void Update()
    {
        if (particleBuffer == null)
            return;

        float deltaTime = Time.deltaTime;

        // Emission
        EmitParticles(deltaTime);

        // Update
        UpdateParticles(deltaTime);

        // Rendering
        RenderParticles();
    }

    private void EmitParticles(float deltaTime)
    {
        timeSinceLastEmission += deltaTime;
        int particlesToEmit = (int)(emissionRate * timeSinceLastEmission);

        if (particlesToEmit <= 0)
            return;

        timeSinceLastEmission -= particlesToEmit / emissionRate;

        computeShader.SetBuffer(emitKernelID, "_ParticleBuffer", particleBuffer);
        computeShader.SetInt("_ParticlesToEmit", particlesToEmit);
        computeShader.SetFloat("_CurrentTime", Time.time);

        int groupCount = Mathf.CeilToInt((float)particlesToEmit / dispatchGroupSize);
        computeShader.Dispatch(emitKernelID, groupCount, 1, 1);
    }

    private void UpdateParticles(float deltaTime)
    {
        computeShader.SetBuffer(updateKernelID, "_ParticleBuffer", particleBuffer);
        computeShader.SetFloat("_DeltaTime", deltaTime);
        computeShader.SetFloat("_CurrentTime", Time.time);

        int groupCount = Mathf.CeilToInt((float)particleCount / dispatchGroupSize);
        computeShader.Dispatch(updateKernelID, groupCount, 1, 1);
    }

    private void RenderParticles()
    {
        if (particleMaterial == null || particleMesh == null)
            return;

        particleMaterial.SetBuffer("_ParticleBuffer", particleBuffer);
        particleMaterial.SetInt("_ParticleCount", particleCount);

        Graphics.DrawMeshInstancedIndirect(
            particleMesh,
            0,
            particleMaterial,
            new Bounds(emitterCenter, Vector3.one * (emitterRadius + schwarzschildRadius) * 2),
            argsBuffer
        );
    }

    private void ReleaseBuffers()
    {
        particleBuffer?.Release();
        argsBuffer?.Release();
        Debug.Log("[BlackHole] Buffers released");
    }

    // ========== Debugging ==========
    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 300, 150));
        GUILayout.Box("Black Hole Particle System", GUILayout.ExpandWidth(true));

        GUILayout.Label($"Particle Count: {particleCount}");
        GUILayout.Label($"FPS: {1f / Time.deltaTime:F1}");
        GUILayout.Label($"Frame Time: {Time.deltaTime * 1000:F2} ms");

        if (GUILayout.Button("Clear Particles"))
        {
            ClearParticles();
        }

        if (GUILayout.Button("Print Debug Info"))
        {
            PrintDebugInfo();
        }

        GUILayout.EndArea();
    }

    private void ClearParticles()
    {
        var particles = new ParticleData[particleCount];
        for (int i = 0; i < particleCount; i++)
        {
            particles[i].flags = 0u;
        }
        particleBuffer.SetData(particles);
        timeSinceLastEmission = 0f;
        Debug.Log("[BlackHole] Particles cleared");
    }

    private void PrintDebugInfo()
    {
        Debug.Log($"=== Black Hole Particle System Debug Info ===");
        Debug.Log($"Particle Count: {particleCount}");
        Debug.Log($"Schwarzschild Radius: {schwarzschildRadius}");
        Debug.Log($"Disk Radius: {diskRadius}");
        Debug.Log($"Emitter Radius: {emitterRadius}");
        Debug.Log($"Emission Rate: {emissionRate} /sec");
        Debug.Log($"Initial Velocity Range: {initialVelocityRange.x} ~ {initialVelocityRange.y}");
        Debug.Log($"Compute Shader: {computeShader?.name ?? "NOT SET"}");
        Debug.Log($"Particle Material: {particleMaterial?.name ?? "NOT SET"}");
    }
}
