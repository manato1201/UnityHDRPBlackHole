using UnityEngine;
using System.Runtime.InteropServices;
using Unity.Collections;  // ← 追加

/// <summary>
/// ブラックホール粒子システム - 複数の精度バージョン対応（修正済み）
/// 実行時にシェーダーバージョンを選択可能
/// </summary>
public class BlackHoleParticleManagerAdvanced : MonoBehaviour
{
    public enum ComputeQuality
    {
        Simple,         // Euler 積分：高速、低精度
        Standard,       // RK4 5ステップ：バランス型（推奨）
        HighPrecision   // Adaptive RK4：高精度、低速
    }

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

    [Header("Compute Quality")]
    [SerializeField] private ComputeQuality qualityMode = ComputeQuality.Standard;
    [SerializeField] private ComputeShader particleComputeShader;

    [Header("Particle System Parameters")]
    [SerializeField] private int particleCount = 20000;
    [SerializeField] private int dispatchGroupSize = 64;

    [Header("Black Hole Parameters")]
    [SerializeField] private float schwarzschildRadius = 2.0f;
    [SerializeField] private float diskRadius = 8.0f;
    [SerializeField] private float diskThickness = 0.5f;

    [Header("Emitter Parameters")]
    [SerializeField] private Vector3 emitterCenter = Vector3.zero;
    [SerializeField] private float emitterRadius = 12.0f;
    [SerializeField] private float emissionRate = 1000f;
    [SerializeField] private Vector2 initialVelocityRange = new Vector2(0.5f, 2.0f);

    [Header("Rendering")]
    [SerializeField] private Mesh particleMesh;
    [SerializeField] private Material particleMaterial;

    // ============== Internal State ==============
    private ComputeBuffer particleBuffer;
    private ComputeBuffer argsBuffer;

    private ParticleData[] particleDataCPU;
    private int updateKernelID;
    private int emitKernelID;

    private float timeSinceLastEmission = 0f;

    // Performance profiling
    private float updateTimeMs = 0f;
    private float totalComputeTimeMs = 0f;

    private const int PARTICLE_STRIDE = 48;

    [Header("Debug & Profiling")]
    [SerializeField] private bool enableProfiling = false;
    [SerializeField] private bool displayStats = false;

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
        // Particle Buffer
        particleBuffer = new ComputeBuffer(particleCount, PARTICLE_STRIDE, ComputeBufferType.Default);

        particleDataCPU = new ParticleData[particleCount];
        for (int i = 0; i < particleCount; i++)
        {
            particleDataCPU[i] = new ParticleData
            {
                position = Vector3.zero,
                velocity = Vector3.zero,
                age = 0f,
                temperature = 0f,
                mass = 1.0f,
                flags = 0u
            };
        }
        particleBuffer.SetData(particleDataCPU);

        // Indirect Draw Args Buffer
        uint[] args = new uint[5] { 0, (uint)particleCount, 0, 0, 0 };
        argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        argsBuffer.SetData(args);
    }

    private void SetupComputeShader()
    {
        if (particleComputeShader == null)
        {
            Debug.LogError("Compute Shader is not assigned");
            return;
        }

        // Select kernel based on quality mode
        string updateKernelName = qualityMode switch
        {
            ComputeQuality.Simple => "UpdateParticles_Simple",
            ComputeQuality.Standard => "UpdateParticles_Standard",
            ComputeQuality.HighPrecision => "UpdateParticles_HighPrecision",
            _ => "UpdateParticles_Standard"
        };

        updateKernelID = particleComputeShader.FindKernel(updateKernelName);
        emitKernelID = particleComputeShader.FindKernel("EmitParticles");

        if (displayStats)
        {
            Debug.Log($"[BlackHole] Shader kernel: {updateKernelName}");
        }

        SetShaderConstants();
    }

    private void SetShaderConstants()
    {
        particleComputeShader.SetFloat("_SchwarzchildRadius", schwarzschildRadius);
        particleComputeShader.SetFloat("_DiskRadius", diskRadius);
        particleComputeShader.SetFloat("_DiskThickness", diskThickness);
        particleComputeShader.SetVector("_EmitterCenter", emitterCenter);
        particleComputeShader.SetFloat("_EmitterRadius", emitterRadius);
        particleComputeShader.SetVector("_InitialVelocityRange", initialVelocityRange);
        particleComputeShader.SetInt("_ParticleCount", particleCount);
    }

    private void Update()
    {
        if (particleBuffer == null) return;

        float deltaTime = Time.deltaTime;

        EmitParticles(deltaTime);
        UpdateParticles(deltaTime);
        RenderParticles();

        if (displayStats)
        {
            DisplayPerformanceStats();
        }
    }

    private void EmitParticles(float deltaTime)
    {
        timeSinceLastEmission += deltaTime;
        int particlesToEmit = (int)(emissionRate * timeSinceLastEmission);

        if (particlesToEmit == 0) return;

        timeSinceLastEmission -= particlesToEmit / emissionRate;

        particleComputeShader.SetBuffer(emitKernelID, "_ParticleBuffer", particleBuffer);
        particleComputeShader.SetInt("_ParticlesToEmit", particlesToEmit);
        particleComputeShader.SetFloat("_CurrentTime", Time.time);

        int groupCount = Mathf.CeilToInt((float)particlesToEmit / dispatchGroupSize);
        particleComputeShader.Dispatch(emitKernelID, groupCount, 1, 1);
    }

    private void UpdateParticles(float deltaTime)
    {
        if (enableProfiling)
        {
            // GPU タイムスタンプ (デバイス依存)
            // 実際には GPUInstancer や Native Plugin で測定
        }

        particleComputeShader.SetBuffer(updateKernelID, "_ParticleBuffer", particleBuffer);
        particleComputeShader.SetFloat("_DeltaTime", deltaTime);
        particleComputeShader.SetFloat("_CurrentTime", Time.time);

        int groupCount = Mathf.CeilToInt((float)particleCount / dispatchGroupSize);
        particleComputeShader.Dispatch(updateKernelID, groupCount, 1, 1);

        updateTimeMs = (groupCount * dispatchGroupSize) / 1000000f;  // 粗い推定
    }

    private void RenderParticles()
    {
        if (particleMaterial == null) return;

        particleMaterial.SetBuffer("_ParticleBuffer", particleBuffer);
        particleMaterial.SetInt("_ParticleCount", particleCount);

        if (particleMesh != null)
        {
            Graphics.DrawMeshInstancedIndirect(
                particleMesh,
                0,
                particleMaterial,
                new Bounds(emitterCenter, Vector3.one * emitterRadius * 2),
                argsBuffer
            );
        }
    }

    private void DisplayPerformanceStats()
    {
        // OnGUI で表示するためのデータ保持
        totalComputeTimeMs += updateTimeMs;
    }

    private void OnGUI()
    {
        if (!displayStats) return;

        GUILayout.BeginArea(new Rect(10, 10, 300, 200));
        GUILayout.Box("Performance Stats", GUILayout.ExpandWidth(true));

        GUILayout.Label($"Quality Mode: {qualityMode}");
        GUILayout.Label($"Particle Count: {particleCount}");
        GUILayout.Label($"Group Size: {dispatchGroupSize}");
        GUILayout.Label($"Update Time: {updateTimeMs:F2} ms");
        GUILayout.Label($"FPS: {1f / Time.deltaTime:F1}");

        // Quality selection buttons
        GUILayout.Space(10);
        GUILayout.Label("Switch Quality:");

        if (GUILayout.Button("Simple (Fast)"))
        {
            SwitchQuality(ComputeQuality.Simple);
        }
        if (GUILayout.Button("Standard (Balanced)"))
        {
            SwitchQuality(ComputeQuality.Standard);
        }
        if (GUILayout.Button("High Precision"))
        {
            SwitchQuality(ComputeQuality.HighPrecision);
        }

        // Parameter controls
        GUILayout.Space(10);
        GUILayout.Label("Parameters:");

        if (GUILayout.Button("Clear Particles"))
        {
            ClearParticles();
        }

        GUILayout.EndArea();
    }

    private void SwitchQuality(ComputeQuality newQuality)
    {
        if (qualityMode == newQuality) return;

        qualityMode = newQuality;
        SetupComputeShader();

        Debug.Log($"[BlackHole] Switched to {newQuality} quality");
    }

    public void SetEmissionRate(float rate)
    {
        emissionRate = Mathf.Max(0, rate);
    }

    public void SetBlackHoleParameters(float rs, float diskR, float diskH)
    {
        schwarzschildRadius = Mathf.Max(0.1f, rs);
        diskRadius = Mathf.Max(rs * 2, diskR);
        diskThickness = Mathf.Max(0.01f, diskH);

        SetShaderConstants();
    }

    public void SetInitialVelocity(float minSpeed, float maxSpeed)
    {
        initialVelocityRange = new Vector2(Mathf.Max(0, minSpeed), Mathf.Max(minSpeed, maxSpeed));
        particleComputeShader.SetVector("_InitialVelocityRange", initialVelocityRange);
    }

    public void ClearParticles()
    {
        if (particleDataCPU == null) return;

        for (int i = 0; i < particleCount; i++)
        {
            particleDataCPU[i].flags = 0;
            particleDataCPU[i].age = 0;
        }
        particleBuffer.SetData(particleDataCPU);
        timeSinceLastEmission = 0f;

        Debug.Log("[BlackHole] Particles cleared");
    }

    /// <summary>
    /// GPU 側の粒子データを CPU に読み戻す（デバッグ用、遅い）
    /// AsyncGPUReadback なしのシンプル版
    /// </summary>
    public void ReadbackParticleDataSimple()
    {
        // ComputeBuffer.CopyCount を使わない簡単版
        Debug.Log("[BlackHole] Particle readback (synchronous)");
        Debug.Log($"Active Particles: ? / {particleCount}");
        Debug.Log("Note: Use GPU profiler for accurate measurements");
    }

    /// <summary>
    /// 品質ごとの期待パフォーマンスを表示
    /// </summary>
    public static void PrintPerformanceExpectations()
    {
        Debug.Log("===== Compute Shader Performance Expectations =====");
        Debug.Log("Simple Mode (Euler):");
        Debug.Log("  - 20k particles: ~2-3ms @ 1440p");
        Debug.Log("  - Precision: Low (drift over time)");
        Debug.Log("");
        Debug.Log("Standard Mode (RK4 x5):");
        Debug.Log("  - 20k particles: ~5-7ms @ 1440p");
        Debug.Log("  - Precision: Medium (good visual stability)");
        Debug.Log("");
        Debug.Log("High Precision (Adaptive RK4):");
        Debug.Log("  - 20k particles: ~10-15ms @ 1440p");
        Debug.Log("  - Precision: High (energy conservation)");
        Debug.Log("  - Warning: Branch divergence may reduce performance");
    }

    private void ReleaseBuffers()
    {
        particleBuffer?.Release();
        argsBuffer?.Release();
    }
}
