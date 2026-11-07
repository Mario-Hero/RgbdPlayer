# RgbdPlayer

Unity 6 深度视频播放器：使用半球壳网格 + 顶点位移播放 RgbdExtractor 导出的深度视频。支持PC桌面运行以及串流头显（SteamVR）.
Unity 6 Depth Video Player: Utilizes hemispherical mesh + vertex displacement to play depth videos exported by RgbdExtractor. Supports PC desktop operation and streaming head-mounted displays (SteamVR)

**深度视频的生成工具见  [RgbdExtractor](https://github.com/Mario-Hero/RgbdExtractor)。**
**For the depth extraction toolkit see [RgbdExtractor](https://github.com/Mario-Hero/RgbdExtractor).**

## 这是一个失败的尝试 This is a failed attempt

在观看VR180 3D视频的时候，相信大家都注意到了，如果头部在roll方向旋转（也就是歪头），近处的画面会发生重叠，导致观感下降。并且VR180是不能前后左右移动的，也就是只有3dof，没有6dof。所以为了进一步提高沉浸感，最近开始流行一种4DGS的视频，可以在空间中自由移动，也可以随意歪头。

但是这种视频需要非常多相机才能拍摄，普通人根本没法拍。如果能把VR180 3D视频转成4DGS那就好了。从双目图像获取深度的研究已经很成熟了（甚至从2d图像获取深度也有了），单目转4dgs的也有，但是双目转4dgs的确实没有。

所以我就退而求其次，用了比较经典的球壳+凹凸的办法播放RGB+深度画面。双目深度获取模型选的2025年的Stereo Any Video，这个时域稳定性比较好。2026年的c-fast-foundationstereo速度挺快的，但是时域不稳定。Stereo Any Video要输入许多帧用于滑动窗口，会导致显存占用过大，目前我用的是640x640分辨率，20帧滑动窗口，12G显存够用。

播放效果如何呢？我只能说稍微有点用，头部可以大幅旋转、轻微移动，并且始终有立体感。如果头移动太远，深度之间的“悬崖”会拉丝得非常严重，影响观感。

深度图的存储，我是用了上下限+对数+10bit HEVC 视频编码，保证近距离精度能到半厘米。但是我感觉模型好像本身也没有这么高精度……

而且这种方式，把双目视频转换成了左边单目+深度，那右边的RGB信息不就丢失了？假如左眼对齐原来的左眼视角，那右眼观看的画面必定会偏移，产生拉丝。

我又尝试左右颠倒，再获取右眼的深度，合并成一个视频。右眼模糊的问题是解决了，但是两个眼睛看到的深度没有对齐，不移动头就没问题，一旦移动头部，就会轻微感觉左右眼不重叠并且出现拉丝。

**总之，该项目的无论单目还是双目深度，跟原本的VR180版本相比，是劣化，稍微移动头部就会感觉近处画面拉丝。**

When watching VR180 3D videos, I believe everyone has noticed that if the head rotates in the roll direction (i.e., tilting the head), the nearby images will overlap, resulting in a diminished viewing experience. Moreover, VR180 does not allow for movement in the forward, backward, left, and right directions, meaning it only has 3 degrees of freedom (3dof) instead of 6 degrees of freedom (6dof). Therefore, to further enhance the immersion experience, a type of 4DGS video has recently become popular, allowing for free movement in space and the ability to tilt the head as desired.

However, such videos require a large number of cameras to shoot, which is simply beyond the reach of ordinary people. It would be great if VR180 3D videos could be converted into 4DGS. Research on obtaining depth from binocular images has already matured (even depth obtained from 2D images has been achieved), and there are also methods for converting monocular images into 4DGS, but there is indeed no method for converting binocular images into 4DGS.

So I settled for the next best thing and used a classic method of combining a spherical shell with bump mapping to play back RGB+depth images. The binocular depth acquisition model I chose is Stereo Any Video from 2025, which has good temporal stability. The c-fast-foundationstereo from 2026 is quite fast, but it lacks temporal stability. Stereo Any Video requires inputting many frames for sliding window purposes, which can lead to excessive memory usage. Currently, I am using a resolution of 640x640, with a sliding window of 20 frames, and 12GB of VRAM is sufficient.

How about the playback effect? I can only say it's somewhat useful. The head can rotate significantly and move slightly, maintaining a three-dimensional sensation throughout. However, if the head moves too far, the "cliff" between depths will become severely distorted, affecting the viewing experience.

For the storage of the depth map, I used an upper and lower limit, logarithmic scaling, and 10-bit HEVC video encoding to ensure accuracy down to half a centimeter at close range. However, it seems to me that the model itself may not have such high accuracy

Moreover, this method converts the binocular video into left monocular + depth, doesn't that mean the RGB information on the right side will be lost? If the left eye aligns with the original left eye view, the image viewed by the right eye will inevitably be shifted, resulting in streaking.

I tried flipping the left and right images, then obtaining the depth of the right eye, and merging them into one video. The issue of blurring in the right eye was resolved, but the depths seen by the two eyes were not aligned. If I didn't move my head, everything was fine, but once I moved it, I could slightly feel that the left and right eyes did not overlap and there was a pulling sensation.

**In summary, whether it's monocular or binocular depth, compared to the original VR180 version, it's a downgrade. Even a slight movement of the head can cause the near image to appear blurry.**

## 运行依赖 Runtime Dependency

安装 ffmpeg.      ffmpeg Installed.

Nvidia显卡，用于H265硬件解码.

Nvidia Graphics card for H265 hardware decoding

## 输入文件 File Inputs

json 的一些关键参数如下:

Some key parameters of JSON are as follows:

| Key          | Value                                                        |
| ------------ | ------------------------------------------------------------ |
| near/far     | 近处和远处的范围，单位为米。The range of near and far, measured in meters. |
| color_file   | RGB video                                                    |
| depth_file   | Depth video                                                  |
| color_layout | mono: 单目视频; sbs_left: SBS视频，实际读取时只读取左侧，但是会增加解码压力<br/>mono: monocular video / sbs_left: SBS video, only the left side is actually read, but it will increase decoding pressure |
| encode       | linear： 线性。全深度误差一致。  log: 对数。近处误差较小。<br/>Linear: Consistent error across all depths. Log: Logarithmic. Smaller error in the near range. |
| bits         | 8 / 10 / 12                                                  |

## 操作 Controls

**桌面 Desktop**

- 按住左键拖动：转头；左键单击：呼出 / 隐藏进度条

  Hold LMB and drag to look; a click without dragging toggles the progress bar

- `WASD` 平移，`Q/E` 升降，`R` 回到原点；播放中按 `Esc` 回主界面，

  `WASD` translates, `Q/E` moves down/up, `R` returns to the origin; `Esc` leaves playback for the home screen.

**VR（串流）VR (streamed)**

- 摇杆左右：播放进度；上下：音量

  Stick left/right scrubs, up/down adjusts volume

- 摇杆单按：重置朝向；长按：重置头部坐标

  Stick click recentres yaw; long press reset the head origin

- `A` 暂停，`B` 退出播放，Trigger 呼出 / 隐藏进度条（指向按钮时即为点击）

  `A` pauses, `B` exits playback, the trigger toggles the progress bar (and clicks buttons when pointing at them)

## 性能 Performance
- 颜色与深度双流独立解码，常见的解码组合是解码VR 8K 8bit H265 + 512x512 10bit H265视频。如果遇到卡顿，先调播放设置，把segments调低，tessLength调高来降低网格数量，从而降低GPU压力。如果帧率仍然不到90FPS，应该就是解码问题，请用[RgbdExtractor](https://github.com/Mario-Hero/RgbdExtractor)的`mono8_drop.py` 把8192x4096视频转换成4096x4096来降低压力。并把json文件的"color_layout"改成"mono", color_file改成对应文件。

  Color and depth streams are decoded independently. A common decoding combination is decoding VR 8K 8bit H265 + 512x512 10bit H265 videos. If you encounter stuttering, first adjust the playback settings by reducing the segments and increasing the tessLength to decrease the number of meshes, thereby reducing GPU pressure. If the frame rate is still below 90FPS, it is likely a decoding issue. Please use the `mono8_drop.py` script from [RgbdExtractor](https://github.com/Mario-Hero/RgbdExtractor) to convert the 8192x4096 video to 4096x4096 to reduce pressure. Also, change the "color_layout" in the json file to "mono" and the "color_file" to the corresponding file.

## 许可 License
MIT License
