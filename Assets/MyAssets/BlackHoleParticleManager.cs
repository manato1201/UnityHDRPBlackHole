using UnityEngine;
using System.Runtime.InteropServices;

/// <summary>
/// ブラックホール周辺の粒子シミュレーション管理クラス
/// Compute Shader で粒子軌道を計算し、Transform が不要な GPU駆動型パーティクルシステム
/// </summary>
public class BlackHoleParticleManager : MonoBehaviour
{
    [System.Serializable]
    public struct ParticleData
    {
        public Vector3 position;      // ワールド座標
        public Vector3 velocity;      // 速度ベクトル [km/s]
        public float age;             // 粒子の年齢 [秒]
        public float temperature;     // 温度指数 (速度から計算される)
        public float mass;            // 粒子質量（現在は未使用、拡張用）
        public uint flags;            // ビットマスク: bit0=alive, bit1-31=reserved
    }

    [Header("Particle System Parameters")]
    [SerializeField] private int particleCount = 10000;
    [SerializeField] private int dispatchGroupSize = 64;
    
    [Header("Black Hole Parameters")]
    [SerializeField] private float schwarzschildRadius = 2.0f;  // 事象の地平面半径 [相対単位]
    [SerializeField] private float diskRadius = 8.0f;           // 降着円盤の外側半径
    [SerializeField] private float diskThickness = 0.5f;        // 円盤の厚さ
    
    [Header("Emitter Parameters")]
    [SerializeField] private Vector3 emitterCenter = Vector3.zero;
    [SerializeField] private float emitterRadius = 12.0f;       // 粒子スポーン範囲
    [SerializeField] private float emissionRate = 1000f;        // 1秒あたりのスポーン数
    [SerializeField] private Vector2 initialVelocityRange = new Vector2(0.5f, 2.0f);
    
    [Header("Compute Shader")]
    [SerializeField] private ComputeShader particleComputeShader;
    
    [Header("Rendering")]
    [SerializeField] private Mesh particleMesh;  // Point cloud 用メッシュ（頂点1個）
    [SerializeField] private Material particleMaterial;
    [SerializeField] private Material trailMaterial;  // Trail 描画用（オプション）
    
    // ============== Internal State ==============
    private ComputeBuffer particleBuffer;
    private ComputeBuffer particleBufferRead;  // ダブルバッファリング用
    private ComputeBuffer argsBuffer;
    private ComputeBuffer emitterBuffer;
    
    private ParticleData[] particleDataCPU;  // CPU側キャッシュ（デバッグ用）
    private int computeKernelID;
    private int emitterKernelID;
    
    private float timeSinceLastEmission = 0f;
    private int aliveParticleCount = 0;
    
    // ============== Constants ==============
    private const int PARTICLE_STRIDE = 48;  // sizeof(ParticleData) = 3*4 + 3*4 + 4 + 4 + 4 + 4 = 48 bytes

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
        // ========== Particle Buffer ==========
        particleBuffer = new ComputeBuffer(particleCount, PARTICLE_STRIDE, ComputeBufferType.Default);
        particleBufferRead = new ComputeBuffer(particleCount, PARTICLE_STRIDE, ComputeBufferType.Default);
        
        // 初期化：全粒子を "死亡状態" で配置
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
                flags = 0u  // bit0=0 → dead
            };
        }
        particleBuffer.SetData(particleDataCPU);
        particleBufferRead.SetData(particleDataCPU);

        // ========== Indirect Draw Args Buffer ==========
        // Mesh.DrawMeshInstancedIndirect 用：[indexCountPerInstance, instanceCount, startIndex, baseVertex, startInstance]
        uint[] args = new uint[5] { 0, (uint)particleCount, 0, 0, 0 };
        argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        argsBuffer.SetData(args);

        // ========== Emitter Control Buffer ==========
        int[] emitterData = new int[4]
        {
            particleCount,        // total capacity
            0,                     // next spawn index (環形バッファ)
            0,                     // emission counter
            0                      // padding
        };
        emitterBuffer = new ComputeBuffer(1, 4 * sizeof(int), ComputeBufferType.Default);
        emitterBuffer.SetData(emitterData);
    }

    private void SetupComputeShader()
    {
        if (particleComputeShader == null)
        {
            Debug.LogError("Particle Compute Shader が未設定です");
            return;
        }

        // カーネルID の取得
        computeKernelID = particleComputeShader.FindKernel("UpdateParticles");
        emitterKernelID = particleComputeShader.FindKernel("EmitParticles");

        // Compute Shader に定数をセット
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

        // ============== Emission Phase ==============
        EmitParticles(deltaTime);

        // ============== Simulation Phase ==============
        UpdateParticles(deltaTime);

        // ============== Rendering Phase ==============
        RenderParticles();
    }

    private void EmitParticles(float deltaTime)
    {
        // スポーン数の計算（deltaTime に比例）
        timeSinceLastEmission += deltaTime;
        int particlesToEmit = (int)(emissionRate * timeSinceLastEmission);

        if (particlesToEmit == 0) return;

        timeSinceLastEmission -= particlesToEmit / emissionRate;

        // Emitter Kernel をセット
        particleComputeShader.SetBuffer(emitterKernelID, "_ParticleBuffer", particleBuffer);
        particleComputeShader.SetBuffer(emitterKernelID, "_EmitterBuffer", emitterBuffer);
        particleComputeShader.SetInt("_ParticlesToEmit", particlesToEmit);
        particleComputeShader.SetFloat("_Time", Time.time);

        // Dispatch: 1次元で十分
        int groupCount = Mathf.CeilToInt((float)particlesToEmit / dispatchGroupSize);
        particleComputeShader.Dispatch(emitterKernelID, groupCount, 1, 1);
    }

    private void UpdateParticles(float deltaTime)
    {
        // Update Kernel をセット
        particleComputeShader.SetBuffer(computeKernelID, "_ParticleBuffer", particleBuffer);
        particleComputeShader.SetFloat("_DeltaTime", deltaTime);
        particleComputeShader.SetFloat("_CurrentTime", Time.time);

        // 全粒子を更新：スレッドグループサイズで割る
        int groupCount = Mathf.CeilToInt((float)particleCount / dispatchGroupSize);
        particleComputeShader.Dispatch(computeKernelID, groupCount, 1, 1);
    }

    private void RenderParticles()
    {
        if (particleMaterial == null) return;

        // Material にバッファを渡す
        particleMaterial.SetBuffer("_ParticleBuffer", particleBuffer);
        particleMaterial.SetInt("_ParticleCount", particleCount);

        // Indirect Draw：GPU が alive 粒子数を数える必要がある場合は別途カウント
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

    /// <summary>
    /// デバッグ用：CPU側に粒子データを読み戻す
    /// 頻繁に呼ぶと遅いので、検証時のみ使用
    /// </summary>
    public void ReadbackParticleData()
    {
        if (particleBuffer == null) return;

        AsyncGPUReadback.Request(particleBuffer, (request) =>
        {
            if (request.hasError)
            {
                Debug.LogError("Particle データ読み戻しエラー");
                return;
            }

            var data = request.GetData<ParticleData>();
            int aliveCount = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if ((data[i].flags & 1u) != 0) aliveCount++;
            }
            Debug.Log($"Active Particles: {aliveCount} / {particleCount}");
        });
    }

    /// <summary>
    /// 粒子バッファをクリア
    /// </summary>
    public void ClearParticles()
    {
        if (particleDataCPU == null) return;

        for (int i = 0; i < particleCount; i++)
        {
            particleDataCPU[i].flags = 0;
        }
        particleBuffer.SetData(particleDataCPU);
        timeSinceLastEmission = 0f;
    }

    private void ReleaseBuffers()
    {
        particleBuffer?.Release();
        particleBufferRead?.Release();
        argsBuffer?.Release();
        emitterBuffer?.Release();
    }

    // ============== Inspector から動的に調整可能なメソッド ==============

    public void SetEmissionRate(float rate)
    {
        emissionRate = Mathf.Max(0, rate);
    }

    public void SetInitialVelocity(float minSpeed, float maxSpeed)
    {
        initialVelocityRange = new Vector2(Mathf.Max(0, minSpeed), Mathf.Max(minSpeed, maxSpeed));
        if (particleComputeShader != null)
        {
            particleComputeShader.SetVector("_InitialVelocityRange", initialVelocityRange);
        }
    }

    public void SetBlackHoleParameters(float rs, float diskR, float diskH)
    {
        schwarzschildRadius = Mathf.Max(0.1f, rs);
        diskRadius = Mathf.Max(rs * 2, diskR);
        diskThickness = Mathf.Max(0.01f, diskH);

        if (particleComputeShader != null)
        {
            particleComputeShader.SetFloat("_SchwarzchildRadius", schwarzschildRadius);
            particleComputeShader.SetFloat("_DiskRadius", diskRadius);
            particleComputeShader.SetFloat("_DiskThickness", diskThickness);
        }
    }

    public int GetAliveParticleCount() => aliveParticleCount;
    public int GetTotalParticleCount() => particleCount;
}
