using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.EventSystems;
using UnityEngine.Video;
using UnityEngine.XR;
using UnityEngine.SpatialTracking;
using UnityEngine.UI;

/// <summary>
/// VR180 双流深度视频播放器 (Unity 6)
/// 双流: 颜色 H.264 8bit 3840² mp4 + 深度 HEVC 8/10/12bit 1024² (gray 码值, yuv420p/10le/12le)
/// 解码: Unity VideoPlayer + Windows Media Foundation, RenderTexture 模式 (D3D11 共享纹理)
/// 循环: sidecar json 的 start_time/end_time(秒) 限定颜色视频只在该区间循环 (end_time=0 到末尾);
///       缺省则整段循环. 深度视频用 ffmpeg -stream_loop 自己无限循环, 播放端按"圈内相对进度"比例
///       映射到深度虚拟帧号 (每绕一圈 +深度总帧数), 因此颜色/深度时长不同也能对齐, 且全程不重启解码
/// 同步: 颜色流 master, 深度流 slave; depthLeadFrames 补偿解码→上屏延迟; 双流就绪后同时 Play
/// 渲染: 半球壳 mesh + Gray10Shell shader 顶点位移 (GPU 曲面细分)
/// 深度解码: c = round(采样值 * 65535) 为原始码值 (8bit 0..255 / 10bit 0..1023 / 12bit 0..4095, ffprobe 自动识别),
///           dn = c / MaxCode, 深度 = near + (far - near) * dn (log 时 d = near*(far/near)^dn)
///
/// 界面: 运行先进入主界面 (打开文件 / 退出 + 最近打开列表), 选择 json 后进入播放器.
///   VR (串流/一体机): 右摇杆单按=重置朝向+界面归位, 右摇杆长按=重置头部坐标, B=退出播放, A=暂停,
///                     右摇杆左右=进度 上下=音量, Trigger=呼出/隐藏进度条 (指向按钮时=点击)
///                     界面世界锁定不跟随头显; 进入播放器时自动以当前头部位置为坐标原点
///   桌面: 按住左键拖动=旋转, WASD=平移, QE=升降, 左键单击(未拖动)=呼出/隐藏进度条, R=回到原点
/// 一体机的文件浏览器暂未实现, 主界面只保留退出.
/// </summary>
public class Gray10Player : MonoBehaviour
{
    public static Gray10Player Instance { get; private set; }

    [Header("视频源 (空 = 命令行 -color=/-depth= 指定; 主界面里选择 json 打开)")]
    public string colorPath = "";
    public string depthPath = "";

    [Header("深度映射 (米)")]
    public float nearM = 0.05f;
    public float farM = 5.0f;
    float _logDepth;   // 0=线性量化反解 1=对数量化反解, 由 sidecar json 的 encode 字段决定
    float _startTime, _endTime;   // sidecar json 播放区间(秒), end=0 表示到末尾; 缺省=整段
    string _colorLayout = "";     // sidecar json 的颜色布局: sbs_left / tb_top / mono; 空=按宽高比推断
    string _metaJsonPath = "";    // 当前播放的元数据 json
    long _startFrame, _endFrame = -1;   // 换算成颜色流帧号
    bool _rangeActive;                  // 是否只在 [startFrame, endFrame) 内循环
    bool _rangeSeeking;                 // 区间回跳 seek 尚未完成, 防止重复触发
    long _colorLoopLen;                 // 颜色一圈的帧数 (区间帧数 或 整段帧数)
    long _depthLoopBase;                // 深度虚拟帧累积偏移: 颜色每循环一圈 += 深度总帧数

    // ---- 静态图片输入 (json 的 color_file/depth_file 指向图片而非视频) ----
    bool _colorIsImage, _depthIsImage;
    Texture2D _imgColorTex, _imgDepthTex;         // 静态颜色 / 深度纹理
    int _imgDepthMax = 1023, _imgDepthBits = 10;  // 静态深度码值满量程 / 位深
    int _depthBitsHint, _depthMaxHint;            // json 的 depth_bits / depth_max (0=未指定)
    string _depthEncode = "";                     // json 的 encode: mm(16bit 毫米图) / linear / log

    [Header("球壳")]
    public int segments = 100;         // 初始细分网格密度, 越大基础网格越密
    public float depthScale = 1.0f;
    [Range(0f, 1f)] public float smoothAmt = 1.0f;
    public float smoothEdge = 0.03f;   // 深度不连续阈值 (0..1 码值空间), 超过则跳过平滑
    [Header("深度断层镂空 (露出后方背景)")]
    [Tooltip("片元米制深度的屏幕空间相对变化率阈值, 超过则丢弃该片元 (0=关闭). 跨前景/背景的面片会被剔除, 露出背景球壳")]
    public float faceCull = 0.04f;
    [Tooltip("背景球壳半径相对深度球壳最大半径的倍数 (>1 表示在后方)")]
    public float bgRadiusMul = 1.1f;
    [Header("周边碎面抑制")]
    [Tooltip("边缘深度下限总开关 (调试时可临时关闭对比效果)")]
    public bool rimClamp = true;
    [Tooltip("球壳边缘角度(度): 方位角/俯仰角超过该值的区域启用深度下限")]
    public float rimAngle = 70f;
    [Tooltip("边缘区域深度下限 (米): 静止时的最小值")]
    public float rimMinDepth = 0.2f;
    [Tooltip("头显后移时深度下限的上限 (米): 随后移距离 1:1 加大, 封顶于此值")]
    public float rimMinDepthMax = 5.0f;
    public int debugView = 0;          // 0=正常 1=颜色 2=深度码值

    [Header("GPU 曲面细分")]
    public float tessLength = 5f;      // 边长阈值(px): 三角形边超过该像素长度时细分, 值越小网格越细
    public float tessMax = 32f;        // 每条边最大细分段数, 封顶防止过细卡顿
    public float tessGradBoost = 50f;  // 深度梯度加成: 深度变化剧烈处(物体边缘)额外加密细分

    [Header("渲染质量")]
    [Range(0, 8)] public int msaa = 2;

    [Header("VR (OpenXR)")]
    public bool vrEnabled = true;
    [Range(0f, 1f)] public float posScale = 1.0f;   // 头显 6DoF 位移衰减: 0=只转不移(不穿帮), 1=完整追踪
    public float rayPitchOffset = 35f;   // 手柄射线俯仰修正(度): OpenXR grip 轴向偏上, 正数向下压
    public bool simulateVR = false;    // PC 模拟 VR: 无头显也走 VR 代码路径, 鼠标键盘代替头显/手柄 (运行时 F2 切换)

    public static bool VrActive { get; private set; }
    public static bool SimActive { get; private set; }   // 当前是 PC 模拟 VR (非真头显)

    Transform _xrOrigin;   // 相机父节点: TPD 把头部位移写入相机 localPosition, 移动 origin 即可平移玩家

    public VideoPlayer ColorVideo { get; private set; }
    public DepthPipeDecoder DepthPipe { get; } = new DepthPipeDecoder();

    Material _mat;
    MeshFilter _shellFilter;
    MeshRenderer _shellRenderer;
    Transform _shellT;
    int _builtSeg;
    MeshFilter _shellFilterR;        // 右眼球壳 (json depth_layout=sbs 时启用), 与左壳同 mesh 同材质
    MeshRenderer _shellRendererR;
    string _depthLayout = "";        // sidecar json 的深度布局: sbs=深度视频左右眼拼接; 空=单左目
    string _bgFileL = "", _bgFileR = "";   // sidecar json 的背景图路径 (相对 json 目录); 空=自动推导 <stem>_bg_L/R.png
    float _eyeIPD;                   // 实测瞳距缓存 (米), 0=未测到
    bool _ipdFallbackWarned;         // 兜底瞳距警告只打一次
    MaterialPropertyBlock _pbL;   // 左壳分眼参数 (惰性创建: Unity 不允许在字段初始化器里 new)
    MaterialPropertyBlock _pbR;   // 右壳分眼参数

    // ---- 背景球壳: 贴在深度球壳后方, 深度断层镂空处露出 ----
    MeshRenderer _bgRendererL, _bgRendererR;
    Material _bgMatL, _bgMatR;
    Texture2D _bgTexL, _bgTexR;
    bool _bgEnabled;   // 是否找到背景图 (左右眼都有才启用)
    bool _pauseBtn;
    bool _quitBtn, _rStickBtn, _lStickBtn;           // 左右手摇杆按键边沿
    float _rStickHoldTime, _lStickHoldTime;           // 左右摇杆按住累计时长
    bool _rStickLongFired, _lStickLongFired;         // 本次按住期间长按是否已触发
    const float StickLongPressSec = 0.6f;            // 摇杆长按阈值

    // ---- Windows 系统音量 P/Invoke (waveOutSetVolume: 0xFFFFFFFF=所有设备; 低 WORD 右声道, 高 WORD 左声道) ----
    [DllImport("winmm.dll")] static extern uint waveOutSetVolume(IntPtr hwo, uint dwVolume);
    static void SetSystemVolume01(float v)
    {
        v = Mathf.Clamp01(v);
        ushort w = (ushort)(v * 0xFFFF);
        uint vol = ((uint)w << 16) | w;
        waveOutSetVolume((IntPtr)0xFFFFFFFF, vol);
    }

    bool _vrRecenterDone;   // VR 头部对齐原点是否已完成
    float _cNear = -1, _cFar = -1, _cScale = -1, _cSmooth = -1, _cSmoothEdge = -1, _cRimAngle = -1;
    float _cTessLen = -1, _cTessMax = -1, _cTessGrad = -1, _cLog = -1;
    int _cView = -1;
    int _cMsaa = -1;
    float _diagT;                 // 同步诊断计时
    int _cStarved, _cDropped;     // 上次报告的断供/丢帧计数
    int _cProduced;               // 上次报告的解码产出计数
    int _cApplied;                // 上次报告的贴图计数
    float _fpsAcc;
    int _fpsCount;
    float _fpsShown;
    string _hint = "";
    bool _videoReady;

    [Header("同步 & 看门狗")]
    public float syncToleranceMs = 50f;
    public float stallTimeout = 2f;
    [Tooltip("深度目标帧超前颜色帧数: 补 ffmpeg 读出 → CPU 拷贝 → R16 上传 → 上屏 的延迟; " +
             "0=不超前; 若深度看起来晚于颜色, 调到 1~3 (深度必须已解码到该帧, 否则仍会断供)")]
    public int depthLeadFrames = 1;
    [Tooltip("限制渲染帧率 (>0 生效): 桌面 200fps 空转会与深度解码抢 GPU/CPU, 导致深度解码掉到 40fps; 0=不限制")]
    public int targetFps = 120;
    // ---- INI 持久化设置 (存程序运行目录 settings.ini) ----
    const string SettingsIniName = "settings.ini";
    public string UiLang { get; private set; } = "";   // "zh" / "en" / ""(未设置, 用系统语言)
    public bool RimClampSetting { get; private set; } = true;
    public int TargetFpsSetting { get; private set; } = 120;
    static string SettingsIniPath()
    {
        try { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, SettingsIniName); }
        catch { return Path.Combine(Directory.GetCurrentDirectory(), SettingsIniName); }
    }
    void LoadSettings()
    {
        string path = SettingsIniPath();
        try
        {
            if (!File.Exists(path))
            {
                UiLang = DetectSystemLang();
                RimClampSetting = rimClamp;
                TargetFpsSetting = targetFps;
                ApplySettings();
                Debug.Log($"[设置] 无 INI, 使用默认: lang={UiLang} rimClamp={RimClampSetting} fps={TargetFpsSetting}");
                return;
            }
            var ini = IniFile.Read(path);
            UiLang = ini.Get("ui", "lang", "");
            if (string.IsNullOrEmpty(UiLang)) UiLang = DetectSystemLang();
            RimClampSetting = ini.GetBool("video", "rimClamp", rimClamp);
            TargetFpsSetting = ini.GetInt("video", "targetFps", targetFps);
            if (TargetFpsSetting != 60 && TargetFpsSetting != 90 && TargetFpsSetting != 120) TargetFpsSetting = 120;
            ApplySettings();
            Debug.Log($"[设置] 已加载 {path}: lang={UiLang} rimClamp={RimClampSetting} fps={TargetFpsSetting}");
        }
        catch (Exception e) { Debug.LogWarning("[设置] 读取失败: " + e.Message); UiLang = DetectSystemLang(); ApplySettings(); }
    }
    void SaveSettings()
    {
        try
        {
            var ini = new IniFile();
            ini.Set("ui", "lang", UiLang);
            ini.Set("video", "rimClamp", RimClampSetting ? "true" : "false");
            ini.Set("video", "targetFps", TargetFpsSetting.ToString());
            ini.Write(SettingsIniPath());
        }
        catch (Exception e) { Debug.LogWarning("[设置] 写入失败: " + e.Message); }
    }
    static string DetectSystemLang()
    {
        var code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName?.ToLowerInvariant() ?? "";
        return code.StartsWith("zh") ? "zh" : "en";
    }
    void ApplySettings()
    {
        rimClamp = RimClampSetting;
        targetFps = TargetFpsSetting;
        if (targetFps > 0) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = targetFps; }
    }

    /// <summary>极简 INI 读写: 单 section + key=value, 支持 # ; 注释. 不依赖第三方库.</summary>
    class IniFile
    {
        readonly Dictionary<string, Dictionary<string, string>> _sec = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex _comment = new Regex(@"^\s*[#;]");
        static readonly Regex _section = new Regex(@"^\s*\[(.+?)\]\s*$");
        static readonly Regex _kv = new Regex(@"^\s*([^=\s]+)\s*=\s*(.*?)\s*$");

        public static IniFile Read(string path)
        {
            var ini = new IniFile();
            string sec = "";
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || _comment.IsMatch(line)) continue;
                var sm = _section.Match(line);
                if (sm.Success) { sec = sm.Groups[1].Value; ini.Ensure(sec); continue; }
                var km = _kv.Match(line);
                if (km.Success) { ini.GetOrAdd(sec)[km.Groups[1].Value] = km.Groups[2].Value; }
            }
            return ini;
        }
        void Ensure(string sec)
        {
            if (!_sec.ContainsKey(sec)) _sec[sec] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        Dictionary<string, string> GetOrAdd(string sec)
        {
            if (!_sec.ContainsKey(sec)) _sec[sec] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return _sec[sec];
        }
        public string Get(string sec, string key, string def)
        {
            return _sec.TryGetValue(sec, out var d) && d.TryGetValue(key, out var v) ? v : def;
        }
        public int GetInt(string sec, string key, int def) { int.TryParse(Get(sec, key, ""), out var r); return r == 0 && Get(sec, key, "") == "" ? def : r; }
        public bool GetBool(string sec, string key, bool def)
        {
            var s = (Get(sec, key, "")).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) return def;
            return s == "1" || s == "true" || s == "yes" || s == "on";
        }
        public void Set(string sec, string key, string val) { GetOrAdd(sec)[key] = val; }
        public void Write(string path)
        {
            var sb = new StringBuilder();
            foreach (var kv in _sec)
            {
                sb.Append('[').Append(kv.Key).Append("]\n");
                foreach (var k2 in kv.Value) sb.Append(k2.Key).Append(" = ").Append(k2.Value).Append('\n');
                sb.Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
        }
    }
    long _cLastFrame = -1, _cPrevFrame = -1;
    float _cStall;
    int _cKick;
    bool _cReady, _manuallyPaused;
    bool _depthPrerolled;   // 初始播放前等深度队列预填充到一定水位, 避免开头几秒深度断供而看到平面球壳
    bool _depthPrerollWarned;
    float _depthPrerollTimer;   // 预填充等待计时 (超时就放弃深度, 直接播颜色)

    RenderTexture _colorRT;
    Vector4 _colorST = new Vector4(1f, 1f, 0f, 0f);   // 颜色 UV 重映射: SBS 左半 / TB 上半 / 单目全幅
    Vector4 _colorSTR = new Vector4(1f, 1f, 0f, 0f);  // 右眼颜色 UV 重映射: SBS 右半 / TB 下半 / 同左眼
    string _layoutDesc = "单目全幅";
    double _vfps = 30.0;    // 颜色视频帧率
    long _vFrames;          // 颜色视频总帧数

    // ---- 界面 / 主界面 ⇄ 播放器 ----
    enum AppState { Home, Player }
    AppState _state = AppState.Home;
    PlayerUI _ui;
    bool _progressVisible = true;
    int loopMode = 0;          // 0=单曲循环 1=列表循环 2=随机
    float _volume = 1f;
    AudioSource _audio;
    bool _standalone;          // 一体机 (Android) 构建

    // ---- 桌面提示屏: VR 激活时桌面 backbuffer 显示 "请在头显中操作" ----
    GameObject _desktopHintGo;
    Canvas _desktopHintCanvas;
    UnityEngine.UI.Text _desktopHintTitle, _desktopHintBody;

    const string RecentKey = "vr180_recent_json";
    const string LastDirKey = "vr180_last_dir";   // 最近一次成功打开文件所在目录 (PlayerPrefs 持久化, 跨重启)
    const int RecentMax = 10;
    readonly List<string> _recents = new List<string>();
    List<string> _playlist = new List<string>();   // 上一首/下一首用的播放列表 (打开文件时快照)
    int _playIndex;
    string _currentJson = "";
    float _seekStepTimer;

    // ---- VR 控制器射线 + UI 交互 (无 XR Interaction Toolkit, 手动实现) ----
    LineRenderer _vrRay;
    bool _rTrigPrev, _lTrigPrev;   // 左右手扳机的上帧状态 (上升/下降沿检测, 换手不丢边沿)
    Slider _dragSlider;            // 当前正在拖动的滑块
    XRNode _activeHand = XRNode.RightHand;   // 当前活动手 (射线跟随)
    const float HudAutoHideSec = 1.5f;
    float _hudTimer;               // 中心 HUD 自动隐藏倒计时 (0 = 隐藏)

    public bool InPlayer { get { return _state == AppState.Player; } }

    // ---------- 生命周期 ----------

    void Awake()
    {
        Instance = this;
        ParseArgs();
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>运行时申请存储读写权限 (Android 6+).</summary>
    void RequestStoragePermissions()
    {
        if (!Permission.HasUserAuthorizedPermission(Permission.ExternalStorageWrite))
            Permission.RequestUserPermission(Permission.ExternalStorageWrite);
        if (!Permission.HasUserAuthorizedPermission(Permission.ExternalStorageRead))
            Permission.RequestUserPermission(Permission.ExternalStorageRead);
    }
#endif

    void Start()
    {
        LoadSettings();   // 读 INI + 应用 fps / rimClamp / 语言 (无 INI 用系统语言 + Inspector 默认值)
        SetupCamera();
        BuildShell();
        BuildDesktopHint();
        SetShellVisible(false);
        SetBackground(true);

        _standalone = Application.platform == RuntimePlatform.Android;
#if UNITY_ANDROID && !UNITY_EDITOR
        RequestStoragePermissions();   // targetSdk=29 + legacy 模式下仍需运行时申请读写权限
#endif
        LoadRecents();
        EnsureUI();

        // 关掉桌面显示器的 VR 镜像 blit: XR General Settings Manager 刚在 Start 初始化好,
        // activeLoader 一般已就绪, 直接 GetLoadedSubsystem<XRDisplaySubsystem> 拿实例设 None.
        // OpenXR 在某些平台可能覆盖这个偏好 (比如 SteamVR 串流),
        // 所以 UpdateHeadPose 里 VrActive 首次变 true 时还会再设一次.
        DisableDesktopMirrorOnce();

        // 命令行显式给了源 (外部脚本/自动化) 时直接进播放器; 否则先显示主界面
        if (!string.IsNullOrEmpty(colorPath) || !string.IsNullOrEmpty(depthPath))
            ActivateCommandLineSource();
        else
            ShowHome();
    }

    /// <summary>关掉桌面显示器的 VR 镜像 blit, 省一次全屏绘制 + 消除 Console 里的 Canvas 警告.</summary>
    void DisableDesktopMirrorOnce()
    {
        try
        {
            var mgr = UnityEngine.XR.Management.XRGeneralSettings.Instance?.Manager;
            if (mgr == null || mgr.activeLoader == null) return;
            var ds = mgr.activeLoader.GetLoadedSubsystem<XRDisplaySubsystem>();
            if (ds != null)
            {
                ds.SetPreferredMirrorBlitMode(XRMirrorViewBlitMode.None);
                Debug.Log("[VR] DisableDesktopMirrorOnce: MirrorBlitMode = None");
            }
        }
        catch (Exception e) { Debug.LogWarning("[VR] DisableDesktopMirrorOnce 异常 (忽略): " + e.Message); }
    }

    void Update()
    {
        EnsureUI();          // 首次创建 / VR 模式切换时重建 (桌面=Screen Space, VR=World Space)
        UpdateHeadPose();    // XR 设备检测 + 手柄/键鼠输入
        SyncMaterialParams();
        if (_state == AppState.Player) UpdatePlayback();
        UpdateFps();
        UpdatePlayerUI();
        if (_ui != null)
        {
            _ui.Update(Time.unscaledDeltaTime);
            // 中心 HUD 自动隐藏倒计时
            if (_hudTimer > 0f)
            {
                _hudTimer -= Time.unscaledDeltaTime;
                if (_hudTimer <= 0f) { _hudTimer = 0f; _ui.SetCenterVisible(false); }
            }
        }

        // 桌面模式 ESC: 主界面设置面板打开 → 先关; 播放器 → 回主界面; 主界面 → 退出游戏
        if (!VrActive && Input.GetKeyDown(KeyCode.Escape))
        {
            if (_ui != null && _ui.HomeSettingsOpen)
                _ui.OpenHomeSettings();
            else if (_state == AppState.Player)
                ShowHome();
            else
                QuitApp();
        }
    }

    /// <summary>LateUpdate: TPD 已更新头部 pose, 在这里做 Recenter + 确保相机 clearFlags 不被 OpenXR 重置.</summary>
    void LateUpdate()
    {
        if (!VrActive || Camera.main == null) return;

        // 1. Recenter: 必须等 TPD 把真实头部 pose 写入 cam.localPosition 后再做,
        //    否则 cam.localPosition 还是 zero, Recenter 白做, UI 就会跑到高空.
        if (!_vrRecenterDone)
        {
            var cam = Camera.main;
            if (cam.transform.localPosition.sqrMagnitude > 1e-4f)
            {
                RecenterHeadToOrigin();
                _vrRecenterDone = true;
                Debug.Log($"[VR] LateUpdate Recenter done, headLocal={cam.transform.localPosition}");
                SnapUIToHead();
            }
        }

        // 2. 紫屏修复: OpenXR 每帧可能把主相机 clearFlags 重置为 Skybox (默认紫色),
        //    这里持续强制设回 SolidColor + 正确背景色.
        var main = Camera.main;
        if (main.clearFlags != CameraClearFlags.SolidColor)
        {
            main.clearFlags = CameraClearFlags.SolidColor;
            SetBackground(_state == AppState.Home);
        }
    }

    void OnDestroy()
    {
        DepthPipe.Dispose();
        ReleaseStillImages();
        if (_ui != null) _ui.Destroy();
        DestroyRay();
        if (_desktopHintGo != null) UnityEngine.Object.Destroy(_desktopHintGo);
        _desktopHintGo = null;
    }

    // ---------- 主界面 / 播放器状态 ----------

    void ShowHome()
    {
        _state = AppState.Home;
        StopPlayback();
        _hint = "";
        _progressVisible = true;
        if (_ui != null)
        {
            _ui.ShowHome(_standalone, _recents);
            _ui.SetHint("");
            if (VrActive) SnapUIToHead();   // 主界面摆到视线正前方并固定
        }
        SetBackground(true);
        UpdateCursor();
        Debug.Log("[UI] 返回主界面");
    }

    public void ToggleProgress()
    {
        if (_state != AppState.Player) return;
        _progressVisible = !_progressVisible;
        if (_ui != null)
        {
            _ui.SetProgressVisible(_progressVisible);
            // 呼出时摆到视线正前方 (界面世界锁定后, 转身可能导致它落在背后); 隐藏则原地不动
            if (_progressVisible && VrActive) SnapUIToHead();
        }
        UpdateCursor();
    }

    void UpdateCursor()
    {
        bool show = VrActive || _state == AppState.Home || _progressVisible;
        Cursor.visible = show;
        Cursor.lockState = CursorLockMode.None;
    }

    void SetBackground(bool home)
    {
        var cam = Camera.main;
        if (cam == null) return;
        cam.backgroundColor = home ? new Color(0.045f, 0.052f, 0.065f, 1f)
                                   : new Color(0.02f, 0.02f, 0.02f, 1f);
    }

    void OpenFileDialog()
    {
        string dir = "";
        if (!string.IsNullOrEmpty(_currentJson)) dir = Path.GetDirectoryName(_currentJson);
        else if (_recents.Count > 0) dir = Path.GetDirectoryName(_recents[0]);
        // 跨重启回落: 上次成功打开文件的目录 (PlayerPrefs)
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = PlayerPrefs.GetString(LastDirKey, "");
        // 最终回落: 平台默认目录 (Android→Download, 桌面→exe 所在目录)
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = PlayerUI.GetDefaultBrowseDir();

        Debug.Log($"[OpenFileDialog] VrActive={VrActive}, ui={_ui != null}, dir='{dir}'");

        if (_ui != null)
        {
            // 一律用 Unity UI 文件浏览器 (VR / 桌面同一个面板, 自做 File API 比系统对话框更统一)
            _ui.ShowFileBrowser(dir, path =>
            {
                Debug.Log($"[OpenFileDialog] callback path='{path}'");
                if (!string.IsNullOrEmpty(path)) ActivateJson(path, true);
            });
        }
        else
        {
            // 兜底 (理论上不会到这里): 没有 UI 时退回系统对话框
            var p = PlayerUI.PickJsonFile(dir);
            if (!string.IsNullOrEmpty(p)) ActivateJson(p, true);
        }
    }

    /// <summary>打开 json 并进入播放器: 路径优先取 json 的 color_file/depth_file, 缺失时回落同名 mp4</summary>
    void ActivateJson(string jsonPath, bool fromHome)
    {
        try
        {
            ActivateJsonInner(jsonPath, fromHome);
        }
        catch (Exception e)
        {
            _hint = "打开 json 崩溃: " + e.Message;
            if (_ui != null) { _ui.ShowToast("打开失败"); _ui.SetHint(_hint); }
            Debug.LogError("[UI] ActivateJson 异常: " + e + "\n" + e.StackTrace);
        }
    }

    void ActivateJsonInner(string jsonPath, bool fromHome)
    {
        if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
        {
            _hint = "文件不存在: " + jsonPath;
            if (_ui != null) { _ui.ShowToast("文件不存在"); _ui.SetHint(_hint); }
            return;
        }

        string cp = null, dp = null;
        TryLoadPathsFromJson(jsonPath, ref cp, ref dp);
        if (string.IsNullOrEmpty(cp) || !File.Exists(cp))
        {
            string f = Path.ChangeExtension(jsonPath, ".mp4");
            cp = File.Exists(f) ? f : null;
        }
        if ((string.IsNullOrEmpty(dp) || !File.Exists(dp)) && cp != null)
        {
            string f = Path.ChangeExtension(cp, null) + "_depth.mp4";
            dp = File.Exists(f) ? f : null;
        }
        if (cp == null) { FailOpen("json 里没有 color_file, 也找不到同名 mp4: " + Path.GetFileName(jsonPath)); return; }
        if (dp == null) { FailOpen("找不到对应的深度视频: " + Path.GetFileName(jsonPath)); return; }

        _currentJson = jsonPath;
        _metaJsonPath = jsonPath;
        // 进度条左上角只显示文件名 (不含后缀), 其他状态信息不再占用该位置
        _hint = Path.GetFileNameWithoutExtension(jsonPath);
        colorPath = cp;
        depthPath = dp;
        AddRecent(jsonPath);
        // 记住这次打开的目录, 下次开文件浏览器 (即使重启过) 直接落在这里
        try { PlayerPrefs.SetString(LastDirKey, Path.GetDirectoryName(jsonPath)); PlayerPrefs.Save(); } catch {}
        if (fromHome)
        {
            _playlist = new List<string>(_recents);
            _playIndex = Mathf.Max(0, _playlist.IndexOf(jsonPath));
        }
        else
        {
            int idx = _playlist.IndexOf(jsonPath);
            _playIndex = idx >= 0 ? idx : 0;
            if (_playlist.Count == 0) { _playlist.Add(jsonPath); _playIndex = 0; }
        }

        _state = AppState.Player;
        _progressVisible = false;   // 进入播放时默认隐藏进度条, 鼠标/扳机呼出
        _manuallyPaused = false;
        _rangeSeeking = false;
        SetShellVisible(true);
        SetBackground(false);
        if (VrActive)
        {
            RecenterHeadToOrigin();   // 以当前头部位置为坐标原点, 保证画面播放舒适
            AlignYaw();               // 球壳正对当前视线
            SnapUIToHead();           // 界面落在正前方并固定
        }
        ReloadVideos();   // 内部重新读 sidecar + 重建播放链

        if (_ui != null)
        {
            _ui.ShowPlayer();
            _ui.SetProgressVisible(false);
            _ui.SetSettingsValues(segments, msaa, tessLength);
            _ui.SetLoopMode(loopMode);
        }
        UpdateCursor();
        Debug.Log($"[UI] 开始播放 {Path.GetFileName(jsonPath)}");
    }

    void FailOpen(string msg)
    {
        _hint = msg;
        if (_ui != null) { _ui.ShowToast("打开失败"); _ui.SetHint(_hint); }
        Debug.LogError("[UI] " + msg);
    }

    /// <summary>命令行给了 color/depth: 不进主界面, 直接播放 (sidecar 仍从同名 json 读取)</summary>
    void ActivateCommandLineSource()
    {
        if (string.IsNullOrEmpty(colorPath) || !File.Exists(colorPath)) { ShowHome(); FailOpen("颜色文件不存在: " + colorPath); return; }
        if (string.IsNullOrEmpty(depthPath) || !File.Exists(depthPath)) { ShowHome(); FailOpen("深度文件不存在: " + depthPath); return; }
        _playlist = new List<string>();
        _playIndex = 0;
        _state = AppState.Player;
        _progressVisible = false;   // 命令行启动同样默认隐藏进度条
        SetShellVisible(true);
        SetBackground(false);
        if (VrActive)
        {
            RecenterHeadToOrigin();
            AlignYaw();
            SnapUIToHead();
        }
        ReloadVideos();
        if (_ui != null)
        {
            _ui.ShowPlayer();
            _ui.SetProgressVisible(false);
            _ui.SetSettingsValues(segments, msaa, tessLength);
            _ui.SetLoopMode(loopMode);
        }
        UpdateCursor();
    }

    void StopPlayback()
    {
        _cReady = false;
        _videoReady = false;
        _rangeSeeking = false;
        _depthPrerolled = false;
        _depthPrerollWarned = false;
        _depthPrerollTimer = 0f;
        _cLastFrame = -1;
        _cPrevFrame = -1;
        _colorLoopLen = 0;
        _vFrames = 0;
        DepthPipe.Kill();
        ReleaseStillImages();
        if (ColorVideo != null)
        {
            ColorVideo.Stop();
            var rt = ColorVideo.targetTexture as RenderTexture;
            if (rt != null) { rt.Release(); Destroy(rt); }
            Destroy(ColorVideo);
            ColorVideo = null;
        }
        _colorRT = null;
        if (_mat != null) _mat.mainTexture = null;
        SetShellVisible(false);
    }

    // ---------- 最近文件 ----------

    void LoadRecents()
    {
        _recents.Clear();
        string s = PlayerPrefs.GetString(RecentKey, "");
        foreach (var line in s.Split('\n'))
        {
            string p = line.Trim();
            if (!string.IsNullOrEmpty(p)) _recents.Add(p);
        }
    }

    void AddRecent(string jsonPath)
    {
        _recents.Remove(jsonPath);
        _recents.Insert(0, jsonPath);
        while (_recents.Count > RecentMax) _recents.RemoveAt(_recents.Count - 1);
        PlayerPrefs.SetString(RecentKey, string.Join("\n", _recents));
        PlayerPrefs.Save();
    }

    // ---------- 上一首 / 下一首 / 循环 ----------

    public void PlayPrev()
    {
        if (_state != AppState.Player) return;
        if (_playlist.Count <= 1) { SeekToFrame(_startFrame); return; }
        _playIndex = (_playIndex - 1 + _playlist.Count) % _playlist.Count;
        ActivateJson(_playlist[_playIndex], false);
    }

    public void PlayNext()
    {
        if (_state != AppState.Player) return;
        if (_playlist.Count <= 1)
        {
            long hi = _endFrame > _startFrame ? _endFrame - 1 : _vFrames - 1;
            SeekToFrame(hi);
            return;
        }
        _playIndex = (_playIndex + 1) % _playlist.Count;
        ActivateJson(_playlist[_playIndex], false);
    }

    bool ShouldAdvanceOnLoop()
    {
        return loopMode != 0 && _playlist.Count > 1;
    }

    /// <summary>列表循环/随机: 播完当前条目自动切下一条 (单条列表时退回单曲循环)</summary>
    void AdvanceTrack()
    {
        if (_playlist.Count <= 1) return;
        if (loopMode == 2)
        {
            int n = UnityEngine.Random.Range(0, _playlist.Count - 1);
            if (n >= _playIndex) n++;
            _playIndex = n;
        }
        else
        {
            _playIndex = (_playIndex + 1) % _playlist.Count;
        }
        ActivateJson(_playlist[_playIndex], false);
    }

    // ---------- 进度 / 音量 ----------

    public void TogglePlay()
    {
        if (ColorVideo == null) return;
        if (ColorVideo.isPlaying)
        {
            ColorVideo.Pause();   // 深度管道随 target 停止贴帧, 恢复后自动追帧
            _manuallyPaused = true;
        }
        else
        {
            _manuallyPaused = false;
            ColorVideo.Play();
            if (_audio != null && !_audio.isPlaying) _audio.Play();
        }
    }

    void HandleSeek(float t)
    {
        if (_state != AppState.Player || ColorVideo == null || !ColorVideo.isPrepared) return;
        long len = _endFrame > _startFrame ? _endFrame - _startFrame : _vFrames;
        if (len <= 0) return;
        SeekToFrame(_startFrame + (long)Math.Round(Mathf.Clamp01(t) * (len - 1)));
    }

    void SeekToFrame(long f)
    {
        if (_state != AppState.Player || ColorVideo == null || !ColorVideo.isPrepared) return;
        long lo = _endFrame > _startFrame ? _startFrame : 0;
        long hi = (_endFrame > _startFrame ? _endFrame : _vFrames) - 1;
        if (hi < lo) hi = lo;
        if (f < lo) f = lo;
        if (f > hi) f = hi;
        ColorVideo.frame = f;
        _rangeSeeking = false;
        _cLastFrame = -1;
        _cPrevFrame = -1;
        _cStall = 0f;
    }

    /// <summary>单手摇杆轴向 (左右手都调同一份状态): y=系统音量 + 中心 HUD 显示; x=seek + 中心 HUD 显示时间</summary>
    void HandleStickAxis(Vector2 axis, float dt)
    {
        // ---- 音量: 上下推 → 同步 Unity Audio + Windows 系统音量 ----
        if (Mathf.Abs(axis.y) > 0.15f)
        {
            float before = _volume;
            SetVolume(_volume + axis.y * 0.6f * dt);
            if (Mathf.Abs(_volume - before) > 0.005f)
            {
                if (_audio != null) _audio.volume = _volume;
#if !UNITY_ANDROID && !UNITY_IOS && !UNITY_WEBGL
                SetSystemVolume01(_volume);   // 串流模式同步到系统混音器
#endif
                ShowCenterHUD($"音量 {Mathf.RoundToInt(_volume * 100)}%");
            }
        }

        // ---- 进度: 左右推 → seek + 中心 HUD 显示 当前/总时长 ----
        if (ColorVideo == null) return;
        if (Mathf.Abs(axis.x) > 0.15f)
        {
            _seekStepTimer -= dt;
            if (_seekStepTimer <= 0f)
            {
                _seekStepTimer = 0.2f;
                long len = _endFrame > _startFrame ? _endFrame - _startFrame : _vFrames;
                float fps = (float)(_vfps > 0 ? _vfps : 30.0);
                float dur = len / fps;
                float stepSec = Mathf.Clamp(dur * 0.01f, 0.02f, 2f);
                SeekToFrame(ColorVideo.frame + (long)Math.Round(axis.x * stepSec * fps));
                long cur = ColorVideo.frame - _startFrame;
                float curSec = cur / fps, totSec = dur;
                ShowCenterHUD($"{FormatTime(curSec)} / {FormatTime(totSec)}");
            }
        }
        else
        {
            _seekStepTimer = 0f;
        }
    }

    void SetVolume(float v)
    {
        v = Mathf.Clamp01(v);
        if (Mathf.Abs(v - _volume) < 0.001f) return;
        _volume = v;
        if (_audio != null) _audio.volume = _volume;
        if (_ui != null) _ui.SetVolumeUI(_volume);   // 进度条音量滑块联动 (内部 SetValueWithoutNotify, 不会回环)
    }

    static string FormatTime(float sec)
    {
        sec = Mathf.Max(0f, sec);
        int m = (int)(sec / 60f);
        int s = (int)(sec - m * 60);
        int ms = (int)((sec - m * 60 - s) * 10f);
        return $"{m:D2}:{s:D2}.{ms}";
    }

    /// <summary>中心 HUD (音量 / seek 反馈). 由 PlayerUI.ShowCenter 负责渲染, 1.5s 自动隐藏.</summary>
    void ShowCenterHUD(string text)
    {
        if (_ui != null) _ui.ShowCenter(text);
        _hudTimer = HudAutoHideSec;
    }

    // ---------- 界面构建 / 刷新 ----------

    void EnsureUI()
    {
        if (Camera.main == null) return;
        if (_ui != null && _ui.WorldSpace == VrActive) return;
        if (_ui != null) { _ui.Destroy(); _ui = null; }
        DestroyRay();
        _dragSlider = null;
        _rTrigPrev = _lTrigPrev = false;
        _ui = new PlayerUI();
        // 根据 XR 设备状态决定 Canvas 类型: 真实 VR 或 PC 模拟 VR 时用 World Space (射线交互);
        // 纯桌面模式用 Screen Space Overlay (鼠标天然可点, Canvas 自动铺满窗口).
        // 即使 Build 时 XR 子系统还没启动误判成 Screen Space, 下一帧 UpdateHeadPose 会更新 VrActive,
        // EnsureUI 检测到 WorldSpace != VrActive 会自动 Destroy + Rebuild 纠正.
        // 用 VrActive 作为 WorldSpace 判定 (VrActive 已在 UpdateHeadPose 里综合 hmdTracked/simulateVR 算好),
        // 不再重复检测 HMD, 避免时序差异导致 EnsureUI 和 UpdateHeadPose 判定不一致.
        bool wantWorldSpace = VrActive;
        _ui.Build(Camera.main, wantWorldSpace, segments, msaa, tessLength);
        BindUI();
        _ui.SetUiLang(UiLang);   // 把 INI/系统语言应用到所有静态 UI 文字
        if (VrActive) { CreateRay(); SnapUIToHead(); }
        ApplyUIState();
        UpdateCursor();
    }

    void BindUI()
    {
        _ui.OnOpenFile = OpenFileDialog;
        _ui.OnQuit = QuitApp;
        _ui.OnOpenRecent = path => ActivateJson(path, true);
        _ui.OnSeek = HandleSeek;
        _ui.OnPlayPause = TogglePlay;
        _ui.OnPrev = PlayPrev;
        _ui.OnNext = PlayNext;
        _ui.OnLoopMode = m => loopMode = m;
        _ui.OnSegments = v => segments = v;
        _ui.OnMsaa = v => msaa = v;
        _ui.OnTessLength = v => tessLength = v;
        _ui.OnDebugView = v =>
        {
            debugView = v;
            _mat?.SetInt("_View", v);
            _cView = v;
        };
        _ui.OnVolume = v => SetVolume(v);
        _ui.OnAutoHide = () =>
        {
            if (!_progressVisible || _ui == null) return;
            _progressVisible = false;
            _ui.SetProgressVisible(false);
            UpdateCursor();
        };
        // ---- 主界面设置面板 ----
        _ui.OnOpenHomeSettings = () =>
        {
            _ui.OpenHomeSettings();
            _ui.SetHomeSettingsValues(UiLang, RimClampSetting, TargetFpsSetting);
            if (VrActive) SnapUIToHead();
        };
        _ui.OnSettingsLang = lang =>
        {
            UiLang = (lang ?? "").StartsWith("zh") ? "zh" : "en";
            SaveSettings();
            if (_ui != null)
            {
                _ui.SetUiLang(UiLang);                       // 全量刷新静态文字
                _ui.SetHomeSettingsValues(UiLang, RimClampSetting, TargetFpsSetting);  // 刷新设置行值
            }
            ApplyDesktopHintLang();   // 同步刷新桌面提示屏
        };
        _ui.OnSettingsRimClamp = v =>
        {
            RimClampSetting = v; rimClamp = v;
            SaveSettings();
            ApplySettings();
            if (_ui != null) _ui.SetHomeSettingsValues(UiLang, RimClampSetting, TargetFpsSetting);
        };
        _ui.OnSettingsFps = fps =>
        {
            TargetFpsSetting = fps; targetFps = fps;
            SaveSettings();
            ApplySettings();
            if (_ui != null) _ui.SetHomeSettingsValues(UiLang, RimClampSetting, TargetFpsSetting);
        };
    }

    void ApplyUIState()
    {
        if (_ui == null) return;
        _ui.SetSettingsValues(segments, msaa, tessLength);
        _ui.SetLoopMode(loopMode);
        if (_state == AppState.Player)
        {
            _ui.ShowPlayer();
            _ui.SetProgressVisible(_progressVisible);
            _ui.SetVolumeUI(_volume);
        }
        else
        {
            _ui.ShowHome(_standalone, _recents);
        }
        // 设置面板初始值: 让打开前先有正确的 INI 值显示
        _ui.SetHomeSettingsValues(UiLang, RimClampSetting, TargetFpsSetting);
        _ui.SetHint(_hint);
    }

    void UpdatePlayerUI()
    {
        if (_ui == null) return;
        _ui.SetHint(_hint);
        if (_state != AppState.Player) return;

        long len = _endFrame > _startFrame ? _endFrame - _startFrame : _vFrames;
        double fps = _vfps > 0 ? _vfps : 30.0;
        float total = len > 0 ? (float)(len / fps) : 0f;
        float cur = 0f;
        if (ColorVideo != null)
        {
            long cf = ColorVideo.frame;
            cur = (float)((cf - _startFrame) / fps);
            if (cur < 0f) cur = 0f;
            if (len > 0) _ui.SetSeek01((float)(cf - _startFrame) / len);
        }
        _ui.SetTimes(cur, total);
        _ui.SetPlayState(ColorVideo != null && ColorVideo.isPlaying);
    }

    // ---------- VR 输入 ----------

    void UpdateHeadPose()
    {
        // F2 运行时切换 PC 模拟 VR
        if (Input.GetKeyDown(KeyCode.F2)) simulateVR = !simulateVR;

        // 头显实际追踪判定: 光有 XR Runtime active 不够 (SteamVR 空跑也 active),
        // 必须 HMD 设备 valid + 能读到 rotation (排除 SteamVR 启动但头显没接的情况)
        bool hmdTracked = false;
        if (vrEnabled && XRSettings.enabled && XRSettings.isDeviceActive)
        {
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            hmdTracked = head.isValid && head.TryGetFeatureValue(CommonUsages.deviceRotation, out _);
        }

        bool deviceActive = hmdTracked;
        bool sim = simulateVR && !deviceActive;   // 真头显优先, 没有设备时才模拟
        bool active = deviceActive || sim;
        if (active != VrActive)
        {
            VrActive = active;
            SimActive = sim;
            if (VrActive && sim)
                Debug.Log("[VR] PC 模拟模式: 左键拖动=视角 WASD=平移 Q/E=升降 T=进度条 Y=重置朝向 R=重置位置 F2=退出模拟");
            else if (VrActive)
                Debug.Log("[VR] 设备已连接: " + XRSettings.loadedDeviceName);
            if (VrActive && !sim)
            {
                TrySetDeviceTrackingOrigin();   // 子系统就绪后重设 Device 原点
                _vrRecenterDone = false;        // 等 TPD 写入头部 pose 后 (LateUpdate) 再 Recenter
            }
            if (VrActive)
            {
                EnsureUI();                    // 立即重建 Canvas: ScreenSpaceOverlay → WorldSpace
                DisableDesktopMirrorOnce();    // OpenXR Runtime 启动后可能覆盖过 Start 时的偏好, 再设一次
                AlignYaw();
                SnapUIToHead();
            }
            // 桌面串流模式 (真 VR + 非 standalone + 非 PC 模拟): 桌面 backbuffer 显示提示屏遮掉主相机画面
            bool needHint = VrActive && !SimActive && !_standalone;
            SetDesktopHintEnabled(needHint);
            UpdateCursor();
        }
        if (!VrActive || Camera.main == null) return;

        if (sim) { SimVRInput(); return; }

        // ---- 左右手完全对称: A=暂停, B=退出播放(播放器内), 摇杆=进度/音量, 扳机=UI ----
        // 摇杆按键 (主界面/播放器都生效): 单按(松开且未达长按阈值)=重置朝向+界面归位, 长按=重置头部坐标
        var rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        var leftHand  = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        bool rA = false, rB = false, rStick = false, rTrig = false; Vector2 rAxis = Vector2.zero;
        bool lA = false, lB = false, lStick = false, lTrig = false; Vector2 lAxis = Vector2.zero;
        if (rightHand.isValid)
        {
            rightHand.TryGetFeatureValue(CommonUsages.primaryButton, out rA);
            rightHand.TryGetFeatureValue(CommonUsages.secondaryButton, out rB);
            rightHand.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out rStick);
            rightHand.TryGetFeatureValue(CommonUsages.triggerButton, out rTrig);
            rightHand.TryGetFeatureValue(CommonUsages.primary2DAxis, out rAxis);
        }
        if (leftHand.isValid)
        {
            leftHand.TryGetFeatureValue(CommonUsages.primaryButton, out lA);
            leftHand.TryGetFeatureValue(CommonUsages.secondaryButton, out lB);
            leftHand.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out lStick);
            leftHand.TryGetFeatureValue(CommonUsages.triggerButton, out lTrig);
            leftHand.TryGetFeatureValue(CommonUsages.primary2DAxis, out lAxis);
        }

        // ---- 摇杆按键: 左右手分别处理 (对称逻辑) ----
        HandleStickPress(rStick, ref _rStickHoldTime, ref _rStickLongFired, ref _rStickBtn, "右摇杆");
        bool lStickBtnPrev = _lStickBtn;
        float lStickHold = _lStickHoldTime; bool lStickLong = _lStickLongFired;
        HandleStickPress(lStick, ref lStickHold, ref lStickLong, ref lStickBtnPrev, "左摇杆");
        _lStickBtn = lStickBtnPrev; _lStickHoldTime = lStickHold; _lStickLongFired = lStickLong;

        // ---- 活动手: 谁扳机/摇杆有输入就跟随谁 ----
        if (rTrig || Mathf.Abs(rAxis.x) > 0.15f || Mathf.Abs(rAxis.y) > 0.15f) _activeHand = XRNode.RightHand;
        else if (lTrig || Mathf.Abs(lAxis.x) > 0.15f || Mathf.Abs(lAxis.y) > 0.15f) _activeHand = XRNode.LeftHand;
        // 有效性回退: 默认右手, 当前活动手失效时换另一只手 (主界面右手射线自动启用, 右手不在用左手)
        if (_activeHand == XRNode.RightHand && !rightHand.isValid && leftHand.isValid) _activeHand = XRNode.LeftHand;
        else if (_activeHand == XRNode.LeftHand && !leftHand.isValid && rightHand.isValid) _activeHand = XRNode.RightHand;

        bool pauseBtn = (rA || lA);
        bool quitBtn  = (rB || lB);
        // 边沿: 取两只 OR 的上升沿 (两手同按也不重复 toggle)
        bool anyPause = pauseBtn && !_pauseBtn;
        bool anyQuit  = quitBtn  && !_quitBtn;
        // 主界面设置面板打开时, B=先关闭它 (最优先), 不继续触发退出播放/退出程序
        if (_ui != null && _ui.HomeSettingsOpen)
        {
            if (anyQuit) { Debug.Log("[VR] B 键关闭主界面设置"); _ui.OpenHomeSettings(); }
        }
        else if (_state == AppState.Player)
        {
            if (anyPause) TogglePlay();
            if (anyQuit) { Debug.Log("[VR] B 键退出播放"); ShowHome(); }
            // 音量和进度: 两只手的摇杆都生效 (轴向对称)
            HandleStickAxis(rAxis, Time.unscaledDeltaTime);
            HandleStickAxis(lAxis, Time.unscaledDeltaTime);
        }
        else if (_ui != null && _ui.FileBrowserOpen)
        {
            // 文件浏览器: B=关闭, 摇杆上下=滚动列表
            if (anyQuit) { Debug.Log("[VR] B 键关闭文件浏览器"); _ui.CloseFileBrowser(); }
            float sy = (Mathf.Abs(rAxis.y) > 0.15f ? rAxis.y : 0f) + (Mathf.Abs(lAxis.y) > 0.15f ? lAxis.y : 0f);
            if (Mathf.Abs(sy) > 0.001f) _ui.ScrollFileBrowser(sy * Time.unscaledDeltaTime * 1.5f);
        }
        _pauseBtn = pauseBtn;
        _quitBtn  = quitBtn;

        // 射线跟随活动手 (扳机边沿按手分别检测, 换手不丢边沿)
        if (_vrRay == null) CreateRay();   // 懒创建: EnsureUI 首次运行时 XR 未就绪, 那里的 CreateRay 被跳过
        var activeDevice = (_activeHand == XRNode.LeftHand) ? leftHand : rightHand;
        bool trig = (_activeHand == XRNode.LeftHand) ? lTrig : rTrig;
        bool trigPrev = (_activeHand == XRNode.LeftHand) ? _lTrigPrev : _rTrigPrev;
        UpdateVRControllerRay(activeDevice, trig, trig && !trigPrev, !trig && trigPrev);
        _rTrigPrev = rTrig; _lTrigPrev = lTrig;
    }

    /// <summary>双手摇杆按键对称处理: 单按=重置朝向+UI归位, 长按=重置头部坐标</summary>
    void HandleStickPress(bool pressed, ref float holdTime, ref bool longFired, ref bool prev, string handName)
    {
        if (pressed)
        {
            holdTime += Time.unscaledDeltaTime;
            if (!longFired && holdTime >= StickLongPressSec)
            {
                longFired = true;
                RecenterHeadToOrigin();
                Debug.Log($"[VR] {handName}长按: 头部坐标已重置到原点");
            }
        }
        else if (prev)
        {
            if (!longFired) { AlignYaw(); SnapUIToHead(); }
            holdTime = 0f;
            longFired = false;
        }
        prev = pressed;
    }

    /// <summary>PC 模拟 VR 下用手柄动作对应的键盘键 (视角/平移由 DesktopController 驱动)</summary>
    void SimVRInput()
    {
        if (Input.GetKeyDown(KeyCode.Y)) { AlignYaw(); SnapUIToHead(); }   // 右摇杆单按
        if (Input.GetKeyDown(KeyCode.R)) ResetCameraPosition();            // 右摇杆长按
        if (Input.GetKeyDown(KeyCode.T)) ToggleProgress();                 // 扳机
    }

    void AlignYaw()
    {
        if (_shellT == null) return;
        var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!SimActive && head.isValid && head.TryGetFeatureValue(CommonUsages.deviceRotation, out var rot))
            _shellT.rotation = Quaternion.Euler(0f, rot.eulerAngles.y, 0f);
        else if (Camera.main != null)   // PC 模拟: 用当前视角 yaw 代替头显朝向
            _shellT.rotation = Quaternion.Euler(0f, Camera.main.transform.eulerAngles.y, 0f);
    }

    /// <summary>把当前头部位置重置到视频原点 (0,0,0) (右摇杆长按 / 模拟模式 R 键)</summary>
    void ResetCameraPosition()
    {
        RecenterHeadToOrigin();
        Debug.Log("[VR] 头部已重置到原点");
    }

    /// <summary>把世界空间 UI 摆到头显水平正前方 (世界锁定; 进入界面/呼出进度条/右摇杆单按时调用)</summary>
    void SnapUIToHead()
    {
        if (_ui == null || Camera.main == null) return;
        _ui.SnapToHead(Camera.main.transform);
    }

    /// <summary>退出 (Editor 里停播放, 打包后关进程). 退出前先关 XR 子系统让 SteamVR/PICO Connect 回落桌面镜像, 头显不再留着空环境.</summary>
    void QuitApp()
    {
        Debug.Log("[UI] 退出应用");
#if UNITY_ANDROID && !UNITY_EDITOR
        // 一体机不走串流, 直接关
#else
        // 串流模式: 先关 OpenXR/XR 子系统让头显回到桌面镜像层, 而不是留着 SteamVR 默认空环境
        try
        {
            var mgr = UnityEngine.XR.Management.XRGeneralSettings.Instance?.Manager;
            if (mgr != null) mgr.StopSubsystems();
        }
        catch (Exception e) { Debug.LogWarning("[UI] StopSubsystems 异常 (忽略): " + e.Message); }
#endif
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ---------- VR 控制器射线 ----------

    void CreateRay()
    {
        if (_vrRay != null || Camera.main == null) return;
        var go = new GameObject("VRControllerRay");
        go.transform.SetParent(Camera.main.transform, false);
        _vrRay = go.AddComponent<LineRenderer>();
        // 用 UIOverlay (ZTest Always): 视频球壳的近处几何(顶点位移后可到眼前)不会挡住射线
        var rayShader = Shader.Find("VR180/UIOverlay");
        _vrRay.material = rayShader != null ? new Material(rayShader)
                                            : new Material(Shader.Find("Sprites/Default"));
        _vrRay.startColor = new Color(0.2f, 0.7f, 1f, 0.9f);
        _vrRay.endColor = new Color(0.2f, 0.7f, 1f, 0.2f);
        _vrRay.startWidth = 0.008f;
        _vrRay.endWidth = 0.002f;
        _vrRay.positionCount = 2;
        _vrRay.useWorldSpace = true;
    }

    void DestroyRay()
    {
        if (_vrRay != null) Destroy(_vrRay.gameObject);
        _vrRay = null;
    }

    /// <summary>
    /// 手柄射线: 画一条可见射线, 与 World Space Canvas 求交, 扳机按下时触发按钮/拖动滑块;
    /// 没指到任何控件时切换进度条界面. 没有用 XR Interaction Toolkit, 纯手动实现.
    /// trigEdge/trigRelease 是活动手扳机的上升/下降沿 (调用方按手分别检测).
    /// </summary>
    void UpdateVRControllerRay(InputDevice hand, bool trig, bool trigEdge, bool trigRelease)
    {
        if (_vrRay == null || _ui == null || _ui.Canvas == null || _ui.Raycaster == null) return;
        var cam = Camera.main;
        if (cam == null) return;

        // 扳机松开: 结束滑块拖动 (无论射线当前是否可见, 避免拖动中隐藏界面后滑块卡死)
        if (trigRelease && _dragSlider != null)
        {
            _ui.SetDragging(false);
            if (_dragSlider == _ui.SeekSlider) _ui.CommitSeek();   // 进度条松手才真正跳转
            _dragSlider = null;
        }

        // 手柄世界位姿 (devicePosition 在追踪空间, 经 XROrigin 变换到世界)
        Vector3 handWorld = cam.transform.position;
        Vector3 handFwd = cam.transform.forward;
        if (hand.isValid && hand.TryGetFeatureValue(CommonUsages.devicePosition, out var hpos)
                         && hand.TryGetFeatureValue(CommonUsages.deviceRotation, out var hrot))
        {
            var origin = _xrOrigin != null ? _xrOrigin : cam.transform.parent;
            if (origin != null)
            {
                handWorld = origin.TransformPoint(hpos);
                // grip 轴向偏上: 加俯仰修正让射线落在自然指向 (相当于 pointer pose)
                handFwd = origin.TransformDirection(hrot * Quaternion.Euler(rayPitchOffset, 0f, 0f) * Vector3.forward);
            }
        }
        Ray ray = new Ray(handWorld, handFwd);

        // 可见性: 主界面始终显示; 播放器只在进度条可见时显示 (进播放器时 _progressVisible=false, 射线自动隐藏)
        bool rayVisible = (_state == AppState.Home) || (_state == AppState.Player && _progressVisible);
        _vrRay.enabled = rayVisible;

        // 射线不可见 (播放器里进度条已隐藏): 扳机按下直接呼出进度条, 射线随进度条一起出现
        if (!rayVisible)
        {
            if (trigEdge && _state == AppState.Player) ToggleProgress();
            return;
        }

        // 射线终点: 与 Canvas 平面求交 (Canvas 面向相机, 法向 = -canvas.forward)
        var canvasT = _ui.Canvas.transform;
        var plane = new Plane(-canvasT.forward, canvasT.position);
        Vector3 hitPoint = handWorld + handFwd * 5f;   // 默认 5m
        float hitDist;
        if (plane.Raycast(ray, out hitDist) && hitDist > 0f) hitPoint = ray.GetPoint(hitDist);

        _vrRay.SetPosition(0, handWorld);
        _vrRay.SetPosition(1, hitPoint);

        // 射线是否落在进度条面板矩形内 (含空白区域), 供自动隐藏判定: 指向面板时不隐藏
        var panelRt = _ui.PlayerPanelRect;
        bool onPanel = false;
        if (panelRt != null)
        {
            Vector3 local = panelRt.InverseTransformPoint(hitPoint);
            onPanel = panelRt.rect.Contains(new Vector2(local.x, local.y));
        }
        _ui.SetPanelPointer(onPanel);

        // 把命中点转成屏幕坐标, 用 GraphicRaycaster 找 UI 元素
        Vector2 screenPos = cam.WorldToScreenPoint(hitPoint);
        var ped = new PointerEventData(EventSystem.current) { position = screenPos };
        var results = new List<RaycastResult>();
        _ui.Raycaster.Raycast(ped, results);

        GameObject hit = results.Count > 0 ? results[0].gameObject : null;
        Button btn = hit != null ? hit.GetComponentInParent<Button>() : null;
        Slider slider = hit != null ? hit.GetComponentInParent<Slider>() : null;

        // 滑块拖动: 扳机按住且正在拖
        if (_dragSlider != null && trig) SetSliderValueFromWorldPoint(_dragSlider, hitPoint);

        if (trigEdge)
        {
            if (btn != null) btn.onClick.Invoke();
            else if (slider != null)
            {
                _dragSlider = slider;
                _ui.SetDragging(true);   // 拖动期间不让刷新把滑块拉回播放位置
                SetSliderValueFromWorldPoint(slider, hitPoint);
            }
            else if (hit == null)
            {
                ToggleProgress();   // 没指到任何控件: 呼出/隐藏进度条界面 (ToggleProgress 内部限定播放器状态)
            }
        }
    }

    /// <summary>根据射线命中点设置滑块值 (命中点投影到滑块背景的 X 轴)</summary>
    void SetSliderValueFromWorldPoint(Slider slider, Vector3 worldPoint)
    {
        if (slider == null) return;
        var bg = slider.transform.Find("Background");
        if (bg == null) return;
        var bgRt = bg.GetComponent<RectTransform>();
        Vector3 localPt = bgRt.InverseTransformPoint(worldPoint);
        float t = Mathf.InverseLerp(-bgRt.rect.width * 0.5f, bgRt.rect.width * 0.5f, localPt.x);
        slider.value = Mathf.Lerp(slider.minValue, slider.maxValue, t);
    }

    // ---------- 视频 ----------

    void StartVideos()
    {
        if (!File.Exists(colorPath))
        {
            _hint = "颜色文件不存在: " + colorPath;
            Debug.LogError(_hint);
            return;
        }
        if (!File.Exists(depthPath))
        {
            _hint = "深度文件不存在: " + depthPath;
            Debug.LogError(_hint);
            return;
        }

        // json 里 color_file/depth_file 可以是图片 (png/tif 等), 也可以是视频
        _colorIsImage = StillImageLoader.IsImage(colorPath);
        _depthIsImage = StillImageLoader.IsImage(depthPath);
        ReleaseStillImages();
        _colorRT = null;   // 上一轮视频的 RT 已销毁, 静态图片模式不能再用
        _vFrames = 0;
        _vfps = 30.0;

        if (_colorIsImage)
            LoadColorImage();
        else
            ColorVideo = CreatePlayer(colorPath, "颜色 (master)");
        // 深度管道 (视频) 在颜色流就绪后再启动, 避免 ffmpeg 提前解码造成启动帧积压 (表现为深度滞后)

        if (_depthIsImage)
            LoadDepthImage();
        else if (_colorIsImage)
        {
            // 静态颜色 + 深度视频: 深度只取首帧常驻显示
            try
            {
                DepthPipe.Start(depthPath);
            }
            catch (Exception e)
            {
                Debug.LogError("[DepthPipe] 启动失败: " + e.Message);
                _hint = "深度管道启动失败: " + e.Message;
            }
        }
    }

    /// <summary>加载静态颜色图片 (无需 VideoPlayer, 直接当纹理用)</summary>
    void LoadColorImage()
    {
        try
        {
            _imgColorTex = StillImageLoader.LoadColor(colorPath, out int w, out int h);
            ApplyColorLayout(w, h);
            Debug.Log($"[Image] 颜色静态图 {w}x{h} ({_layoutDesc})");
        }
        catch (Exception e)
        {
            _hint = "颜色图片加载失败: " + e.Message;
            Debug.LogError(_hint);
        }
    }

    /// <summary>加载静态深度图片 (R16 码值纹理, 满量程按 json depth_bits/depth_max 或数据推断)</summary>
    void LoadDepthImage()
    {
        try
        {
            _imgDepthTex = StillImageLoader.LoadDepth(depthPath, _depthEncode, _depthBitsHint, _depthMaxHint,
                                                    nearM, farM, _logDepth,
                                                    out _imgDepthMax, out _imgDepthBits);
            Debug.Log($"[Image] 深度静态图 {_imgDepthTex.width}x{_imgDepthTex.height} ({_imgDepthBits}bit, 满量程 {_imgDepthMax})");
        }
        catch (Exception e)
        {
            _hint = "深度图片加载失败: " + e.Message;
            Debug.LogError(_hint);
        }
    }

    void ReleaseStillImages()
    {
        if (_imgColorTex != null) { Destroy(_imgColorTex); _imgColorTex = null; }
        if (_imgDepthTex != null) { Destroy(_imgDepthTex); _imgDepthTex = null; }
        if (_bgTexL != null) { Destroy(_bgTexL); _bgTexL = null; }
        if (_bgTexR != null) { Destroy(_bgTexR); _bgTexR = null; }
    }

    /// <summary>按 json color_layout 或宽高比决定颜色 UV 重映射 (SBS 左半 / TB 上半 / 单目全幅)</summary>
    void ApplyColorLayout(int w, int h)
    {
        // 宽屏 2:1 = 左右双目 SBS (每眼 1:1), 只取左半作为单目颜色; 深度始终单目
        float ar = (float)w / h;
        if (_colorLayout == "sbs_left" || _colorLayout == "sbs")
        {
            _colorST = new Vector4(0.5f, 1f, 0f, 0f);
            _layoutDesc = $"SBS->左半 {w / 2}x{h}";
        }
        else if (_colorLayout == "tb_top" || _colorLayout == "tb")
        {
            _colorST = new Vector4(1f, 0.5f, 0f, 0.5f);
            _layoutDesc = $"上下->上半 {w}x{h / 2}";
        }
        else if (_colorLayout == "mono")
        {
            _colorST = new Vector4(1f, 1f, 0f, 0f);
            _layoutDesc = "单目全幅";
        }
        else if (ar > 1.6f)
        {
            _colorST = new Vector4(0.5f, 1f, 0f, 0f);
            _layoutDesc = $"SBS->左半 {w / 2}x{h}";
        }
        else if (ar < 0.625f)
        {
            _colorST = new Vector4(1f, 0.5f, 0f, 0.5f);
            _layoutDesc = $"上下->上半 {w}x{h / 2}";
        }
        else
        {
            _colorST = new Vector4(1f, 1f, 0f, 0f);
            _layoutDesc = "单目全幅";
        }
        // 右眼颜色区域 (双目模式 depth_layout=sbs): SBS 取右半, TB 取下半, 单目与左眼相同
        if (_colorST == new Vector4(0.5f, 1f, 0f, 0f)) _colorSTR = new Vector4(0.5f, 1f, 0.5f, 0f);
        else if (_colorST == new Vector4(1f, 0.5f, 0f, 0.5f)) _colorSTR = new Vector4(1f, 0.5f, 0f, 0f);
        else _colorSTR = _colorST;
    }

    VideoPlayer CreatePlayer(string path, string label)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException($"[{label}] 路径为空");
        if (!File.Exists(path)) throw new FileNotFoundException($"[{label}] 文件不存在: " + path);

        if (_audio == null)
        {
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.volume = _volume;
        }

        var vp = gameObject.AddComponent<VideoPlayer>();
        vp.playOnAwake = false;
        vp.renderMode = VideoRenderMode.RenderTexture;
        vp.isLooping = true;
        // 音量控制需要走 AudioSource; 无音轨的视频只是没有声音, 不影响画面
        vp.audioOutputMode = VideoAudioOutputMode.AudioSource;
        vp.controlledAudioTrackCount = 1;
        vp.SetTargetAudioSource(0, _audio);
        // VideoPlayer.url 在 Win/Android 上直接吃本地路径即可 (Unity 内部自己处理 file:// 转换),
        // 别用 new Uri(path).AbsoluteUri —— Linux/Android 风格路径 (没有盘符) Mono 会抛 UriFormatException
        vp.url = path;
        vp.errorReceived += (v, msg) => Debug.LogError($"[{label}] {msg}");
        vp.Prepare();
        vp.prepareCompleted += v =>
        {
            int w = (int)v.width, h = (int)v.height;
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.Default)
            {
                name = "ColorRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            v.targetTexture = rt;
            _cReady = true;
            // 输入布局: 优先用 json 的 color_layout (确定性强), 缺失时按宽高比推断
            ApplyColorLayout(w, h);
            Debug.Log($"[Video] {label} 已就绪 {w}x{h} ({_layoutDesc})");

            // 播放区间: json 的 start_time/end_time (秒); end_time=0 或缺失 = 到末尾, 两者皆缺省 = 整段循环
            double fps = v.frameRate > 0.0 ? v.frameRate : 30.0;
            long frameCount = (long)v.frameCount;
            _vfps = fps;
            _vFrames = frameCount;
            bool hasRange = (_startTime > 0f || _endTime > 0f) && frameCount > 0;
            if (hasRange)
            {
                _startFrame = (long)Math.Round(_startTime * fps);
                _endFrame = _endTime > 0f ? (long)Math.Round(_endTime * fps) : frameCount;
                if (_startFrame >= frameCount) _startFrame = 0;      // 起点越界: 退回整段
                if (_endFrame <= _startFrame || _endFrame > frameCount) _endFrame = frameCount;
                _rangeActive = _endFrame > _startFrame;
            }
            else
            {
                _startFrame = 0;
                _endFrame = frameCount;
                _rangeActive = false;
            }
            // 颜色一圈的帧数: 深度按这个圈做比例映射 (深度视频长度可能不同)
            _colorLoopLen = _rangeActive ? (_endFrame - _startFrame) : frameCount;
            _depthLoopBase = 0;
            vp.isLooping = !_rangeActive;   // 区间循环由 Update 手动跳回起点, 整段走播放器原生循环
            if (_startFrame > 0) vp.frame = _startFrame;
            Debug.Log(_rangeActive
                ? $"[Video] 循环区间 {_startTime:F2}s..{_endTime:F2}s (帧 {_startFrame}..{_endFrame}) / 共 {frameCount} 帧"
                : $"[Video] 整段循环 ({frameCount} 帧)");

            // 颜色流就绪后再起深度管道, 避免积压导致深度滞后 (深度是图片时不用起管道)
            if (!_depthIsImage)
            {
                try
                {
                    DepthPipe.Start(depthPath);   // ffmpeg nvdec 硬解管道, 10/12bit 码值直达 CPU 纹理
                    _depthPrerolled = false;
                    _depthPrerollWarned = false;
                }
                catch (Exception e)
                {
                    Debug.LogError("[DepthPipe] 启动失败: " + e.Message);
                    _hint = "深度管道启动失败: " + e.Message;
                }
            }
        };
        return vp;
    }

    /// <summary>
    /// 深度管道重启 (循环 wrap / 区间回跳 / 解码失败 / 3 秒无帧) 时:
    ///   先暂停颜色流, 等深度队列重新预填充到门槛后, 由 Update 顶部的预填充逻辑自动恢复播放.
    ///   避免颜色在深度管道冷启动的 ~1 秒里继续前进, 把队列抽空导致深度断供/卡顿.
    /// 不设 _manuallyPaused, 这样用户没有手动暂停时预填充逻辑会自动 Play.
    /// </summary>
    void RestartDepthAndHoldColor()
    {
        DepthPipe.Restart(depthPath);
        _depthLoopBase = 0;   // 深度管道从帧0 重新解码, 虚拟帧号也归零
        _depthPrerolled = false;
        _depthPrerollWarned = false;
        _depthPrerollTimer = 0f;
        if (!_manuallyPaused && ColorVideo != null && ColorVideo.isPlaying)
        {
            ColorVideo.Pause();
            ColorVideo.frame = _startFrame;   // 颜色也回到圈起点, 与从帧0 重启的深度重新对齐
            _cPrevFrame = -1;                 // 避免回跳被判成"循环一圈"而重复推进虚拟帧号
            _cLastFrame = -1;
        }
    }

    // ---------- 每帧: 播放/同步 ----------

    void UpdatePlayback()
    {
        // 纹理绑定: 视频走 VideoPlayer 的 RT, 静态图片直接用自己的纹理
        RenderTexture cRT = ColorVideo != null ? ColorVideo.targetTexture as RenderTexture : null;
        if (cRT != null) _colorRT = cRT;

        // === 颜色流 ready 后启动: 若深度走管道, 先等队列预填充若干帧再播颜色, 避免开头深度断供 ===
        if (_cReady && !_manuallyPaused && ColorVideo != null && !ColorVideo.isPlaying)
        {
            if (!_depthIsImage && !_depthPrerolled)
            {
                // 预填充门槛: 至少 10 帧 (约 170ms@60fps), 抵消解码启动抖动与上传延迟; 队列满(24)最佳但等更久
                const int prerollMin = 10;
                const float prerollTimeout = 6f;   // 最多等 6 秒, 超时就放弃深度直接播颜色 (避免永远黑屏)
                if (DepthPipe.Queued < prerollMin)
                {
                    _depthPrerollTimer += Time.unscaledDeltaTime;
                    if (!_depthPrerollWarned)
                    {
                        _depthPrerollWarned = true;
                        Debug.Log($"[Sync] 等待深度队列预填充 ({DepthPipe.Queued}/{prerollMin})... 超时={prerollTimeout}s, 已等={_depthPrerollTimer:F1}s, DepthPipe.Ready={DepthPipe.Ready}, Failed={DepthPipe.Failed}");
                    }
                    if (_depthPrerollTimer >= prerollTimeout)
                    {
                        Debug.LogWarning($"[Sync] 深度预填充超时 ({DepthPipe.Queued}/{prerollMin}), 放弃深度直接播颜色. 管道Ready={DepthPipe.Ready}, Failed={DepthPipe.Failed}");
                        _depthPrerolled = true;   // 放弃 preroll, 让颜色继续
                        // 如果管道失败了, 禁用深度让 shader 显示平面球壳 (比黑屏强)
                        if (DepthPipe.Failed) Debug.LogWarning("[Sync] 深度管道已失败, 画面会是平面球壳");
                    }
                    else
                    {
                        return;   // 还在等, 跳过本帧贴帧
                    }
                }
                else
                {
                    _depthPrerolled = true;
                    Debug.Log($"[Sync] 深度队列预填充完成 ({DepthPipe.Queued} 帧), 启动颜色流");
                }
            }
            if (_startFrame > 0) ColorVideo.frame = _startFrame;   // 区间起点 (部分后端 prepared 期 seek 会被丢弃)
            ColorVideo.Play();
            if (_audio != null && !_audio.isPlaying) _audio.Play();
            Debug.Log("[Sync] 颜色流启动, 深度管道贴帧");
        }

        Texture colorTex = _colorRT != null ? (Texture)_colorRT : _imgColorTex;
        Texture depthTex = DepthPipe.Texture != null ? (Texture)DepthPipe.Texture : _imgDepthTex;
        int depthMax = DepthPipe.Texture != null ? DepthPipe.MaxCode : _imgDepthMax;
        if (_mat != null && colorTex != null) _mat.mainTexture = colorTex;
        if (_mat != null && depthTex != null)
        {
            _mat.SetTexture("_DepthTex", depthTex);
            _mat.SetVector("_DepthTexel", new Vector4(1f / depthTex.width, 1f / depthTex.height, depthTex.width, depthTex.height));
            _mat.SetFloat("_DepthMax", depthMax);   // 码值满量程 1023/4095, shader 反解除数
            _mat.SetInt("_DepthVFlip", _depthIsImage ? 0 : 1);   // 视频深度 rawvideo top-down, shader 翻 V; 静态图加载时已 vflip
            ApplyEyeSplit();   // 颜色/深度半幅与分眼参数走 MaterialPropertyBlock, 左右壳各自生效
            if (!_videoReady && colorTex != null) _videoReady = true;   // 左上角只显示文件名, 不再报分辨率/位深
        }

        // === 看门狗: 只看颜色流 ===
        HandleStall(ColorVideo, ref _cLastFrame, ref _cStall, ref _cKick, "彩");

        // === 区间循环: 颜色流到 end 跳回 start; 深度管道用 -stream_loop 自己循环, 不重启 ===
        if (_rangeActive && !_manuallyPaused && ColorVideo != null && ColorVideo.isPlaying
            && _endFrame > _startFrame)
        {
            long cf = ColorVideo.frame;
            if (!_rangeSeeking && cf >= _endFrame - 1)
            {
                if (ShouldAdvanceOnLoop())
                {
                    Debug.Log("[UI] 区间结束, 自动切下一首");
                    AdvanceTrack();
                    return;
                }
                _rangeSeeking = true;
                ColorVideo.Pause();
                ColorVideo.frame = _startFrame;
                ColorVideo.Play();
                if (_audio != null) _audio.time = 0f;
                if (_audio != null && !_audio.isPlaying) _audio.Play();
                _cLastFrame = -1;
                _cStall = 0f;
                Debug.Log($"[Sync] 区间循环 {_startFrame}..{_endFrame}");
            }
            else if (_rangeSeeking && cf < _endFrame - 1)
            {
                _rangeSeeking = false;   // 回跳完成
            }
        }

        // === 深度管道: 贴帧 + 健康 ===
        if (DepthPipe.Ready && _colorIsImage)
        {
            DepthPipe.UpdateTo(0);   // 静态颜色 + 深度视频: 只贴首帧
        }
        else if (!_manuallyPaused && ColorVideo != null && ColorVideo.isPlaying && DepthPipe.Ready)
        {
            long cf = ColorVideo.frame;
            // 颜色循环一圈 (区间回跳 / 整段 wrap): 深度虚拟帧号整体推进一圈
            // 深度管道自己用 -stream_loop 连续解码, 这里只是把"第几圈"反映到虚拟帧号上
            if (_cPrevFrame >= 0 && cf < _cPrevFrame - 50 && DepthPipe.FrameCount > 0
                && _colorLoopLen > 0)
            {
                if (ShouldAdvanceOnLoop())
                {
                    Debug.Log("[UI] 整段播完, 自动切下一首");
                    AdvanceTrack();
                    return;
                }
                _depthLoopBase += DepthPipe.FrameCount;
                Debug.Log($"[Sync] 颜色回到起点 (帧{cf}), 深度虚拟帧推进 +{DepthPipe.FrameCount}");
            }
            DepthPipe.UpdateTo(DepthTargetFrame(cf));
        }
        // 深度同步诊断: 每 2 秒报一次队列水位/断供/丢帧/滞后, 区分"解码跟不上"还是"渲染掉帧"
        if (DepthPipe.Ready)
        {
            _diagT += Time.unscaledDeltaTime;
            if (_diagT >= 2f)
            {
                _diagT = 0f;
                int st = DepthPipe.Starved, dr = DepthPipe.Dropped, pr = DepthPipe.Produced;
                int ap = DepthPipe.Applied;
                if (st != _cStarved || dr != _cDropped || pr != _cProduced || ap != _cApplied)
                {
                    // Restart 会把计数清零, 用 Max(0) 避免出现负数
                    long lag = DepthPipe.LastTarget - DepthPipe.AppliedFrame;
                    Debug.Log($"[Sync] 深度队列 {DepthPipe.Queued} 帧, 滞后 {lag} 帧 (目标{DepthPipe.LastTarget}/已贴{DepthPipe.AppliedFrame}), " +
                              $"断供 +{Mathf.Max(0, st - _cStarved)} 次, 丢帧 +{Mathf.Max(0, dr - _cDropped)}, " +
                              $"解码产出 +{Mathf.Max(0, pr - _cProduced)}, 贴图 +{Mathf.Max(0, ap - _cApplied)}, " +
                              $"渲染 {_fpsShown:F1} fps");
                    _cStarved = st; _cDropped = dr; _cProduced = pr; _cApplied = ap;
                }
            }
        }
        // 深度管道健康: 解码失败或长时间无帧时重启
        if (DepthPipe.Ready && DepthPipe.Failed)
        {
            Debug.LogWarning("[DepthPipe] 解码失败, 重启. stderr: " + DepthPipe.Error);
            RestartDepthAndHoldColor();
        }
        else if (DepthPipe.Ready && !DepthPipe.Eof && DepthPipe.IdleTime > 3f)
        {
            Debug.LogWarning("[DepthPipe] 3 秒无帧, 重启");
            RestartDepthAndHoldColor();
        }
        _cPrevFrame = ColorVideo != null ? ColorVideo.frame : -1;
    }

    /// <summary>
    /// 颜色帧号 -> 深度"虚拟帧号" (跨循环单调递增, 深度管道用 -stream_loop 一直在解码).
    /// 颜色只播 [startFrame, endFrame) 这一圈, 深度视频的帧数可能和这一圈的帧数不同
    /// (例如颜色是整段视频、深度只覆盖推理区间), 所以按"圈内相对进度"做比例映射, 不能简单相减.
    /// </summary>
    long DepthTargetFrame(long colorFrame)
    {
        long rel = colorFrame - _startFrame;
        if (rel < 0) rel = 0;
        long colorLen = _colorLoopLen > 0 ? _colorLoopLen : 1;
        long depthLen = DepthPipe.FrameCount;
        long mapped = depthLen > 0 ? (long)Math.Round((double)rel * depthLen / colorLen) : rel;
        long t = _depthLoopBase + mapped + depthLeadFrames;
        if (depthLen > 0)   // 夹在本圈内, 免得提前读到下一圈的帧
        {
            long hi = _depthLoopBase + depthLen - 1;
            if (t > hi) t = hi;
        }
        return t < 0 ? 0 : t;
    }

    void HandleStall(VideoPlayer vp, ref long lastFrame, ref float timer, ref int kicks, string tag)
    {
        if (vp == null || !vp.isPlaying || !vp.isPrepared) return;
        if (vp.frame == lastFrame)
        {
            timer += Time.unscaledDeltaTime;
            // isLooping 模式下停在最后一帧也是异常 (正常应 wrap 回 0), 一律 seek 恢复
            if (timer >= stallTimeout && vp.frameCount > 0)
            {
                timer = 0f; kicks++;
                Debug.LogWarning($"[{tag}] 解码停滞, seek 恢复 (第{kicks}次) 帧{vp.frame}");
                long pos = vp.frame;
                vp.Pause();
                vp.frame = pos;
                vp.Play();
                if (_audio != null && !_audio.isPlaying) _audio.Play();
                // 注意: 不要在这里重启深度管道. 深度有预解码队列, seek 后靠丢旧帧追上即可;
                // 重启要从文件头重新解码, 反而造成数秒的深度错位/黑深度.
            }
        }
        else { lastFrame = vp.frame; timer = 0f; }
    }

    void UpdateFps()
    {
        _fpsAcc += Time.unscaledDeltaTime;
        _fpsCount++;
        if (_fpsAcc >= 0.5f)
        {
            _fpsShown = _fpsCount / _fpsAcc;
            _fpsAcc = 0f; _fpsCount = 0;
            if (_ui != null) _ui.SetFps(Mathf.RoundToInt(_fpsShown));
        }
    }

    /// <summary>Inspector 实时调参 (球壳/细分/深度参数), 与 UI 设置面板共用同一批字段</summary>
    void SyncMaterialParams()
    {
        if (_mat != null)
        {
            if (!Mathf.Approximately(tessLength, _cTessLen)) { _cTessLen = tessLength; _mat.SetFloat("_TessLength", tessLength); }
            if (!Mathf.Approximately(tessMax, _cTessMax)) { _cTessMax = tessMax; _mat.SetFloat("_TessMax", tessMax); }
            if (!Mathf.Approximately(tessGradBoost, _cTessGrad)) { _cTessGrad = tessGradBoost; _mat.SetFloat("_GradBoost", tessGradBoost); }
            if (!Mathf.Approximately(depthScale, _cScale)) { _cScale = depthScale; _mat.SetFloat("_Scale", depthScale); }
            if (!Mathf.Approximately(smoothAmt, _cSmooth)) { _cSmooth = smoothAmt; _mat.SetFloat("_Smooth", smoothAmt); }
            if (!Mathf.Approximately(smoothEdge, _cSmoothEdge)) { _cSmoothEdge = smoothEdge; _mat.SetFloat("_SmoothEdge", smoothEdge); }
            // rimClamp 关闭时把阈值拉到 999°, 任何方向都触发不了 → 等效关闭
            float effRimAngle = rimClamp ? rimAngle : 999f;
            if (!Mathf.Approximately(effRimAngle, _cRimAngle)) { _cRimAngle = effRimAngle; _mat.SetFloat("_RimAngle", effRimAngle); }
            // 边缘深度下限随头显后移加大: 后移 0 → rimMinDepth, 与后移距离 1:1 增长, 封顶 rimMinDepthMax
            float headBack = 0f;
            if (Camera.main != null) headBack = Mathf.Max(0f, -Camera.main.transform.localPosition.z);
            _mat.SetFloat("_RimMinDepth", Mathf.Min(rimMinDepth + headBack, rimMinDepthMax));
            if (debugView != _cView) { _cView = debugView; _mat.SetInt("_View", debugView); }
            if (_logDepth != _cLog) { _cLog = _logDepth; _mat.SetFloat("_LogDepth", _logDepth); }
            if (msaa != _cMsaa) { _cMsaa = msaa; QualitySettings.antiAliasing = msaa; }
            if (!Mathf.Approximately(nearM, _cNear) || !Mathf.Approximately(farM, _cFar))
            {
                _cNear = nearM; _cFar = farM;
                _mat.SetFloat("_Near", nearM);
                _mat.SetFloat("_Far", farM);
                UpdateBgRadius();   // farM 变化时背景半径同步更新
            }
            // 深度断层镂空阈值 (0=关闭): 跨前景/背景的面片被丢弃, 露出后方背景球壳.
            // 仅在找到背景图时启用, 否则关闭避免无背景视频出现黑孔.
            _mat.SetFloat("_FaceCull", _bgEnabled ? faceCull : 0f);
        }
        if (_shellFilter != null && segments != _builtSeg)
        {
            _builtSeg = segments;
            var m = BuildHemisphere(segments);
            _shellFilter.sharedMesh = m;
            if (_shellFilterR != null) _shellFilterR.sharedMesh = m;
            // 背景球壳共用同一半球 mesh
            if (_bgRendererL != null) _bgRendererL.GetComponent<MeshFilter>().sharedMesh = m;
            if (_bgRendererR != null) _bgRendererR.GetComponent<MeshFilter>().sharedMesh = m;
        }
    }

    // ---------- 场景搭建 ----------

    void SetupCamera()
    {
        var cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            cam = go.AddComponent<Camera>();
        }
        // XR Origin 父节点: TPD 把头部 pose 写入相机 localPosition,
        // 要让头部落在世界原点只需把 origin 平移到 -localPosition, 不会和 TPD 抢 position
        if (_xrOrigin == null)
        {
            var originGo = new GameObject("XROrigin");
            _xrOrigin = originGo.transform;
        }
        _xrOrigin.position = Vector3.zero;
        _xrOrigin.rotation = Quaternion.identity;
        cam.transform.SetParent(_xrOrigin, true);
        cam.transform.localPosition = Vector3.zero;
        cam.transform.localRotation = Quaternion.identity;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 100f;
        cam.depth = 0f;   // 默认 0; -1 在某些 XR 配置下会导致场景不渲染
        cam.fieldOfView = 90f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.02f, 0.02f, 0.02f, 1f);
        if (msaa > 0 && QualitySettings.antiAliasing != msaa)
            QualitySettings.antiAliasing = msaa;
        if (cam.GetComponent<DesktopController>() == null)
            cam.gameObject.AddComponent<DesktopController>();
        // XR 头显: 相机必须挂 TrackedPoseDriver, XR 才会把渲染提交到头显眼缓冲;
        // 否则头显只看到清除色 (背景), 场景物体不显示
        var tpd = cam.GetComponent<TrackedPoseDriver>();
        if (tpd == null)
            tpd = cam.gameObject.AddComponent<TrackedPoseDriver>();
        // Device 原点 = 眼平 (头显启动时头部位移≈0), 避免 Floor 原点把真实身高带入导致初始视角过高.
        // 新版 TPD 无 trackingOrigin 属性, 通过 XRInputSubsystem 设置.
        // 注意: Start() 时 XR 子系统可能尚未就绪, 设置会失败; 在 VR 首次激活时重试.
        TrySetDeviceTrackingOrigin();
    }

    /// <summary>把所有 XRInputSubsystem 的追踪原点设为 Device (眼平)</summary>
    void TrySetDeviceTrackingOrigin()
    {
        var subsystems = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        foreach (var s in subsystems)
            s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
    }

    /// <summary>把当前头部位置对齐到世界原点 (0,0,0): origin.position = -camera.localPosition</summary>
    void RecenterHeadToOrigin()
    {
        var cam = Camera.main;
        if (cam == null || _xrOrigin == null) return;
        _xrOrigin.position = -cam.transform.localPosition;
    }

    void BuildDesktopHint()
    {
        if (_desktopHintGo != null) return;

        // 只用一个 ScreenSpaceOverlay Canvas (不绑 worldCamera, 不设 targetDisplay),
        // 让桌面主相机自己的 clearFlags=SolidColor 给背景铺深色, 提示屏 Canvas 自动叠在上面.
        // 独立 Camera 在 Unity 6 + XR 下容易被 Runtime 接管导致桌面完全黑屏.
        _desktopHintGo = new GameObject("DesktopHintCanvas");
        _desktopHintCanvas = _desktopHintGo.AddComponent<Canvas>();
        _desktopHintCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _desktopHintCanvas.sortingOrder = 9999;
        _desktopHintGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        _desktopHintGo.AddComponent<GraphicRaycaster>();

        // 深色全屏 Panel (盖住 VR 画面残留)
        var panel = new GameObject("Panel");
        panel.transform.SetParent(_desktopHintGo.transform, false);
        var panelImg = panel.AddComponent<Image>();
        panelImg.color = new Color(0.02f, 0.02f, 0.02f, 1f);
        var panelRt = (RectTransform)panel.transform;
        panelRt.anchorMin = Vector2.zero;
        panelRt.anchorMax = Vector2.one;
        panelRt.offsetMin = Vector2.zero;
        panelRt.offsetMax = Vector2.zero;

        // 标题
        _desktopHintTitle = CreateHintText(_desktopHintGo.transform, "Title", 36, TextAnchor.MiddleCenter);
        var titleRt = (RectTransform)_desktopHintTitle.transform;
        titleRt.anchorMin = new Vector2(0.5f, 0.5f);
        titleRt.anchorMax = new Vector2(0.5f, 0.5f);
        titleRt.anchoredPosition = new Vector2(0, 40);
        titleRt.sizeDelta = new Vector2(800, 60);

        // 正文
        _desktopHintBody = CreateHintText(_desktopHintGo.transform, "Body", 24, TextAnchor.MiddleCenter);
        var bodyRt = (RectTransform)_desktopHintBody.transform;
        bodyRt.anchorMin = new Vector2(0.5f, 0.5f);
        bodyRt.anchorMax = new Vector2(0.5f, 0.5f);
        bodyRt.anchoredPosition = new Vector2(0, -20);
        bodyRt.sizeDelta = new Vector2(800, 40);

        ApplyDesktopHintLang();
        _desktopHintGo.SetActive(false);
    }

    Text CreateHintText(Transform parent, string name, int fontSize, TextAnchor anchor)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (t.font == null) t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = fontSize;
        t.alignment = anchor;
        t.color = Color.white;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        return t;
    }

    void ApplyDesktopHintLang()
    {
        // 跟随 Gray10Player.UiLang 设置 (LoadSettings 里确定, SetUiLang 时同步)
        bool zh = !UiLang.StartsWith("en");   // 默认中文
        _desktopHintTitle.text = zh ? "VR 模式已激活" : "VR Mode Active";
        _desktopHintBody.text  = zh ? "请在头显中操作"   : "Please use your VR headset";
    }

    void SetDesktopHintEnabled(bool enabled)
    {
        if (_desktopHintGo == null) return;
        if (enabled) ApplyDesktopHintLang();
        _desktopHintGo.SetActive(enabled);
    }

    void BuildShell()
    {
        var mesh = BuildHemisphere(segments);
        // 球壳根节点: 只负责 yaw 对齐 (原 _shellT 语义); 左右壳挂在下面,
        // 双目模式时各偏移 ±IPD/2 —— 深度是"到对应眼睛的距离", 球心必须与该眼重合才能正确融合
        var root = new GameObject("ShellRoot");
        _shellT = root.transform;
        var go = new GameObject("DepthShell");
        go.transform.SetParent(_shellT, false);
        _shellFilter = go.AddComponent<MeshFilter>();
        _shellFilter.sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        _shellRenderer = renderer;

        var shader = Shader.Find("VR180/Gray10Shell");
        _mat = new Material(shader);
        renderer.sharedMaterial = _mat;
        _builtSeg = segments;
        renderer.enabled = false;

        // 右眼球壳: 同 mesh 同材质, 与左壳同级跟随根节点; 双目模式 (depth_layout=sbs) 才启用
        var goR = new GameObject("DepthShellR");
        goR.transform.SetParent(_shellT, false);
        _shellFilterR = goR.AddComponent<MeshFilter>();
        _shellFilterR.sharedMesh = mesh;
        _shellRendererR = goR.AddComponent<MeshRenderer>();
        _shellRendererR.sharedMaterial = _mat;
        _shellRendererR.enabled = false;

        // ---- 背景球壳: 同 mesh, 无深度位移, 半径 = farM * depthScale * bgRadiusMul (在深度球壳后方) ----
        // 镂空处 (深度断层剔除) 露出背景. 左右眼各一个, 与深度壳同级跟随根节点, 偏移 ±IPD/2
        var bgShader = Shader.Find("VR180/BgShell");
        if (bgShader != null)
        {
            _bgMatL = new Material(bgShader);
            _bgMatR = new Material(bgShader);

            var bgL = new GameObject("BgShell");
            bgL.transform.SetParent(_shellT, false);
            bgL.AddComponent<MeshFilter>().sharedMesh = mesh;
            _bgRendererL = bgL.AddComponent<MeshRenderer>();
            _bgRendererL.sharedMaterial = _bgMatL;
            _bgRendererL.enabled = false;

            var bgR = new GameObject("BgShellR");
            bgR.transform.SetParent(_shellT, false);
            bgR.AddComponent<MeshFilter>().sharedMesh = mesh;
            _bgRendererR = bgR.AddComponent<MeshRenderer>();
            _bgRendererR.sharedMaterial = _bgMatR;
            _bgRendererR.enabled = false;

            UpdateBgRadius();
        }
        else
        {
            Debug.LogWarning("[BgShell] 找不到 VR180/BgShell shader, 背景球壳未创建");
        }

        ApplyEyeSplit();
    }

    /// <summary>背景球壳半径 = farM * depthScale * bgRadiusMul (始终在深度球壳最大半径之外)</summary>
    void UpdateBgRadius()
    {
        if (_bgRendererL == null) return;
        float r = Mathf.Max(farM * depthScale * bgRadiusMul, 0.1f);
        // hemisphere mesh 半径 = farM, 缩放系数 = 目标半径 / farM
        float s = r / Mathf.Max(farM, 0.001f);
        _bgRendererL.transform.localScale = new Vector3(s, s, s);
        if (_bgRendererR != null) _bgRendererR.transform.localScale = new Vector3(s, s, s);
    }

    /// <summary>实测瞳距 (米): 读 XR 左右眼设备位置; 测不到时用典型值 64mm, 测到后缓存</summary>
    float MeasureEyeIPD()
    {
        if (_eyeIPD > 0.001f) return _eyeIPD;
        var ld = InputDevices.GetDeviceAtXRNode(XRNode.LeftEye);
        var rd = InputDevices.GetDeviceAtXRNode(XRNode.RightEye);
        Vector3 lp, rp;
        if (ld.TryGetFeatureValue(CommonUsages.devicePosition, out lp) &&
            rd.TryGetFeatureValue(CommonUsages.devicePosition, out rp))
        {
            float d = Vector3.Distance(lp, rp);
            if (d > 0.03f && d < 0.12f)
            {
                _eyeIPD = d;   // 合理范围才采信
                Debug.Log($"[DualEye] 采信 IPD = {_eyeIPD * 1000f:F1} mm  (L={lp} R={rp})");
            }
            else if (!_ipdFallbackWarned)
            {
                _ipdFallbackWarned = true;
                Debug.LogWarning($"[DualEye] 瞳距读数异常 L={lp} R={rp} 距离={d * 1000f:F1}mm (设备有效 L={ld.isValid} R={rd.isValid}), 暂用 64mm 兜底");
            }
        }
        else if (!_ipdFallbackWarned)
        {
            _ipdFallbackWarned = true;
            Debug.LogWarning($"[DualEye] 读不到眼位 (设备有效 L={ld.isValid} R={rd.isValid}), 暂用 64mm 兜底");
        }
        return _eyeIPD > 0.001f ? _eyeIPD : 0.0645f;   // OpenXR 运行时常把左右眼都报成头部中心, 实测失败时用 64.5mm
    }

    /// <summary>球壳显隐: 右壳只在双目深度 (depth_layout=sbs) 时跟随左壳</summary>
    void SetShellVisible(bool v)
    {
        if (_shellRenderer != null) _shellRenderer.enabled = v;
        if (_shellRendererR != null) _shellRendererR.enabled = v && _depthLayout == "sbs";
        // 背景球壳跟随深度壳显隐 (镂空处才可见)
        if (_bgRendererL != null) _bgRendererL.enabled = v && _bgEnabled;
        if (_bgRendererR != null) _bgRendererR.enabled = v && _bgEnabled && _depthLayout == "sbs" && VrActive;
    }

    /// <summary>
    /// 分眼参数 (MaterialPropertyBlock, 两壳共享材质互不影响):
    /// 左壳 _EyeIndex=0 / 右壳=1 (shader 按 unity_StereoEyeIndex 裁掉不属于本壳的眼睛);
    /// 双目深度时深度纹理各取半幅, 颜色 SBS 左壳取左半、右壳取右半; 旧单目导出 _EyeIndex=-1 不裁剪
    /// </summary>
    void ApplyEyeSplit()
    {
        bool dual = _depthLayout == "sbs";

        // ===== 单目: 直接在材质上设参数 (与 vr_player_unity6_single 行为一致), 不用 PropertyBlock =====
        if (!dual)
        {
            // 清除之前双目模式残留的 PropertyBlock, 回退到材质参数
            if (_shellRenderer != null) _shellRenderer.SetPropertyBlock(null);
            if (_shellRendererR != null) _shellRendererR.SetPropertyBlock(null);
            if (_mat != null)
            {
                _mat.SetFloat("_EyeIndex", -1f);           // 双眼都画
                _mat.SetVector("_ColorST", _colorST);      // 单目全幅 / SBS 取左半
                _mat.SetVector("_DepthST", new Vector4(1f, 1f, 0f, 0f));   // 深度全幅
            }
            if (_shellRenderer != null) _shellRenderer.transform.localPosition = Vector3.zero;
            if (_shellRendererR != null) _shellRendererR.enabled = false;
            // 背景: 单目只用左背景壳, 双眼都画
            if (_bgRendererL != null)
            {
                _bgMatL.SetFloat("_EyeIndex", -1f);
                _bgRendererL.transform.localPosition = Vector3.zero;
                _bgRendererL.enabled = _bgEnabled && _shellRenderer != null && _shellRenderer.enabled;
            }
            if (_bgRendererR != null) _bgRendererR.enabled = false;
            return;
        }

        // ===== 双目: MaterialPropertyBlock 分眼, 左右壳各取半幅 =====
        _pbL ??= new MaterialPropertyBlock();
        _pbR ??= new MaterialPropertyBlock();
        _pbL.Clear();
        _pbL.SetFloat("_EyeIndex", 0f);
        _pbL.SetVector("_ColorST", _colorST);
        _pbL.SetVector("_DepthST", new Vector4(0.5f, 1f, 0f, 0f));
        if (_shellRenderer != null) _shellRenderer.SetPropertyBlock(_pbL);
        if (_shellRendererR != null)
        {
            _pbR.Clear();
            _pbR.SetFloat("_EyeIndex", 1f);
            _pbR.SetVector("_ColorST", _colorSTR);
            _pbR.SetVector("_DepthST", new Vector4(0.5f, 1f, 0.5f, 0f));
            _shellRendererR.SetPropertyBlock(_pbR);
        }
        // 球心对眼: 双目且 VR 激活时左右壳各偏移 ±IPD/2
        float ipd = dual && VrActive ? MeasureEyeIPD() : 0f;
        if (_shellRenderer != null)
            _shellRenderer.transform.localPosition = new Vector3(-ipd * 0.5f, 0f, 0f);
        if (_shellRendererR != null)
            _shellRendererR.transform.localPosition = new Vector3(ipd * 0.5f, 0f, 0f);
        // 右壳只在双目+VR 时显示
        if (_shellRendererR != null && _shellRenderer != null)
            _shellRendererR.enabled = _shellRenderer.enabled && dual && VrActive;

        // 背景球壳分眼
        if (_bgRendererL != null)
        {
            _bgMatL.SetFloat("_EyeIndex", 0f);
            _bgRendererL.transform.localPosition = new Vector3(-ipd * 0.5f, 0f, 0f);
            _bgRendererL.enabled = _bgEnabled && _shellRenderer != null && _shellRenderer.enabled;
        }
        if (_bgRendererR != null)
        {
            _bgMatR.SetFloat("_EyeIndex", 1f);
            _bgRendererR.transform.localPosition = new Vector3(ipd * 0.5f, 0f, 0f);
            _bgRendererR.enabled = _bgEnabled && dual && VrActive && _shellRendererR != null && _shellRendererR.enabled;
        }
    }

    Mesh BuildHemisphere(int seg)
    {
        if (seg < 8) seg = 8;
        int vertCount = (seg + 1) * (seg + 1);
        var verts = new Vector3[vertCount];
        var uvs = new Vector2[vertCount];
        var tris = new int[seg * seg * 6];

        for (int y = 0; y <= seg; y++)
        {
            float v = (float)y / seg;
            float lat = (v - 0.5f) * Mathf.PI;
            float cl = Mathf.Cos(lat);
            for (int x = 0; x <= seg; x++)
            {
                float u = (float)x / seg;
                // VR180: 水平 180° (lon -π/2..+π/2), 垂直 180° (lat -π/2..+π/2)
                float lon = (u - 0.5f) * Mathf.PI;
                int i = y * (seg + 1) + x;
                verts[i] = new Vector3(Mathf.Sin(lon) * cl, Mathf.Sin(lat), Mathf.Cos(lon) * cl) * farM;
                uvs[i] = new Vector2(u, v);
            }
        }

        int t = 0;
        for (int y = 0; y < seg; y++)
        {
            for (int x = 0; x < seg; x++)
            {
                int a = y * (seg + 1) + x;
                int b = a + 1;
                int c = a + seg + 1;
                int d = c + 1;
                // 反转 winding: 让内表面为正面 (相机在球壳内部, Cull Back 时仍可见)
                tris[t++] = a; tris[t++] = c; tris[t++] = b;
                tris[t++] = b; tris[t++] = c; tris[t++] = d;
            }
        }

        var mesh = new Mesh { name = "Hemisphere" };
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---------- 参数 ----------

    void ParseArgs()
    {
        string projRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string wsRoot = Path.GetFullPath(Path.Combine(projRoot, ".."));

        colorPath = FindArg("color", "");
        depthPath = FindArg("depth", "");

        if (string.IsNullOrEmpty(depthPath) && !string.IsNullOrEmpty(colorPath))
            depthPath = Path.ChangeExtension(colorPath, null) + "_depth.mp4";   // 独立深度 mp4 (HEVC 10bit 无损, log 量化)

        if (!string.IsNullOrEmpty(colorPath) && !Path.IsPathRooted(colorPath))
            colorPath = Path.GetFullPath(Path.Combine(wsRoot, colorPath));
        if (!string.IsNullOrEmpty(depthPath) && !Path.IsPathRooted(depthPath))
            depthPath = Path.GetFullPath(Path.Combine(wsRoot, depthPath));

        // near/far 只从 sidecar json 读, 不在命令行单独指定 (见 LoadSidecar)
        if (int.TryParse(FindArg("segments", ""), out var s)) segments = s;
        if (int.TryParse(FindArg("view", ""), out var v)) debugView = v;
        if (float.TryParse(FindArg("scale", ""), out var sc)) depthScale = sc;
        if (float.TryParse(FindArg("smooth", ""), out var sm)) smoothAmt = sm;
        if (float.TryParse(FindArg("tess", ""), out var tl)) tessLength = tl;
        if (float.TryParse(FindArg("tessmax", ""), out var tm)) tessMax = tm;
        if (float.TryParse(FindArg("tessgrad", ""), out var tg)) tessGradBoost = tg;
        if (int.TryParse(FindArg("msaa", ""), out var ma)) msaa = ma;
        if (int.TryParse(FindArg("vr", ""), out var vr)) vrEnabled = vr != 0;
        if (int.TryParse(FindArg("simvr", ""), out var sv)) simulateVR = sv != 0;
        if (float.TryParse(FindArg("posscale", ""), out var ps)) posScale = Mathf.Clamp01(ps);
    }

    /// <summary>从 json 读 color_file/depth_file (相对 json 目录解析), 只填充尚未指定的路径</summary>
    static void TryLoadPathsFromJson(string json, ref string color, ref string depth)
    {
        try
        {
            if (!File.Exists(json))
            {
                Debug.LogError("[加载] 元数据 json 不存在: " + json);
                return;
            }
            string dir = Path.GetDirectoryName(json);
            string text = File.ReadAllText(json);
            var mc = Regex.Match(text, "\"color_file\"\\s*:\\s*\"([^\"]+)\"");
            var md = Regex.Match(text, "\"depth_file\"\\s*:\\s*\"([^\"]+)\"");
            if (string.IsNullOrEmpty(color) && mc.Success)
                color = Path.GetFullPath(Path.Combine(dir, mc.Groups[1].Value));
            if (string.IsNullOrEmpty(depth) && md.Success)
                depth = Path.GetFullPath(Path.Combine(dir, md.Groups[1].Value));
        }
        catch { }
    }

    static string FindArg(string key, string def)
    {
        string prefix = "-" + key + "=";
        foreach (var a in Environment.GetCommandLineArgs())
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return a.Substring(prefix.Length);
        return def;
    }

    void LoadSidecar()
    {
        _startTime = 0f;
        _endTime = 0f;
        _colorLayout = "";
        _depthLayout = "";   // 缺省=单左目; sbs=深度左右眼拼接
        _bgFileL = ""; _bgFileR = "";
        _depthBitsHint = 0;   // json 的深度位深/满量程提示 (图片输入时用)
        _depthMaxHint = 0;
        _depthEncode = "";
        try
        {
            // 依次找: 当前元数据 json / 颜色视频同名 json / 深度视频同名 json
            var cands = new List<string>();
            if (!string.IsNullOrEmpty(_metaJsonPath)) cands.Add(_metaJsonPath);
            cands.Add(Path.ChangeExtension(colorPath, ".json"));
            cands.Add(Path.ChangeExtension(depthPath, ".json"));
            foreach (string json in cands)
            {
                if (!File.Exists(json)) continue;
                string text = File.ReadAllText(json);
                var mNear = Regex.Match(text, "\"near(?:_m)?\"\\s*:\\s*([0-9.]+)");
                var mFar = Regex.Match(text, "\"far(?:_m)?\"\\s*:\\s*([0-9.]+)");
                if (mNear.Success) float.TryParse(mNear.Groups[1].Value, out nearM);
                if (mFar.Success) float.TryParse(mFar.Groups[1].Value, out farM);
                // 量化方式: json 缺省 encode 字段 = 旧版线性编码; mm = 16bit 毫米深度图 (图片输入)
                var mEnc = Regex.Match(text, "\"encode\"\\s*:\\s*\"([a-z_0-9]+)\"");
                if (mEnc.Success) _depthEncode = mEnc.Groups[1].Value;
                _logDepth = _depthEncode == "log" ? 1f : 0f;
                // 播放区间(秒): 缺失 = 整段循环; end_time=0 = 播放到颜色视频末尾
                var mStart = Regex.Match(text, "\"start_time\"\\s*:\\s*([0-9.]+)");
                var mEnd = Regex.Match(text, "\"end_time\"\\s*:\\s*([0-9.]+)");
                if (mStart.Success)
                    float.TryParse(mStart.Groups[1].Value, NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out _startTime);
                if (mEnd.Success)
                    float.TryParse(mEnd.Groups[1].Value, NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out _endTime);
                // 颜色布局: 宽屏 2:1 的 SBS 双目视频只取左半
                var mLayout = Regex.Match(text, "\"color_layout\"\\s*:\\s*\"([a-z_0-9]+)\"");
                if (mLayout.Success) _colorLayout = mLayout.Groups[1].Value;
                // 深度布局: sbs = 深度视频左右眼拼接 (左半左眼, 右半右眼), 双球壳分眼渲染; 缺省=单左目
                var mDLayout = Regex.Match(text, "\"depth_layout\"\\s*:\\s*\"([a-z_0-9]+)\"");
                _depthLayout = mDLayout.Success ? mDLayout.Groups[1].Value : "";
                // 背景图: 左右眼背景贴图路径, 相对 json 所在目录解析; 缺省=自动推导 <stem>_bg_L/R.png
                string jsonDir = Path.GetDirectoryName(json);
                var mbgL = Regex.Match(text, "\"bg_file_L\"\\s*:\\s*\"([^\"]+)\"");
                var mbgR = Regex.Match(text, "\"bg_file_R\"\\s*:\\s*\"([^\"]+)\"");
                if (mbgL.Success) _bgFileL = Path.IsPathRooted(mbgL.Groups[1].Value) ? mbgL.Groups[1].Value : Path.Combine(jsonDir, mbgL.Groups[1].Value);
                if (mbgR.Success) _bgFileR = Path.IsPathRooted(mbgR.Groups[1].Value) ? mbgR.Groups[1].Value : Path.Combine(jsonDir, mbgR.Groups[1].Value);
                // 深度图片位深/满量程 (图片输入时无法像视频那样从像素格式探到位深, 允许 json 显式给出)
                var mBits = Regex.Match(text, "\"depth_bits\"\\s*:\\s*([0-9]+)");
                if (mBits.Success) int.TryParse(mBits.Groups[1].Value, out _depthBitsHint);
                else
                {
                    var mb = Regex.Match(text, "\"bits\"\\s*:\\s*([0-9]+)");   // process_video.py 的 meta 用 "bits"
                    if (mb.Success) int.TryParse(mb.Groups[1].Value, out _depthBitsHint);
                }
                var mDmax = Regex.Match(text, "\"depth_max\"\\s*:\\s*([0-9]+)");
                if (mDmax.Success) int.TryParse(mDmax.Groups[1].Value, out _depthMaxHint);
                break;
            }
        }
        catch { }
    }

    /// <summary>切换视频文件后重建整条播放链 (VideoPlayer + 深度管道 + sidecar 参数)</summary>
    void ReloadVideos()
    {
        _videoReady = false;
        _cReady = false;
        _cKick = 0; _cStall = 0;
        _cLastFrame = -1; _cPrevFrame = -1;
        _depthLoopBase = 0; _colorLoopLen = 0;
        _depthPrerolled = false; _depthPrerollWarned = false;
        _rangeSeeking = false;
        _colorIsImage = false; _depthIsImage = false;
        _vFrames = 0;
        ReleaseStillImages();
        if (ColorVideo != null)
        {
            ColorVideo.Stop();
            if (ColorVideo.targetTexture != null)
            {
                ColorVideo.targetTexture.Release();
                Destroy(ColorVideo.targetTexture);
            }
            Destroy(ColorVideo);
            ColorVideo = null;
        }
        DepthPipe.Kill();
        LoadSidecar();
        _mat.SetFloat("_Near", nearM);
        _mat.SetFloat("_Far", farM);
        _mat.SetFloat("_LogDepth", _logDepth);
        LoadBackgroundTextures();
        StartVideos();
    }

    /// <summary>
    /// 加载背景图.
    /// 单目 (无 depth_layout=sbs): 只需一张背景图, 优先 json 的 bg_file, 其次 bg_file_L,
    ///   再回退 <stem>_bg.png / <stem>_bg_L.png.
    /// 双目: 需要左右眼各一张 (bg_file_L/bg_file_R 或 <stem>_bg_L.png / <stem>_bg_R.png).
    /// 找到则启用背景球壳, 找不到则关闭.
    /// </summary>
    void LoadBackgroundTextures()
    {
        _bgEnabled = false;
        if (_bgMatL == null) return;
        if (string.IsNullOrEmpty(colorPath) || !File.Exists(colorPath)) return;

        bool dual = _depthLayout == "sbs";
        string dir = Path.GetDirectoryName(colorPath);
        string stem = Path.GetFileNameWithoutExtension(colorPath);

        // 释放旧背景纹理
        if (_bgTexL != null) { Destroy(_bgTexL); _bgTexL = null; }
        if (_bgTexR != null) { Destroy(_bgTexR); _bgTexR = null; }

        // 左眼背景路径 (单目也用这一张)
        string bgLPath = !string.IsNullOrEmpty(_bgFileL) ? _bgFileL
            : Path.Combine(dir, stem + "_bg_L.png");
        // 单目兜底: 试 <stem>_bg.png
        if (!dual && !File.Exists(bgLPath))
        {
            string alt = Path.Combine(dir, stem + "_bg.png");
            if (File.Exists(alt)) bgLPath = alt;
        }

        if (!File.Exists(bgLPath))
        {
            Debug.Log($"[BgShell] 未找到背景图 ({Path.GetFileName(bgLPath)}), 背景球壳关闭");
            return;
        }

        try
        {
            _bgTexL = LoadPng(bgLPath);
            if (_bgTexL == null) { Debug.LogWarning("[BgShell] 背景图加载失败"); return; }
            _bgMatL.SetTexture("_BgTex", _bgTexL);

            if (dual)
            {
                string bgRPath = !string.IsNullOrEmpty(_bgFileR) ? _bgFileR
                    : Path.Combine(dir, stem + "_bg_R.png");
                if (!File.Exists(bgRPath))
                {
                    Debug.Log($"[BgShell] 双目模式缺少右眼背景图 ({Path.GetFileName(bgRPath)}), 背景球壳关闭");
                    return;
                }
                _bgTexR = LoadPng(bgRPath);
                if (_bgTexR == null) { Debug.LogWarning("[BgShell] 右眼背景图加载失败"); return; }
                _bgMatR.SetTexture("_BgTex", _bgTexR);
                Debug.Log($"[BgShell] 双目背景已加载: L={_bgTexL.width}x{_bgTexL.height} R={_bgTexR.width}x{_bgTexR.height}");
            }
            else
            {
                Debug.Log($"[BgShell] 单目背景已加载: {_bgTexL.width}x{_bgTexL.height}");
            }
            _bgEnabled = true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[BgShell] 加载背景图异常: " + e.Message);
            _bgEnabled = false;
        }
    }

    /// <summary>从 PNG 文件加载 Texture2D (sRGB, 不生成 mipmap).</summary>
    static Texture2D LoadPng(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        if (!tex.LoadImage(data, false)) { Destroy(tex); return null; }
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        return tex;
    }
}
