// VR180 双流深度视频球壳位移 shader - GPU 曲面细分版 / Unity 6
// 输入: _MainTex = 颜色纹理 (3840x3840 H.264 8bit), _DepthTex = 深度纹理 (R16 码值)
// 深度解码: R16 raw = 码值原样 (10bit 0..1023 / 12bit 0..4095), dn = raw / _DepthMax,
//           真实深度 = _Near + (_Far - _Near) * dn (log 时 d = _Near * (_Far/_Near)^dn)
// 平滑: 3x3 截尾均值 (去 min/max), 深度不连续处跳过
// 曲面细分: hull 按屏幕边长 + 深度梯度决定细分因子, domain 对每个新顶点采样深度图做径向位移
Shader "VR180/Gray10Shell"
{
    Properties
    {
        _MainTex ("Color (3840x3840)", 2D) = "black" {}
        _DepthTex ("Depth (R16)", 2D) = "gray" {}
        _Near ("Near (m)", Float) = 0.05
        _Far ("Far (m)", Float) = 5.0
        _Scale ("Scale", Float) = 1.0
        _View ("DebugView (0=正常 1=颜色 2=深度码值 3=线框)", Int) = 0
        _Smooth ("Smooth Amount", Range(0, 2)) = 1.2
        _SmoothEdge ("Smooth Edge (0..1 code)", Float) = 0.006
        _FaceCull ("Face Cull (rel per px, 0=off)", Float) = 0.04
        _DepthMax ("Depth Max Code (1023/4095)", Float) = 1023
        _TessLength ("Tess Edge Length (px)", Float) = 1.5
        _TessMax ("Tess Max", Float) = 32.0
        _GradBoost ("Tess Gradient Boost", Float) = 50.0
        _RimAngle ("Rim Angle (deg)", Float) = 80.0
        _RimMinDepth ("Rim Min Depth (m)", Float) = 0.1
        _EyeIndex ("Eye Index (-1=both)", Float) = -1
        _DepthST ("Depth UV Remap", Vector) = (1, 1, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Back    // 球壳单面: 只渲染内侧面 (mesh winding 已反转, 正面朝内)

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4 _MainTex_ST;
        sampler2D _DepthTex;
        float4 _DepthTex_ST;
        float4 _DepthTexel;   // xy = 1/(宽,高) 深度纹理像素步长, zw = (宽, 高)
        float _Near, _Far, _Scale, _Smooth, _SmoothEdge;
        float _FaceCull;   // 深度断层剔除: 片元的米制深度随屏幕像素的相对变化率阈值, 0=关闭
        float _DepthMax;   // 码值满量程: 10bit=1023, 12bit=4095 (播放端按源像素格式自动设置)
        float _LogDepth;   // 0=线性量化 (d=near+(far-near)*v), 1=对数量化 (d=near*(far/near)^v)
        float4 _ColorST;   // 颜色 UV 重映射: SBS 取左半 (0.5,1,0,0), 上下取上半 (1,0.5,0,0.5), 单目 (1,1,0,0)
        float4 _DepthST;   // 深度 UV 重映射: 双目拼接取半幅 (0.5,1,0,0)/(0.5,1,0.5,0), 单目 (1,1,0,0)
        float _EyeIndex;   // 本壳所属眼睛: 0=左 1=右, -1=双眼都画 (旧单目导出); 由 MaterialPropertyBlock 逐壳设置
        int _View;
        int _DepthVFlip;   // 视频深度 (ffmpeg rawvideo 无 vflip, top-down) 需翻转 V; 静态图已 vflip 则不翻
        float _TessLength, _TessMax, _GradBoost;
        float _RimAngle, _RimMinDepth;   // 视场边缘深度下限: 角度阈值(度) / 最小深度(米, 头显后移时由脚本加大)

        struct appdata
        {
            float4 vertex : POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        // vertex -> hull 传递结构
        struct v2h
        {
            float4 vertex : POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
            float3 bary : TEXCOORD1;              // 细分顶点在 patch 内的重心坐标 (线框模式)
            nointerpolation float tessF : TEXCOORD2;  // 本 patch 的 inside 细分因子 (整个 patch 相同)
            float dm : TEXCOORD3;                     // 位移后的米制深度: 片元里算屏幕梯度, 做断层剔除
            UNITY_VERTEX_OUTPUT_STEREO
        };

        struct tessf
        {
            float edge[3] : SV_TessFactor;
            float inside : SV_InsideTessFactor;
        };

        // 深度归一化值 dn = c/_DepthMax: _DepthTex 为 R16 unorm, tex2D 采样值 r = raw/65535
        // 这里做手动 2×2 bilinear 在整数码值层面加权 —— 避免 Unity Bilinear 后再 round 导致台阶
        float DepthCode(float2 uv)
        {
            uv = uv * _DepthST.xy + _DepthST.zw;      // 双目深度拼接: 本壳取对应半幅
            float2 duv = float2(uv.x, _DepthVFlip > 0 ? (1.0 - uv.y) : uv.y);
            float2 pix = duv * _DepthTexel.zw;         // texel 坐标 (0..W, 0..H)
            float2 fi = floor(pix);                     // texel 左下角整数坐标
            float2 fw = pix - fi;                       // frac 权重
            float2 eps = 0.5 * _DepthTexel.xy;          // Unity tex2D 采样点在 texel 中心 (+0.5 texel)
            float4 r00 = tex2Dlod(_DepthTex, float4((fi + float2(0,0)) * _DepthTexel.xy + eps, 0, 0)).r;
            float4 r10 = tex2Dlod(_DepthTex, float4((fi + float2(1,0)) * _DepthTexel.xy + eps, 0, 0)).r;
            float4 r01 = tex2Dlod(_DepthTex, float4((fi + float2(0,1)) * _DepthTexel.xy + eps, 0, 0)).r;
            float4 r11 = tex2Dlod(_DepthTex, float4((fi + float2(1,1)) * _DepthTexel.xy + eps, 0, 0)).r;
            // 各自 round 回整数码值 (保留 16bit 精度), 再按 frac 加权
            float c00 = round(r00.r * 65535.0);
            float c10 = round(r10.r * 65535.0);
            float c01 = round(r01.r * 65535.0);
            float c11 = round(r11.r * 65535.0);
            float top = lerp(c00, c10, fw.x);
            float bot = lerp(c01, c11, fw.x);
            float code = lerp(top, bot, fw.y);
            return saturate(code / _DepthMax);
        }

        float DepthSmoothed(float2 uv)
        {
            float dc = DepthCode(uv);
            if (_Smooth <= 0.001) return dc;
            float2 eps = 2.0 * _DepthTexel.xy;   // 约 2 深度像素
            float2 dirs[8] = {
                float2( 1,  0), float2(-1,  0), float2( 0,  1), float2( 0, -1),
                float2( 1,  1), float2(-1,  1), float2( 1, -1), float2(-1, -1)
            };
            // 截尾均值: 9 采样去掉 min/max, 剩余 7 个平均; 孤立尖刺被吃掉不扩散
            float sum = dc;
            float mn = dc, mx = dc;
            [unroll]
            for (int i = 0; i < 8; i++)
            {
                float d = DepthCode(uv + dirs[i] * eps);
                sum += d;
                mn = min(mn, d);
                mx = max(mx, d);
            }
            float trimmed = (sum - mn - mx) / 7.0;
            // 边缘保护: 中心与截尾均值差很大 = 真实深度不连续处, 不平滑
            if (abs(dc - trimmed) > _SmoothEdge) return dc;
            return lerp(dc, trimmed, _Smooth);
        }

        // 深度码值 v -> 米制深度 (按 meta json 的 encode 字段选择量化反解)
        float DepthMeter(float2 uv)
        {
            float v = DepthSmoothed(uv);
            return _LogDepth > 0.5 ? _Near * pow(_Far / _Near, v) : _Near + (_Far - _Near) * v;
        }

        // ---------- vertex (直通到 hull) ----------
        v2h vert(appdata v)
        {
            v2h o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_TRANSFER_INSTANCE_ID(v, o);
            o.vertex = v.vertex;
            o.uv = v.uv;
            return o;
        }

        // ---------- hull: patch 常量 (细分因子) ----------

        // 屏幕空间边长 (像素): 用于决定该边细分多少段
        float EdgeLenPx(float4 v0, float4 v1)
        {
            float4 c0 = UnityObjectToClipPos(v0);
            float4 c1 = UnityObjectToClipPos(v1);
            if (c0.w <= 0.0 || c1.w <= 0.0) return 1.0;
            float2 s0 = (c0.xy / c0.w) * 0.5 + 0.5;
            float2 s1 = (c1.xy / c1.w) * 0.5 + 0.5;
            float2 px = abs(s1 - s0) * _ScreenParams.xy;
            return max(length(px) / max(_TessLength, 1.0), 1.0);
        }

        tessf hsConst(InputPatch<v2h, 3> v, uint pid : SV_PrimitiveID)
        {
            tessf o;
            o.edge[0] = min(EdgeLenPx(v[1].vertex, v[2].vertex), _TessMax);
            o.edge[1] = min(EdgeLenPx(v[2].vertex, v[0].vertex), _TessMax);
            o.edge[2] = min(EdgeLenPx(v[0].vertex, v[1].vertex), _TessMax);
            o.inside = min(max(max(o.edge[0], o.edge[1]), o.edge[2]), _TessMax);

            // 深度梯度加成: 边两端深度差大 = 物体边缘, 额外加密细分 (显式展开, 避免可变下标)
            {
                float2 ua = v[1].uv, ub = v[2].uv;
                float d0 = DepthCode(ua), d1 = DepthCode(ub), dm = DepthCode((ua + ub) * 0.5);
                o.edge[0] = min(max(o.edge[0], saturate(abs(dm - (d0 + d1) * 0.5) * _GradBoost) * _TessMax), _TessMax);
            }
            {
                float2 ua = v[2].uv, ub = v[0].uv;
                float d0 = DepthCode(ua), d1 = DepthCode(ub), dm = DepthCode((ua + ub) * 0.5);
                o.edge[1] = min(max(o.edge[1], saturate(abs(dm - (d0 + d1) * 0.5) * _GradBoost) * _TessMax), _TessMax);
            }
            {
                float2 ua = v[0].uv, ub = v[1].uv;
                float d0 = DepthCode(ua), d1 = DepthCode(ub), dm = DepthCode((ua + ub) * 0.5);
                o.edge[2] = min(max(o.edge[2], saturate(abs(dm - (d0 + d1) * 0.5) * _GradBoost) * _TessMax), _TessMax);
            }
            return o;
        }

        [domain("tri")]
        [partitioning("integer")]
        [outputtopology("triangle_cw")]
        [outputcontrolpoints(3)]
        [patchconstantfunc("hsConst")]
        v2h hsMain(InputPatch<v2h, 3> v, uint id : SV_OutputControlPointID, uint pid : SV_PrimitiveID)
        {
            return v[id];
        }

        // ---------- domain: 重心插值 + 深度位移 ----------

        [domain("tri")]
        v2f domMain(tessf tf, OutputPatch<v2h, 3> v, float3 bary : SV_DomainLocation, uint pid : SV_PrimitiveID)
        {
            v2f o;
            UNITY_SETUP_INSTANCE_ID(v[0]);
            UNITY_INITIALIZE_OUTPUT(v2f, o);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            float3 p = v[0].vertex.xyz * bary.x + v[1].vertex.xyz * bary.y + v[2].vertex.xyz * bary.z;
            float2 uv = v[0].uv * bary.x + v[1].uv * bary.y + v[2].uv * bary.z;

            // 每个细分顶点采样深度图, 沿径向收缩到真实深度
            float depth = DepthMeter(uv);
            // 周边深度下限: 视场边缘 (|方位角| 或 |俯仰角| > _RimAngle) 的深度值不可靠, 头显移动后
            // 暴露为朝向相机的碎面片. 直接把边缘区域深度抬高到不小于 _RimMinDepth (5° 软化过渡带)
            float3 dir0 = normalize(p);                  // 位移前的单位球方向
            float rimRad = radians(_RimAngle);
            float az = abs(atan2(dir0.x, dir0.z));       // 方位角 (相对正前方)
            float el = abs(asin(saturate(dir0.y)));      // 俯仰角
            float rimW = smoothstep(rimRad - 0.087, rimRad, max(az, el));
            depth = lerp(depth, max(depth, _RimMinDepth), rimW);
            p = dir0 * depth * _Scale;

            o.pos = UnityObjectToClipPos(p);
            o.dm = depth;         // 米制深度传给片元, 用于断层剔除
            o.uv = uv;
            o.bary = bary;        // 重心坐标, 线框模式用
            o.tessF = tf.inside;  // patch 细分因子, 线框密度跟随它
            return o;
        }

        // ---------- fragment ----------

        fixed4 frag(v2f i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            // 双目分眼: 本壳只属于一只眼, 不匹配的眼整壳丢弃
            // unity_StereoEyeIndex 任何渲染模式下都有声明, 无需宏守卫:
            //   multipass = Unity 每眼自动设置的 uniform;  instanced/multiview = 上一行宏从 stereoTargetEyeIndex 恢复;  单目 = 恒 0
            if (_EyeIndex > -0.5 && (int)unity_StereoEyeIndex != (int)(_EyeIndex + 0.5f))
                discard;
            // 线框模式: 细分网格跟随 tessFactor, 凸起顶点染红显示位移细节
            if (_View == 3)
            {
                // frac(bary * tessF) 在 patch 内重复, 画出跟随细分密度的网格
                float3 g = frac(i.bary * i.tessF);
                float3 dg = fwidth(g) + 1e-6;
                float3 ag = smoothstep(float3(0, 0, 0), dg * 1.5, g);
                float edge = 1.0 - min(min(ag.x, ag.y), ag.z);
                // 位移凸起: 位移越大越红 (相对 _Scale)
                float disp = saturate((DepthMeter(i.uv) - _Near) / max(_Scale, 0.001));
                float3 baseCol = lerp(float3(0.03, 0.06, 0.03), float3(0.3, 0.05, 0.05), disp);
                float3 lineCol = lerp(float3(0.15, 1.0, 0.35), float3(1.0, 0.3, 0.2), disp);
                return fixed4(lerp(baseCol, lineCol, edge), 1);
            }
            // 颜色按输入布局重映射 (SBS 左半 / TB 上半 / 单目全幅), 深度始终单目
            float2 cuv = i.uv * _ColorST.xy + _ColorST.zw;
            if (_View == 1)   // 颜色原样
                return fixed4(tex2D(_MainTex, cuv).rgb, 1);
            if (_View == 2)   // 深度码值
            {
                float d = DepthCode(i.uv);
                return fixed4(d, d, d, 1);
            }
            return fixed4(tex2D(_MainTex, cuv).rgb, 1);
        }
        ENDCG

        Pass
        {
            Name "Main"
            CGPROGRAM
            #pragma target 4.6
            #pragma vertex vert
            #pragma hull hsMain
            #pragma domain domMain
            #pragma fragment frag
            #pragma multi_compile_instancing
            ENDCG
        }
    }
}
