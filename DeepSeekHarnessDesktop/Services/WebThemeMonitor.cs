using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace DeepSeekHarnessDesktop.Services;

public sealed record WebSkinAppearance(bool IsLight, string Accent, string Background, string Surface, string Border, string Text, string Muted, string SkinId);

public static partial class WebThemeMonitor
{
    public const string LightMessage = "dsh-theme:light";
    public const string DarkMessage = "dsh-theme:dark";

    public static bool TryReadMessage(string? message, out bool isLight)
    {
        if (string.Equals(message, LightMessage, StringComparison.Ordinal)) { isLight = true; return true; }
        if (string.Equals(message, DarkMessage, StringComparison.Ordinal)) { isLight = false; return true; }
        isLight = false;
        return false;
    }

    public static bool TryReadAppearanceJson(string? json, out WebSkinAppearance appearance)
    {
        appearance = null!;
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "dsh-appearance") return false;
            var skinId = root.TryGetProperty("skinId", out var id) ? id.GetString() ?? string.Empty : string.Empty;
            var value = new WebSkinAppearance(
                root.GetProperty("isLight").GetBoolean(),
                ReadColor(root, "accent"), ReadColor(root, "background"), ReadColor(root, "surface"),
                ReadColor(root, "border"), ReadColor(root, "text"), ReadColor(root, "muted"),
                skinId[..Math.Min(skinId.Length, 100)]);
            if (!TryParseCssColor(value.Background, out _) || !TryParseCssColor(value.Text, out _)) return false;
            appearance = value;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static bool TryParseCssColor(string? value, out MediaColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value) || value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return false;
        var text = value.Trim();
        try
        {
            if (text.StartsWith('#')) { color = (MediaColor)MediaColorConverter.ConvertFromString(text); return true; }
        }
        catch (FormatException) { return false; }
        var match = RgbPattern().Match(text);
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ||
            !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ||
            !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return false;
        color = MediaColor.FromRgb((byte)Math.Clamp(Math.Round(r), 0, 255), (byte)Math.Clamp(Math.Round(g), 0, 255), (byte)Math.Clamp(Math.Round(b), 0, 255));
        return true;
    }

    private static string ReadColor(JsonElement root, string property) => root.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    [GeneratedRegex(@"^rgba?\(\s*([0-9.]+)[, ]+\s*([0-9.]+)[, ]+\s*([0-9.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RgbPattern();

    public const string Script = """
        (() => {
          if (window.__dshDesktopThemeMonitor) return;
          window.__dshDesktopThemeMonitor = true;
          let last = '', pending = 0, settle = 0;
          const valid = value => value && value !== 'transparent' && !/^rgba\([^)]*,\s*0\s*\)$/.test(value);
          const style = element => element ? getComputedStyle(element) : null;
          const first = values => values.find(valid) || '';
          const rgb = value => String(value || '').match(/[0-9.]+/g)?.slice(0, 3).map(Number) || [];
          const colorful = value => { const c = rgb(value); return c.length === 3 && Math.max(...c) - Math.min(...c) >= 24; };
          const cssVariable = (computed, pattern) => {
            if (!computed) return '';
            const names = Array.from(computed).filter(name => name.startsWith('--') && pattern.test(name));
            return first(names.map(name => computed.getPropertyValue(name).trim()));
          };
          const emit = () => {
            pending = 0;
            const root = document.documentElement, body = document.body;
            if (!body) return;
            const rs = style(root), bs = style(body);
            const sidebar = document.querySelector('aside,[class*="sidebar"],[class*="SideBar"]');
            const card = document.querySelector('textarea,[class*="card"],[class*="panel"],[class*="surface"],main');
            const accentNode = document.querySelector('button[class*="primary"],[class*="accent"],[aria-selected="true"],[data-state="active"]');
            const ss = style(sidebar), cs = style(card), as = style(accentNode);
            const rootScheme = (rs.colorScheme || root.style.colorScheme || '').trim().toLowerCase();
            const isLight = body.hasAttribute('data-ds-dark-theme') ? false : rootScheme === 'light' ? true : rootScheme === 'dark' ? false : (() => {
              const m = (bs.backgroundColor || '').match(/[0-9.]+/g);
              return m?.length >= 3 ? (+m[0] * .299 + +m[1] * .587 + +m[2] * .114) > 150 : false;
            })();
            const payload = {
              kind: 'dsh-appearance', isLight,
              accent: first([
                cssVariable(rs, /(?:primary|accent|brand)(?:-|$)/i),
                rs.getPropertyValue('--dsw-alias-primary'), rs.getPropertyValue('--color-primary'),
                ...[as?.backgroundColor, as?.borderColor, as?.color].filter(colorful)
              ]),
              background: first([bs.backgroundColor, rs.backgroundColor]),
              surface: first([cs?.backgroundColor, ss?.backgroundColor, bs.backgroundColor]),
              border: first([cssVariable(rs, /(?:border|divider|outline)(?:-|$)/i), cs?.borderColor, ss?.borderColor, rs.getPropertyValue('--dsw-alias-border')]),
              text: first([bs.color, cs?.color]),
              muted: first([cssVariable(rs, /(?:text-secondary|muted|subtle|foreground-secondary)/i), rs.getPropertyValue('--dsw-alias-text-secondary'), ss?.color, cs?.color]),
              skinId: String(root.dataset.dshSkin || root.dataset.skin || root.dataset.theme || body.dataset.skin || body.dataset.theme || '').slice(0, 100)
            };
            const fingerprint = JSON.stringify(payload);
            if (fingerprint === last) return;
            last = fingerprint;
            window.chrome.webview.postMessage(payload);
          };
          const schedule = () => {
            if (!pending) pending = setTimeout(emit, 100);
            clearTimeout(settle);
            settle = setTimeout(emit, 480);
          };
          const attach = () => {
            if (!document.body) { requestAnimationFrame(attach); return; }
            new MutationObserver(schedule).observe(document.documentElement, { attributes: true, childList: true, subtree: true, attributeFilter: ['class', 'style', 'data-theme', 'data-skin', 'data-dsh-skin', 'data-ds-dark-theme'] });
            addEventListener('resize', schedule, { passive: true });
            document.fonts?.ready?.then(schedule);
            schedule();
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', attach, { once: true }); else attach();
        })();
        """;
}
