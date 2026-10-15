using UnityEngine;
using UnityEngine.EventSystems;

namespace RZDirectReload.Client.Inputs;

/// <summary>
/// 轮盘期间的光标 / UI 管理（与技能轮盘同款做法 + 每帧重断言）：
///  · 光标解锁 + 可见 + 软件光标纹理 —— 游戏会持续把光标锁回去，必须每帧 Maintain() 反复压回；
///  · 临时禁用游戏 EventSystem，防止点选被游戏 UI 抢走；
///  · 关闭时逐项恢复原状。
/// </summary>
internal static class CursorGate
{
    private static bool _active;
    private static CursorLockMode _prevLock;
    private static bool _prevVisible;
    private static EventSystem? _blockedEventSystem;
    private static bool _blockedWasEnabled;
    private static Texture2D? _cursorTexture;
    private static bool _cursorApplied;

    internal static void Enter()
    {
        if (_active)
        {
            return;
        }

        _active = true;

        try
        {
            _prevLock = Cursor.lockState;
            _prevVisible = Cursor.visible;
        }
        catch
        {
            // ignore
        }

        Maintain();
    }

    /// <summary>每帧调用（轮盘打开期间）：把被游戏锁回去的光标重新压回自由态。</summary>
    internal static void Maintain()
    {
        if (!_active)
        {
            return;
        }

        try
        {
            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
            }

            if (!Cursor.visible)
            {
                Cursor.visible = true;
            }

            if (_cursorTexture == null)
            {
                _cursorTexture = BuildCursorTexture();
            }

            if (_cursorTexture != null && !_cursorApplied)
            {
                Cursor.SetCursor(_cursorTexture, Vector2.zero, CursorMode.ForceSoftware);
                _cursorApplied = true;
            }

            var es = EventSystem.current;
            if (es != null && es.enabled)
            {
                if (_blockedEventSystem == null)
                {
                    _blockedEventSystem = es;
                    _blockedWasEnabled = true;
                }

                es.enabled = false;
            }
        }
        catch
        {
            // fail-open: 光标操作失败不阻塞轮盘逻辑
        }
    }

    internal static void Exit()
    {
        if (!_active)
        {
            return;
        }

        _active = false;

        try
        {
            if (_blockedEventSystem != null)
            {
                _blockedEventSystem.enabled = _blockedWasEnabled;
            }

            _blockedEventSystem = null;

            if (_cursorApplied)
            {
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
                _cursorApplied = false;
            }

            Cursor.lockState = _prevLock;
            Cursor.visible = _prevVisible;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[DirectReload] cursor exit failed: {ex.Message}");
        }
    }

    /// <summary>程序生成准星式软件光标（环 + 十字 + 中心点）。</summary>
    private static Texture2D? BuildCursorTexture()
    {
        try
        {
            const int size = 16;
            const float center = (size - 1) * 0.5f;
            var pixels = new Color32[size * size];
            var clear = new Color32(0, 0, 0, 0);
            var bright = new Color32(90, 220, 255, 255);
            var dim = new Color32(18, 22, 28, 200);

            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = clear;
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d <= 6.4f)
                    {
                        bool ring = d > 4.6f;
                        bool cross = (Mathf.Abs(dx) <= 0.8f && d <= 5.6f) ||
                                     (Mathf.Abs(dy) <= 0.8f && d <= 5.6f);
                        pixels[y * size + x] = (ring || cross) ? bright : dim;
                    }
                }
            }

            for (int y = (int)center - 1; y <= (int)center + 1; y++)
            {
                for (int x = (int)center - 1; x <= (int)center + 1; x++)
                {
                    if (x >= 0 && x < size && y >= 0 && y < size)
                    {
                        pixels[y * size + x] = bright;
                    }
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "RZDirectReload Cursor",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }
        catch
        {
            return null;
        }
    }
}
