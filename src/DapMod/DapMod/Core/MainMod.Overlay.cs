using UnityEngine;

namespace DapMod.Core;

public partial class MainMod
{
    private void EnsureOverlayTextures()
    {
        _whiteTex ??= MakeSolidTex(new Color(0.93f, 0.95f, 0.98f, 1f));
        _blackTex ??= MakeSolidTex(new Color(0.05f, 0.07f, 0.10f, 0.78f));
        _cursorTex ??= MakeSolidTex(new Color(0.70f, 0.93f, 1f, 1f));
        _perfectTex ??= MakeSolidTex(new Color(0.60f, 1f, 0.82f, 0.92f));
        _goodTex ??= MakeSolidTex(new Color(1f, 0.86f, 0.58f, 0.80f));
        _panelTex ??= MakeSolidTex(new Color(0.03f, 0.04f, 0.06f, 0.30f));
        _overlayPanelStyle ??= MakePanelStyle(new Color(0.05f, 0.07f, 0.10f, 0.92f));
        _overlayAreaStyle ??= MakePanelStyle(new Color(0.03f, 0.05f, 0.08f, 0.98f));
        _overlayOutlineStyle ??= MakePanelStyle(new Color(0.90f, 0.93f, 0.97f, 0.95f));
        _overlayTitleStyle ??= MakeLabelStyle(24, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
        _overlayHintStyle ??= MakeLabelStyle(14, FontStyle.Normal, new Color(0.92f, 0.95f, 0.98f, 0.98f), TextAnchor.MiddleCenter);
        _overlayMicroStyle ??= MakeLabelStyle(12, FontStyle.Normal, new Color(0.74f, 0.80f, 0.88f, 1f), TextAnchor.MiddleCenter);
        _overlayTagStyle ??= MakeLabelStyle(11, FontStyle.Bold, new Color(0.14f, 0.16f, 0.19f, 1f), TextAnchor.MiddleCenter);
        _overlayCursorStyle ??= MakeLabelStyle(30, FontStyle.Bold, new Color(0.70f, 0.93f, 1f, 1f), TextAnchor.MiddleCenter);
        _overlayTargetStyle ??= MakeLabelStyle(34, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
        _resultTitleStyle ??= MakeLabelStyle(17, FontStyle.Bold, Color.white);
        _resultDetailStyle ??= MakeLabelStyle(12, FontStyle.Normal, new Color(0.90f, 0.93f, 0.97f, 0.96f));
    }

    private Texture2D MakeSolidTex(Color color)
    {
        Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, color);
        tex.Apply();
        return tex;
    }

    private void DrawDapOverlay()
    {
        float introAlpha = GetOverlayIntroAlpha();
        float cardWidth = Mathf.Min(448f, Screen.width * 0.42f);
        float cardHeight = Mathf.Min(420f, Screen.height * 0.54f);
        float boxSize = Mathf.Min(cardWidth - 64f, cardHeight - 150f);
        float panelX = (Screen.width - cardWidth) * 0.5f;
        float panelY = (Screen.height - cardHeight) * 0.5f;

        Rect screenRect = new Rect(0f, 0f, Screen.width, Screen.height);
        Rect panelRect = new Rect(panelX, panelY, cardWidth, cardHeight);
        Rect areaRect = new Rect(panelX + ((cardWidth - boxSize) * 0.5f), panelY + 92f, boxSize, boxSize);
        Vector2 perfectPoint = NormalizedPointToPixels(PerfectZoneCenter, areaRect);
        Vector2 startPoint = NormalizedPointToPixels(DapCursorStart, areaRect);
        Vector2 cursorPoint = NormalizedPointToPixels(_dapCursor, areaRect);
        string npcName = _currentNpcTarget != null ? _currentNpcTarget.name : "NPC";

        Color oldColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.36f * introAlpha);
        GUI.DrawTexture(screenRect, _blackTex!);

        GUI.color = new Color(1f, 1f, 1f, introAlpha);
        GUI.DrawTexture(panelRect, _blackTex!);
        DrawOutline(panelRect, 2f, _whiteTex!);

        GUI.color = new Color(0.60f, 1f, 0.82f, 0.95f * introAlpha);
        GUI.DrawTexture(new Rect(panelRect.x + 18f, panelRect.y + 16f, panelRect.width - 36f, 4f), _whiteTex!);
        GUI.color = new Color(1f, 1f, 1f, introAlpha);

        DrawOutlinedLabel(new Rect(panelRect.x + 16f, panelRect.y + 22f, panelRect.width - 32f, 30f), "DAP UP", _overlayTitleStyle!, Color.white);
        DrawOutlinedLabel(new Rect(panelRect.x + 16f, panelRect.y + 48f, panelRect.width - 32f, 22f), npcName, _overlayHintStyle!, new Color(0.70f, 0.93f, 1f, 1f));
        DrawOutlinedLabel(new Rect(panelRect.x + 16f, panelRect.y + 68f, panelRect.width - 32f, 20f), "guide the marker into the center and left click", _overlayMicroStyle!, new Color(0.88f, 0.92f, 0.98f, 1f));

        GUI.color = new Color(1f, 1f, 1f, 0.94f * introAlpha);
        GUI.DrawTexture(areaRect, _panelTex!);
        DrawOutline(areaRect, 1.5f, _whiteTex!);

        GUI.color = new Color(0.90f, 0.93f, 0.97f, 0.18f * introAlpha);
        GUI.DrawTexture(new Rect(areaRect.x + 12f, perfectPoint.y - 1f, areaRect.width - 24f, 2f), _whiteTex!);
        GUI.DrawTexture(new Rect(perfectPoint.x - 1f, areaRect.y + 12f, 2f, areaRect.height - 24f), _whiteTex!);
        GUI.color = new Color(1f, 1f, 1f, introAlpha);

        DrawOutlinedLabel(new Rect(panelRect.x + 16f, areaRect.yMax + 10f, panelRect.width - 32f, 22f), "clean dap -> direct conversation", _overlayHintStyle!, Color.white);
        DrawOutlinedLabel(new Rect(panelRect.x + 16f, areaRect.yMax + 34f, panelRect.width - 32f, 20f), "perfect grants xp once per day", _overlayMicroStyle!, new Color(0.86f, 0.91f, 0.98f, 1f));

        for (int i = 1; i <= 7; i++)
        {
            float t = i / 8f;
            Vector2 dotPoint = Vector2.Lerp(startPoint, perfectPoint, t);
            DrawOutlinedLabel(new Rect(dotPoint.x - 8f, dotPoint.y - 8f, 16f, 16f), ".", _overlayMicroStyle!, new Color(1f, 1f, 1f, 0.72f));
        }

        float goodDiameter = Mathf.Max(96f, GoodZoneRadius * 2f * boxSize);
        Rect goodRect = NormalizedRectToPixels(PerfectZoneCenter, goodDiameter, goodDiameter, areaRect);
        GUI.color = new Color(1f, 0.86f, 0.58f, 0.92f * introAlpha);
        DrawOutline(goodRect, 2f, _whiteTex!);
        GUI.color = new Color(1f, 1f, 1f, introAlpha);

        DrawOutlinedLabel(new Rect(goodRect.x - 20f, goodRect.y - 26f, goodRect.width + 40f, 18f), "GOOD WINDOW", _overlayMicroStyle!, new Color(1f, 0.90f, 0.45f, 1f));
        DrawOutlinedLabel(new Rect(perfectPoint.x - 70f, perfectPoint.y + 22f, 140f, 18f), "PERFECT", _overlayMicroStyle!, new Color(0.68f, 1f, 0.84f, 1f));
        DrawOutlinedLabel(new Rect(perfectPoint.x - 24f, perfectPoint.y - 28f, 48f, 56f), "+", _overlayTargetStyle!, new Color(0.68f, 1f, 0.84f, 1f));
        DrawOutlinedLabel(new Rect(startPoint.x + 14f, startPoint.y - 12f, 82f, 20f), "START", _overlayMicroStyle!, Color.white);
        DrawOutlinedLabel(new Rect(cursorPoint.x - 20f, cursorPoint.y - 22f, 40f, 40f), "O", _overlayCursorStyle!, new Color(0.70f, 0.93f, 1f, 1f));

        GUI.color = oldColor;
    }

    private void DrawDapResultBanner()
    {
        float width = 420f;
        float height = 86f;
        float x = (Screen.width - width) * 0.5f;
        float y = Screen.height - height - 96f;
        float alpha = GetResultBannerAlpha();

        Rect panelRect = new Rect(x, y, width, height);
        Color oldColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.DrawTexture(panelRect, _blackTex!);
        DrawOutline(panelRect, 1.5f, _whiteTex!);

        GUI.color = new Color(_dapResultAccent.r, _dapResultAccent.g, _dapResultAccent.b, alpha);
        GUI.DrawTexture(new Rect(x + 12f, y + 12f, 4f, height - 24f), _whiteTex!);
        GUI.DrawTexture(new Rect(x + 24f, y + 12f, 92f, 18f), _whiteTex!);
        GUI.color = oldColor;

        DrawOutlinedLabel(
            new Rect(x + 32f, y + 8f, width - 48f, 24f),
            _dapResultTitle ?? string.Empty,
            WithTextColor(_resultTitleStyle!, _dapResultAccent),
            _dapResultAccent);

        DrawOutlinedLabel(
            new Rect(x + 32f, y + 38f, width - 48f, 22f),
            _dapResultDetail ?? string.Empty,
            _resultDetailStyle!,
            new Color(0.92f, 0.95f, 0.98f, 1f));

        GUI.color = oldColor;
    }

    private void DrawRect(Rect rect, Texture2D texture)
    {
        GUI.DrawTexture(rect, texture);
    }

    private void DrawOutline(Rect rect, float thickness, Texture2D texture)
    {
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, rect.width, thickness), texture);
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMax - thickness, rect.width, thickness), texture);
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, thickness, rect.height), texture);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.yMin, thickness, rect.height), texture);
    }

    private Rect NormalizedRectToPixels(Vector2 normalizedPoint, float width, float height, Rect areaRect)
    {
        Vector2 p = NormalizedPointToPixels(normalizedPoint, areaRect);
        return new Rect(p.x - (width * 0.5f), p.y - (height * 0.5f), width, height);
    }

    private Vector2 NormalizedPointToPixels(Vector2 normalizedPoint, Rect areaRect)
    {
        float x = areaRect.x + (normalizedPoint.x * areaRect.width);
        float y = areaRect.y + ((1f - normalizedPoint.y) * areaRect.height);
        return new Vector2(x, y);
    }

    private void DrawLine(Vector2 a, Vector2 b, float thickness, Color color)
    {
        Matrix4x4 matrix = GUI.matrix;
        Color oldColor = GUI.color;

        Vector2 delta = b - a;
        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        float length = delta.magnitude;

        GUI.color = color;
        GUIUtility.RotateAroundPivot(angle, a);
        GUI.DrawTexture(new Rect(a.x, a.y - (thickness * 0.5f), length, thickness), _whiteTex!);
        GUI.matrix = matrix;
        GUI.color = oldColor;
    }

    private static GUIStyle MakePanelStyle(Color backgroundColor)
    {
        Texture2D background = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        background.SetPixel(0, 0, backgroundColor);
        background.Apply();

        GUIStyle style = new GUIStyle(GUI.skin.box);
        style.normal.background = background;
        style.border = new RectOffset(0, 0, 0, 0);
        style.padding = new RectOffset(0, 0, 0, 0);
        style.margin = new RectOffset(0, 0, 0, 0);
        return style;
    }

    private static GUIStyle MakeLabelStyle(int fontSize, FontStyle fontStyle, Color textColor, TextAnchor alignment = TextAnchor.MiddleLeft)
    {
        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            fontStyle = fontStyle,
            alignment = alignment,
            richText = false
        };

        style.normal.textColor = textColor;
        return style;
    }

    private static GUIStyle WithTextColor(GUIStyle style, Color textColor)
    {
        style.normal.textColor = textColor;
        return style;
    }

    private static void DrawOutlinedLabel(Rect rect, string text, GUIStyle style, Color textColor)
    {
        Color oldColor = style.normal.textColor;
        style.normal.textColor = Color.black;
        GUI.Label(new Rect(rect.x - 1f, rect.y, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x + 1f, rect.y, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x, rect.y - 1f, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x, rect.y + 1f, rect.width, rect.height), text, style);

        style.normal.textColor = textColor;
        GUI.Label(rect, text, style);
        style.normal.textColor = oldColor;
    }

    private float GetOverlayIntroAlpha()
    {
        if (_dapOverlayStartTime < 0f)
        {
            return 1f;
        }

        float t = Mathf.Clamp01((Time.time - _dapOverlayStartTime) / OverlayIntroDuration);
        return t * t * (3f - (2f * t));
    }

    private float GetResultBannerAlpha()
    {
        if (_dapResultDisplayUntil < 0f)
        {
            return 1f;
        }

        float fadeOut = Mathf.Clamp01(_dapResultDisplayUntil - Time.time);
        return Mathf.Clamp01(fadeOut);
    }
}
