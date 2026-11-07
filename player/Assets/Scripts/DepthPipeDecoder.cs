using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// ffmpeg 管道深度解码器 (绕开 Unity MF 解码链的 8bit 硬限):
///   ffmpeg -hwaccel cuda 硬解 8/10/12bit HEVC -> gray/gray10le/gray12le (Y 平面码值) 经 stdout 管道
///   -> 后台线程读帧 -> 主线程写 R16 纹理
/// 位深: ffprobe 读源像素格式 (yuv420p=8bit / yuv420p10le=10bit / yuv420p12le=12bit), 码值 0..255/1023/4095 原样
/// 纹理约定: R16 raw = 码值原样, shader 用 round(r * 65535) / _DepthMax 还原 (_DepthMax = 255/1023/4095)
/// </summary>
#if !UNITY_ANDROID || UNITY_EDITOR
public class DepthPipeDecoder : IDisposable
{
    Process _proc;
    Stream _out;
    Thread _thread;
    readonly object _lock = new object();
    readonly Queue<ushort[]> _q = new Queue<ushort[]>();     // 预解码队列 (读线程一路解码, 主线程按帧号取)
    readonly Queue<long> _qi = new Queue<long>();            // 与 _q 对应的帧号
    readonly Stack<ushort[]> _free = new Stack<ushort[]>();  // 帧缓冲复用池, 避免每帧 new
    const int MaxQueue = 24;    // 预解码队列深度 (帧) = 24 * 1.6MB (1280x640 R16), 够抵消解码抖动
    int _starved, _dropped;     // 诊断: 主线程要帧时队列空(断供) / 追帧时丢弃的过期帧
    int _produced;              // 诊断: 读线程累计解码出的帧数 (真实解码吞吐)
    int _applied;               // 诊断: 成功上传到纹理的帧数 (长时间不增长 = 深度冻结)
    long _appliedFrame = -1;    // 诊断: 最近一次上传的帧号 (与目标帧相减即当前深度滞后量)
    int _w, _h;
    int _bits = 10;             // 深度码值位深: ffprobe 读源像素格式 (8=8bit, 10=10bit, 12=12bit)
    double _fps = 30.0;         // 深度视频帧率 (ffprobe r_frame_rate), 用于时长映射与重启 seek
    long _frameCount;           // 深度视频总帧数 (ffprobe nb_frames, 未知时 0)
    int _streams = 1;           // 视频流数, >1 = 双轨容器 (深度在轨1, 解码需 -map 0:v:1)
    int _gen;                   // 重启代数, 防止旧线程污染新状态
    int _hwTry;                 // 0=nvdec(cuda) 1=软解多线程; d3d11va 对 Main10 静默回退单线程软解(实测仅~20fps), 弃用
    bool _eof, _failed, _checked, _checkedPrecision;
    bool _shift6;               // 管道输出被放大到 16bit 满量程 (10bit=code<<6 / 12bit=code<<4) 时自动右移归一
    string _lastErr = "";
    Texture2D _tex;
    long _lastFrameTick;        // 最近收到帧的时刻 (Environment.TickCount, 后台线程安全)
    long _lastTarget = -1;      // 最近一次 UpdateTo 的目标帧 (seek 联动用)

    public bool Ready { get; private set; }
    public bool Eof { get { return Volatile.Read(ref _eof); } }
    public bool Failed { get { return Volatile.Read(ref _failed); } }
    public string Error { get { return _lastErr; } }
    public Texture2D Texture { get { return _tex; } }
    public int Width { get { return _w; } }
    public int Height { get { return _h; } }
    public int MaxCode { get; private set; }    // 码值满量程: 8bit=255, 10bit=1023, 12bit=4095 (ProbeSize 后生效)
    public int Bits { get { return _bits; } }
    public double Fps { get { return _fps; } }              // 深度视频帧率
    public long FrameCount { get { return _frameCount; } }   // 深度视频总帧数 (0=未知)
    public float IdleTime
    {
        get
        {
            // 队列里有帧 = 解码正常 (读线程可能正因队列满而阻塞), 不能算"无帧", 否则看门狗会误杀重启
            if (Queued > 0) return 0f;
            return (Environment.TickCount - Interlocked.Read(ref _lastFrameTick)) / 1000f;
        }
    }
    public long LastTarget { get { return Volatile.Read(ref _lastTarget); } }
    public int Queued { get { lock (_lock) return _qi.Count; } }          // 已预解码待播的帧数 (越大说明解码越超前)
    public int Starved { get { return Volatile.Read(ref _starved); } }    // 主线程要帧时队列为空的累计次数
    public int Dropped { get { return Volatile.Read(ref _dropped); } }    // 追帧时丢弃的过期帧累计数
    public int Produced { get { return Volatile.Read(ref _produced); } }   // 读线程累计解码帧数
    public int Applied { get { return Volatile.Read(ref _applied); } }     // 累计成功贴到纹理的帧数
    public long AppliedFrame { get { return Volatile.Read(ref _appliedFrame); } }   // 最近贴图的帧号
    public bool Looping { get { return _frameCount > 0; } }                // 管道是否用 -stream_loop 无限循环

    /// <summary>起 ffmpeg 硬解进程 (ffprobe 查宽高, 纹理只建一次, Restart 可复用)</summary>
    public void Start(string path)
    {
        ProbeSize(path);
        EnsureTexture();          // 尺寸变化 (换 json / 换分辨率) 必须重建, 否则 CopyFrom 长度不一致
        _checked = false;         // 新文件: 重新做 16bit 满量程检测
        _shift6 = false;
        _checkedPrecision = false;
        _eof = false;
        _failed = false;
        StartProcess(path, 0);
    }

    /// <summary>按当前探测到的 _w/_h 保证 R16 纹理尺寸正确 (复用或重建)</summary>
    void EnsureTexture()
    {
        if (_tex != null && _tex.width == _w && _tex.height == _h) return;
        if (_tex != null)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(_tex);
            else UnityEngine.Object.DestroyImmediate(_tex);
        }
        _tex = new Texture2D(_w, _h, TextureFormat.R16, false, true)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "DepthPipeTex"
        };
        Debug.Log($"[DepthPipe] 重建深度纹理 {_w}x{_h} (R16)");
    }

    void ProbeSize(string path)
    {
        // 双轨容器 (彩色轨0 + 深度轨1): 取最后一条视频流的宽高, 并记录流数供 -map 使用
        var psi = new ProcessStartInfo("ffprobe",
            $"-v error -select_streams v -show_entries stream=width,height,pix_fmt,r_frame_rate,nb_frames -of csv=p=0 \"{path}\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using (var p = Process.Start(psi))
        {
            string[] lines = p.StandardOutput.ReadToEnd().Trim()
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            p.WaitForExit();
            if (lines.Length == 0)
                throw new Exception("ffprobe 未找到视频流");
            var parts = lines[lines.Length - 1].Split(',');
            if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), out _w) || !int.TryParse(parts[1].Trim(), out _h))
                throw new Exception("ffprobe 解析宽高失败: " + lines[lines.Length - 1]);
            // 像素格式判断位深: 含 "12"=12bit (yuv420p12le/gray12le), 含 "10"=10bit, 其余 8bit (yuv420p/gray)
            string pf = parts.Length > 2 ? parts[2].Trim() : "";
            _bits = pf.Contains("12") ? 12 : (pf.Contains("10") ? 10 : 8);
            MaxCode = (1 << _bits) - 1;
            // 帧率 (如 60000/1001): 重启 seek 的 -ss 必须按真实帧率换算, 硬编码 30 会让 60fps 深度偏 2 倍时间
            if (parts.Length > 3)
            {
                var fr = parts[3].Trim().Split('/');
                if (fr.Length == 2
                    && double.TryParse(fr[0], System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out double num)
                    && double.TryParse(fr[1], System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out double den)
                    && den > 0 && num > 0)
                    _fps = num / den;
            }
            // 总帧数 (部分容器为 N/A): 颜色/深度时长不一致时按帧数比例映射
            if (parts.Length > 4 && long.TryParse(parts[4].Trim(), out long nf) && nf > 0)
                _frameCount = nf;
            _streams = lines.Length;
        }
    }

    void StartProcess(string path, long startFrame)
    {
        int gen = ++_gen;
        string ss = startFrame > 0
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "-ss {0:F6} ", startFrame / _fps)
            : "";
        // 无限循环: 深度视频很短 (常 <5s), 颜色循环一圈就重启一次 ffmpeg 会冷启动 ~1s, 期间深度必然滞后.
        // 已知总帧数时让 ffmpeg 自己循环, 读出的帧号单调递增, 播放端用"虚拟帧号"(每圈 +总帧数) 对齐即可, 永不重启.
        string loop = (_frameCount > 0 && startFrame == 0) ? "-stream_loop -1 " : "";
        string map = _streams > 1 ? "-map 0:v:1 " : "";   // 双轨: 深度在轨1, 不指定会默认抓轨0(彩色)
        // 解码后端: 优先 nvdec (实测 640² Main10 无损 660fps), 不可用时降级软解多线程 (275fps)
        // 不用 -hwaccel_output_format cuda: 输出 gray10le 是 CPU 格式, 显式 hwdownload 反而多一次中转;
        //   直接 -hwaccel cuda 让 ffmpeg 自动下载, 配合 rawvideo 直通最快
        // 不做 vflip: ffmpeg rawvideo 是 top-down, 改在 shader 里翻 V 坐标 (GPU 几乎零成本, 省一次 8MB CPU 拷贝)
        string hw = _hwTry == 0 ? "-hwaccel cuda " : "-threads 0 ";
        string fmt = _bits == 12 ? "gray12le" : (_bits == 10 ? "gray10le" : "gray");   // 与源位深一致, 避免格式转换缩放码值
        var psi = new ProcessStartInfo("ffmpeg",
            $"-v error {loop}{ss}{hw}-i \"{path}\" {map}-f rawvideo -pix_fmt {fmt} -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        _proc = Process.Start(psi);
        _proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _lastErr = e.Data; };
        _proc.BeginErrorReadLine();   // 必须消费 stderr, 否则缓冲满会死锁 ffmpeg
        _out = _proc.StandardOutput.BaseStream;
        Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount);
        _thread = new Thread(() => ReadLoop(gen)) { IsBackground = true, Name = "DepthPipe" };
        _thread.Start();
        Ready = true;
        Debug.Log($"[DepthPipe] 解码已启动 ({(_hwTry == 0 ? "nvdec" : "软解多线程")}) {_w}x{_h}" + (startFrame > 0 ? $" (从帧{startFrame})" : ""));
    }

    void ReadLoop(int gen)
    {
        int frameBytes = _w * _h * (_bits == 8 ? 1 : 2);   // 8bit 管道每像素 1 字节
        var raw = new byte[frameBytes];
        long index = 0;
        try
        {
            while (true)
            {
                if (!ReadExactly(raw, frameBytes)) break;
                // 背压: 队列满 (主线程消费不过来 / 还没开始播) 时稍等, 让管道缓冲自然限流 ffmpeg
                lock (_lock)
                {
                    while (_q.Count >= MaxQueue && gen == _gen) Monitor.Wait(_lock, 4);
                    if (gen != _gen) return;
                    ushort[] buf = _free.Count > 0 ? _free.Pop() : new ushort[_w * _h];
                    if (buf.Length != _w * _h) buf = new ushort[_w * _h];   // 换文件/分辨率后复用池里的旧尺寸作废
                    if (_bits == 8)
                        for (int i = 0; i < buf.Length; i++) buf[i] = raw[i];   // 8bit 扩宽到 ushort 码值
                    else
                        Buffer.BlockCopy(raw, 0, buf, 0, frameBytes);
                    _q.Enqueue(buf);
                    _qi.Enqueue(index);
                    Monitor.PulseAll(_lock);
                }
                index++;
                Interlocked.Increment(ref _produced);
                Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount);
            }
            if (gen == _gen) Volatile.Write(ref _eof, true);
        }
        catch (Exception e)
        {
            if (gen == _gen)
            {
                Volatile.Write(ref _failed, true);
                Debug.LogWarning("[DepthPipe] 读线程异常: " + e.Message + (string.IsNullOrEmpty(_lastErr) ? "" : " | stderr: " + _lastErr));
            }
        }
    }

    bool ReadExactly(byte[] buf, int n)
    {
        int off = 0;
        while (off < n)
        {
            int r = _out.Read(buf, off, n - off);
            if (r <= 0) return off == 0;   // 正常 EOF; 半帧截断按异常流处理
            off += r;
        }
        return true;
    }

    /// <summary>
    /// 主线程每帧调用: 与颜色流帧号对齐, 且永远不使用"比颜色新"的帧.
    /// 规则: 队列里只保留 <= target 的最新一帧 (更旧的丢掉), 命中就上传纹理;
    ///   队头比 target 新 -> 保持上一帧, 等颜色追上来.
    /// 关键: 解码比颜色稍慢时, 必须用"最新但落后"的帧顶上, 不能把落后的帧全丢掉 ——
    ///   那样队头永远小于 target, 深度会一直停在旧帧 (表现为深度卡在第一帧).
    /// 同时绝不用超前帧 (渲染帧率高于视频帧率时那会让深度跑到颜色前面再冻结).
    /// </summary>
    public bool UpdateTo(long targetFrame)
    {
        Volatile.Write(ref _lastTarget, targetFrame);
        ushort[] ready = null;
        long gotIdx = -1;
        lock (_lock)
        {
            // 帧号连续递增, 所以队头的第二帧号 = Peek()+1; 若它仍 <= target, 说明队头已经过期
            while (_qi.Count > 1 && _qi.Peek() + 1 <= targetFrame)
            {
                _free.Push(_q.Dequeue());
                _qi.Dequeue();
                _dropped++;
            }
            if (_qi.Count > 0 && _qi.Peek() <= targetFrame)
            {
                ready = _q.Dequeue();
                gotIdx = _qi.Dequeue();
            }
            else if (_qi.Count == 0)
            {
                _starved++;
            }
            Monitor.PulseAll(_lock);   // 队列腾出空位, 唤醒读线程
        }
        if (ready == null) return false;
        Interlocked.Increment(ref _applied);
        Volatile.Write(ref _appliedFrame, gotIdx);

        // 首帧量程检测: 预期码值 0..MaxCode (8bit=255 / 10bit=1023 / 12bit=4095); 部分 ffmpeg/hwaccel
        // 路径会把管道输出放大到 16bit 满量程 (10bit=code<<6, 12bit=code<<4), 此时右移归一
        if (!_checked)
        {
            long mx = 0;
            for (int i = 0; i < ready.Length; i += 4097)
                if (ready[i] > mx) mx = ready[i];
            _shift6 = mx > MaxCode * 2;
            _checked = true;
            if (_shift6) Debug.Log($"[DepthPipe] 检测到管道输出为 16bit 满量程 (code<<{16 - _bits}), 自动右移归一");
        }
        if (_shift6)
        {
            int sh = 16 - _bits;
            for (int i = 0; i < ready.Length; i++) ready[i] >>= sh;
        }

        // 兜底: 纹理尺寸必须与管道帧长一致 (换分辨率/换文件时), 否则 NativeArray.Copy 抛异常
        if (_tex == null || _tex.width != _w || _tex.height != _h || ready.Length != _w * _h)
        {
            Debug.LogWarning($"[DepthPipe] 尺寸不匹配 (纹理 {(_tex == null ? "null" : _tex.width + "x" + _tex.height)}, " +
                             $"帧数组 {ready.Length}, 探测 {_w}x{_h}), 重建纹理并丢弃本帧");
            EnsureTexture();
            Recycle(ready);
            return false;
        }

        _tex.GetRawTextureData<ushort>().CopyFrom(ready);   // 码值 0..MaxCode 原样写 R16

        if (!_checkedPrecision)
        {
            _checkedPrecision = true;
            CheckPrecision(ready);
        }
        _tex.Apply(false);
        Recycle(ready);                                     // 用完还回复用池, 避免每帧 2MB 垃圾
        return true;
    }

    void Recycle(ushort[] buf)
    {
        if (buf == null) return;
        lock (_lock)
        {
            if (_free.Count < MaxQueue + 4) _free.Push(buf);
        }
    }

    void CheckPrecision(ushort[] f)
    {
        int mn = int.MaxValue, mx = 0;
        double sum = 0;
        for (int i = 0; i < f.Length; i += 997)
        {
            if (f[i] < mn) mn = f[i];
            if (f[i] > mx) mx = f[i];
            sum += f[i];
        }
        int row = _h / 2;
        float minStep = 1f, last = -1f;
        int distinct = 0;
        for (int x = Mathf.Max(0, _w / 2 - 256); x < Mathf.Min(_w, _w / 2 + 256); x++)
        {
            float d = f[row * _w + x] / (float)MaxCode;
            if (d > 0.001f && d < 0.999f)
            {
                if (last > 0f)
                {
                    float s = Mathf.Abs(d - last);
                    if (s > 1e-6f && s < minStep) minStep = s;
                }
                distinct++;
                last = d;
            }
        }
        float estBits = Mathf.Log(1f / minStep, 2f);
        Debug.Log($"[Depth 精度] 管道{_bits}bit(CPU侧) 码值min={mn} max={mx} mean={sum / (f.Length / 997):F1}, " +
                  $"最小步长={minStep:F6} (约 {estBits:F1}bit 等效), 中心行有效像素={distinct}");
    }

    /// <summary>重启解码进程 (循环回零 / 卡死恢复); 解码失败时自动降级软解后重试</summary>
    public void Restart(string path, long startFrame = 0)
    {
        if (Volatile.Read(ref _failed) && _hwTry == 0)
        {
            _hwTry = 1;
            Debug.LogWarning("[DepthPipe] nvdec 不可用, 降级为软解多线程重试");
        }
        Kill();
        Volatile.Write(ref _eof, false);
        Volatile.Write(ref _failed, false);
        _checked = false;
        Volatile.Write(ref _starved, 0);
        Volatile.Write(ref _dropped, 0);
        lock (_lock) { ClearQueue(); }
        StartProcess(path, startFrame);
    }

    /// <summary>清空预解码队列 (调用方需持有 _lock), 缓冲回收进复用池</summary>
    void ClearQueue()
    {
        while (_q.Count > 0) _free.Push(_q.Dequeue());
        _qi.Clear();
    }

    public void Kill()
    {
        Ready = false;
        _gen++;                       // 使旧读线程退出
        lock (_lock) { ClearQueue(); Monitor.PulseAll(_lock); }
        try { if (_thread != null && _thread.IsAlive) _thread.Join(200); } catch { }
        _thread = null;
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        try { _out?.Dispose(); } catch { }
        _out = null;
    }

    public void Dispose()
    {
        Kill();
        if (_tex != null) UnityEngine.Object.Destroy(_tex);
        _tex = null;
    }
}
#endif

/// <summary>
/// 静态图片输入 (json 的 color_file/depth_file 指向 png/tif 等图片而非视频):
///   颜色: ffmpeg -> rgb24 raw -> RGB24 纹理 (任意 ffmpeg 能解的图像格式)
///   深度: ffmpeg -> gray/gray16le raw -> R16 码值纹理 (与视频深度管道同一套码值约定)
/// 深度位深/满量程: json 的 depth_bits / depth_max 优先, 缺失时按像素格式和数据最大值推断
/// </summary>
public static class StillImageLoader
{
    static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".tga", ".exr", ".webp" };

    public static bool IsImage(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Array.IndexOf(Exts, Path.GetExtension(path).ToLowerInvariant()) >= 0;
    }

    /// <summary>加载颜色图片为 RGB24 纹理 (行序已翻转, 与 Unity bottom-up 对齐)</summary>
    public static Texture2D LoadColor(string path, out int w, out int h)
    {
        Probe(path, out w, out h, out _);
        byte[] raw = RunFfmpeg($"-v error -noautorotate -i \"{path}\" -vf vflip -f rawvideo -pix_fmt rgb24 -", w * h * 3);
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false, true)
        {
            name = Path.GetFileName(path),
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        tex.LoadRawTextureData(raw);
        tex.Apply(false);
        return tex;
    }

    /// <summary>
    /// 加载深度图片为 R16 码值纹理; maxCode = 码值满量程 (255/1023/4095/65535).
    /// encode: "mm" = 16bit 毫米图 (process_video.py --pack depthpng), 需按 near/far 换算成归一化码值;
    ///         其它 = 图片里已是码值, 位深由 json 提示/像素格式/数据最大值推断.
    /// </summary>
    public static Texture2D LoadDepth(string path, string encode, int bitsHint, int maxHint,
                                      float nearM, float farM, float logDepth,
                                      out int maxCode, out int bits)
    {
        Probe(path, out int w, out int h, out string pixFmt);
        int srcBits = SrcBits(pixFmt);
        bool wide = srcBits > 8;
        string fmt = wide ? "gray16le" : "gray";
        byte[] raw = RunFfmpeg($"-v error -i \"{path}\" -vf vflip -f rawvideo -pix_fmt {fmt} -", w * h * (wide ? 2 : 1));

        var v = new ushort[w * h];
        if (wide) Buffer.BlockCopy(raw, 0, v, 0, v.Length * 2);
        else for (int i = 0; i < v.Length; i++) v[i] = raw[i];

        // 毫米图: 存的是绝对深度(mm), 先换算成 shader 期望的归一化码值 (与 near/far/log 映射一致)
        if (encode == "mm" || encode == "millimeter" || encode == "millimeters")
        {
            bits = 16;
            maxCode = 65535;
            float span = Mathf.Max(farM - nearM, 1e-6f);
            float logSpan = (nearM > 0f && farM > nearM) ? Mathf.Log(farM / nearM) : 0f;
            for (int i = 0; i < v.Length; i++)
            {
                float m = v[i] * 0.001f;
                float dn = (logDepth > 0.5f && logSpan > 0)
                    ? Mathf.Log(Mathf.Max(m, 1e-6f) / nearM) / logSpan
                    : (m - nearM) / span;
                v[i] = (ushort)Mathf.RoundToInt(Mathf.Clamp01(dn) * 65535f);
            }
            return MakeDepthTex(w, h, v, Path.GetFileName(path));
        }

        // 数据最大值 (跨步采样即可) 用于推断满量程
        int mx = 0;
        for (int i = 0; i < v.Length; i += 4097) if (v[i] > mx) mx = v[i];

        int shift = 0;
        if (bitsHint == 8 || bitsHint == 10 || bitsHint == 12 || bitsHint == 16)
        {
            bits = bitsHint;
            maxCode = bits == 16 ? 65535 : (1 << bits) - 1;
            if (bits < 16 && mx > maxCode * 2) shift = 16 - bits;   // 源为 16bit 满量程存储 (code<<shift)
        }
        else if (maxHint > 0)
        {
            maxCode = maxHint;
            bits = BitsOfMax(maxHint);
            if (mx > maxCode * 2) shift = 16 - bits;
        }
        else if (!wide || mx <= 255)
        {
            bits = 8; maxCode = 255;
        }
        else if (mx <= 1023)
        {
            bits = 10; maxCode = 1023;
        }
        else if (mx <= 4095)
        {
            bits = 12; maxCode = 4095;
        }
        else
        {
            // 全 16bit 满量程 (10/12bit 码值左移存储): 以 65535 归一化后 dn 与原始码值等价
            bits = 16; maxCode = 65535;
        }
        if (shift > 0) for (int i = 0; i < v.Length; i++) v[i] >>= shift;

        return MakeDepthTex(w, h, v, Path.GetFileName(path));
    }

    static Texture2D MakeDepthTex(int w, int h, ushort[] v, string name)
    {
        var tex = new Texture2D(w, h, TextureFormat.R16, false, true)
        {
            name = name,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        tex.GetRawTextureData<ushort>().CopyFrom(v);
        tex.Apply(false);
        return tex;
    }

    static int BitsOfMax(int m)
    {
        if (m >= 65535) return 16;
        if (m >= 4095) return 12;
        if (m >= 1023) return 10;
        return 8;
    }

    /// <summary>像素格式 -> 源位深 (图片的 pix_fmt 形如 gray16be / rgb24 / rgba)</summary>
    static int SrcBits(string pf)
    {
        if (pf.Contains("16")) return 16;
        if (pf.Contains("12")) return 12;
        if (pf.Contains("10")) return 10;
        if (pf.Contains("9")) return 9;
        return 8;
    }

    /// <summary>ffprobe 读图片宽高与像素格式</summary>
    static void Probe(string path, out int w, out int h, out string pixFmt)
    {
        var psi = new ProcessStartInfo("ffprobe",
            $"-v error -select_streams v -show_entries stream=width,height,pix_fmt -of csv=p=0 \"{path}\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using (var p = Process.Start(psi))
        {
            string[] lines = p.StandardOutput.ReadToEnd().Trim()
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            p.WaitForExit();
            if (lines.Length == 0) throw new Exception("ffprobe 未识别到图片: " + path);
            var parts = lines[0].Split(',');
            if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), out w) || !int.TryParse(parts[1].Trim(), out h))
                throw new Exception("ffprobe 解析图片宽高失败: " + lines[0]);
            pixFmt = parts.Length > 2 ? parts[2].Trim() : "";
        }
    }

    /// <summary>跑一次 ffmpeg 并读满 expectBytes 的 rawvideo 输出</summary>
    static byte[] RunFfmpeg(string args, int expectBytes)
    {
        var psi = new ProcessStartInfo("ffmpeg", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using (var p = Process.Start(psi))
        {
            var err = new System.Text.StringBuilder();
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) err.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            var buf = new byte[expectBytes];
            int off = 0;
            while (off < expectBytes)
            {
                int r = p.StandardOutput.BaseStream.Read(buf, off, expectBytes - off);
                if (r <= 0) break;
                off += r;
            }
            p.WaitForExit();
            if (off < expectBytes)
                throw new Exception($"图片解码数据不足 (期望 {expectBytes} 字节, 实际 {off})"
                                    + (err.Length > 0 ? " | " + err.ToString().Trim() : ""));
            return buf;
        }
    }
}
