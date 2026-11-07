using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// Unity 6 gray10 解码精度批量探测 (batchmode 无 GUI):
///   Unity.exe -batchmode -executeMethod Gray10Probe.Run -projectPath <proj> -logFile <log>
/// 同一探测视频依次用 ARGB32 / R16 / ARGBHalf / DefaultHDR 渲染目标解码,
/// 回读右半深度码值与设计基准比对 -> out/unity6_probe/report.json
/// 基准: 带 b = b*32 (0..992), 棋盘 512/513, 渐变 0..1022, 白 1023, 黑 0
/// </summary>
public static class Gray10Probe
{
    const double PerFormatTimeout = 90.0;
    static readonly string[] FormatNames = { "ARGB32", "R16", "ARGBHalf", "DefaultHDR" };
    static readonly RenderTextureFormat[] Formats =
    {
        RenderTextureFormat.ARGB32, RenderTextureFormat.R16,
        RenderTextureFormat.ARGBHalf, RenderTextureFormat.DefaultHDR
    };

    [Serializable] class Bands { public int y0, band_px, code_step, n_bands; }
    [Serializable] class Checker { public int y0, y1; public int[] codes; }
    [Serializable] class Region { public int y0, y1; }
    [Serializable] class Meta
    {
        public int W, H, fps, frames;
        public Bands bands;
        public Checker checker;
        public Region ramp, white, black;
    }

    static Meta _meta;
    static string _video, _outDir;
    static int _fmtIdx = -1;
    static VideoPlayer _vp;
    static RenderTexture _rt;
    static double _deadline;
    static bool _played;
    static readonly List<Dictionary<string, object>> _results = new List<Dictionary<string, object>>();

    [MenuItem("Tools/Gray10 解码精度探测")]
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        _video = FindArg(args, "probe-video", "out/unity6_probe/probe_gray10.mp4");
        _outDir = FindArg(args, "probe-out", "out/unity6_probe");
        string ws = Directory.GetParent(Application.dataPath).Parent.FullName;
        if (!Path.IsPathRooted(_video)) _video = Path.GetFullPath(Path.Combine(ws, _video));
        if (!Path.IsPathRooted(_outDir)) _outDir = Path.GetFullPath(Path.Combine(ws, _outDir));
        Directory.CreateDirectory(_outDir);

        string metaText = File.ReadAllText(Path.Combine(_outDir, "probe_meta.json"));
        _meta = JsonUtility.FromJson<Meta>(metaText);
        Debug.Log($"[Probe] video={_video}");
        Debug.Log($"[Probe] meta {metaText.Replace("\n", " ")}");
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        try
        {
            if (_fmtIdx == -1) { NextFormat(); return; }
            if (_vp == null) return;

            if (EditorApplication.timeSinceStartup > _deadline)
            {
                Record("timeout", "prepare/play 未在时限内完成");
                Teardown(); NextFormat(); return;
            }
            if (!_played)
            {
                if (!_vp.isPrepared) return;
                _vp.Play();
                _played = true;
                Debug.Log("[Probe] play");
                return;
            }
            if (_vp.frame < 10) return;

            AnalyzeCurrent();
            Teardown();
            NextFormat();
        }
        catch (Exception e)
        {
            Record("exception", e.Message);
            Debug.LogException(e);
            try { Teardown(); } catch { }
            NextFormat();
        }
    }

    static void NextFormat()
    {
        _fmtIdx++;
        if (_fmtIdx >= Formats.Length) { Finish(); return; }
        Debug.Log($"[Probe] === RT 格式 {FormatNames[_fmtIdx]} ===");

        var go = new GameObject("probe_" + FormatNames[_fmtIdx]);
        _vp = go.AddComponent<VideoPlayer>();
        _vp.playOnAwake = false;
        _vp.renderMode = VideoRenderMode.RenderTexture;
        _vp.audioOutputMode = VideoAudioOutputMode.None;
        _vp.isLooping = true;
        _vp.url = new Uri(_video).AbsoluteUri;
        _vp.errorReceived += (v, msg) => Debug.LogError("[Probe][vp] " + msg);
        _vp.prepareCompleted += v =>
        {
            int w = (int)v.width, h = (int)v.height;
            _rt = new RenderTexture(w, h, 0, Formats[_fmtIdx], RenderTextureReadWrite.Linear)
            {
                name = "probe_rt_" + FormatNames[_fmtIdx],
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _rt.Create();
            v.targetTexture = _rt;
            Debug.Log($"[Probe] prepared {w}x{h}");
        };
        _played = false;
        _deadline = EditorApplication.timeSinceStartup + PerFormatTimeout;
        _vp.Prepare();
    }

    static void AnalyzeCurrent()
    {
        if (_rt == null) { Record("error", "RT 为空"); return; }
        int W = _rt.width, H = _rt.height;
        int rw = W / 2;

        var prev = RenderTexture.active;
        RenderTexture.active = _rt;
        Texture2D tex = null, snap = null;
        try
        {
            tex = new Texture2D(rw, H, TextureFormat.RGBAFloat, false);
            tex.ReadPixels(new Rect(rw, 0, rw, H), 0, 0);
            tex.Apply();
            var px = tex.GetPixels();   // 行 0 = 视频最底行

            var res = new Dictionary<string, object>();
            res["format"] = FormatNames[_fmtIdx];
            res["rt_size"] = W + "x" + H;

            // ── 带纹: 视频 y -> 回读行 yR = H-1-y ──
            int nb = _meta.bands.n_bands, bp = _meta.bands.band_px, cs = _meta.bands.code_step;
            var meas = new double[nb];
            var errs = new double[nb];
            for (int b = 0; b < nb; b++)
            {
                int yR = H - 1 - (b * bp + bp / 2);
                var vals = new List<float>();
                for (int x = rw / 2 - 200; x < rw / 2 + 200; x++) vals.Add(px[yR * rw + x].r);
                vals.Sort();
                float med = vals[vals.Count / 2];
                meas[b] = System.Math.Round(med * 1023.0, 2);
                errs[b] = System.Math.Round(med * 1023.0 - b * cs, 2);
            }
            res["band_measured"] = meas;
            res["band_err_vs_design"] = errs;

            // limited range 症状: 设计码 64 的带若被 limited->full 展开, 会塌到 0 附近
            double err64 = System.Math.Abs(meas[2] - 2 * cs);
            double errMid = 0, errHigh = 0;
            for (int b = 4; b < nb; b++) errMid += System.Math.Abs(errs[b]);
            errMid /= (nb - 4);
            res["err_at_code64"] = err64;
            res["mean_abs_err"] = System.Math.Round(errMid, 2);

            // sRGB 检查: 线性映射 vs 伽马(1/2.2) 哪个更贴合带值
            double linRes = 0, gamRes = 0;
            for (int b = 1; b < nb; b++)
            {
                double expect = b * cs / 1023.0;
                double m = meas[b] / 1023.0;
                linRes += System.Math.Abs(m - expect);
                gamRes += System.Math.Abs(m - System.Math.Pow(expect, 1.0 / 2.2));
            }
            res["srgb_suspect"] = gamRes < linRes * 0.7;
            res["lin_resid"] = System.Math.Round(linRes / (nb - 1), 5);
            res["gam_resid"] = System.Math.Round(gamRes / (nb - 1), 5);

            // ── 棋盘: 相邻码 512/513 可分性 ──
            int yChk = H - 1 - (_meta.checker.y0 + (_meta.checker.y1 - _meta.checker.y0) / 2);
            var chkVals = new List<int>();
            for (int x = 0; x < rw; x += 2) chkVals.Add((int)System.Math.Round(px[yChk * rw + x].r * 1023.0));
            var chkSet = new HashSet<int>(chkVals);
            res["checker_distinct"] = new List<int>(chkSet);
            res["checker_10bit_ok"] = chkSet.Contains(512) && chkSet.Contains(513) && chkSet.Count == 2;

            // ── 渐变: 独立级数 + 最小步长 ──
            int yRmp = H - 1 - (_meta.ramp.y0 + (_meta.ramp.y1 - _meta.ramp.y0) / 2);
            int prevCode = -1, minStep = int.MaxValue, distinct = 0;
            var seen = new HashSet<int>();
            for (int x = 0; x < rw; x++)
            {
                int c = (int)System.Math.Round(px[yRmp * rw + x].r * 1023.0);
                if (seen.Add(c)) distinct++;
                if (prevCode >= 0 && c != prevCode)
                {
                    int st = System.Math.Abs(c - prevCode);
                    if (st < minStep) minStep = st;
                }
                prevCode = c;
            }
            res["ramp_distinct"] = distinct;
            res["ramp_min_step"] = minStep;
            res["effective_bits"] = minStep > 0 ? System.Math.Round(System.Math.Log(1023.0 / minStep, 2), 2) : 0;

            // ── 黑白端点 ──
            res["white_code"] = System.Math.Round(px[(H - 1 - (_meta.white.y0 + 2)) * rw + rw / 2].r * 1023.0, 1);
            res["black_code"] = System.Math.Round(px[(H - 1 - (_meta.black.y0 + 2)) * rw + rw / 2].r * 1023.0, 1);

            // ── 快照 PNG ──
            snap = new Texture2D(W, H, TextureFormat.RGBA32, false);
            snap.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            snap.Apply();
            File.WriteAllBytes(Path.Combine(_outDir, "probe_rt_" + FormatNames[_fmtIdx] + ".png"), snap.EncodeToPNG());

            _results.Add(res);
            Debug.Log($"[Probe] {FormatNames[_fmtIdx]}: ramp distinct={distinct} minStep={minStep} " +
                      $"effBits={res["effective_bits"]} checker={res["checker_10bit_ok"]} meanAbsErr={res["mean_abs_err"]}");
        }
        finally
        {
            RenderTexture.active = prev;
            if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            if (snap != null) UnityEngine.Object.DestroyImmediate(snap);
        }
    }

    static void Record(string status, string detail)
    {
        var res = new Dictionary<string, object>
        {
            ["format"] = _fmtIdx >= 0 && _fmtIdx < FormatNames.Length ? FormatNames[_fmtIdx] : "?",
            ["status"] = status,
            ["detail"] = detail
        };
        _results.Add(res);
        Debug.LogError($"[Probe] {res["format"]}: {status} {detail}");
    }

    static void Teardown()
    {
        if (_rt != null) { UnityEngine.Object.DestroyImmediate(_rt); _rt = null; }
        if (_vp != null) { UnityEngine.Object.DestroyImmediate(_vp.gameObject); _vp = null; }
    }

    static void Finish()
    {
        EditorApplication.update -= Tick;
        var sb = new StringBuilder();
        sb.Append("{\"results\": [");
        for (int i = 0; i < _results.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{");
            bool first = true;
            foreach (var kv in _results[i])
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("\"").Append(kv.Key).Append("\": ");
                sb.Append(JsonValue(kv.Value));
            }
            sb.Append("}");
        }
        sb.Append("]");
        sb.Append(", \"video\": \"").Append(_video.Replace("\\", "/")).Append("\"");
        sb.Append(", \"unity\": \"").Append(Application.unityVersion).Append("\"");
        sb.Append(", \"graphics\": \"").Append(SystemInfo.graphicsDeviceName).Append("\"");
        sb.Append("}");
        string path = Path.Combine(_outDir, "report.json");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Debug.Log($"[Probe] 报告已写入 {path}");
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    static string JsonValue(object v)
    {
        if (v == null) return "null";
        if (v is bool b) return b ? "true" : "false";
        if (v is string s) return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        if (v is System.Collections.IEnumerable en && !(v is string))
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var e in en) { if (!first) sb.Append(","); first = false; sb.Append(JsonValue(e)); }
            return sb.Append("]").ToString();
        }
        return v.ToString().Replace(",", ".");
    }

    static string FindArg(string[] args, string key, string def)
    {
        string prefix = "-" + key + "=";
        foreach (var a in args)
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return a.Substring(prefix.Length);
        return def;
    }
}
