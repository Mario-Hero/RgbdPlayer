#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Android MediaCodec 深度解码器 (方案B: 10/12bit HEVC 硬解).
/// 替代 Windows 的 ffmpeg 管道, 提供完全相同的 public API, Gray10Player 无需改动.
///
/// 解码: Java 插件 DepthMediaCodec 用 MediaCodec+ImageReader 解 HEVC, 取 Y 平面码值;
///       C# 后台线程调 decodeNextFrame(), 从直接缓冲 memcpy 出 ushort[] 入队.
/// 位深: Java 侧从 Y 平面 pixelStride 自动探测 (1=8bit, 2=10/12bit), 首帧校准 10 vs 12.
/// 纹理: R16, 码值 0..MaxCode 原样, shader 用 round(r*65535)/_DepthMax 还原.
/// </summary>
public class DepthPipeDecoder : IDisposable
{
    // ---- Java 插件 ----
    AndroidJavaObject _java;
    int _yBufferSize;

    // ---- 帧队列 (与 Windows 版一致) ----
    readonly object _lock = new object();
    readonly Queue<ushort[]> _q = new Queue<ushort[]>();
    readonly Queue<long> _qi = new Queue<long>();
    readonly Stack<ushort[]> _free = new Stack<ushort[]>();
    const int MaxQueue = 12;

    Thread _thread;
    int _w, _h, _bits = 10;
    double _fps = 30.0;
    long _frameCount;
    int _gen;

    int _starved, _dropped, _applied;
    long _produced;
    long _appliedFrame = -1;
    long _lastFrameTick;
    long _lastTarget = -1;

    bool _eof, _failed;
    string _lastErr = "";
    Texture2D _tex;

    public bool Ready { get; private set; }
    public bool Eof { get { return Volatile.Read(ref _eof); } }
    public bool Failed { get { return Volatile.Read(ref _failed); } }
    public string Error { get { return _lastErr; } }
    public Texture2D Texture { get { return _tex; } }
    public int Width { get { return _w; } }
    public int Height { get { return _h; } }
    public int MaxCode { get; private set; }
    public int Bits { get { return _bits; } }
    public double Fps { get { return _fps; } }
    public long FrameCount { get { return _frameCount; } }
    public float IdleTime
    {
        get
        {
            if (Queued > 0) return 0f;
            return (Environment.TickCount - Interlocked.Read(ref _lastFrameTick)) / 1000f;
        }
    }
    public long LastTarget { get { return Volatile.Read(ref _lastTarget); } }
    public int Queued { get { lock (_lock) return _qi.Count; } }
    public int Starved { get { return Volatile.Read(ref _starved); } }
    public int Dropped { get { return Volatile.Read(ref _dropped); } }
    public int Produced { get { return (int)Volatile.Read(ref _produced); } }
    public int Applied { get { return Volatile.Read(ref _applied); } }
    public long AppliedFrame { get { return Volatile.Read(ref _appliedFrame); } }
    public bool Looping { get { return _frameCount > 0; } }

    /// <summary>启动 MediaCodec 解码 (双轨自动选深度轨).</summary>
    public void Start(string path)
    {
        _failed = false;
        _eof = false;
        KillJava();

        try
        {
            _java = new AndroidJavaObject("com.stereoanyvideo.depth.DepthMediaCodec");
            bool ok = _java.Call<bool>("open", path);
            if (!ok) throw new Exception("DepthMediaCodec.open 返回 false (详见 logcat DepthMediaCodec)");

            _w = _java.Call<int>("getWidth");
            _h = _java.Call<int>("getHeight");
            _fps = _java.Call<double>("getFps");
            _frameCount = _java.Call<long>("getFrameCount");

            // 位深首帧后由 Java 探测, 先按 10 建纹理 (首帧会校准)
            _bits = 10;
            MaxCode = 1023;

            // 不碰 native address 指针了 (Android Harmony 没公开 DirectByteBuffer 类, libunity.so 的 GetDirectBufferAddress 又被 Unity 6 移除).
            // 改用 Java getYBufferBytes() 返回 byte[], 每次 ReadLoop 跨 JNI 拿. 每帧 ~819KB 拷贝, Android 扛得住.
            _yBufferSize = _w * _h * 2;
            Debug.Log($"[DepthMC] 模式=byte[] 拷贝 size={_yBufferSize} ({_w}x{_h}x2)");

            EnsureTexture();

            int gen = ++_gen;
            Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount);
            _thread = new Thread(() => ReadLoop(gen)) { IsBackground = true, Name = "DepthMediaCodec" };
            _thread.Start();
            Ready = true;
            Debug.Log($"[DepthMC] 解码已启动 MediaCodec {_w}x{_h} (首帧后校准位深)");
        }
        catch (Exception e)
        {
            _failed = true;
            _lastErr = e.Message;
            Debug.LogError("[DepthMC] 启动失败: " + e.Message);
            KillJava();
        }
    }

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
            name = "DepthMCTex"
        };
        Debug.Log($"[DepthMC] 重建深度纹理 {_w}x{_h} (R16)");
    }

    void ReadLoop(int gen)
    {
        Debug.Log("[DepthMC] ReadLoop 线程已启动, gen=" + gen);
        try
        {
            int noFrameCount = 0;
            while (gen == _gen)
            {
                bool hasFrame = _java.Call<bool>("decodeNextFrame");
                if (!hasFrame)
                {
                    noFrameCount++;
                    if (noFrameCount <= 5 || noFrameCount % 30 == 0)
                        Debug.Log($"[DepthMC] decodeNextFrame=false 连续 {noFrameCount} 次, 总 produce={Interlocked.Read(ref _produced)}");
                    // 不是真 EOF: 可能是 MediaCodec 正在初始化等第一帧, 再等一会
                    if (noFrameCount > 120)   // ~4s@30fps 还没帧, 放弃
                    {
                        Debug.LogWarning("[DepthMC] 等了 120 次 decodeNextFrame 还没帧, 判 EOF");
                        Volatile.Write(ref _eof, true);
                        break;
                    }
                    Thread.Sleep(10);
                    continue;
                }
                noFrameCount = 0;

                // 首帧后从 Java 拿真实位深, 必要时重建 MaxCode (纹理尺寸不变)
                if (_produced == 0)
                {
                    int bd = _java.Call<int>("getBitDepth");
                    if (bd > 0 && bd != _bits)
                    {
                        _bits = bd;
                        MaxCode = (1 << _bits) - 1;
                        Debug.Log($"[DepthMC] 位深校准: {_bits}bit (MaxCode={MaxCode})");
                    }
                    Debug.Log($"[DepthMC] ReadLoop 首帧已拿到, w={_w} h={_h}, bitDepth={bd}");
                }

                lock (_lock)
                {
                    while (_q.Count >= MaxQueue && gen == _gen) Monitor.Wait(_lock, 4);
                    if (gen != _gen) return;

                    ushort[] buf = _free.Count > 0 ? _free.Pop() : new ushort[_w * _h];
                    if (buf.Length != _w * _h) buf = new ushort[_w * _h];

                    // 从 Java 拿 byte[] (已转好 ushort LE 码值), BlockCopy 到 ushort[].
                    // 避开所有 native pointer / DllImport / JNI raw array API 问题.
                    byte[] raw = _java.Call<byte[]>("getYBufferBytes");
                    if (raw == null || raw.Length < buf.Length * 2)
                        throw new InvalidOperationException($"getYBufferBytes 长度不对: {(raw != null ? raw.Length : -1)} 期望 {buf.Length * 2}");
                    Buffer.BlockCopy(raw, 0, buf, 0, buf.Length * 2);

                    _q.Enqueue(buf);
                    _qi.Enqueue(Interlocked.Read(ref _produced));
                    Monitor.PulseAll(_lock);
                }
                Interlocked.Increment(ref _produced);
                Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount);
            }
        }
        catch (Exception e)
        {
            if (gen == _gen)
            {
                Volatile.Write(ref _failed, true);
                _lastErr = e.Message;
                Debug.LogWarning("[DepthMC] 读线程异常: " + e.Message);
            }
        }
    }

    /// <summary>主线程每帧: 与颜色帧号对齐, 取 <= target 的最新帧上传纹理.</summary>
    public bool UpdateTo(long targetFrame)
    {
        Volatile.Write(ref _lastTarget, targetFrame);
        ushort[] ready = null;
        long gotIdx = -1;
        lock (_lock)
        {
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
            else if (_qi.Count == 0) { _starved++; }
            Monitor.PulseAll(_lock);
        }
        if (ready == null) return false;

        Interlocked.Increment(ref _applied);
        Volatile.Write(ref _appliedFrame, gotIdx);

        if (_tex == null || _tex.width != _w || _tex.height != _h || ready.Length != _w * _h)
        {
            EnsureTexture();
            Recycle(ready);
            return false;
        }
        _tex.GetRawTextureData<ushort>().CopyFrom(ready);
        _tex.Apply(false);
        Recycle(ready);
        return true;
    }

    void Recycle(ushort[] buf)
    {
        if (buf == null) return;
        lock (_lock) { if (_free.Count < MaxQueue + 4) _free.Push(buf); }
    }

    void ClearQueue()
    {
        while (_q.Count > 0) _free.Push(_q.Dequeue());
        _qi.Clear();
    }

    /// <summary>seek 到指定帧并继续解码 (循环回零 / 追帧).</summary>
    public void Restart(string path, long startFrame = 0)
    {
        KillJava();
        Volatile.Write(ref _eof, false);
        Volatile.Write(ref _failed, false);
        Volatile.Write(ref _starved, 0);
        Volatile.Write(ref _dropped, 0);
        lock (_lock) { ClearQueue(); }

        try
        {
            _java = new AndroidJavaObject("com.stereoanyvideo.depth.DepthMediaCodec");
            bool ok = _java.Call<bool>("open", path);
            if (!ok) throw new Exception("DepthMediaCodec.open 失败");
            _w = _java.Call<int>("getWidth");
            _h = _java.Call<int>("getHeight");
            _fps = _java.Call<double>("getFps");
            _frameCount = _java.Call<long>("getFrameCount");

            if (startFrame > 0)
                _java.Call("seekTo", startFrame);

            _yBufferSize = _w * _h * 2;
            EnsureTexture();
            int gen = ++_gen;
            Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount);
            _thread = new Thread(() => ReadLoop(gen)) { IsBackground = true, Name = "DepthMediaCodec" };
            _thread.Start();
            Ready = true;
            Debug.Log($"[DepthMC] 重启 {_w}x{_h}" + (startFrame > 0 ? $" (seek 帧{startFrame})" : ""));
        }
        catch (Exception e)
        {
            _failed = true;
            _lastErr = e.Message;
            Debug.LogError("[DepthMC] 重启失败: " + e.Message);
        }
    }

    void KillJava()
    {
        Ready = false;
        _gen++;
        lock (_lock) { ClearQueue(); Monitor.PulseAll(_lock); }
        try { if (_thread != null && _thread.IsAlive) _thread.Join(200); } catch { }
        _thread = null;
        try { _java?.Call("release"); } catch { }
        try { _java?.Dispose(); } catch { }
        _java = null;
    }

    public void Kill() { KillJava(); }

    public void Dispose()
    {
        Kill();
        if (_tex != null) UnityEngine.Object.Destroy(_tex);
        _tex = null;
    }
}
#endif
