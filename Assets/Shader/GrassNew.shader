Shader "Custom/GrassNew"
{
    Properties
    {
        _Color ("Color", Color) = (0.5, 0.8, 0.2, 1)
        _ColorVariation ("Color Variation", Range(0, 1)) = 0.1
        
        [Header(Wind)]
        _WindStrength ("Wind Strength", Range(0, 2)) = 0.5
        _WindSpeed ("Wind Speed", Range(0, 10)) = 1.0
        _WindScale ("Wind Scale", Range(0.1, 10)) = 1.0
        _WindStartHeight ("Wind Start Height", Range(0, 1)) = 0.2
        _WindHeightInfluence ("Wind Height Influence", Range(1, 5)) = 2.0
        
        [Header(Highlights)]
        _SpecColor ("Specular Color", Color) = (1, 1, 0.8, 1)
        _SpecPower ("Specular Power", Range(1, 128)) = 64
        _SpecIntensity ("Specular Intensity", Range(0, 2)) = 0.5
        _TipHighlightStart ("Tip Highlight Start", Range(0.5, 1)) = 0.7
        _TipHighlightSharpness ("Tip Highlight Sharpness", Range(1, 16)) = 8
    }
    
    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniverPipeline"
            "RenderType" = "Opaque"
        }
        Cull off
        
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        
        // 实例数据结构体
        struct GrassData
        {
            float3 position;
            float4 rotation; // 四元数表示旋转
            float distanceFactor;
        };
        
        // 实例数据缓冲区
        StructuredBuffer<GrassData> _GrassDataBuffer;
        
        // 四元数旋转函数
        float3 RotateVector(float3 v, float4 r)
        {
            float3 v2 = cross(r.xyz, v) * 2.0;
            return v + r.w * v2 + cross(r.xyz, v2);
        }
        ENDHLSL
        
        Pass
        {
            Name "ForwardLit"
            Tags{"LightMode"="UniversalForward"}
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SCREEN_SPACE_OCCLUSION
            
            struct appdata
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            
            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float3 normal : NORMAL;
                float4 color : COLOR;
                float3 positionWS : TEXCOORD1;
                float3 viewDir : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };
            
            half _WindStrength;
            half _WindSpeed;
            half _WindScale;
            half _WindStartHeight;
            half _WindHeightInfluence;
            float4 _Color;
            half _ColorVariation;
            float4 _SpecColor;
            half _SpecPower;
            half _SpecIntensity;
            half _TipHighlightStart;
            half _TipHighlightSharpness;
            
            v2f vert (appdata v,uint id: SV_InstanceID)
            {
                v2f o;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                
                // 获取实例数据
                GrassData data = _GrassDataBuffer[id];
                float Variance = GenerateHashedRandomFloat(id);
                // 应用实例变换
                float3 scaledPos = v.positionOS.xyz * lerp(0.7,1,Variance);
                float3 rotatedPos = RotateVector(scaledPos, data.rotation);
                float3 finalPos = rotatedPos + data.position;
                
                // 添加风力效果 - 只对超过最小高度的部分应用
                float windTime = _Time.y * _WindSpeed;
                float windValue = sin(windTime + data.position.x * _WindScale) * _WindStrength * Variance;
                
                // 计算风力影响因子 - 使用平滑的渐变效果，使风力从底部向顶部逐渐增强
                float heightRatio = v.uv.y; // UV的Y坐标表示从底到顶的相对高度
                float windFactor = 0;
                
                if (heightRatio > _WindStartHeight) {
                    // 使用平滑的幂函数使草尖的摇摆更加明显
                    windFactor = pow((heightRatio - _WindStartHeight) / (1.0 - _WindStartHeight), _WindHeightInfluence);
                }
                
                // 应用风力
                finalPos.x += windValue * windFactor;
                // 添加轻微的Z轴运动
                finalPos.z += windValue * windFactor * 0.3;
                
                // 转换到剪裁空间
                o.vertex = TransformObjectToHClip(finalPos);
                
                // 旋转法线
                o.normal = normalize(RotateVector(v.normalOS, data.rotation));
                
                // 传递纹理坐标
                o.uv = v.uv;
                
                // 混合顶点色与实例色
                o.color = clamp(Variance,_ColorVariation,1);
                o.color.a = data.distanceFactor;
                // 世界坐标用于其他效果
                o.positionWS = finalPos;
                
                // 计算视线方向用于高光计算
                float3 worldCameraPos = _WorldSpaceCameraPos.xyz;
                o.viewDir = normalize(worldCameraPos - finalPos);
                
                return o;
            }
            
            half4 frag (v2f i) : SV_Target
            {
                Light mainLight = GetMainLight();
                // 计算基础颜色(添加颜色变化)
                float3 baseColor = _Color.rgb * i.color.rgb;
                
                // 从底到顶的渐变效果
                float grassGradient = lerp(0.7, 1.0, i.uv.y);
                baseColor *= grassGradient;
                
                // 基础光照 - 简单的半兰伯特模型
                float3 normal = normalize(i.normal);
                float ndotl = saturate(dot(normal, mainLight.direction)) * 0.5 + 0.5;
                
                // 计算高光 - 但只在草的顶部区域
                float3 halfVector = normalize(mainLight.direction + i.viewDir);
                float specularFactor = pow(max(0, dot(normal, halfVector)), 1/_SpecPower);
                
                // 创建草尖高光掩码 - 只在接近顶部的区域显示高光
                float tipMask = 0;
                if (i.uv.y > _TipHighlightStart) {
                    // 使用幂函数创建锐利的过渡
                    tipMask = pow((i.uv.y - _TipHighlightStart) / (1.0 - _TipHighlightStart), _TipHighlightSharpness);
                }
                
                // 应用高光
                float3 specular = _SpecColor.rgb * specularFactor * _SpecIntensity * tipMask * i.color.a;
                
                // 最终颜色 = 基础颜色 * 纹理 * 光照 + 高光
                half4 finalColor;
                finalColor.rgb = (baseColor + specular)  * mainLight.color * ndotl;
                finalColor.a = 1;

                #ifdef _ADDITIONAL_LIGHTS
                uint additionalLightsCount = GetAdditionalLightsCount();
                for (uint lightIndex = 0u; lightIndex < additionalLightsCount; ++lightIndex)
                {
                    Light light = GetAdditionalLight(lightIndex, i.positionWS);
                    halfVector = normalize(light.direction + i.viewDir);
                    specularFactor = pow(max(0, dot(normal, halfVector)), _SpecPower);
                    ndotl = max(dot(normal, light.direction), 0.0);
                    specular = _SpecColor.rgb * specularFactor * _SpecIntensity * tipMask;
                    finalColor.rgb += (baseColor + specular)  * light.color * light.distanceAttenuation * ndotl;
                }
                #endif
                
                return finalColor;
            }
            ENDHLSL
        }
        
        // 投射阴影
        Pass
        {
            Name "ShadowCaster"
            Tags{"LightMode" = "ShadowCaster"}
            
            ZWrite On
            ZTest LEqual
            
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma target 4.5
            
            struct ShadowAppdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };
            
            struct ShadowV2F
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };
            
            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff;
            half _WindStrength;
            half _WindSpeed;
            half _WindScale;
            half _WindStartHeight;
            half _WindHeightInfluence;
            
            ShadowV2F ShadowVert(ShadowAppdata v)
            {
                ShadowV2F o;
                
                // 获取实例数据
                GrassData data = _GrassDataBuffer[v.instanceID];
                
                // 应用实例变换
                float3 scaledPos = v.vertex.xyz;
                float3 rotatedPos = RotateVector(scaledPos, data.rotation);
                float3 finalPos = rotatedPos + data.position;
                
                // 风力效果 - 与主渲染通道保持一致
                float windTime = _Time.y * _WindSpeed;
                float windValue = sin(windTime + data.position.x * _WindScale) * _WindStrength;
                
                float heightRatio = v.uv.y;
                float windFactor = 0;
                
                if (heightRatio > _WindStartHeight) {
                    windFactor = pow((heightRatio - _WindStartHeight) / (1.0 - _WindStartHeight), _WindHeightInfluence);
                }
                
                finalPos.x += windValue * windFactor;
                finalPos.z += windValue * windFactor * 0.3;
                
                o.pos = TransformObjectToHClip(finalPos);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                
                return o;
            }
            
            half4 ShadowFrag(ShadowV2F i) : SV_Target
            {
                half alpha = tex2D(_MainTex, i.uv).a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}