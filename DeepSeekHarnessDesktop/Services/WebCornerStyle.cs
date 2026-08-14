namespace DeepSeekHarnessDesktop.Services;

public static class WebCornerStyle
{
    public const string Script = """
        (() => {
          const apply = (rounded) => {
            const radius = rounded ? '12px 12px 0 0' : '0px';
            const clip = rounded ? 'inset(0 round 12px 12px 0 0)' : 'none';
            const set = (element) => {
              if (!element) return;
              element.style.setProperty('border-radius', radius, 'important');
              element.style.setProperty('overflow', 'hidden', 'important');
              element.style.setProperty('clip-path', clip, 'important');
            };
            set(document.documentElement);
            set(document.body);
            set(document.getElementById('root'));
          };
          window.__dshDesktopSetRoundedCorners = apply;
          const start = () => apply(true);
          if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', start, { once: true });
          else start();
        })();
        """;
}
