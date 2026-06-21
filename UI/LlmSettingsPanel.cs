using Godot;
using MegaCrit.Sts2.Core.Logging;
using Sts2ContextCoach.Localization;
using Sts2ContextCoach.Telemetry;

namespace Sts2ContextCoach.UI;

/// <summary>
/// Draggable in-game panel (PanelContainer + flat style, similar in spirit to
/// <see href="https://github.com/BAIGUANGMEI/STS2-DamageTracker">STS2-DamageTracker</see> overlay) for LLM endpoint + API key.
/// </summary>
public static class LlmSettingsPanel
{
    private const string NodePanel = "ContextCoachLlmSettingsPanel";
    private const string NodeStatus = "ContextCoachLlmStatus";

    public static void Attach(CanvasLayer layer, float topRightX, float setupButtonY)
    {
        if (layer.GetNodeOrNull(NodePanel) != null)
            return;

        var setupBtn = new Button
        {
            Name = "ContextCoachLlmSetupButton",
            Text = LocalizationManager.T("ui.llm_setup_button"),
            TooltipText = LocalizationManager.T("ui.llm_setup_tooltip")
        };
        setupBtn.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        setupBtn.Position = new Vector2(topRightX, setupButtonY);
        setupBtn.CustomMinimumSize = new Vector2(220f, 36f);
        setupBtn.Size = new Vector2(220f, 36f);
        setupBtn.MouseFilter = Control.MouseFilterEnum.Stop;
        setupBtn.AddThemeFontSizeOverride("font_size", 14);
        ApplySmallButtonChrome(setupBtn);
        layer.AddChild(setupBtn);

        var panel = new PanelContainer
        {
            Name = NodePanel,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        // Align right edge with other top-right controls (same pattern as Export / LLM toggle).
        panel.Position = new Vector2(topRightX, setupButtonY + 42f);
        panel.CustomMinimumSize = new Vector2(420f, 0f);
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.78f),
            BorderColor = new Color(0.35f, 0.55f, 0.75f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 10
        });

        var outer = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        outer.AddThemeConstantOverride("separation", 6);

        var header = new HBoxContainer();
        header.MouseFilter = Control.MouseFilterEnum.Stop;
        header.CustomMinimumSize = new Vector2(0f, 30f);
        var title = new Label
        {
            Text = LocalizationManager.T("ui.llm_setup_title"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        title.AddThemeFontSizeOverride("font_size", 16);
        title.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 1f));
        header.AddChild(title);
        var closeBtn = new Button { Text = LocalizationManager.T("ui.llm_setup_close"), Flat = true };
        closeBtn.CustomMinimumSize = new Vector2(36f, 28f);
        closeBtn.AddThemeFontSizeOverride("font_size", 14);
        closeBtn.Pressed += () => { panel.Visible = false; };
        header.AddChild(closeBtn);
        outer.AddChild(header);
        EnableDragByHandle(header, panel);

        outer.AddChild(MakeHint(LocalizationManager.T("ui.llm_setup_hint_env")));

        outer.AddChild(MakeFieldLabel(LocalizationManager.T("ui.llm_setup_base_url")));
        var baseUrlEdit = new LineEdit
        {
            Text = ContextCoachConfig.Current.LlmBaseUrl ?? "",
            ClearButtonEnabled = true
        };
        baseUrlEdit.AddThemeFontSizeOverride("font_size", 13);
        baseUrlEdit.PlaceholderText = "https://openrouter.ai/api/v1";
        outer.AddChild(baseUrlEdit);

        outer.AddChild(MakeFieldLabel(LocalizationManager.T("ui.llm_setup_model")));
        var modelEdit = new LineEdit
        {
            Text = ContextCoachConfig.Current.LlmModel ?? "",
            ClearButtonEnabled = true
        };
        modelEdit.AddThemeFontSizeOverride("font_size", 13);
        modelEdit.PlaceholderText = "openai/gpt-4o-mini";
        outer.AddChild(modelEdit);

        outer.AddChild(MakeFieldLabel(LocalizationManager.T("ui.llm_setup_api_key")));
        var keyEdit = new LineEdit
        {
            Text = ContextCoachConfig.Current.LlmApiKey ?? "",
            Secret = true,
            ClearButtonEnabled = true
        };
        keyEdit.AddThemeFontSizeOverride("font_size", 13);
        keyEdit.PlaceholderText = LocalizationManager.T("ui.llm_setup_key_placeholder");
        outer.AddChild(keyEdit);

        var status = new Label
        {
            Name = NodeStatus,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        status.AddThemeFontSizeOverride("font_size", 12);
        status.AddThemeColorOverride("font_color", new Color(0.75f, 0.82f, 0.9f));

        void RefreshStatus()
        {
            var src = ContextCoachConfig.DescribeLlmKeySource();
            status.Text = string.Format(LocalizationManager.T("ui.llm_setup_status"), src);
        }

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 8);
        var clearKeyBtn = new Button { Text = LocalizationManager.T("ui.llm_setup_clear_key") };
        clearKeyBtn.CustomMinimumSize = new Vector2(100f, 32f);
        clearKeyBtn.Pressed += () =>
        {
            try
            {
                keyEdit.Text = "";
                ContextCoachConfig.Current.LlmApiKey = null;
                ContextCoachConfig.Save();
                RefreshStatus();
                Log.Info("[ContextCoach][LLM] API key cleared from config (file)");
            }
            catch (Exception ex)
            {
                Log.Warn($"[ContextCoach][LLM] clear key failed: {ex.Message}");
            }
        };
        ApplySmallButtonChrome(clearKeyBtn);
        row.AddChild(clearKeyBtn);

        var saveBtn = new Button { Text = LocalizationManager.T("ui.llm_setup_save") };
        saveBtn.CustomMinimumSize = new Vector2(100f, 32f);
        saveBtn.Pressed += () =>
        {
            try
            {
                ContextCoachConfig.Current.LlmBaseUrl = string.IsNullOrWhiteSpace(baseUrlEdit.Text)
                    ? "https://openrouter.ai/api/v1"
                    : baseUrlEdit.Text.Trim();
                ContextCoachConfig.Current.LlmModel = string.IsNullOrWhiteSpace(modelEdit.Text)
                    ? "openai/gpt-4o-mini"
                    : modelEdit.Text.Trim();
                var k = keyEdit.Text?.Trim();
                ContextCoachConfig.Current.LlmApiKey = string.IsNullOrEmpty(k) ? null : k;
                ContextCoachConfig.Save();
                var src = ContextCoachConfig.DescribeLlmKeySource();
                var keyLen = ContextCoachConfig.TryGetLlmApiKey()?.Length ?? 0;
                Log.Info($"[ContextCoach][LLM] settings saved (key_source={src}, resolved_key_len={keyLen})");
                RefreshStatus();
            }
            catch (Exception ex)
            {
                Log.Warn($"[ContextCoach][LLM] save failed: {ex.Message}");
            }
        };
        ApplySmallButtonChrome(saveBtn);
        row.AddChild(saveBtn);
        outer.AddChild(row);
        outer.AddChild(status);

        panel.AddChild(outer);
        layer.AddChild(panel);

        RefreshStatus();

        setupBtn.Pressed += () =>
        {
            panel.Visible = !panel.Visible;
            if (!panel.Visible)
                return;
            baseUrlEdit.Text = ContextCoachConfig.Current.LlmBaseUrl ?? "";
            modelEdit.Text = ContextCoachConfig.Current.LlmModel ?? "";
            keyEdit.Text = ContextCoachConfig.Current.LlmApiKey ?? "";
            RefreshStatus();
        };
    }

    private static Label MakeFieldLabel(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 12);
        l.AddThemeColorOverride("font_color", new Color(0.85f, 0.88f, 0.94f));
        return l;
    }

    private static Label MakeHint(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 11);
        l.AddThemeColorOverride("font_color", new Color(0.65f, 0.72f, 0.8f));
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return l;
    }

    private static void ApplySmallButtonChrome(Button button)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0.14f, 0.32f, 0.5f, 0.95f),
            BorderColor = new Color(0.45f, 0.65f, 0.88f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        var h = (StyleBoxFlat)sb.Duplicate();
        h.BgColor = new Color(0.18f, 0.4f, 0.62f, 1f);
        var p = (StyleBoxFlat)sb.Duplicate();
        p.BgColor = new Color(0.1f, 0.24f, 0.4f, 1f);
        button.AddThemeStyleboxOverride("normal", sb);
        button.AddThemeStyleboxOverride("hover", h);
        button.AddThemeStyleboxOverride("pressed", p);
        button.AddThemeColorOverride("font_color", Colors.White);
    }

    private static void EnableDragByHandle(Control handle, Control panelToMove)
    {
        var dragging = false;
        handle.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
                dragging = mb.Pressed;
            else if (e is InputEventMouseMotion mm && dragging)
            {
                var vp = panelToMove.GetViewport();
                var visible = vp.GetVisibleRect().Size;
                var next = panelToMove.Position + mm.Relative;
                var minX = -visible.X + 16f;
                var maxX = -20f;
                var minY = 6f;
                var maxY = MathF.Max(6f, visible.Y - panelToMove.Size.Y - 6f);
                panelToMove.Position = new Vector2(
                    Math.Clamp(next.X, minX, maxX),
                    Math.Clamp(next.Y, minY, maxY));
            }
        };
    }
}
