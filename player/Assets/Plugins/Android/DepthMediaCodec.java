package com.stereoanyvideo.depth;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaExtractor;
import android.media.MediaFormat;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Android MediaCodec HEVC 深度解码器 (8/10/12 bit).
 *
 * 解码流程: MediaExtractor 选轨 -> MediaCodec 异步解码 -> ImageReader(YUV_420_888) 取 Y 平面.
 * Y 平面即深度码值: 8bit=gray(pixelStride=1), 10/12bit=P010/P012(pixelStride=2, 码值左对齐).
 *
 * 输出: 直接 ByteBuffer (little-endian ushort), 码值 0..MaxCode 原样, 与 Windows ffmpeg 管道一致.
 * C# 侧通过 AndroidJNI.FromDirectByteBuffer 拿指针直接 memcpy 到 R16 纹理.
 *
 * 线程: codec callback 在 HandlerThread; ImageReader callback 同线程;
 *       decodeNextFrame() 由 C# 后台线程调用, 同步等输出图.
 */
public class DepthMediaCodec
{
    private static final String TAG = "DepthMediaCodec";

    private MediaExtractor  extractor;
    private MediaCodec      codec;
    private ImageReader     imageReader;
    private HandlerThread   handlerThread;
    private Handler         handler;

    private int     width       = 0;
    private int     height      = 0;
    private int     bitDepth    = 8;
    private long    frameCount  = 0;
    private double  fps         = 30.0;
    private int     videoTrack  = -1;

    private ByteBuffer  yBuffer;
    private int         yBufferSize = 0;
    private byte[]      yBytes;          // C# 读取用: 从 direct yBuffer 拷出来的 byte[], 避免 JNI address 问题

    private final Object    imageLock    = new Object();
    private Image           pendingImage = null;
    private volatile boolean outputEof   = false;
    private volatile boolean released    = false;

    /** 打开深度视频, 双轨容器自动选最后一条视频轨(深度轨). */
    public boolean open(String path)
    {
        try
        {
            extractor = new MediaExtractor();
            extractor.setDataSource(path);

            int tracks = extractor.getTrackCount();
            for (int i = 0; i < tracks; i++)
            {
                MediaFormat f = extractor.getTrackFormat(i);
                String mime = f.getString(MediaFormat.KEY_MIME);
                if (mime != null && mime.startsWith("video/"))
                    videoTrack = i;
            }
            if (videoTrack < 0) { Log.e(TAG, "no video track"); return false; }

            MediaFormat format = extractor.getTrackFormat(videoTrack);
            extractor.selectTrack(videoTrack);

            width  = format.getInteger(MediaFormat.KEY_WIDTH);
            height = format.getInteger(MediaFormat.KEY_HEIGHT);
            if (format.containsKey(MediaFormat.KEY_FRAME_RATE))
                fps = format.getInteger(MediaFormat.KEY_FRAME_RATE);
            // MediaExtractor/MediaFormat 均无 frame count API, frameCount 保持默认 0 (未知), C# 侧按 0 处理

            handlerThread = new HandlerThread("DepthCodec");
            handlerThread.start();
            handler = new Handler(handlerThread.getLooper());

            imageReader = ImageReader.newInstance(width, height,
                android.graphics.ImageFormat.YUV_420_888, 4);
            imageReader.setOnImageAvailableListener(reader -> {
                synchronized (imageLock)
                {
                    if (pendingImage != null) pendingImage.close();
                    pendingImage = reader.acquireLatestImage();
                    Log.d(TAG, "ImageReader callback: got " + (pendingImage != null ? "image" : "null") + ", w=" + width + " h=" + height);
                    imageLock.notifyAll();
                }
            }, handler);

            String mime = format.getString(MediaFormat.KEY_MIME);
            Log.d(TAG, "open: mime=" + mime + " w=" + width + " h=" + height + " fps=" + fps + " videoTrack=" + videoTrack);
            codec = MediaCodec.createDecoderByType(mime);
            Log.d(TAG, "open: codec=" + (codec != null ? codec.getName() : "null"));
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible);

            // 异步回调模式: 输入喂数据, 输出检测 EOS + 渲染到 Surface(ImageReader)
            codec.setCallback(new MediaCodec.Callback()
            {
                @Override public void onInputBufferAvailable(MediaCodec mc, int index)
                {
                    if (released) return;
                    try
                    {
                        ByteBuffer buf = mc.getInputBuffer(index);
                        int size = extractor.readSampleData(buf, 0);
                        if (size < 0)
                        {
                            mc.queueInputBuffer(index, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                        }
                        else
                        {
                            long pts = extractor.getSampleTime();
                            mc.queueInputBuffer(index, 0, size, pts, 0);
                            extractor.advance();
                        }
                    }
                    catch (Exception e) { Log.w(TAG, "input err: " + e.getMessage()); }
                }

                @Override public void onOutputBufferAvailable(MediaCodec mc, int index, MediaCodec.BufferInfo info)
                {
                    if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0)
                    {
                        Log.d(TAG, "onOutputBufferAvailable: EOS");
                        signalOutputEof();
                    }
                    else
                    {
                        if (frameCount < 3) Log.d(TAG, "onOutputBufferAvailable: idx=" + index + " size=" + info.size + " pts=" + info.presentationTimeUs);
                        frameCount++;
                    }
                    mc.releaseOutputBuffer(index, true);   // true=渲染到 ImageReader Surface
                }

                @Override public void onOutputFormatChanged(MediaCodec mc, MediaFormat format) {}
                @Override public void onError(MediaCodec mc, MediaCodec.CodecException e)
                {
                    Log.e(TAG, "codec error: " + e.getMessage());
                    signalOutputEof();
                }
            }, handler);

            codec.configure(format, imageReader.getSurface(), null, 0);
            codec.start();

            yBufferSize = width * height * 2;
            yBuffer = ByteBuffer.allocateDirect(yBufferSize);
            yBuffer.order(ByteOrder.LITTLE_ENDIAN);

            Log.i(TAG, String.format("open ok %dx%d %.1ffps frames=%d", width, height, fps, frameCount));
            return true;
        }
        catch (Exception e)
        {
            Log.e(TAG, "open failed: " + e.getMessage(), e);
            release();
            return false;
        }
    }

    // ---- 属性 ----
    public int    getWidth()      { return width; }
    public int    getHeight()     { return height; }
    public int    getBitDepth()   { return bitDepth; }
    public long   getFrameCount() { return frameCount; }
    public double getFps()        { return fps; }
    public ByteBuffer getYBuffer(){ return yBuffer; }

    /** 返回 yBuffer 内容的 byte[] (给 C# 做 BlockCopy 到 ushort[]). 每次 decodeNextFrame 后调用. */
    public byte[] getYBufferBytes()
    {
        if (yBuffer == null) return null;
        if (yBytes == null || yBytes.length != yBufferSize) yBytes = new byte[yBufferSize];
        // yBuffer 在 extractYPlane 里已经 flip() 过, position=0, limit=capacity=yBufferSize
        int pos = yBuffer.position();
        int lim = yBuffer.limit();
        yBuffer.position(0).limit(yBufferSize);
        yBuffer.get(yBytes, 0, yBufferSize);
        yBuffer.position(pos).limit(lim);   // 还原
        return yBytes;
    }

    /** 解码下一帧到 yBuffer. true=成功, false=EOF. 由 C# 后台线程循环调用. */
    public boolean decodeNextFrame()
    {
        if (released) return false;

        while (!released)
        {
            synchronized (imageLock)
            {
                if (pendingImage != null)
                {
                    Image img = pendingImage;
                    pendingImage = null;
                    extractYPlane(img);
                    img.close();
                    return true;
                }
                if (outputEof) return false;
                try { imageLock.wait(50); } catch (InterruptedException ignored) {}
            }
        }
        return false;
    }

    /** 从 Image 提取 Y 平面 -> yBuffer (ushort LE 码值). 首帧探测位深. */
    private void extractYPlane(Image img)
    {
        Image.Plane[] planes = img.getPlanes();
        if (planes == null || planes.length == 0) return;

        Image.Plane yPlane = planes[0];
        ByteBuffer src = yPlane.getBuffer();
        src.order(ByteOrder.LITTLE_ENDIAN);   // P010/P012 为 little-endian, 显式设置避免平台默认值差异
        int pixelStride = yPlane.getPixelStride();   // 1=8bit, 2=10/12bit
        int rowStride   = yPlane.getRowStride();

        yBuffer.clear();

        if (pixelStride == 1)
        {
            bitDepth = 8;
            for (int y = 0; y < height; y++)
            {
                int rowOff = y * rowStride;
                for (int x = 0; x < width; x++)
                    yBuffer.putShort((short)(src.get(rowOff + x) & 0xFF));
            }
        }
        else
        {
            if (bitDepth == 8) bitDepth = 10;   // 先假定 10, 首帧校准

            // 10bit 低 6 位全 0, 12bit 低 4 位全 0
            if (bitDepth == 10)
            {
                int sampled = 0;
                boolean low6zero = true;
                for (int y = 0; y < height && sampled < 256; y += 4)
                {
                    int rowOff = y * rowStride;
                    for (int x = 0; x < width && sampled < 256; x += 16)
                    {
                        int raw = src.getShort(rowOff + x * 2) & 0xFFFF;
                        if ((raw & 0x3F) != 0) { low6zero = false; break; }
                        sampled++;
                    }
                    if (!low6zero) break;
                }
                if (!low6zero && sampled > 0) bitDepth = 12;
            }

            int shift = 16 - bitDepth;
            for (int y = 0; y < height; y++)
            {
                int rowOff = y * rowStride;
                for (int x = 0; x < width; x++)
                {
                    int raw = src.getShort(rowOff + x * 2) & 0xFFFF;
                    yBuffer.putShort((short)((raw >> shift) & 0xFFFF));
                }
            }
        }
        yBuffer.flip();
    }

    /** seek 到指定帧 (按 fps 换算). 清空 codec 缓冲. */
    public void seekTo(long frameIndex)
    {
        if (extractor == null || released) return;
        try
        {
            long timeUs = (long)(frameIndex * 1_000_000.0 / Math.max(fps, 1.0));
            extractor.seekTo(timeUs, MediaExtractor.SEEK_TO_PREVIOUS_SYNC);
            codec.flush();
            synchronized (imageLock)
            {
                if (pendingImage != null) { pendingImage.close(); pendingImage = null; }
                outputEof = false;
            }
        }
        catch (Exception e) { Log.w(TAG, "seekTo failed: " + e.getMessage()); }
    }

    private void signalOutputEof()
    {
        synchronized (imageLock) { outputEof = true; imageLock.notifyAll(); }
    }

    public void release()
    {
        released = true;
        synchronized (imageLock)
        {
            if (pendingImage != null) { pendingImage.close(); pendingImage = null; }
            imageLock.notifyAll();
        }
        try { if (codec != null) { codec.stop(); codec.release(); } } catch (Exception ignored) {}
        try { if (imageReader != null) imageReader.close(); } catch (Exception ignored) {}
        try { if (extractor != null) extractor.release(); } catch (Exception ignored) {}
        try { if (handlerThread != null) handlerThread.quitSafely(); } catch (Exception ignored) {}
        codec = null; imageReader = null; extractor = null; handlerThread = null;
        Log.i(TAG, "released");
    }
}
