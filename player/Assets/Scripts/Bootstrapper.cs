using UnityEngine;

/// <summary>
/// 运行时自动搭建: 场景里不需要预先摆放任何东西,
/// 进入 Play 模式后自动创建 Gray10Player (VideoPlayer + 球壳 + 控制器)
/// </summary>
public static class Bootstrapper
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        if (Gray10Player.Instance != null) return;
        new GameObject("VR180Player").AddComponent<Gray10Player>();
    }
}
