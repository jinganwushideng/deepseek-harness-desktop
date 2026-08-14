namespace DeepSeekHarnessDesktop.Services;

public static class WebThemeMonitor
{
    public const string LightMessage = "dsh-theme:light";
    public const string DarkMessage = "dsh-theme:dark";

    public static bool TryReadMessage(string? message, out bool isLight)
    {
        if (string.Equals(message, LightMessage, StringComparison.Ordinal))
        {
            isLight = true;
            return true;
        }
        if (string.Equals(message, DarkMessage, StringComparison.Ordinal))
        {
            isLight = false;
            return true;
        }
        isLight = false;
        return false;
    }

    public const string Script = """
        (() => {
          if (window.__dshDesktopThemeMonitor) return;
          window.__dshDesktopThemeMonitor = true;
          let last = '';
          const emit = () => {
            const rootScheme = document.documentElement.style.colorScheme.trim().toLowerCase();
            let scheme = '';
            if (document.body?.hasAttribute('data-ds-dark-theme')) scheme = 'dark';
            else if (rootScheme === 'light' || rootScheme === 'dark') scheme = rootScheme;
            if (!scheme || scheme === last) return;
            last = scheme;
            window.chrome.webview.postMessage('dsh-theme:' + scheme);
          };
          const attach = () => {
            if (!document.body) { requestAnimationFrame(attach); return; }
            new MutationObserver(emit).observe(document.body, {
              attributes: true,
              attributeFilter: ['data-ds-dark-theme', 'class', 'style']
            });
            new MutationObserver(emit).observe(document.documentElement, {
              attributes: true,
              attributeFilter: ['class', 'style']
            });
            emit();
          };
          if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', attach, { once: true });
          else attach();
        })();
        """;
}
