using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 桌面键盘鼠标输入 (挂主相机), 只在播放器界面生效:
///   按住左键拖动 = 旋转视角, 左键单击 (未拖动) = 呼出/隐藏进度条,
///   WASD = 前后左右平移, Q/E = 升降, 空格 = 暂停/播放, R = 回到原点 (位置+朝向复位)
/// 真头显下视角由 TrackedPoseDriver 接管, 本脚本不干预 (PC 模拟 VR 时仍由本脚本驱动).
/// </summary>
public class DesktopController : MonoBehaviour
{
    public float moveSpeed = 2.0f;      // 平移速度 (米/秒)
    public float lookSpeed = 3.0f;      // 旋转灵敏度 (度/像素)
    public float dragThreshold = 6f;    // 位移超过该像素数才算"拖动", 否则算单击

    float _yaw, _pitch;
    bool _pressed;      // 左键处于按下状态 (且按下时不在 UI 上)
    bool _dragged;      // 本次按下已经产生拖动
    Vector3 _pressPos;

    void OnEnable()
    {
        SyncAnglesFromTransform();
    }

    /// <summary>以当前相机朝向作为旋转起点, 避免接管瞬间跳一下</summary>
    void SyncAnglesFromTransform()
    {
        var e = transform.eulerAngles;
        _pitch = e.x > 180f ? e.x - 360f : e.x;
        _yaw = e.y;
    }

    void Update()
    {
        var gp = Gray10Player.Instance;
        if (gp == null || !gp.InPlayer) return;             // 主界面用 uGUI 鼠标交互, 不动相机
        if (Gray10Player.VrActive && !Gray10Player.SimActive) return;   // 真头显: TPD 接管视角

        UpdateLook(gp);
        UpdateMove();
        if (Input.GetKeyDown(KeyCode.Space)) gp.TogglePlay();
        // 桌面模式 (非真头显/非模拟 VR) 按 R 回到原点; 模拟 VR 下 R 由 Gray10Player.SimVRInput 处理
        if (!Gray10Player.VrActive && Input.GetKeyDown(KeyCode.R)) ResetView();
    }

    /// <summary>相机回到世界原点: 位置清零 + 朝向复位</summary>
    void ResetView()
    {
        transform.position = Vector3.zero;
        _yaw = 0f;
        _pitch = 0f;
        transform.rotation = Quaternion.identity;
    }

    /// <summary>左键拖动旋转 + 单击切换进度条</summary>
    void UpdateLook(Gray10Player gp)
    {
        bool overUI = PointerOverUI();

        if (Input.GetMouseButtonDown(0))
        {
            _pressed = !overUI;          // 按在 UI 控件上时交给 uGUI, 不旋转也不切换
            _dragged = false;
            _pressPos = Input.mousePosition;
        }

        if (_pressed && Input.GetMouseButton(0))
        {
            if (!_dragged &&
                (Input.mousePosition - _pressPos).sqrMagnitude >= dragThreshold * dragThreshold)
                _dragged = true;

            if (_dragged)
            {
                _yaw += Input.GetAxis("Mouse X") * lookSpeed;
                _pitch -= Input.GetAxis("Mouse Y") * lookSpeed;
                _pitch = Mathf.Clamp(_pitch, -89f, 89f);
            }
        }

        if (_pressed && Input.GetMouseButtonUp(0))
        {
            // 单击 (没拖动过, 且松开时不在 UI 上) = 呼出/隐藏进度条
            if (!_dragged && !PointerOverUI()) gp.ToggleProgress();
            _pressed = false;
        }

        // 始终按内部 yaw/pitch 应用, 保证拖动过程中不会被别的逻辑带偏
        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    /// <summary>WASD 平移 (只按水平朝向) + Q/E 升降</summary>
    void UpdateMove()
    {
        float h = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        float v = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
        float u = (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f);
        if (h == 0f && v == 0f && u == 0f) return;

        float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? 4f : 1f) * Time.deltaTime;
        var flat = Quaternion.Euler(0f, _yaw, 0f) * new Vector3(h, 0f, v);   // 抬头时前进不往天上飞
        transform.position += (flat + Vector3.up * u) * speed;
    }

    /// <summary>鼠标是否悬在 uGUI 控件上 (悬停时把输入让给 UI)</summary>
    bool PointerOverUI()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }
}
