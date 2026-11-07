using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

/// <summary>
/// 主界面 + 播放器进度条 (uGUI, 桌面 Screen Space / VR World Space 共用):
///   主界面: 打开文件 / 退出 + 最近打开列表 (点击条目重新播放对应 json)
///   播放器: 第一行调试选项 (segments / MSAA / tessLength), 第二行进度条 (可点击跳转),
///           第三行 左=当前时间 右=总时间 中间=循环模式 / 上一首 / 暂停播放 / 下一首
/// 按钮图标全部用代码生成的纹理, 不依赖字体里的符号; 中文用系统字体保证显示.
/// </summary>
public class PlayerUI
{
    public Canvas Canvas { get; private set; }
    public GraphicRaycaster Raycaster { get; private set; }
    public Slider SeekSlider { get { return _seekSlider; } }
    public bool WorldSpace { get; private set; }
    public bool ProgressVisible { get { return _progressVisible; } }
    public RectTransform PlayerPanelRect { get { return _playerPanel != null ? (RectTransform)_playerPanel.transform : null; } }
    public bool FileBrowserOpen { get { return _fbPanel != null && _fbPanel.activeSelf; } }

    // ---- 回调 ----
    public Action OnOpenFile;
    public Action OnQuit;
    public Action<string> OnOpenRecent;
    public Action<float> OnSeek;        // 进度条松手提交跳转, 0..1
    public Action OnPlayPause;
    public Action OnPrev;
    public Action OnNext;
    public Action<int> OnLoopMode;      // 0=单曲 1=列表 2=随机
    public Action<int> OnSegments;
    public Action<int> OnMsaa;
    public Action<float> OnTessLength;
    public Action<float> OnVolume;      // 音量滑块 0..1
    public Action OnAutoHide;           // 无人指向进度条面板 5 秒后触发, 由外部真正隐藏面板
    public Action<int> OnDebugView;     // 0=正常渲染, 3=线框网格预览
    public Action<string> OnSettingsLang;       // "zh" / "en"
    public Action<bool> OnSettingsRimClamp;
    public Action<int> OnSettingsFps;            // 60 / 90 / 120
    public Action OnOpenHomeSettings;

    public const float AutoHideDelay = 5f;

    const float CW = 1600f, CH = 900f;
    static readonly int[] MsaaVals = { 0, 2, 4, 8 };

    // ============ 国际化 ============
    string _activeLang = "zh";
    readonly List<(UnityEngine.UI.Text txt, string key)> _locTexts = new List<(UnityEngine.UI.Text, string)>();
    void CacheLoc(UnityEngine.UI.Text txt, string key) { if (txt != null) _locTexts.Add((txt, key)); }
    static string T(string key, string lang) {
        return lang switch {
            "en" => _EN.TryGetValue(key, out var es) ? es : key,
            _ => _ZH.TryGetValue(key, out var zs) ? zs : key,
        };
    }
    static readonly Dictionary<string, string> _ZH = new Dictionary<string, string>(StringComparer.Ordinal) {
        {"home.title", "VR180 RGBD播放器"},
        {"home.subtitle", "选择一份 JSON 元数据开始播放"},
        {"home.openFile", "打开文件"}, {"home.settings", "设置"}, {"home.quit", "退出"},
        {"home.recent", "最近打开"}, {"home.noFiles", "还没有打开过文件"},
        {"home.standaloneNote", "一体机文件管理器暂未实现\n请通过串流或桌面模式打开文件"},
        {"settings.title", "设置"}, {"settings.back", "← 返回"},
        {"settings.lang", "语言"}, {"settings.rimClamp", "外围深度限制"}, {"settings.fps", "帧率限制"},
        {"on", "开"}, {"off", "关"},
        {"player.debug", "调试选项"}, {"player.prev", "上一首"}, {"player.playPause", "暂停播放"}, {"player.next", "下一首"},
        {"player.gridOn", "网格预览 开"}, {"player.gridOff", "网格预览 关"},
        {"msaa.disabled", "关闭"},
        {"close", "关闭"},
        {"fb.title", "打开 JSON 文件"}, {"fb.up", "⬆ 返回上一级"}, {"fb.cancel", "取消"},
        {"fb.thisPc", "此电脑"},
        {"fb.noJson", "(此目录下没有 .json 文件)"},
    };
    static readonly Dictionary<string, string> _EN = new Dictionary<string, string>(StringComparer.Ordinal) {
        {"home.title", "VR180 RGBD Player"},
        {"home.subtitle", "Select a JSON metadata file to start"},
        {"home.openFile", "Open File"}, {"home.settings", "Settings"}, {"home.quit", "Quit"},
        {"home.recent", "Recent"}, {"home.noFiles", "No files opened yet"},
        {"home.standaloneNote", "File manager not yet available on standalone.\nUse streaming or desktop mode."},
        {"settings.title", "Settings"}, {"settings.back", "← Back"},
        {"settings.lang", "Language"}, {"settings.rimClamp", "Edge Depth Limit"}, {"settings.fps", "Frame Rate Limit"},
        {"on", "On"}, {"off", "Off"},
        {"player.debug", "Debug"}, {"player.prev", "Prev"}, {"player.playPause", "Play/Pause"}, {"player.next", "Next"},
        {"player.gridOn", "Grid Preview On"}, {"player.gridOff", "Grid Preview Off"},
        {"msaa.disabled", "Off"},
        {"close", "Close"},
        {"fb.title", "Open JSON File"}, {"fb.up", "⬆ Parent"}, {"fb.cancel", "Cancel"},
        {"fb.thisPc", "This PC"},
        {"fb.noJson", "(No .json files in this directory)"},
    };

    /// <summary>外部切换语言时调用, 刷新所有缓存的 UI 文本 + 状态型按钮/行值.</summary>
    public void SetUiLang(string lang) {
        _activeLang = (lang ?? "").StartsWith("en") ? "en" : "zh";
        foreach (var (txt, key) in _locTexts) if (txt != null) txt.text = T(key, _activeLang);
        // 状态型值 (key 随状态变化, 不走静态缓存):
        if (_debugBtn != null) {
            var t = _debugBtn.GetComponentInChildren<Text>();
            if (t != null) t.text = _debugGridOn ? T("player.gridOn", _activeLang) : T("player.gridOff", _activeLang);
        }
        if (_hRimVal != null) _hRimVal.text = _hRimOn ? T("on", _activeLang) : T("off", _activeLang);
        if (_hLangVal != null) _hLangVal.text = _hLangKey == "zh" ? "中文" : "English";
        // UpdateSettingLabels 里 msaa==0 显示 T("msaa.disabled"), 但 _msaaVal 没 CacheLoc, 手动刷
        if (_msaaVal != null) {
            int curMsaa = _msaaSlider != null ? MsaaVals[Mathf.Clamp(Mathf.RoundToInt(_msaaSlider.value), 0, MsaaVals.Length - 1)] : 0;
            _msaaVal.text = curMsaa == 0 ? T("msaa.disabled", _activeLang) : (curMsaa + "x");
        }
    }

    GameObject _canvasGo;
    Font _font;
    Material _uiMat;   // VR 世界空间 UI 专用材质 (忽略深度, 永远不会被视频几何遮挡)

    GameObject _home, _playerPanel, _settingsPanel, _homeSettingsPanel, _recentList, _recentEmpty, _standaloneNote;
    Text _homeHint, _status, _toast, _timeCur, _timeTotal, _recentTitle, _centerText;
    Button _openBtn;
    Image _toastBg;
    Image _centerBg;   // 中心 HUD 显示音量/seek 反馈 (Overlay, 永远不被视频遮挡)

    // ---- 文件浏览器 (VR 模式下替代 Win32 OpenFileDialog) ----
    GameObject _fbPanel;
    Text _fbPathText;
    RectTransform _fbContentRt;
    Button _fbUpBtn;
    ScrollRect _fbScroll;   // 文件列表滚动区 (摇杆上下滚动用)
    string _fbCurrentDir = "";
    string _fbSelectedPath = "";
    Action<string> _fbOnPick;

    Slider _seekSlider;
    bool _settingSeek, _draggingSeek;
    float _totalSec;

    Image _playIcon, _loopIcon, _volIcon;
    Text _loopOne;
    Sprite _playSprite, _pauseSprite, _prevSprite, _nextSprite, _loopSprite, _randomSprite, _circleSprite, _volSprite;

    Slider _segSlider, _msaaSlider, _tessSlider;
    Text _segVal, _msaaVal, _tessVal;

    Slider _volSlider;

    bool _progressVisible = true;
    float _toastTimer;
    int _loopModeView;

    float _autoHideTimer;        // 无人指向面板的累计时间
    bool _vrPointerOnPanel;      // VR 射线当前是否指向面板 (Gray10Player 每帧写入)

    // ================= 构建 =================

    public void Build(Camera cam, bool worldSpace, int segments, int msaa, float tessLength)
    {
        WorldSpace = worldSpace;
        _font = LoadFont();
        EnsureEventSystem();
        _locTexts.Clear();  // 重建时清空旧缓存 (Destroy 掉的 Text 引用会变死)

        // VR: World Space Canvas 默认按深度测试, 进度条挂在相机前 2m 时会被视频里更近的
        // 几何(桌面/人物)挡住 -> 换用忽略深度的 UI 材质, 保证界面始终可见
        if (worldSpace)
        {
            var sh = Shader.Find("VR180/UIOverlay");
            if (sh != null)
            {
                _uiMat = new Material(sh) { name = "UIOverlay" };
            }
            else Debug.LogWarning("[UI] 找不到 VR180/UIOverlay 着色器, VR 界面可能被视频遮挡");
        }

        _canvasGo = new GameObject("PlayerUI");
        if (worldSpace && cam != null)
        {
            // 世界锁定: 不挂相机 (挂相机会让界面跟随头显转动). 位置由 SnapToHead 摆放到
            // 头显水平正前方 2m、视线下方约 25°, 摆放后固定不动, 右摇杆单按重新归位
            // 1600x900 * 0.0015 = 2.4m x 1.35m, 在眼前 2m 处约 62° x 37° 视角, 按钮文字可读
            _canvasGo.transform.localScale = Vector3.one * 0.0015f;
        }
        Canvas = _canvasGo.AddComponent<Canvas>();
        Canvas.renderMode = worldSpace ? RenderMode.WorldSpace : RenderMode.ScreenSpaceOverlay;
        if (worldSpace) Canvas.worldCamera = cam;
        var rt = _canvasGo.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(CW, CH);
        var scaler = _canvasGo.AddComponent<CanvasScaler>();
        if (worldSpace)
        {
            scaler.dynamicPixelsPerUnit = 10f;
        }
        else
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(CW, CH);
            scaler.matchWidthOrHeight = 0.5f;
        }
        Raycaster = _canvasGo.AddComponent<GraphicRaycaster>();

        _playSprite = Icon(IconKind.Play);
        _pauseSprite = Icon(IconKind.Pause);
        _prevSprite = Icon(IconKind.Prev);
        _nextSprite = Icon(IconKind.Next);
        _loopSprite = Icon(IconKind.Loop);
        _randomSprite = Icon(IconKind.Random);
        _circleSprite = Icon(IconKind.Circle);
        _volSprite = Icon(IconKind.Volume);

        BuildHome();
        BuildPlayer(segments, msaa, tessLength);
        BuildSettings(segments, msaa, tessLength);
        BuildFileBrowser();
        BuildHomeSettings();

        if (worldSpace && cam != null) SnapToHead(cam.transform);
        SetProgressVisible(true);
        ShowHome(false, new List<string>());
    }

    public void Destroy()
    {
        if (_canvasGo != null) UnityEngine.Object.Destroy(_canvasGo);
        _canvasGo = null;
        if (_uiMat != null) UnityEngine.Object.Destroy(_uiMat);
        _uiMat = null;
        Canvas = null;
        Raycaster = null;
    }

    // ---- 可调参数 (Inspector 可见; Gray10Player 里也暴露了对应字段, 改动处会实时生效) ----
    // Home (主界面) 参数
    [Header("主界面 (Home)")]
    [Tooltip("主界面到头显的直线距离 (米)")]
    public float homeDistance = 1.3f;
    [Tooltip("主界面相对视线的垂直偏移 (米, 负=向下)")]
    public float homeYOffset = -0.35f;
    [Tooltip("主界面的 pitch 旋转 (度)")]
    public float homePitchDeg = 10f;
    // Player (进度条) 参数
    [Header("播放器进度条")]
    [Tooltip("进度条到头显的直线距离 (米)")]
    public float playerDistance = 0.9f;
    [Tooltip("进度条相对视线的垂直偏移 (米, 负=向下)")]
    public float playerYOffset = -0.35f;
    [Tooltip("进度条的 pitch 旋转 (度)")]
    public float playerPitchDeg = 15f;
    [Tooltip("进度条面板宽度占 Canvas 宽度的比例 (1=全宽 1600, 0.5=半宽 800)")]
    public float playerWidthFrac = 0.5f;
    // File Browser 参数
    [Header("文件浏览器")]
    [Tooltip("文件浏览器到头显的直线距离 (米)")]
    public float fbDistance = 1.2f;
    [Tooltip("文件浏览器相对视线的垂直偏移 (米, 负=向下)")]
    public float fbYOffset = -0.4f;
    [Tooltip("文件浏览器的 pitch 旋转 (度)")]
    public float fbPitchDeg = 20f;

    /// <summary>把世界空间 Canvas 摆到头显正前方. 内部自动判断当前显示 Home/Player/文件浏览器, 用对应的 distance/yOffset/pitch.</summary>
    public void SnapToHead(Transform head)
    {
        if (_canvasGo == null || head == null) return;
        // 文件浏览器打开时优先用它的参数
        bool useFB = _fbPanel != null && _fbPanel.activeSelf;
        bool usePlayer = !useFB && (_playerPanel != null && _playerPanel.activeSelf)
                       && (_home == null || !_home.activeSelf);
        float dist = useFB ? fbDistance : usePlayer ? playerDistance : homeDistance;
        float yoff = useFB ? fbYOffset  : usePlayer ? playerYOffset  : homeYOffset;
        float pitch = useFB ? fbPitchDeg : usePlayer ? playerPitchDeg : homePitchDeg;

        Vector3 fwd = head.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        else fwd.Normalize();
        _canvasGo.transform.position = head.position + fwd * dist + new Vector3(0f, yoff, 0f);
        Quaternion yaw = Quaternion.LookRotation(fwd);
        _canvasGo.transform.rotation = yaw * Quaternion.Euler(pitch, 0f, 0f);
    }

    static void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        go.AddComponent<StandaloneInputModule>();
    }

    Font LoadFont()
    {
        // 中文必须走系统字体 (内置 LegacyRuntime/Arial 没有 CJK 字形)
        var f = Font.CreateDynamicFontFromOSFont(
            new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial" }, 24);
        if (f != null) return f;
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    // ================= 主界面 =================

    void BuildHome()
    {
        _home = Panel(_canvasGo.transform, "Home", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                      new Color(0.045f, 0.052f, 0.065f, 1f)).gameObject;

        var tTitle = Label(_home.transform, "标题", T("home.title", _activeLang), 44, TextAnchor.MiddleCenter, Color.white,
              new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 190f), new Vector2(1000f, 60f), true);
        CacheLoc(tTitle, "home.title");
        var tSub = Label(_home.transform, "副标题", T("home.subtitle", _activeLang), 20, TextAnchor.MiddleCenter,
              new Color(1f, 1f, 1f, 0.55f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
              new Vector2(0f, 135f), new Vector2(1000f, 30f));
        CacheLoc(tSub, "home.subtitle");

        // 三个按钮等宽 200, 中心间距 240 (各 20px 间隙): -240 / 0 / 240
        _openBtn = TextButton(_home.transform, "打开文件", T("home.openFile", _activeLang), 24,
                              new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-240f, 55f),
                              new Vector2(200f, 66f), new Color(0.16f, 0.38f, 0.62f, 1f),
                              () => { if (OnOpenFile != null) OnOpenFile(); });
        CacheLoc(_openBtn.GetComponentInChildren<Text>(), "home.openFile");
        var btnSettings = TextButton(_home.transform, "设置", T("home.settings", _activeLang), 24,
                   new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 55f),
                   new Vector2(200f, 66f), new Color(0.16f, 0.17f, 0.2f, 1f),
                   () => { if (OnOpenHomeSettings != null) OnOpenHomeSettings(); });
        CacheLoc(btnSettings.GetComponentInChildren<Text>(), "home.settings");
        var btnQuit = TextButton(_home.transform, "退出", T("home.quit", _activeLang), 24,
                   new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(240f, 55f),
                   new Vector2(200f, 66f), new Color(0.16f, 0.17f, 0.2f, 1f),
                   () => { if (OnQuit != null) OnQuit(); });
        CacheLoc(btnQuit.GetComponentInChildren<Text>(), "home.quit");

        _recentTitle = Label(_home.transform, "最近标题", T("home.recent", _activeLang), 20, TextAnchor.MiddleLeft,
                             new Color(1f, 1f, 1f, 0.6f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                             new Vector2(0f, -10f), new Vector2(760f, 28f));
        CacheLoc(_recentTitle, "home.recent");

        _recentList = new GameObject("RecentList", typeof(RectTransform));
        _recentList.transform.SetParent(_home.transform, false);
        Place((RectTransform)_recentList.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 1f),
              new Vector2(0f, -36f), new Vector2(760f, 380f));

        _recentEmpty = Label(_home.transform, "空列表", T("home.noFiles", _activeLang), 20, TextAnchor.MiddleLeft,
                             new Color(1f, 1f, 1f, 0.32f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                             new Vector2(0f, -60f), new Vector2(760f, 30f)).gameObject;
        CacheLoc(_recentEmpty.GetComponent<Text>(), "home.noFiles");

        _standaloneNote = Label(_home.transform, "一体机说明",
              T("home.standaloneNote", _activeLang), 20, TextAnchor.MiddleLeft,
              new Color(1f, 0.85f, 0.5f, 0.8f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
              new Vector2(0f, -30f), new Vector2(760f, 80f)).gameObject;
        CacheLoc(_standaloneNote.GetComponent<Text>(), "home.standaloneNote");
        _standaloneNote.SetActive(false);

        _homeHint = Label(_home.transform, "提示", "", 18, TextAnchor.MiddleCenter,
                          new Color(1f, 0.6f, 0.5f, 0.9f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                          new Vector2(0f, 24f), new Vector2(1400f, 28f));
    }

    // ---- 主界面设置面板 (语言 / 外围深度限制 / 帧率) ----
    Text _hLangVal, _hRimVal, _hFpsVal;   // 三行的当前值文本 (需要动态更新)
    string _hLangKey = "zh";              // 内部状态, 不依赖显示文字
    bool _hRimOn = true;                  // 内部状态, 不依赖显示文字
    string[] _langChoices = { "zh", "en" };
    bool[] _rimChoices = { true, false };
    int[] _fpsChoices = { 60, 90, 120, 144, 240 };

    void BuildHomeSettings()
    {
        _homeSettingsPanel = Panel(_canvasGo.transform, "HomeSettings", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                                    Vector2.zero, new Vector2(1080f, 560f),
                                    new Color(0.08f, 0.085f, 0.1f, 1f)).gameObject;
        _homeSettingsPanel.GetComponent<Image>().raycastTarget = true;
        _homeSettingsPanel.SetActive(false);

        // panel 坐标系: 中心(0,0), 左=-540, 右=+540, 顶=+280, 底=-280
        // 所有元素统一用 anchor=(0.5,0.5) 中心锚点, Panel/Place 行为一致

        // 标题 + 返回按钮 (顶部左对齐)
        var pTitle = Label(_homeSettingsPanel.transform, "标题", T("settings.title", _activeLang), 24, TextAnchor.MiddleLeft, Color.white,
              new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-400f, 230f), new Vector2(200f, 32f));
        CacheLoc(pTitle, "settings.title");
        var pBack = TextButton(_homeSettingsPanel.transform, "BackBtn", T("settings.back", _activeLang), 18,
                   new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                   new Vector2(-420f, 190f), new Vector2(160f, 36f),
                   new Color(0.18f, 0.2f, 0.26f, 1f), () => _homeSettingsPanel.SetActive(false));
        CacheLoc(pBack.GetComponentInChildren<Text>(), "settings.back");

        // 三行布局 (每行 y 中心, 左列标题 200px + 32px gap + 箭头行 520px):
        // 行中心 y: row1=130, row2=40, row3=-50
        var hdrClr = new Color(1f, 1f, 1f, 0.55f);

        // Row 1: 语言
        var hdr1 = Label(_homeSettingsPanel.transform, "LangHdr", T("settings.lang", _activeLang), 20, TextAnchor.MiddleLeft, hdrClr,
              new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
              new Vector2(-400f, 130f), new Vector2(200f, 48f));
        CacheLoc(hdr1, "settings.lang");
        BuildSettingRow(_homeSettingsPanel.transform, "LangRow",
                        -104f, 130f, out var lL, out var lR, out _hLangVal);
        lL.onClick.AddListener(() => { int i = Array.IndexOf(_langChoices, _hLangKey); if (i < 0) i = 0; string next = _langChoices[(i - 1 + _langChoices.Length) % _langChoices.Length]; _hLangKey = next; if (OnSettingsLang != null) OnSettingsLang(next); _hLangVal.text = (_activeLang == "zh" ? "中文" : "English"); });
        lR.onClick.AddListener(() => { int i = Array.IndexOf(_langChoices, _hLangKey); if (i < 0) i = 0; string next = _langChoices[(i + 1) % _langChoices.Length]; _hLangKey = next; if (OnSettingsLang != null) OnSettingsLang(next); _hLangVal.text = (_activeLang == "zh" ? "中文" : "English"); });

        // Row 2: 外围深度限制
        var hdr2 = Label(_homeSettingsPanel.transform, "RimHdr", T("settings.rimClamp", _activeLang), 20, TextAnchor.MiddleLeft, hdrClr,
              new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
              new Vector2(-400f, 40f), new Vector2(200f, 48f));
        CacheLoc(hdr2, "settings.rimClamp");
        BuildSettingRow(_homeSettingsPanel.transform, "RimRow",
                        -104f, 40f, out var rL, out var rR, out _hRimVal);
        rL.onClick.AddListener(() => { _hRimOn = !_hRimOn; if (OnSettingsRimClamp != null) OnSettingsRimClamp(_hRimOn); _hRimVal.text = _hRimOn ? T("on", _activeLang) : T("off", _activeLang); });
        rR.onClick.AddListener(() => { _hRimOn = !_hRimOn; if (OnSettingsRimClamp != null) OnSettingsRimClamp(_hRimOn); _hRimVal.text = _hRimOn ? T("on", _activeLang) : T("off", _activeLang); });

        // Row 3: 帧率限制
        var hdr3 = Label(_homeSettingsPanel.transform, "FpsHdr", T("settings.fps", _activeLang), 20, TextAnchor.MiddleLeft, hdrClr,
              new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
              new Vector2(-400f, -50f), new Vector2(200f, 48f));
        CacheLoc(hdr3, "settings.fps");
        BuildSettingRow(_homeSettingsPanel.transform, "FpsRow",
                        -104f, -50f, out var fL, out var fR, out _hFpsVal);
        fL.onClick.AddListener(() => { if (int.TryParse(_hFpsVal.text, out var cur)) { int i = Array.IndexOf(_fpsChoices, cur); if (i < 0) i = 0; int next = _fpsChoices[(i - 1 + _fpsChoices.Length) % _fpsChoices.Length]; if (OnSettingsFps != null) OnSettingsFps(next); _hFpsVal.text = next.ToString(); } });
        fR.onClick.AddListener(() => { if (int.TryParse(_hFpsVal.text, out var cur)) { int i = Array.IndexOf(_fpsChoices, cur); if (i < 0) i = 0; int next = _fpsChoices[(i + 1) % _fpsChoices.Length]; if (OnSettingsFps != null) OnSettingsFps(next); _hFpsVal.text = next.ToString(); } });
    }

    /// <summary>构建一行设置: xCenter/yCenter 是行容器中心位置 (anchor=pivot=(0.5,0.5)), rowBg sizeDelta=520x48.
    /// 左箭头< | 中间值文本 (stretch) | 右箭头>
    /// 值文本绕开 Place (Place 强制 anchorMax=anchorMin), 手动设 anchorMin=(0,0) anchorMax=(1,1) 做 stretch.</summary>
    void BuildSettingRow(Transform parent, string name, float xCenter, float yCenter,
                         out Button leftBtn, out Button rightBtn, out Text valueTxt)
    {
        var rowBg = Panel(parent, name,
                          new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                          new Vector2(xCenter, yCenter), new Vector2(520f, 48f),
                          new Color(1f, 1f, 1f, 0.05f));
        rowBg.GetComponent<Image>().raycastTarget = true;

        // 左箭头 < : 容器最左边
        leftBtn = TextButton(rowBg.transform, "L", "<", 22,
                             new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                             new Vector2(0f, 0f), new Vector2(48f, 48f),
                             new Color(0.18f, 0.2f, 0.26f, 1f), null);
        // 右箭头 > : 容器最右边 (anchor=pivot=(1,0.5), pos=(0,0) 让按钮右边缘与容器右边缘 flush)
        rightBtn = TextButton(rowBg.transform, "R", ">", 22,
                              new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                              new Vector2(0f, 0f), new Vector2(48f, 48f),
                              new Color(0.18f, 0.2f, 0.26f, 1f), null);

        // 中间值文本: 绕开 Place (Place 强制 anchorMax=anchorMin), 手动设 anchorMin=0, anchorMax=1 做 stretch.
        // 左右各留 52px offset 避开箭头 (48px 宽 + 4px 间隙).
        var go = new GameObject("V", typeof(RectTransform));
        go.transform.SetParent(rowBg.transform, false);
        valueTxt = go.AddComponent<Text>();
        valueTxt.font = _font;
        valueTxt.text = "?";
        valueTxt.fontSize = 22;
        valueTxt.alignment = TextAnchor.MiddleCenter;
        valueTxt.color = Color.white;
        valueTxt.raycastTarget = false;
        valueTxt.horizontalOverflow = HorizontalWrapMode.Overflow;
        valueTxt.verticalOverflow = VerticalWrapMode.Overflow;
        Skin(valueTxt);
        var vrt = (RectTransform)valueTxt.transform;
        vrt.anchorMin = new Vector2(0f, 0f);
        vrt.anchorMax = new Vector2(1f, 1f);
        vrt.offsetMin = new Vector2(52f, 4f);
        vrt.offsetMax = new Vector2(-52f, -4f);
    }

    // ---- 已由上面内联实现的辅助: 保留旧签名避免旧代码引用 ----
    Text MakeSettingRow(Transform parent, string name, Vector2 topLeft, out Button leftBtn, out Button rightBtn, out Text valueTxt)
    {
        var rowBg = Panel(parent, name + "Bg", new Vector2(0f, 1f), new Vector2(1f, 1f),
                           topLeft + new Vector2(-12f, 12f), new Vector2(-24f, 12f),
                           new Color(1f, 1f, 1f, 0.05f));
        rowBg.GetComponent<Image>().raycastTarget = false;
        leftBtn = TextButton(rowBg.transform, "Left", "<", 22, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(12f, 0f), new Vector2(48f, 48f), new Color(0.18f, 0.2f, 0.26f, 1f), null);
        rightBtn = TextButton(rowBg.transform, "Right", ">", 22, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-60f, 0f), new Vector2(48f, 48f), new Color(0.18f, 0.2f, 0.26f, 1f), null);
        valueTxt = Label(rowBg.transform, "Val", "?", 22, TextAnchor.MiddleCenter, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(120f, 40f));
        return valueTxt;
    }
    static int Cycle<T>(T[] arr, int idx, int delta) { if (arr == null || arr.Length == 0) return 0; if (idx < 0) idx = 0; return (idx + delta + arr.Length) % arr.Length; }
    string Cycle(string[] arr, int idx, int delta) { if (arr == null || arr.Length == 0) return ""; if (idx < 0) idx = 0; return arr[(idx + delta + arr.Length) % arr.Length]; }
    int Cycle(int[] arr, int idx, int delta) { if (arr == null || arr.Length == 0) return 0; if (idx < 0) idx = 0; return arr[(idx + delta + arr.Length) % arr.Length]; }

    /// <summary>外部设置值变更后更新主界面设置面板的三行文本 (同时刷新内部状态)</summary>
    public void SetHomeSettingsValues(string lang, bool rimClamp, int fps)
    {
        _hLangKey = (lang ?? "").StartsWith("en") ? "en" : "zh";
        _hRimOn = rimClamp;
        if (_hLangVal != null) _hLangVal.text = _hLangKey == "zh" ? "中文" : "English";
        if (_hRimVal != null) _hRimVal.text = rimClamp ? T("on", _activeLang) : T("off", _activeLang);
        if (_hFpsVal != null) _hFpsVal.text = fps.ToString();
    }

    /// <summary>外部: 打开/关闭主界面设置面板 (返回按钮会自动关闭)</summary>
    public void OpenHomeSettings()
    {
        if (_homeSettingsPanel != null) _homeSettingsPanel.SetActive(true);
    }

    /// <summary>外部查询: 主界面设置面板当前是否打开</summary>
    public bool HomeSettingsOpen { get { return _homeSettingsPanel != null && _homeSettingsPanel.activeSelf; } }

    /// <summary>调试面板 FPS 显示 (调试面板打开时 Gray10Player 每帧调用).</summary>
    public void SetFps(int fps) { if (_fpsLabel != null) _fpsLabel.text = fps + " FPS"; }

    /// <summary>显示主界面: 一体机与桌面共享同一套 Unity UI 文件浏览器, 一律显示打开按钮和最近列表.</summary>
    public void ShowHome(bool standalone, List<string> recents)
    {
        if (_home == null) return;
        _home.SetActive(true);
        _playerPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        if (_homeSettingsPanel != null) _homeSettingsPanel.SetActive(false);

        // 一体机 (Android) 和桌面 (Windows) 现在都用自写的 Unity UI 文件浏览器,
        // 不再隐藏打开按钮/最近列表, 也不显示"请用串流"的提示.
        _openBtn.gameObject.SetActive(true);
        _recentList.SetActive(true);
        if (_recentTitle != null) _recentTitle.gameObject.SetActive(true);
        if (_standaloneNote != null) _standaloneNote.SetActive(false);

        int shown = RebuildRecents(recents ?? new List<string>());
        _recentEmpty.SetActive(shown == 0);
    }

    int RebuildRecents(List<string> recents)
    {
        var t = _recentList.transform;
        for (int i = t.childCount - 1; i >= 0; i--)
        {
            var child = t.GetChild(i).gameObject;
            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
        }
        // 单行列表: 每行一条, 最多 6 条 (再多会压到页面底部的提示文字)
        int n = Mathf.Min(recents.Count, 6);
        for (int i = 0; i < n; i++)
        {
            string path = recents[i];
            string name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name)) name = path;
            var btn = TextButton(t, "最近" + i, name, 22, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                                 new Vector2(362f, -i * 54f), new Vector2(1520f, 48f),   // 框宽 1520; 中心右移 362 → 框左缘 -398, 文字起点 -380 与上方"最近打开"标题对齐
                                 new Color(1f, 1f, 1f, 0.07f),
                                 () => { if (OnOpenRecent != null) OnOpenRecent(path); });
            var txt = btn.GetComponentInChildren<Text>();
            if (txt != null)
            {
                txt.alignment = TextAnchor.MiddleLeft;
                var trt = txt.GetComponent<RectTransform>();
                trt.offsetMin = new Vector2(18f, 0f);
                trt.offsetMax = new Vector2(-18f, 0f);
            }
        }
        return n;
    }

    // ================= 播放器 (进度条) =================

    void BuildPlayer(int segments, int msaa, float tessLength)
    {
        // 宽度按 playerWidthFrac 居中收窄 (VR 里全宽 1600 太宽, 默认半宽 800)
        float pw = Mathf.Clamp(playerWidthFrac, 0.2f, 1f);
        _playerPanel = Panel(_canvasGo.transform, "PlayerPanel",
                             new Vector2(0.5f - pw * 0.5f, 0f), new Vector2(0.5f + pw * 0.5f, 0f),
                             Vector2.zero, Vector2.zero,
                             new Color(0.02f, 0.02f, 0.025f, 0.62f), 240f).gameObject;

        // 第一行: 左文件名, 右调试选项
        _status = Label(_playerPanel.transform, "状态", "", 16, TextAnchor.MiddleLeft,
                        new Color(1f, 1f, 1f, 0.65f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                        new Vector2(26f, -10f), new Vector2(520f, 30f));
        var btnDbg = TextButton(_playerPanel.transform, "调试选项", T("player.debug", _activeLang), 18,
                   new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-22f, -10f),
                   new Vector2(150f, 38f), new Color(0.15f, 0.15f, 0.18f, 0.9f), ToggleSettings);
        CacheLoc(btnDbg.GetComponentInChildren<Text>(), "player.debug");

        // 第二行: 左时间 | 进度条(占总宽 80%) | 右时间, 时间紧贴蓝条两端
        // 蓝条左右各留 10% 宽度, 时间右/左对齐到蓝条外 12px 处
        const float gapFrac = 12f / CW;
        _timeCur = Label(_playerPanel.transform, "当前时间", "00:00", 20, TextAnchor.MiddleRight, Color.white,
                         new Vector2(0.1f - gapFrac, 1f), new Vector2(1f, 0.5f), new Vector2(0f, -67f),
                         new Vector2(140f, 30f));
        _timeTotal = Label(_playerPanel.transform, "总时间", "00:00", 20, TextAnchor.MiddleLeft, Color.white,
                           new Vector2(0.9f + gapFrac, 1f), new Vector2(0f, 0.5f), new Vector2(0f, -67f),
                           new Vector2(140f, 30f));

        // 蓝条高度 = 18 * 0.8 ≈ 14.4px, 比圆点(18*1.1≈19.8)略小
        _seekSlider = BuildSlider(_playerPanel.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                                  new Vector2(0f, -60f), new Vector2(CW * 0.8f, 18f), 0f, 1f, 0f, 0.8f);
        // 横向改为按父级宽度拉伸: 任何窗口比例(含正方形)下都正好占 80%, 不会压到两端时间
        var srt = (RectTransform)_seekSlider.transform;
        srt.anchorMin = new Vector2(0.1f, 1f);
        srt.anchorMax = new Vector2(0.9f, 1f);
        srt.pivot = new Vector2(0.5f, 1f);
        srt.anchoredPosition = new Vector2(0f, -60f);
        srt.sizeDelta = new Vector2(0f, 18f);
        _seekSlider.onValueChanged.AddListener(v =>
        {
            if (_settingSeek) return;
            if (_timeCur != null) _timeCur.text = FormatTime(v * _totalSec);
        });
        var et = _seekSlider.gameObject.AddComponent<EventTrigger>();
        AddTrigger(et, EventTriggerType.PointerDown, d => _draggingSeek = true);
        AddTrigger(et, EventTriggerType.PointerUp, d => { _draggingSeek = false; CommitSeek(); });

        // 第三行: 中间按钮
        var loopBtn = IconButton(_playerPanel.transform, "循环模式", _loopSprite, new Vector2(0.5f, 1f),
                                 new Vector2(0.5f, 1f), new Vector2(-110f, -128f), new Vector2(44f, 40f),
                                 CycleLoopMode);
        _loopIcon = loopBtn.transform.Find("Icon").GetComponent<Image>();
        _loopOne = Label(loopBtn.transform, "单曲", "1", 14, TextAnchor.MiddleCenter, Color.white,
                         new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(30f, 30f), true);
        _loopOne.raycastTarget = false;

        IconButton(_playerPanel.transform, "上一首", _prevSprite, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                   new Vector2(-55f, -128f), new Vector2(48f, 44f),
                   () => { if (OnPrev != null) OnPrev(); });
        var playBtn = IconButton(_playerPanel.transform, "暂停播放", _playSprite, new Vector2(0.5f, 1f),
                                 new Vector2(0.5f, 1f), new Vector2(0f, -124f), new Vector2(56f, 52f),
                                 () => { if (OnPlayPause != null) OnPlayPause(); });
        _playIcon = playBtn.transform.Find("Icon").GetComponent<Image>();
        IconButton(_playerPanel.transform, "下一首", _nextSprite, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                   new Vector2(55f, -128f), new Vector2(48f, 44f),
                   () => { if (OnNext != null) OnNext(); });

        // 音量控件 (第三行最右): 图标 + 横条 (垂直居中对齐)
        var volBtn = IconButton(_playerPanel.transform, "音量", _volSprite, new Vector2(1f, 1f),
                                new Vector2(1f, 1f), new Vector2(-200f, -128f), new Vector2(40f, 38f), null);
        _volIcon = volBtn.transform.Find("Icon").GetComponent<Image>();
        // pos.y 是 top 边缘距父顶的像素 (anchor+ pivot 都是 top-right):
        // 图标 top=-128 size=38 → center = -128 - 19 = -147
        // 滑块 size=14, 要 center=-147 → top = -147 - 7 = -154? 不对:
        // anchoredPosition.y 在 pivot=top-right 时 = top edge 距 anchor(top-right) 的偏移
        // 即 pos.y=-128 → top edge 在 parent.top - 128
        // center = top - size.y/2 = -128 - 19 = -147
        // 滑块要 center=-147 → top = -147 - 7 = -154 → pos.y = -154
        _volSlider = BuildSlider(_playerPanel.transform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                                 new Vector2(-78f, -140f), new Vector2(120f, 14f), 0f, 1f, 1f, 0.5f);
        _volSlider.onValueChanged.AddListener(v =>
        {
            if (_settingSeek || _draggingSeek) return;   // 复用 flag: 拖拽时不回写外部状态
            if (OnVolume != null) OnVolume(v);
        });
        var volEt = _volSlider.gameObject.AddComponent<EventTrigger>();
        AddTrigger(volEt, EventTriggerType.PointerDown, d => { _draggingSeek = true; });
        AddTrigger(volEt, EventTriggerType.PointerUp, d => { _draggingSeek = false; });

        SetLoopMode(0);

        // 音量/提示浮层 (不随进度条一起隐藏)
        _toastBg = Panel(_canvasGo.transform, "ToastBg", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                         new Vector2(0f, 260f), new Vector2(300f, 44f), new Color(0f, 0f, 0f, 0.6f));
        _toastBg.raycastTarget = false;
        _toastBg.gameObject.SetActive(false);
        _toast = Label(_toastBg.transform, "Toast", "", 20, TextAnchor.MiddleCenter, Color.white,
                       new Vector2(0f, 0f), new Vector2(1f, 1f), Vector2.zero, new Vector2(300f, 44f));
        var trt = _toast.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(8f, 6f);
        trt.offsetMax = new Vector2(-16f, -12f);

        // 中心 HUD (音量/seek 反馈). 画布绝对中心, 半透明黑底, 大号粗体白字.
        // 注意: Panel() 锚点相同时 pivot=锚点, anchoredPosition 是中心位置 -> 居中传 (0,0);
        //       文本在 stretch 模式下 offset 必须也是 (0,0) 才正好填满黑框 (旧值导致文字跑出框外)
        _centerBg = Panel(_canvasGo.transform, "CenterHUD", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                          Vector2.zero, new Vector2(360f, 64f), new Color(0f, 0f, 0f, 0.6f));
        _centerBg.GetComponent<Image>().raycastTarget = false;
        _centerBg.gameObject.SetActive(false);
        _centerText = Label(_centerBg.transform, "CenterText", "", 26, TextAnchor.MiddleCenter, Color.white,
                            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(360f, 64f));
        _centerText.fontStyle = FontStyle.Bold;
    }

    void BuildSettings(int segments, int msaa, float tessLength)
    {
        // 挂进度条面板下: 跟随进度条的宽度收窄和显隐, 弹出在进度条右上角上方 (底边距条顶 8px)
        _settingsPanel = Panel(_playerPanel.transform, "Settings", new Vector2(1f, 1f), new Vector2(1f, 1f),
                               new Vector2(0f, 408f), new Vector2(560f, 400f),
                               new Color(0.07f, 0.075f, 0.09f, 0.95f)).gameObject;
        _settingsPanel.GetComponent<Image>().raycastTarget = true;   // 挡住面板内的误点击
        _settingsPanel.SetActive(false);

        var sTitle = Label(_settingsPanel.transform, "标题", T("player.debug", _activeLang), 20, TextAnchor.MiddleLeft, Color.white,
              new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -12f), new Vector2(300f, 30f));
        CacheLoc(sTitle, "player.debug");
        TextButton(_settingsPanel.transform, "关闭", "×", 24, new Vector2(1f, 1f), new Vector2(1f, 1f),
                   new Vector2(-12f, -8f), new Vector2(44f, 38f), new Color(0.15f, 0.15f, 0.18f, 0.9f),
                   () => _settingsPanel.SetActive(false));

        _segSlider = SettingRow(0, "segments", 32f, 1024f, segments, out _segVal);
        _msaaSlider = SettingRow(1, "MSAA", 0f, MsaaVals.Length - 1,
                                 Mathf.Max(0, Array.IndexOf(MsaaVals, msaa)), out _msaaVal);
        _tessSlider = SettingRow(2, "tessLength", 2f, 32f, tessLength, out _tessVal);

        // 最下面一行: 网格预览切换按钮 (左) + FPS 显示 (右)
        float btnY = -54f - 3 * 82f;   // row 3 y = -300f
        _debugBtn = TextButton(_settingsPanel.transform, "DebugGrid", T("player.gridOff", _activeLang), 18,
                               new Vector2(0f, 1f), new Vector2(0f, 1f),
                               new Vector2(24f, btnY), new Vector2(200f, 38f),
                               new Color(0.18f, 0.2f, 0.26f, 1f), ToggleDebugView);
        _fpsLabel = Label(_settingsPanel.transform, "FPS", "—", 18, TextAnchor.MiddleRight, Color.white,
                          new Vector2(1f, 1f), new Vector2(1f, 1f),
                          new Vector2(-24f, btnY), new Vector2(180f, 38f));

        UpdateSettingLabels(segments, msaa, tessLength);

        _segSlider.onValueChanged.AddListener(v =>
        {
            int s = Mathf.RoundToInt(v);
            UpdateSettingLabels(s, CurrentMsaa(), CurrentTess());
            if (OnSegments != null) OnSegments(s);
        });
        _msaaSlider.onValueChanged.AddListener(v =>
        {
            int m = MsaaVals[Mathf.Clamp(Mathf.RoundToInt(v), 0, MsaaVals.Length - 1)];
            UpdateSettingLabels(Mathf.RoundToInt(_segSlider.value), m, CurrentTess());
            if (OnMsaa != null) OnMsaa(m);
        });
        _tessSlider.onValueChanged.AddListener(v =>
        {
            UpdateSettingLabels(Mathf.RoundToInt(_segSlider.value), CurrentMsaa(), v);
            if (OnTessLength != null) OnTessLength(v);
        });
    }

    bool _debugGridOn = false;
    UnityEngine.UI.Button _debugBtn;
    Text _fpsLabel;

    void ToggleDebugView()
    {
        _debugGridOn = !_debugGridOn;
        if (_debugBtn != null)
        {
            // 项目全部用 uGUI Text (不是 TextMeshPro), 之前查 TMP_Text 永远为 null 导致文字不更新
            var txt = _debugBtn.GetComponentInChildren<Text>();
            if (txt != null) txt.text = _debugGridOn ? T("player.gridOn", _activeLang) : T("player.gridOff", _activeLang);
        }
        if (OnDebugView != null) OnDebugView(_debugGridOn ? 3 : 0);   // shader _View: 0=正常, 3=线框
    }

    Slider SettingRow(int row, string label, float min, float max, float value, out Text valueText)
    {
        float y = -54f - row * 82f;
        Label(_settingsPanel.transform, label, label, 17, TextAnchor.MiddleLeft, Color.white,
              new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, y), new Vector2(180f, 28f));
        valueText = Label(_settingsPanel.transform, label + "值", "", 17, TextAnchor.MiddleRight, Color.white,
                          new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, y), new Vector2(140f, 28f));
        return BuildSlider(_settingsPanel.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                           new Vector2(24f, y - 34f), new Vector2(512f, 20f), min, max, value);
    }

    int CurrentMsaa() { return MsaaVals[Mathf.Clamp(Mathf.RoundToInt(_msaaSlider.value), 0, MsaaVals.Length - 1)]; }
    float CurrentTess() { return _tessSlider.value; }

    void UpdateSettingLabels(int seg, int msaa, float tess)
    {
        if (_segVal != null) _segVal.text = seg.ToString();
        if (_msaaVal != null) _msaaVal.text = msaa == 0 ? T("msaa.disabled", _activeLang) : (msaa + "x");
        if (_tessVal != null) _tessVal.text = tess.ToString("F0");
    }

    void ToggleSettings()
    {
        _settingsPanel.SetActive(!_settingsPanel.activeSelf);
    }

    void CycleLoopMode()
    {
        int mode = (_loopModeView + 1) % 3;
        SetLoopMode(mode);
        if (OnLoopMode != null) OnLoopMode(mode);
    }

    public void SetLoopMode(int mode)
    {
        _loopModeView = Mathf.Clamp(mode, 0, 2);
        if (_loopIcon != null) _loopIcon.sprite = _loopModeView == 2 ? _randomSprite : _loopSprite;
        if (_loopOne != null) _loopOne.gameObject.SetActive(_loopModeView == 0);
    }

    // ================= 文件浏览器 (VR 替代 Win32 OpenFileDialog) =================

    void BuildFileBrowser()
    {
        // 1080x560 面板, 不透明. 面板锚点都在 (0.5, 0.5), anchoredPosition 是中心偏移:
        //   VR (WorldSpace): 右移 270px 避开视觉中心, 玩家双眼对称分布, 面板稍偏右看起来更居中
        //   桌面 (ScreenSpace): 直接居中, anchoredPosition=(0,0)
        Vector2 pos = WorldSpace ? new Vector2(270f, 0f) : Vector2.zero;
        _fbPanel = Panel(_canvasGo.transform, "FileBrowser", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                         pos, new Vector2(1080f, 560f),
                         new Color(0.08f, 0.085f, 0.1f, 1f)).gameObject;
        _fbPanel.GetComponent<Image>().raycastTarget = true;
        _fbPanel.SetActive(false);

        // 标题
        var fbTitle = Label(_fbPanel.transform, "标题", T("fb.title", _activeLang), 22, TextAnchor.MiddleLeft, Color.white,
              new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -14f), new Vector2(400f, 30f));
        CacheLoc(fbTitle, "fb.title");

        // 向上一级按钮 (⬆ 图标用文字代替, 因为我们没有专门的文件夹图标 sprite)
        _fbUpBtn = TextButton(_fbPanel.transform, "UpBtn", T("fb.up", _activeLang), 18,
                              new Vector2(0f, 1f), new Vector2(0f, 1f),
                              new Vector2(24f, -64f), new Vector2(160f, 36f),
                              new Color(0.18f, 0.2f, 0.26f, 1f), NavigateUp);
        CacheLoc(_fbUpBtn.GetComponentInChildren<Text>(), "fb.up");

        // 取消按钮 (返回上一级右侧): 关闭文件浏览器, 回到主界面
        var fbCancel = TextButton(_fbPanel.transform, "CancelBtn", T("fb.cancel", _activeLang), 18,
                   new Vector2(0f, 1f), new Vector2(0f, 1f),
                   new Vector2(196f, -64f), new Vector2(120f, 36f),
                   new Color(0.3f, 0.15f, 0.15f, 1f), CloseFileBrowser);
        CacheLoc(fbCancel.GetComponentInChildren<Text>(), "fb.cancel");

        // 路径显示: 从取消按钮右侧开始铺满剩余宽度 (左缘锚 0 + 偏移 332, 不再挤在右半边)
        var pathBg = Panel(_fbPanel.transform, "PathBg", new Vector2(0f, 1f), new Vector2(1f, 1f),
                           new Vector2(332f, -50f), new Vector2(-24f, -18f),
                           new Color(0.04f, 0.045f, 0.06f, 1f));
        pathBg.GetComponent<Image>().raycastTarget = false;
        _fbPathText = Label(pathBg.transform, "PathText", "", 16, TextAnchor.MiddleLeft, new Color(0.8f, 0.9f, 1f, 1f),
                            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        // Label 不支持拉伸锚点, 手动覆盖: 文本铺满 pathBg (左 12 右 8 内边距)
        var prt = _fbPathText.GetComponent<RectTransform>();
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.offsetMin = new Vector2(12f, 0f); prt.offsetMax = new Vector2(-8f, 0f);

        // ScrollRect 视口 (矩形裁剪). 不用 Mask(模板缓冲): 模板读/写需要对运行时动态创建的子图形
        // 做材质变体, 跟自定义 UIOverlay 材质组合不可靠 (行会被遮没 -> 黑框无字);
        // RectMask2D 走 _ClipRect 裁剪, UIOverlay shader 已支持 UNITY_UI_CLIP_RECT.
        var viewportGo = new GameObject("Viewport", typeof(RectTransform));
        viewportGo.transform.SetParent(_fbPanel.transform, false);
        var mask = viewportGo.AddComponent<Image>();
        mask.color = new Color(0.04f, 0.045f, 0.06f, 1f);
        mask.raycastTarget = true;
        Skin(mask);
        var maskRt = (RectTransform)viewportGo.transform;
        maskRt.anchorMin = new Vector2(0f, 0f);
        maskRt.anchorMax = new Vector2(1f, 1f);
        maskRt.offsetMin = new Vector2(24f, 24f);   // 底部不再留按钮区, 列表延伸到底
        maskRt.offsetMax = new Vector2(-24f, -124f);
        viewportGo.AddComponent<RectMask2D>();
        _fbScroll = viewportGo.AddComponent<ScrollRect>();
        _fbScroll.viewport = maskRt;
        _fbScroll.horizontal = false;
        _fbScroll.vertical = true;
        _fbScroll.movementType = ScrollRect.MovementType.Clamped;
        _fbScroll.scrollSensitivity = 40f;
        _fbScroll.inertia = true;
        Skin(viewportGo.GetComponent<Image>());

        // Content (纵向布局, 固定行高)
        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(viewportGo.transform, false);
        _fbContentRt = (RectTransform)contentGo.transform;
        _fbContentRt.anchorMin = new Vector2(0f, 1f);
        _fbContentRt.anchorMax = new Vector2(1f, 1f);
        _fbContentRt.pivot = new Vector2(0.5f, 1f);
        _fbContentRt.offsetMin = new Vector2(0f, 0f);
        _fbContentRt.offsetMax = new Vector2(0f, 0f);
        var vlg = contentGo.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
        vlg.padding = new RectOffset(6, 6, 6, 6);
        vlg.spacing = 2f;
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;   // 行高由每行的 LayoutElement.preferredHeight 提供 (不再依赖 sizeDelta)
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        var csf = contentGo.AddComponent<UnityEngine.UI.ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        _fbScroll.content = _fbContentRt;

        // 无取消/确认按钮: 点击文件名直接播放; 关闭由进入播放器/B 键/外部逻辑负责

        // 初始目录
        _fbCurrentDir = GetDefaultBrowseDir();
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>SAF 回调: 接收系统文件选择器返回的 Uri.</summary>
    class SafResultListener : AndroidJavaProxy
    {
        public System.Action<int, int, AndroidJavaObject> Callback;
        public SafResultListener()
            : base("com.unity3d.player.UnityPlayerActivity$IActivityResultListener") { }
        void onActivityResult(int requestCode, int resultCode, AndroidJavaObject data)
            => Callback?.Invoke(requestCode, resultCode, data);
    }

    /// <summary>用 Android SAF (系统文件选择器) 选一个文件. Scoped Storage 下唯一可靠的方式.
    /// 选中后把文件内容复制到 persistentDataPath 临时文件, 回调返回该路径 (取消则返回 null).</summary>
    public static void AndroidSafPickFile(System.Action<string> onPicked)
    {
        int reqCode = 0x5A1C;
        AndroidJavaObject activity = null;
        SafResultListener listener = null;
        try
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

            listener = new SafResultListener();
            listener.Callback = (req, res, data) =>
            {
                try { activity.Call("unregisterActivityResultListener", listener); } catch { }
                if (res == -1 && data != null)   // RESULT_OK
                {
                    try
                    {
                        var uri = data.Call<AndroidJavaObject>("getData");
                        string content = ReadUriText(activity, uri);
                        string tmp = System.IO.Path.Combine(Application.persistentDataPath,
                            "saf_" + System.DateTime.Now.Ticks + ".json");
                        System.IO.File.WriteAllText(tmp, content);
                        onPicked?.Invoke(tmp);
                    }
                    catch (System.Exception e) { Debug.LogError("[SAF] 读取失败: " + e.Message); onPicked?.Invoke(null); }
                }
                else onPicked?.Invoke(null);
            };

            activity.Call("registerActivityResultListener", listener);

            using var intent = new AndroidJavaObject("android.content.Intent",
                "android.intent.action.OPEN_DOCUMENT");
            intent.Call<AndroidJavaObject>("addCategory", "android.intent.category.OPENABLE");
            intent.Call<AndroidJavaObject>("setType", "*/*");
            activity.Call("startActivityForResult", intent, reqCode);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[SAF] 启动失败: " + e.Message);
            onPicked?.Invoke(null);
        }
    }

    static string ReadUriText(AndroidJavaObject activity, AndroidJavaObject uri)
    {
        var cr = activity.Call<AndroidJavaObject>("getContentResolver");
        var stream = cr.Call<AndroidJavaObject>("openInputStream", uri);
        var reader = new AndroidJavaObject("java.io.BufferedReader",
            new AndroidJavaObject("java.io.InputStreamReader", stream));
        var sb = new System.Text.StringBuilder();
        string line;
        while ((line = reader.Call<string>("readLine")) != null)
            sb.AppendLine(line);
        try { stream.Call("close"); } catch { }
        return sb.ToString();
    }
#endif

    /// <summary>平台相关的默认文件浏览器目录: Android 与 桌面 都指向用户下载目录.</summary>
    public static string GetDefaultBrowseDir()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // 优先用 Android 标准 API 拿用户的公共下载目录 (Environment.DIRECTORY_DOWNLOADS)
        try
        {
            using var env = new AndroidJavaClass("android.os.Environment");
            var dlDir = env.CallStatic<AndroidJavaObject>("getExternalStoragePublicDirectory", "Download");
            string dl = dlDir.Call<string>("getAbsolutePath");
            if (!string.IsNullOrEmpty(dl) && Directory.Exists(dl)) return dl;
        }
        catch { /* 回退到硬编码路径 */ }
        foreach (var dl in new[] { "/storage/emulated/0/Download", "/storage/emulated/0/Downloads",
                                    "/sdcard/Download", "/sdcard/Downloads",
                                    "/storage/self/primary/Download", "/storage/self/primary/Downloads",
                                    Application.persistentDataPath })
            if (!string.IsNullOrEmpty(dl) && Directory.Exists(dl)) return dl;
        return Application.persistentDataPath;
#else
        // 桌面 / Editor: 用户的下载目录 (Windows 是 C:\Users\xxx\Downloads, macOS 是 ~/Downloads)
        // Environment.SpecialFolder.MyDownloads 在 Unity 的 .NET 运行时里不可用, 自己拼
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dl = Path.Combine(home, "Downloads");
            if (!string.IsNullOrEmpty(home) && Directory.Exists(dl)) return dl;
        }
        catch { }
        // 兜底: 某些环境拼不出下载目录, 回落到 exe 目录
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        if (Directory.Exists(dir)) return dir;
        dir = Application.dataPath;
        if (Directory.Exists(dir)) return dir;
        try { dir = Directory.GetCurrentDirectory(); } catch { dir = ""; }
        return Directory.Exists(dir) ? dir : "C:\\";
#endif
    }

    /// <summary>打开文件浏览器面板 (VR 模式), 单击 .json 直接回调 onPick. 不做 SnapToHead: FB 与主界面共用 Canvas, 原地叠加显示, 界面不跳变.</summary>
    public void ShowFileBrowser(string initialDir, Action<string> onPick)
    {
        _fbOnPick = onPick;
        _fbSelectedPath = "";
        if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
            _fbCurrentDir = initialDir;
        else if (!string.IsNullOrEmpty(_fbCurrentDir) && Directory.Exists(_fbCurrentDir))
        {
            // 保留上次浏览的有效目录
        }
        else
        {
            // 都不可用: 进入"此电脑"顶层, 让用户自己选盘符
            _fbCurrentDir = null;
        }
        Debug.Log($"[FB] ShowFileBrowser: initialDir='{initialDir}', _fbCurrentDir='{_fbCurrentDir}', panel? {_fbPanel != null}, contentRt? {_fbContentRt != null}");
        if (_fbPanel != null) _fbPanel.SetActive(true);

        // World Space Canvas 下 LayoutGroup 需要等 Canvas.willRenderCanvases 事件才能正确算 child 布局
        // 直接 RefreshFileList 会出现 row 创建了但 ContentSizeFitter 还没驱动 → 行尺寸 0 → 看不见
        Canvas.willRenderCanvases += CoRefreshOnCanvas;
    }

    void CoRefreshOnCanvas()
    {
        Canvas.willRenderCanvases -= CoRefreshOnCanvas;   // 自注销, 只跑一次
        RefreshFileList();
    }

    public void CloseFileBrowser()
    {
        if (_fbPanel != null) _fbPanel.SetActive(false);
        _fbSelectedPath = "";
    }

    /// <summary>文件浏览器打开时用手柄摇杆滚动列表 (delta=归一化位置增量, 正=向上滚)</summary>
    public void ScrollFileBrowser(float delta)
    {
        if (_fbScroll != null && FileBrowserOpen)
            _fbScroll.verticalNormalizedPosition = Mathf.Clamp01(_fbScroll.verticalNormalizedPosition + delta);
    }

    void ConfirmFileBrowser()
    {
        if (_fbOnPick == null) return;
        string path = _fbSelectedPath;   // 先取出路径: CloseFileBrowser 会清空 _fbSelectedPath
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            var cb = _fbOnPick;
            _fbOnPick = null;
            CloseFileBrowser();
            cb(path);
        }
    }

    void NavigateUp()
    {
        try
        {
            // 已经在最顶层 (此电脑): 不能再 Up
            if (string.IsNullOrEmpty(_fbCurrentDir)) return;
            var parent = Directory.GetParent(_fbCurrentDir);
            if (parent == null || !parent.Exists)
            {
                // 到达盘符根 (C:\) 或其他逻辑顶层: 进入"此电脑"视图, 列出所有盘符
                _fbCurrentDir = null;
            }
            else
            {
                _fbCurrentDir = parent.FullName;
            }
            RefreshFileList();
        }
        catch (Exception e) { Debug.LogWarning("[FB] NavigateUp 异常: " + e.Message); }
    }

    void RefreshFileList()
    {
        if (_fbContentRt == null) { Debug.LogError("[FB] _fbContentRt 为 null, RefreshFileList 放弃"); return; }
        // 清空旧行
        for (int i = _fbContentRt.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(_fbContentRt.GetChild(i).gameObject);

        // ---- 此电脑顶层: 列出所有盘符 ----
        if (string.IsNullOrEmpty(_fbCurrentDir))
        {
            _fbPathText.text = T("fb.thisPc", _activeLang);
            _fbUpBtn.interactable = false;   // 顶层, 不能再 Up

            List<string> drives = new List<string>();
            try { drives = new List<string>(Directory.GetLogicalDrives()); }
            catch (Exception e) { Debug.LogWarning("[FB] GetLogicalDrives 失败: " + e.Message); }
            drives.Sort(StringComparer.OrdinalIgnoreCase);

            int shown = 0;
            foreach (var d in drives)
            {
                try { if (!Directory.Exists(d)) continue; } catch { continue; }
                shown++;
                string name = d.TrimEnd('\\');   // "C:\" → "C:"
                AddRow($"💾 {name}", new Color(0.5f, 0.85f, 1f, 1f), () =>
                {
                    _fbCurrentDir = d;
                    _fbSelectedPath = "";
                    RefreshFileList();
                });
            }
            if (shown == 0)
                AddRow(T("fb.noJson", _activeLang), new Color(0.5f, 0.5f, 0.5f, 1f), null);

            if (_fbPanel != null)
            {
                var panelRt = (RectTransform)_fbPanel.transform;
                LayoutRebuilder.ForceRebuildLayoutImmediate(panelRt);
            }
            if (_canvasGo != null) Canvas.ForceUpdateCanvases();
            return;
        }

        // ---- 普通目录 ----
        _fbPathText.text = _fbCurrentDir;

        List<string> dirs = new List<string>();
        List<string> jsonFiles = new List<string>();
        try
        {
            dirs = new List<string>(Directory.GetDirectories(_fbCurrentDir));
        }
        catch (Exception e) { Debug.LogWarning("[FB] 列目录失败 " + _fbCurrentDir + ": " + e.Message); }
        try
        {
            foreach (var f in Directory.GetFiles(_fbCurrentDir, "*.json")) jsonFiles.Add(f);
            foreach (var f in Directory.GetFiles(_fbCurrentDir, "*.JSON")) if (!jsonFiles.Contains(f)) jsonFiles.Add(f);
        }
        catch (Exception e) { Debug.LogWarning("[FB] 列 json 失败: " + e.Message); }

        // 按名称排序
        dirs.Sort();
        jsonFiles.Sort();

        Debug.Log($"[FB] RefreshFileList: dir='{_fbCurrentDir}', dirs={dirs.Count}, json={jsonFiles.Count}");

        // Up 按钮状态: 盘符根目录 (C:\) Up 有效 —— 返回到此电脑视图; 普通目录也有效
        _fbUpBtn.interactable = true;

        // 先列目录 (图标 📁)
        foreach (var d in dirs)
        {
            string name = Path.GetFileName(d);
            AddRow($"📁 {name}", new Color(0.5f, 0.85f, 1f, 1f), () =>
            {
                _fbCurrentDir = d;
                _fbSelectedPath = "";
                RefreshFileList();
            });
        }
        // 再列 json 文件 (单击直接播放)
        foreach (var f in jsonFiles)
        {
            string name = Path.GetFileName(f);
            AddRow($"📄 {name}", Color.white, () =>
            {
                _fbSelectedPath = f;
                ConfirmFileBrowser();
            });
        }

        if (dirs.Count == 0 && jsonFiles.Count == 0)
            AddRow(T("fb.noJson", _activeLang), new Color(0.5f, 0.5f, 0.5f, 1f), null);

        // 强制 ScrollRect Content + 整个 FB 面板立刻重算 Layout (Mask / Viewport / Content 三层嵌套)
        if (_fbPanel != null)
        {
            var panelRt = (RectTransform)_fbPanel.transform;
            LayoutRebuilder.ForceRebuildLayoutImmediate(panelRt);
        }
        if (_canvasGo != null) Canvas.ForceUpdateCanvases();
    }

    /// <summary>往 ScrollRect Content 加一行 (目录行=单击进入; 文件行=单击直接播放)</summary>
    void AddRow(string label, Color textColor, Action onClick)
    {
        var go = new GameObject("Row", typeof(RectTransform));
        go.transform.SetParent(_fbContentRt, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0f);
        Skin(img);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.colors = HoverColors();
        // VLG childControlHeight=true: 行高由 LayoutElement 提供; 行位置/宽度全部由 VLG 控制, 不要手设锚点
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 32f;

        // 行文本: 横向全拉伸 (旧版用 sizeDelta=(-10,32) 负宽度, 文本矩形翻折导致整行不渲染)
        var txt = Label(go.transform, "RowText", label, 18, TextAnchor.MiddleLeft, textColor,
                        Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        var trt = txt.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(10f, 0f);
        trt.offsetMax = new Vector2(-4f, 0f);

        if (onClick != null)
            btn.onClick.AddListener(() => { onClick(); });
    }

    // ================= 状态刷新 =================

    public void ShowPlayer()
    {
        if (_home == null) return;
        _home.SetActive(false);
        CloseFileBrowser();   // 进入播放器时关掉文件浏览器面板 (如果还开着)
        _playerPanel.SetActive(_progressVisible);
    }

    public void SetProgressVisible(bool v)
    {
        _progressVisible = v;
        _autoHideTimer = 0f;   // 每次显示/隐藏都重新计时
        if (_playerPanel != null) _playerPanel.SetActive(v);
        if (!v && _settingsPanel != null) _settingsPanel.SetActive(false);
    }

    /// <summary>外部更新音量显示 (不触发 OnVolume 回调, 避免无限回环)</summary>
    public void SetVolumeUI(float v)
    {
        if (_volSlider == null) return;
        _volSlider.SetValueWithoutNotify(Mathf.Clamp01(v));
    }

    /// <summary>VR 射线是否落在进度条面板矩形内 (由 Gray10Player 每帧传入)</summary>
    public void SetPanelPointer(bool on)
    {
        _vrPointerOnPanel = on;
        if (on) _autoHideTimer = 0f;
    }

    public void SetHint(string s)
    {
        if (_homeHint != null) _homeHint.text = s ?? "";
        if (_status != null) _status.text = s ?? "";
    }

    public void ShowToast(string s)
    {
        if (_toast == null) return;
        _toast.text = s;
        _toastTimer = 1.8f;
        _toastBg.gameObject.SetActive(true);
    }

    /// <summary>中心 HUD: 显示音量百分比或 seek 进度时间 (Gray10Player 负责 1.5s 自动隐藏)</summary>
    public void ShowCenter(string text)
    {
        if (_centerText == null || _centerBg == null) return;
        _centerText.text = text ?? "";
        _centerBg.gameObject.SetActive(true);
    }

    public void SetCenterVisible(bool v)
    {
        if (_centerBg != null) _centerBg.gameObject.SetActive(v);
    }

    public void SetTimes(float cur, float total)
    {
        _totalSec = Mathf.Max(0f, total);
        if (_timeTotal != null) _timeTotal.text = FormatTime(total);
        if (_timeCur != null && !_draggingSeek) _timeCur.text = FormatTime(cur);
    }

    /// <summary>VR 射线拖动滑块期间置位, 阻止刷新时把滑块拉回当前播放位置</summary>
    public void SetDragging(bool v)
    {
        _draggingSeek = v;
    }

    public void SetSeek01(float t)
    {
        if (_seekSlider == null || _draggingSeek) return;
        _settingSeek = true;
        _seekSlider.value = Mathf.Clamp01(t);
        _settingSeek = false;
    }

    public void CommitSeek()
    {
        if (_seekSlider != null && OnSeek != null) OnSeek(_seekSlider.value);
    }

    public void SetPlayState(bool playing)
    {
        if (_playIcon != null) _playIcon.sprite = playing ? _pauseSprite : _playSprite;
    }

    public void SetSettingsValues(int segments, int msaa, float tess)
    {
        if (_segSlider == null) return;
        _segSlider.SetValueWithoutNotify((float)segments);
        _msaaSlider.SetValueWithoutNotify((float)Mathf.Max(0, Array.IndexOf(MsaaVals, msaa)));
        _tessSlider.SetValueWithoutNotify(tess);
        UpdateSettingLabels(segments, msaa, tess);
    }

    public void Update(float dt)
    {
        if (_toastTimer > 0f)
        {
            _toastTimer -= dt;
            if (_toastTimer <= 0f && _toastBg != null) _toastBg.gameObject.SetActive(false);
        }
        UpdateAutoHide(dt);
    }

    /// <summary>鼠标/VR 射线都不在面板上, 且调试选项未打开时, 5 秒后请求隐藏进度条</summary>
    void UpdateAutoHide(float dt)
    {
        if (_playerPanel == null || !_progressVisible) { _autoHideTimer = 0f; return; }
        if (_settingsPanel != null && _settingsPanel.activeSelf) { _autoHideTimer = 0f; return; }
        if (_draggingSeek) { _autoHideTimer = 0f; return; }
        if (PointerOnPanel()) { _autoHideTimer = 0f; return; }
        _autoHideTimer += dt;
        if (_autoHideTimer >= AutoHideDelay)
        {
            _autoHideTimer = 0f;
            if (OnAutoHide != null) OnAutoHide();
        }
    }

    /// <summary>指针(鼠标或 VR 射线)是否落在进度条面板矩形内, 包含空白区域</summary>
    bool PointerOnPanel()
    {
        if (WorldSpace && _vrPointerOnPanel) return true;
        RectTransform rt = PlayerPanelRect;
        if (rt == null) return false;
        // 桌面用的鼠标; 世界空间画布(模拟 VR) 需要用画布相机做屏幕点判定
        Camera cam = Canvas != null && Canvas.renderMode != RenderMode.ScreenSpaceOverlay
                     ? Canvas.worldCamera : null;
        return RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, cam);
    }

    static string FormatTime(float sec)
    {
        if (sec < 0f || float.IsNaN(sec)) sec = 0f;
        int m = (int)(sec / 60f);
        int s = (int)(sec % 60f);
        return string.Format("{0:00}:{1:00}", m, s);
    }

    // ================= uGUI 构建辅助 =================

    /// <summary>给控件套用 VR 专用材质 (桌面模式下 _uiMat 为空, 保持内置默认材质)</summary>
    void Skin(Graphic g)
    {
        if (_uiMat != null && g != null) g.material = _uiMat;
    }

    /// <summary>面板背景: 四边锚点相同时按 pos/size 摆放, 否则按 anchors/offset 拉伸</summary>
    Image Panel(Transform parent, string name, Vector2 aMin, Vector2 aMax, Vector2 oMin, Vector2 oMax,
                Color color, float height = 0f)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        Skin(img);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = aMin;
        rt.anchorMax = aMax;
        if (aMin == aMax)
        {
            rt.pivot = aMin;
            rt.anchoredPosition = oMin;
            rt.sizeDelta = oMax;
        }
        else
        {
            rt.pivot = new Vector2(0.5f, 0f);
            rt.offsetMin = oMin;
            rt.offsetMax = oMax;
            if (height > 0f)
            {
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = new Vector2(0f, height);
            }
        }
        return img;
    }

    static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    Text Label(Transform parent, string name, string txt, int size, TextAnchor align, Color color,
               Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 sz, bool bold = false)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = _font;
        t.text = txt;
        t.fontSize = size;
        t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        t.color = color;
        t.alignment = align;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        Skin(t);
        Place((RectTransform)go.transform, anchor, pivot, pos, sz);
        return t;
    }

    /// <summary>文本按钮: 背景 Image (接收点击) + 居中文字</summary>
    Button TextButton(Transform parent, string name, string label, int fontSize,
                      Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, Color bg, Action onClick)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = bg;
        Skin(img);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.colors = HoverColors();
        Place((RectTransform)go.transform, anchor, pivot, pos, size);
        var txt = Label(go.transform, "Text", label, fontSize, TextAnchor.MiddleCenter, Color.white,
                        Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        var trt = txt.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(8f, 4f);
        trt.offsetMax = new Vector2(-8f, -4f);
        btn.onClick.AddListener(() => { if (onClick != null) onClick(); });
        return btn;
    }

    /// <summary>图标按钮: 无底色的点击区 + 居中白色图标</summary>
    Button IconButton(Transform parent, string name, Sprite icon, Vector2 anchor, Vector2 pivot,
                      Vector2 pos, Vector2 size, Action onClick)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0f);   // 透明: 只有白色图标, 不要背后的黑色方块
        Skin(img);
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.colors = HoverColors();
        Place((RectTransform)go.transform, anchor, pivot, pos, size);

        var iconGo = new GameObject("Icon", typeof(RectTransform));
        iconGo.transform.SetParent(go.transform, false);
        var iconImg = iconGo.AddComponent<Image>();
        iconImg.sprite = icon;
        iconImg.color = Color.white;
        iconImg.raycastTarget = false;
        iconImg.preserveAspect = true;
        Skin(iconImg);
        var irt = (RectTransform)iconGo.transform;
        irt.anchorMin = Vector2.zero;
        irt.anchorMax = Vector2.one;
        irt.offsetMin = new Vector2(9f, 7f);
        irt.offsetMax = new Vector2(-9f, -7f);
        btn.onClick.AddListener(() => { if (onClick != null) onClick(); });
        return btn;
    }

    static ColorBlock HoverColors()
    {
        return new ColorBlock
        {
            normalColor = Color.white,
            highlightedColor = new Color(0.78f, 0.88f, 1f, 1f),
            pressedColor = new Color(0.6f, 0.75f, 0.95f, 1f),
            selectedColor = Color.white,
            disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f),
            colorMultiplier = 1f,
            fadeDuration = 0.08f
        };
    }

    Slider BuildSlider(Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size,
                       float min, float max, float value, float barRatio = 0.35f)
    {
        var go = new GameObject("Slider", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var slider = go.AddComponent<Slider>();
        Place((RectTransform)go.transform, anchor, pivot, pos, size);

        var bg = new GameObject("Background", typeof(RectTransform));
        bg.transform.SetParent(go.transform, false);
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(1f, 1f, 1f, 0.16f);
        Skin(bgImg);
        var bgRt = (RectTransform)bg.transform;
        bgRt.anchorMin = new Vector2(0f, 0.5f);
        bgRt.anchorMax = new Vector2(1f, 0.5f);
        bgRt.pivot = new Vector2(0.5f, 0.5f);
        bgRt.sizeDelta = new Vector2(0f, size.y * barRatio);

        var fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(go.transform, false);
        var faRt = (RectTransform)fillArea.transform;
        faRt.anchorMin = Vector2.zero;
        faRt.anchorMax = Vector2.one;
        faRt.offsetMin = Vector2.zero;
        faRt.offsetMax = Vector2.zero;

        var fill = new GameObject("Fill", typeof(RectTransform));
        fill.transform.SetParent(fillArea.transform, false);
        var fillImg = fill.AddComponent<Image>();
        fillImg.color = new Color(0.25f, 0.62f, 1f, 0.95f);
        Skin(fillImg);
        var fillRt = (RectTransform)fill.transform;
        fillRt.anchorMin = new Vector2(0f, 0.5f);
        fillRt.anchorMax = new Vector2(1f, 0.5f);
        fillRt.pivot = new Vector2(0.5f, 0.5f);
        // Slider 每帧也会把 fill 的纵向锚点强制拉成 0..1 (高度 = 滑块高度 + sizeDelta.y),
        // 和 handle 一样要减去滑块高度, 否则蓝条会粗成两倍
        fillRt.sizeDelta = new Vector2(0f, size.y * barRatio - size.y);
        slider.fillRect = fillRt;

        var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(go.transform, false);
        var haRt = (RectTransform)handleArea.transform;
        haRt.anchorMin = Vector2.zero;
        haRt.anchorMax = Vector2.one;
        haRt.offsetMin = Vector2.zero;
        haRt.offsetMax = Vector2.zero;

        var handle = new GameObject("Handle", typeof(RectTransform));
        handle.transform.SetParent(handleArea.transform, false);
        var handleImg = handle.AddComponent<Image>();
        handleImg.sprite = _circleSprite;
        handleImg.color = Color.white;
        handleImg.preserveAspect = true;   // 保证圆点不被拉成椭圆
        Skin(handleImg);
        var hRt = (RectTransform)handle.transform;
        hRt.anchorMin = new Vector2(0f, 0.5f);
        hRt.anchorMax = new Vector2(0f, 0.5f);
        hRt.pivot = new Vector2(0.5f, 0.5f);
        // Slider 每帧会把 handle 的纵向锚点强制拉成 0..1 (高度 = 滑块高度 + sizeDelta.y),
        // 所以纵向 sizeDelta 要减去滑块高度, 才能得到正圆
        float dot = size.y * 1.1f;
        hRt.sizeDelta = new Vector2(dot, dot - size.y);
        slider.handleRect = hRt;
        slider.targetGraphic = handleImg;

        slider.minValue = min;
        slider.maxValue = max;
        slider.value = value;
        return slider;
    }

    static void AddTrigger(EventTrigger et, EventTriggerType type, Action<BaseEventData> cb)
    {
        var entry = new EventTrigger.Entry { eventID = type };
        entry.callback.AddListener(d => cb(d));
        et.triggers.Add(entry);
    }

    // ================= 图标纹理 (代码生成) =================

    enum IconKind { Play, Pause, Prev, Next, Loop, Random, Circle, Volume }

    Sprite Icon(IconKind kind)
    {
        const int N = 128;
        // 带 mipmap: 图标在界面上只占 20~40px, 128px 纹理必须降采样;
        // 没有 mip 时逐级收缩会抖动/发糊, 有 mip + 三线性过滤边缘才干净
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, true);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Trilinear;
        var px = new Color32[N * N];
        const int SS = 8;   // 8x8 超采样: 边缘 alpha 有 64 级, 比 3x3 平滑得多
        for (int y = 0; y < N; y++)
        {
            for (int x = 0; x < N; x++)
            {
                int hit = 0;
                for (int sy = 0; sy < SS; sy++)
                {
                    for (int sx = 0; sx < SS; sx++)
                    {
                        float fx = x + (sx + 0.5f) / SS;
                        float fy = y + (sy + 0.5f) / SS;
                        if (InsideIcon(kind, fx, fy)) hit++;
                    }
                }
                byte a = (byte)(hit * 255 / (SS * SS));
                px[y * N + x] = new Color32(255, 255, 255, a);
            }
        }
        tex.SetPixels32(px);
        tex.Apply(true);   // 生成 mip 链
        return Sprite.Create(tex, new Rect(0f, 0f, N, N), new Vector2(0.5f, 0.5f), 100f);
    }

    static bool InsideIcon(IconKind kind, float x, float y)
    {
        switch (kind)
        {
            case IconKind.Play:
                return Tri(x, y, new Vector2(38f, 18f), new Vector2(38f, 110f), new Vector2(106f, 64f));
            case IconKind.Pause:
                return InRect(x, y, 36f, 20f, 58f, 108f) || InRect(x, y, 70f, 20f, 92f, 108f);
            case IconKind.Prev:
                return InRect(x, y, 26f, 22f, 40f, 106f) ||
                       Tri(x, y, new Vector2(102f, 18f), new Vector2(102f, 110f), new Vector2(46f, 64f));
            case IconKind.Next:
                return Tri(x, y, new Vector2(26f, 18f), new Vector2(26f, 110f), new Vector2(82f, 64f)) ||
                       InRect(x, y, 88f, 22f, 102f, 106f);
            case IconKind.Loop:
                return LoopShape(x, y);
            case IconKind.Random:
                return RandomShape(x, y);
            case IconKind.Circle:
                return Dist2(x, y, 64f, 64f) <= 55f * 55f;
            case IconKind.Volume:
                // Material Design 喇叭: 矩形手柄 + 三角喇叭口 + 两根弧形声波带 (与参考图一致)
                // x y x1 x2 y1 y2
                bool body = InRect(x, y, 23f, 49f, 52f, 84f) ||
                            Tri(x, y, new Vector2(90f, 20f), new Vector2(90f, 110f), new Vector2(30f, 64f));
                return body;
                // 声波弧 (圆心在三角顶点 112,64, 向右展开的两条弧带)
                // bool arcS = ArcBand(x, y, 120f, 64f, 22f, 30f, -40f, 40f);
                // bool arcL = ArcBand(x, y, 130f, 64f, 44f, 56f, -55f, 55f);
                // return body || arcS || arcL;
        }
        return false;
    }

    static bool LoopShape(float x, float y)
    {
        float dx = x - 64f, dy = y - 58f;
        float r = Mathf.Sqrt(dx * dx + dy * dy);
        if (r >= 32f && r <= 44f)
        {
            float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;   // -180..180
            if (!(ang > 2f && ang < 62f)) return true;          // 右上缺口给箭头
        }
        float a = 62f * Mathf.Deg2Rad;
        Vector2 p = new Vector2(64f + 38f * Mathf.Cos(a), 58f + 38f * Mathf.Sin(a));
        Vector2 dir = new Vector2(Mathf.Sin(a), -Mathf.Cos(a));   // 顺时针切向
        return Arrow(x, y, p + dir * 20f, dir, 26f, 17f);
    }

    static bool RandomShape(float x, float y)
    {
        return Segment(x, y, 24f, 30f, 98f, 98f, 7f) ||
               Arrow(x, y, new Vector2(102f, 102f), new Vector2(0.707f, 0.707f), 26f, 17f) ||
               Segment(x, y, 24f, 98f, 98f, 30f, 7f) ||
               Arrow(x, y, new Vector2(102f, 26f), new Vector2(0.707f, -0.707f), 26f, 17f);
    }

    static bool Arrow(float x, float y, Vector2 tip, Vector2 dir, float len, float halfW)
    {
        Vector2 d = dir.normalized;
        Vector2 perp = new Vector2(-d.y, d.x);
        Vector2 baseP = tip - d * len;
        return Tri(x, y, tip, baseP + perp * halfW, baseP - perp * halfW);
    }

    static bool Segment(float x, float y, float ax, float ay, float bx, float by, float w)
    {
        Vector2 p = new Vector2(x, y), a = new Vector2(ax, ay), b = new Vector2(bx, by);
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
        return (p - (a + ab * t)).sqrMagnitude <= w * w;
    }

    static bool InRect(float x, float y, float x0, float y0, float x1, float y1)
    {
        return x >= x0 && x <= x1 && y >= y0 && y <= y1;
    }

    static float Dist2(float x, float y, float cx, float cy)
    {
        float dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy;
    }

    static bool Tri(float x, float y, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = (x - b.x) * (a.y - b.y) - (a.x - b.x) * (y - b.y);
        float d2 = (x - c.x) * (b.y - c.y) - (b.x - c.x) * (y - c.y);
        float d3 = (x - a.x) * (c.y - a.y) - (c.x - a.x) * (y - a.y);
        bool hasNeg = (d1 < 0f) || (d2 < 0f) || (d3 < 0f);
        bool hasPos = (d1 > 0f) || (d2 > 0f) || (d3 > 0f);
        return !(hasNeg && hasPos);
    }

    /// <summary>圆环扇形带 (圆心 cx,cy; 半径 [r0,r1]; 角度区间 [a0,a1] 度, 0°=右, 正=逆时针)</summary>
    static bool ArcBand(float x, float y, float cx, float cy, float r0, float r1, float a0, float a1)
    {
        float dx = x - cx, dy = y - cy;
        float r2 = dx * dx + dy * dy;
        float r02 = r0 * r0, r12 = r1 * r1;
        if (r2 < r02 || r2 > r12) return false;
        float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;   // -180..180
        // 归一化角度区间到 [-180,180] 处理跨 -180/180 边界
        if (a0 > a1) { float t = a0; a0 = a1; a1 = t; }
        return ang >= a0 && ang <= a1;
    }

    // ================= 系统文件对话框 =================

    /// <summary>选择 json 文件 (编辑器用 EditorUtility, Windows 打包用系统 GetOpenFileName)</summary>
    public static string PickJsonFile(string initialDir)
    {
#if UNITY_EDITOR
        return EditorUtility.OpenFilePanel("选择 JSON 元数据", initialDir, "json");
#elif UNITY_STANDALONE_WIN
        try
        {
            if (Application.platform == RuntimePlatform.WindowsPlayer)
                return WinPickJson(initialDir);
        }
        catch (Exception e) { Debug.LogWarning("[UI] 打开文件对话框失败: " + e.Message); }
        return null;
#else
        return null;
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    class OpenFileName
    {
        public int structSize;
        public IntPtr dlgOwner;
        public IntPtr instance;
        [MarshalAs(UnmanagedType.LPWStr)] public string filter;
        [MarshalAs(UnmanagedType.LPWStr)] public string customFilter;
        public int maxCustFilter;
        public int filterIndex;
        public StringBuilder file;
        public int maxFile;
        public StringBuilder fileTitle;
        public int maxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string initialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string title;
        public int flags;
        public short fileOffset;
        public short fileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string defExt;
        public IntPtr custData;
        public IntPtr hook;
        [MarshalAs(UnmanagedType.LPWStr)] public string templateName;
        public IntPtr reservedPtr;
        public int reservedInt;
        public int flagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

    static string WinPickJson(string initialDir)
    {
        var file = new StringBuilder(1024);
        var title = new StringBuilder(260);
        var ofn = new OpenFileName
        {
            structSize = Marshal.SizeOf(typeof(OpenFileName)),
            filter = "JSON 元数据 (*.json)\0*.json\0所有文件 (*.*)\0*.*\0\0",
            filterIndex = 1,
            file = file,
            maxFile = file.Capacity,
            fileTitle = title,
            maxFileTitle = title.Capacity,
            initialDir = initialDir,
            title = "选择 JSON 元数据",
            flags = 0x00001000 | 0x00000800 | 0x00000008 | 0x00000004,   // 文件必须存在|路径必须存在|不改目录|只读
            defExt = "json"
        };
        return GetOpenFileName(ofn) ? file.ToString() : null;
    }
#endif
}
