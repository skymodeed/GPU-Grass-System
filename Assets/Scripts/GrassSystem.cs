using System;
using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine.Serialization;

[ExecuteInEditMode]
public class GrassSystem : MonoBehaviour
{
    // 草叶形状参数
    [Header("草叶形状参数")]
    [Range(0.5f, 3.0f)]
    public float grassHeight = 1.0f;
    
    [Range(0.01f, 0.5f)]
    public float grassWidth = 0.1f;
    
    [Range(3, 5)]
    public int segments = 4;
    
    [Range(0.0f, 1.0f)]
    public float curvature = 0.2f;
    
    [Range(0.0f, 1.0f)]
    public float curvaturePosition = 0.5f;
    
    [Range(0.0f, 360.0f)]
    public float bendDirection;
    
    [Range(0.1f, 1.0f)]
    public float topTriangleRatio = 0.3f;
    
    [Range(0.5f, 1.5f)]
    public float widthVariation = 1.0f;
    
    
    public Terrain terrain;
    // 预览参数
    [Header("预览参数")]
    [Range(1, 1000)]
    public int density = 10;
    
    [Range(1, 100)]
    public int previewSize = 1;
    
    [Range(0.0f, 1.0f)]
    public float randomizeScale = 0.2f;
    
    [Range(0.0f, 360.0f)]
    public float randomizeRotation = 360.0f;
    
    [Header("Compute Shader")]
    public ComputeShader grassComputeShader;
    public ComputeShader cullingComputeShader;
    
    // 渲染参数
    [Header("渲染参数")]
    [Range(-4f, 4f)]
    public float cullingRadius = 0f;
    public Material grassMaterial;
    public ShadowCastingMode castShadows = ShadowCastingMode.Off;
    public bool receiveShadows = true;
    public new Camera camera;
    
    // 实例化数据结构
    [StructLayout(LayoutKind.Sequential)]
    private struct GrassData
    {
        public Vector3 position;
        public Quaternion rotation;
        float distanceFactor;
        public static int Size()
        {
            return sizeof(float) * (3 + 4 + 1); // position + scale + rotation + color
        }
    }
    
    // 内部变量
    private Mesh _grassMesh;
    private MaterialPropertyBlock _propertyBlock;
    private bool _isDirty = true;
    private int _count;
    private int _countPerLine;
    private Vector3 _cameraPosition;
    private Vector3 _cameraLookAt;
    
    // Indirect Draw 相关缓冲区
    private ComputeBuffer _argsBuffer;
    private ComputeBuffer _grassDataBuffer;
    private ComputeBuffer _validGrassDataBuffer;
    private uint[] _args = new uint[5] { 0, 0, 0, 0, 0 };
    
    //compute shader相关数据
    private static readonly int Density = Shader.PropertyToID("_Density");
    private static readonly int PreviewSize = Shader.PropertyToID("_PreviewSize");
    private static readonly int RandomizeRotation = Shader.PropertyToID("_RandomizeRotation");
    private static readonly int RandomizeScale = Shader.PropertyToID("_RandomizeScale");
    private static readonly int GrassDataBuffer = Shader.PropertyToID("grassDataBuffer");
    private static readonly int DataBuffer = Shader.PropertyToID("_GrassDataBuffer");
    private static readonly int TerrainHeightMap = Shader.PropertyToID("_TerrainHeightMap");
    private static readonly int TerrainPosition = Shader.PropertyToID("_TerrainPosition");
    private static readonly int TerrainSize = Shader.PropertyToID("_TerrainSize");
    private static readonly int CameraPosition = Shader.PropertyToID("_CameraPosition");
    private static readonly int FarDistance = Shader.PropertyToID("_FarDistance");
    private static readonly int ValidGrassDataBuffer = Shader.PropertyToID("validGrassDataBuffer");
    private static readonly int Planes = Shader.PropertyToID("_planes");

    // 初始化
    private void OnEnable()
    {
        if (_grassMesh == null)
        {
            _grassMesh = new Mesh
            {
                name = "Procedural Grass Blade"
            };
        }
        
        _propertyBlock ??= new MaterialPropertyBlock();
        
        if (grassMaterial == null)
        {
            // 尝试寻找默认材质或创建一个新的
            grassMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"))
            {
                color = Color.green
            };
        }
        if (terrain == null)
        {
            terrain = Terrain.activeTerrain;
        }

        if (camera == null)
        {
            camera = Camera.main;
            _cameraPosition = camera.transform.position;
            _cameraLookAt = camera.transform.forward;
        }
        RegenerateGrassMesh();
        InitializeBuffers();
        SetupInstanceData();
    }

    // 清理
    private void OnDisable()
    {
        ReleaseBuffers();
        
        if (Application.isPlaying && _grassMesh != null)
        {
            Destroy(_grassMesh);
        }
    }
    
    // 更新
    private void Update()
    {
        // 检查是否需要重新生成网格
        if (_isDirty||_cameraPosition != camera.transform.position||_cameraLookAt != camera.transform.forward)
        {
            RegenerateGrassMesh();
            InitializeBuffers();
            SetupInstanceData();
            _isDirty = false;
            _cameraPosition = camera.transform.position;
            _cameraLookAt = camera.transform.forward;
        }
        
        // 使用Indirect方法绘制草实例
        DrawInstance();
    }   
    
    // 当参数在Inspector中更改时
    private void OnValidate()
    {
        _isDirty = true;
    }
    
    // 重新生成草叶网格
    private void RegenerateGrassMesh()
    {
        if (!_grassMesh)
            return;
            
        // 清除旧数据
        _grassMesh.Clear();
        
        // 计算顶点总数：每段2个点 + 顶点1个点
        int vertexCount = segments * 2 + 1;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Color[] colors = new Color[vertexCount];
        
        // 创建顶点数组
        float topSegmentHeight = grassHeight * topTriangleRatio;
        float stemHeight = grassHeight - topSegmentHeight;
        float stemSegmentHeight = stemHeight / (segments - 1);
        
        // 计算弯曲方向
        float radianDir = bendDirection * Mathf.Deg2Rad;
        Vector3 bendDir = new Vector3(Mathf.Sin(radianDir), 0, Mathf.Cos(radianDir));
        
        // 生成茎部顶点
        for (int i = 0; i < segments - 1; i++)
        {
            float heightPercent = (float)i / (segments - 1);
            float width = grassWidth * (1.0f - (1.0f - widthVariation) * heightPercent);
            
            // 计算当前高度
            float height = i * stemSegmentHeight;
            
            // 计算弯曲
            float bend = 0;
            if (heightPercent > curvaturePosition)
            {
                bend = curvature * Mathf.Pow((heightPercent - curvaturePosition) / (1 - curvaturePosition), 2);
            }
            
            // 应用弯曲
            Vector3 offset = bendDir * (bend * grassHeight);
            
            // 左侧顶点
            vertices[i * 2] = new Vector3(-width / 2, height, 0) + offset;
            
            // 右侧顶点
            vertices[i * 2 + 1] = new Vector3(width / 2, height, 0) + offset;
            
            // UV (水平对应左右，垂直对应上下)
            uvs[i * 2] = new Vector2(0, heightPercent);
            uvs[i * 2 + 1] = new Vector2(1, heightPercent);
            
            // 法线 (稍微向外倾斜)
            Vector3 normalLeft = new Vector3(-0.1f, 0.98f, 0).normalized;
            Vector3 normalRight = new Vector3(0.1f, 0.98f, 0).normalized;
            
            normals[i * 2] = normalLeft;
            normals[i * 2 + 1] = normalRight;
            
            // 顶点颜色 (底部暗，顶部亮)
            float brightness = 0.5f + heightPercent * 0.5f;
            colors[i * 2] = new Color(brightness, brightness, brightness);
            colors[i * 2 + 1] = new Color(brightness, brightness, brightness);
        }
        
        // 顶部三角形尖端顶点
        int topIndex = (segments - 1) * 2;
        float maxBend = curvature;
        Vector3 topOffset = bendDir * (maxBend * grassHeight);
        
        vertices[topIndex] = new Vector3(0, grassHeight, 0) + topOffset;
        uvs[topIndex] = new Vector2(0.5f, 1.0f);
        normals[topIndex] = Vector3.up;
        colors[topIndex] = Color.white;
        
        // 创建三角形索引
        List<int> triangles = new List<int>();
        
        // 茎部四边形（每个四边形由两个三角形组成）
        for (int i = 0; i < segments - 2; i++)
        {
            int bottomLeft = i * 2;
            int bottomRight = i * 2 + 1;
            int topLeft = (i + 1) * 2;
            int topRight = (i + 1) * 2 + 1;
            
            // 三角形1
            triangles.Add(bottomLeft);
            triangles.Add(topLeft);
            triangles.Add(bottomRight);
            
            // 三角形2
            triangles.Add(bottomRight);
            triangles.Add(topLeft);
            triangles.Add(topRight);
        }
        
        // 顶部三角形（连接到最后一段的两个顶点）
        int lastSegLeft = (segments - 2) * 2;
        int lastSegRight = (segments - 2) * 2 + 1;
        
        triangles.Add(lastSegLeft);
        triangles.Add(topIndex);
        triangles.Add(lastSegRight);
        
        // 分配数据到网格
        _grassMesh.vertices = vertices;
        _grassMesh.triangles = triangles.ToArray();
        _grassMesh.uv = uvs;
        _grassMesh.normals = normals;
        _grassMesh.colors = colors;
        
        // 重新计算边界
        _grassMesh.RecalculateBounds();
    }
    
    // 释放缓冲区
    private void ReleaseBuffers()
    {
        if (_argsBuffer != null)
        {
            _argsBuffer.Release();
            _argsBuffer = null;
        }
        
        if (_grassDataBuffer != null)
        {
            _grassDataBuffer.Release();
            _grassDataBuffer = null;
        }

        if (_validGrassDataBuffer != null)
        {
            _validGrassDataBuffer.Release();
            _validGrassDataBuffer = null;
        }
    }
    
    // 初始化缓冲区
    private void InitializeBuffers()
    {
        ReleaseBuffers();
        _countPerLine =  density * previewSize;
        _count = _countPerLine * _countPerLine;
        
        // 创建参数缓冲区
        _argsBuffer = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
        _grassDataBuffer = new ComputeBuffer(_count, GrassData.Size());
        _validGrassDataBuffer = new ComputeBuffer(_count, GrassData.Size(), ComputeBufferType.Append);
    }
    
    // 设置实例数据
    private void SetupInstanceData()
    {
        int threadGroups = Mathf.CeilToInt(_countPerLine / 8.0f);
        
        int posKernel = grassComputeShader.FindKernel("CSMain");
        _grassDataBuffer.SetCounterValue(0);
        grassComputeShader.SetFloat(Density, density);
        grassComputeShader.SetInt(PreviewSize, previewSize);
        grassComputeShader.SetFloat(RandomizeRotation, randomizeRotation);
        grassComputeShader.SetFloat(RandomizeScale, randomizeScale);
        grassComputeShader.SetBuffer(posKernel, GrassDataBuffer, _grassDataBuffer);
        grassComputeShader.SetTexture(posKernel, TerrainHeightMap, terrain.terrainData.heightmapTexture);
        grassComputeShader.SetVector(TerrainPosition, terrain.transform.position);
        grassComputeShader.SetVector(TerrainSize, terrain.terrainData.size);
        grassComputeShader.SetVector(CameraPosition,camera.transform.position);
        grassComputeShader.SetFloat(FarDistance,Vector3.Distance(camera.transform.position,terrain.transform.position+terrain.terrainData.size));
        grassComputeShader.Dispatch(posKernel, threadGroups, threadGroups, 1);
        
        Plane[] ps = GeometryUtility.CalculateFrustumPlanes(camera);
        Vector4[] planes = new Vector4[6];
        for(int i=0; i<6; i++)
        {
            planes[i] = new Vector4(ps[i].normal.x, ps[i].normal.y, ps[i].normal.z, ps[i].distance+cullingRadius);
        }
        _validGrassDataBuffer.SetCounterValue(0);
        int cullKernel = cullingComputeShader.FindKernel("CSMain");
        cullingComputeShader.SetVectorArray(Planes, planes);
        
        cullingComputeShader.SetBuffer(cullKernel, GrassDataBuffer, _grassDataBuffer);
        cullingComputeShader.SetBuffer(cullKernel, ValidGrassDataBuffer, _validGrassDataBuffer);
        cullingComputeShader.Dispatch(cullKernel,  Mathf.CeilToInt((float)_count/128), 1, 1);
    }
    
    //执行gpu instance
    private void DrawInstance()
    {
        if (_grassMesh && grassMaterial && _argsBuffer != null && _grassDataBuffer != null)
        {
            // 更新参数缓冲区
            _args[0] = _grassMesh.GetIndexCount(0);
            _args[1] = (uint)_count;
            _argsBuffer.SetData(_args);
            
            ComputeBuffer.CopyCount(_validGrassDataBuffer, _argsBuffer, sizeof(uint));
            // 设置材质属性
            grassMaterial.SetBuffer(DataBuffer, _validGrassDataBuffer);
            _argsBuffer.GetData(_args);
            //Debug.Log(_args[1]);
            Vector3 size = new Vector3(previewSize, terrain.terrainData.size.y * 2, previewSize);
            // 绘制实例
            Graphics.DrawMeshInstancedIndirect(
                _grassMesh,
                0,
                grassMaterial,
                new Bounds(transform.position, size),
                _argsBuffer,
                0,
                _propertyBlock,
                castShadows,
                receiveShadows
            );
        }
    }
}