using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Settings;
using RZDirectReload.Client.Inputs;
using UnityEngine;
using UnityEngine.UI;

namespace RZDirectReload.Client.Visuals;

/// <summary>
/// 战局内轮盘覆盖层（sortingOrder 32000 的运行时 Canvas）：
///   · 环形扇区（自动 + 弹药种类）+ 高亮 + 选中外弧
///   · 每扇区：游戏图标 + 伤害/穿深 + 数量（图标池）
///   · 中心：悬停详情 / 当前选择 + 操作提示
///   · 轮盘下方：页点指示（当前页大 + 高亮）
/// 全部程序绘制 + 图标取自游戏官方图标系统，无资源包。
/// 显示门控：仅【战局内 + 活着 + 轮盘打开】时显示。
/// </summary>
internal sealed class AmmoWheelOverlay : IDisposable
{
    private const float WheelInnerRadius = 88f;
    private const float WheelOuterRadius = 168f;
    private const float LabelEdgeMarginX = 80f;
    private const float IconSize = 44f;

    /// <summary>“图标 + 描述”组合的锚点半径（环带中线；图标上提、文字在正下方，组合居中于扇区）。</summary>
    private const float ComboRadius = 128f;

    /// <summary>组合内：图标中心相对锚点的垂直偏移（上提）。</summary>
    private const float ComboIconLift = 16f;

    /// <summary>组合内：文字中心相对锚点的垂直偏移（落在图标正下方）。</summary>
    private const float ComboTextDrop = -23f;

    private Canvas? _canvas;
    private RectTransform? _canvasRect;
    private AmmoWheelGraphic? _graphic;
    private Font? _font;
    private bool? _shown;
    private bool _createFailed;
    private bool _updateErrorLogged;

    private readonly List<Text> _labels = new();
    private readonly List<string> _pendingTexts = new();
    private readonly List<Vector2> _pendingPositions = new();
    private readonly List<Color> _pendingColors = new();

    private readonly List<Image> _icons = new();
    private readonly List<Sprite?> _pendingSprites = new();
    private readonly List<Vector2> _pendingIconPositions = new();
    private readonly List<Color> _pendingIconColors = new();

    internal void Update()
    {
        if (!Ensure())
        {
            return;
        }

        bool show = AmmoWheelController.IsOpen && IsInRaidOperable();
        if (_shown != show)
        {
            _shown = show;
            Plugin.Log.LogInfo($"[DirectReload] wheel overlay {(show ? "shown" : "hidden")}");
            _canvas!.gameObject.SetActive(show);
        }

        if (!show)
        {
            return;
        }

        CursorGate.Maintain();

        try
        {
            _graphic!.Clear();
            _pendingTexts.Clear();
            _pendingPositions.Clear();
            _pendingColors.Clear();
            _pendingSprites.Clear();
            _pendingIconPositions.Clear();
            _pendingIconColors.Clear();

            DrawWheel();

            _graphic.Apply();
            FlushLabels();
            FlushIcons();
        }
        catch (Exception ex)
        {
            if (!_updateErrorLogged)
            {
                _updateErrorLogged = true;
                Plugin.Log.LogError($"[DirectReload] wheel overlay update failed: {ex}");
            }
        }
    }

    private static bool IsInRaidOperable()
    {
        try
        {
            var owner = GamePlayerOwner.MyPlayer;
            var main = Singleton<GameWorld>.Instance?.MainPlayer;
            if (owner == null || main == null || owner != main)
            {
                return false;
            }

            return owner.HealthController != null && owner.HealthController.IsAlive;
        }
        catch
        {
            return false;
        }
    }

    // ── 轮盘绘制 ────────────────────────────────────────────

    private void DrawWheel()
    {
        var slots = AmmoWheelController.Slots;
        int count = slots.Count;
        if (count == 0 || _canvasRect == null)
        {
            return;
        }

        float sector = 360f / count;

        for (int i = 0; i < count; i++)
        {
            var slot = slots[i];
            float start = 90f - i * sector;
            float end = 90f - (i + 1) * sector;
            bool hot = AmmoWheelController.HighlightedIndex == i;

            Color fill;
            if (slot.Unavailable)
            {
                fill = new Color(0.10f, 0.10f, 0.12f, 0.50f);
            }
            else if (hot)
            {
                fill = new Color(0.12f, 0.75f, 0.95f, 0.45f);
            }
            else
            {
                fill = new Color(0.07f, 0.09f, 0.13f, 0.66f);
            }

            _graphic!.AddSector(start, end, WheelInnerRadius, WheelOuterRadius, fill);

            if (slot.Selected)
            {
                _graphic.AddSector(
                    start, end,
                    WheelOuterRadius + 3f, WheelOuterRadius + 7f,
                    new Color(0.15f, 0.9f, 1f, 0.85f));
            }

            float mid = (start + end) * 0.5f;
            float rad = mid * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

            // 图标（自动格无图标）——组合上半：锚点上提
            if (!slot.IsAuto && slot.Ammo != null)
            {
                var sprite = slot.Ammo.Icon?.Sprite;
                var pos = dir * ComboRadius + new Vector2(0f, ComboIconLift);
                var tint = slot.Unavailable
                    ? new Color(1f, 1f, 1f, 0.30f)
                    : Color.white;

                if (sprite != null)
                {
                    _pendingSprites.Add(sprite);
                    _pendingIconPositions.Add(pos);
                    _pendingIconColors.Add(tint);
                }
                else
                {
                    // 占位框
                    _graphic.AddQuad(
                        new Rect(pos.x - IconSize * 0.5f, pos.y - IconSize * 0.5f, IconSize, IconSize),
                        new Color(0.25f, 0.30f, 0.36f, slot.Unavailable ? 0.15f : 0.35f));
                }
            }

            // 文本
            string text;
            if (slot.IsAuto)
            {
                text = Localize("自动\nAUTO", "AUTO\n(by penetration)");
            }
            else if (slot.Ammo != null)
            {
                text = $"{Localize("伤", "dmg")}{slot.Ammo.Damage}·{Localize("穿", "pen")}{slot.Ammo.Pen}\n×{slot.Ammo.Count}";
            }
            else
            {
                text = "—";
            }

            var color = slot.Unavailable
                ? new Color(0.55f, 0.55f, 0.58f, 0.9f)
                : hot ? Color.white : new Color(0.85f, 0.88f, 0.92f, 0.88f);

            _pendingTexts.Add(text);
            _pendingPositions.Add(dir * ComboRadius + new Vector2(0f, ComboTextDrop));
            _pendingColors.Add(color);
        }

        // 中心圆（信息区底）
        _graphic.AddCircle(Vector2.zero, WheelInnerRadius - 6f, new Color(0.05f, 0.06f, 0.08f, 0.88f));

        // 中心信息（悬停详情 / 当前选择）+ 提示
        string centerText;
        var highlighted = AmmoWheelController.HighlightedIndex;
        if (highlighted >= 0 && highlighted < slots.Count)
        {
            var slot = slots[highlighted];
            if (slot.IsAuto)
            {
                centerText = Localize("自动（穿深最高优先）\n松开或点击 = 选定", "AUTO (highest penetration first)\nRelease or click to select");
            }
            else if (slot.Ammo != null)
            {
                centerText = slot.Unavailable
                    ? Localize($"{slot.Ammo.Name}\n无弹药，不可选", $"{slot.Ammo.Name}\nno ammo — unavailable")
                    : Localize(
                        $"{slot.Ammo.Name}\n伤{slot.Ammo.Damage}·穿{slot.Ammo.Pen} ×{slot.Ammo.Count}\n松开或点击 = 选定",
                        $"{slot.Ammo.Name}\ndmg{slot.Ammo.Damage}·pen{slot.Ammo.Pen} ×{slot.Ammo.Count}\nRelease or click to select");
            }
            else
            {
                centerText = string.Empty;
            }
        }
        else
        {
            var selection = AmmoSelectionState.Current;
            string current = selection.Mode == AmmoSelectionMode.Auto
                ? Localize("自动", "AUTO")
                : (_currentSelectionName() ?? Localize("已选弹药", "custom ammo"));
            centerText = Localize($"当前：{current}\n移动鼠标选择弹药", $"Current: {current}\nMove to select");
        }

        _pendingTexts.Add(centerText);
        _pendingPositions.Add(Vector2.zero);
        _pendingColors.Add(new Color(0.92f, 0.95f, 1f, 0.95f));

        // 页点（轮盘下方）
        int pageCount = AmmoWheelController.PageCount;
        if (pageCount > 1)
        {
            int page = AmmoWheelController.Page;
            float baseY = -(WheelOuterRadius + 26f);
            for (int p = 0; p < pageCount; p++)
            {
                float x = (p - (pageCount - 1) * 0.5f) * 18f;
                if (p == page)
                {
                    _graphic.AddCircle(new Vector2(x, baseY), 5.5f, new Color(0.15f, 0.9f, 1f, 0.95f));
                }
                else
                {
                    _graphic.AddCircle(new Vector2(x, baseY), 3f, new Color(0.5f, 0.55f, 0.6f, 0.7f));
                }
            }
        }
    }

    private static string? _currentSelectionName()
    {
        try
        {
            var info = AmmoWheelController.CurrentSelectionInfo;
            if (info != null)
            {
                return info.Name;
            }

            var last = AmmoSelectionState.Current.LastUsed;
            var selection = AmmoSelectionState.Current;
            if (last != null && selection.TemplateId == last.TemplateId)
            {
                return last.Name;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string Localize(string zh, string en)
    {
        try
        {
            var language = Singleton<SettingsManager>.Instance?.Game?.Settings?.Language?.Value;
            return string.Equals(language, "ch", StringComparison.OrdinalIgnoreCase) ? zh : en;
        }
        catch
        {
            return en;
        }
    }

    // ── 标签池 ──────────────────────────────────────────────

    private void FlushLabels()
    {
        if (_font == null)
        {
            _font = LoadFont();
        }

        while (_labels.Count < _pendingTexts.Count)
        {
            _labels.Add(CreateLabel());
        }

        for (int i = 0; i < _labels.Count; i++)
        {
            var label = _labels[i];
            bool used = i < _pendingTexts.Count;

            if (label.gameObject.activeSelf != used)
            {
                label.gameObject.SetActive(used);
            }

            if (!used)
            {
                continue;
            }

            if (label.text != _pendingTexts[i])
            {
                label.text = _pendingTexts[i];
            }

            label.color = _pendingColors[i];

            if (label.font != _font)
            {
                label.font = _font;
            }

            var rect = (RectTransform)label.transform;
            var position = _pendingPositions[i];
            if (_canvasRect != null)
            {
                var bounds = _canvasRect.rect;
                position.x = Mathf.Clamp(position.x, bounds.xMin + LabelEdgeMarginX, bounds.xMax - LabelEdgeMarginX);
                position.y = Mathf.Clamp(position.y, bounds.yMin + 14f, bounds.yMax - 14f);
            }

            rect.anchoredPosition = position;
        }
    }

    private Text CreateLabel()
    {
        var go = new GameObject("RZDirectReload Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Outline));
        go.transform.SetParent(_canvasRect, false);

        var label = go.GetComponent<Text>();
        label.font = _font;
        label.fontSize = 12;
        label.alignment = TextAnchor.MiddleCenter;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.raycastTarget = false;

        var outline = go.GetComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.95f);
        outline.effectDistance = new Vector2(1f, -1f);

        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(300f, 64f);
        return label;
    }

    // ── 图标池 ──────────────────────────────────────────────

    private void FlushIcons()
    {
        while (_icons.Count < _pendingSprites.Count)
        {
            _icons.Add(CreateIcon());
        }

        for (int i = 0; i < _icons.Count; i++)
        {
            var icon = _icons[i];
            bool used = i < _pendingSprites.Count;

            if (icon.gameObject.activeSelf != used)
            {
                icon.gameObject.SetActive(used);
            }

            if (!used)
            {
                continue;
            }

            if (icon.sprite != _pendingSprites[i])
            {
                icon.sprite = _pendingSprites[i];
            }

            icon.color = _pendingIconColors[i];

            var rect = (RectTransform)icon.transform;
            var position = _pendingIconPositions[i];
            if (_canvasRect != null)
            {
                var bounds = _canvasRect.rect;
                position.x = Mathf.Clamp(position.x, bounds.xMin + 30f, bounds.xMax - 30f);
                position.y = Mathf.Clamp(position.y, bounds.yMin + 30f, bounds.yMax - 30f);
            }

            rect.anchoredPosition = position;
        }
    }

    private Image CreateIcon()
    {
        var go = new GameObject("RZDirectReload Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(_canvasRect, false);

        var image = go.GetComponent<Image>();
        image.raycastTarget = false;
        image.preserveAspect = true;

        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(IconSize, IconSize);
        return image;
    }

    // ── 基建 ────────────────────────────────────────────────

    private bool Ensure()
    {
        if (_createFailed)
        {
            return false;
        }

        if (_canvas != null && _graphic != null)
        {
            return true;
        }

        // 上一场战局遗留：场景卸载会销毁 canvas 及其全部子物体，池里的 Text/Image
        // 全成了已销毁对象（访问即 MissingReferenceException → 标签/图标整体不渲染，
        // 只剩空轮盘）。重建时清空池 + 重置显示状态，子物体随新 canvas 全部重建。
        _labels.Clear();
        _icons.Clear();
        _shown = null;

        try
        {
            var canvasGo = new GameObject("RZDirectReload Overlay", typeof(RectTransform), typeof(Canvas));
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32000;
            _canvasRect = (RectTransform)canvasGo.transform;

            var meshGo = new GameObject("Wheel Mesh", typeof(RectTransform), typeof(CanvasRenderer), typeof(AmmoWheelGraphic));
            meshGo.transform.SetParent(_canvasRect, false);
            var meshRect = (RectTransform)meshGo.transform;
            meshRect.anchorMin = Vector2.zero;
            meshRect.anchorMax = Vector2.one;
            meshRect.offsetMin = Vector2.zero;
            meshRect.offsetMax = Vector2.zero;

            _graphic = meshGo.GetComponent<AmmoWheelGraphic>();
            _graphic.raycastTarget = false;

            _font = LoadFont();
            Plugin.Log.LogInfo("[DirectReload] wheel overlay created");
            return true;
        }
        catch (Exception ex)
        {
            _createFailed = true; // 不再每帧重试（防 GC/日志风暴）
            Plugin.Log.LogError($"[DirectReload] wheel overlay init failed: {ex}");
            return false;
        }
    }

    private static Font? LoadFont()
    {
        try
        {
            var font = Font.CreateDynamicFontFromOSFont("Segoe UI", 13);
            if (font != null)
            {
                return font;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_canvas != null)
            {
                UnityEngine.Object.Destroy(_canvas.gameObject);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[DirectReload] wheel overlay dispose failed: {ex.Message}");
        }

        _canvas = null;
        _canvasRect = null;
        _graphic = null;
        _labels.Clear();
        _icons.Clear();
    }
}
