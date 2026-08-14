namespace DeepSeekHarnessDesktop.Services;

public static class ResponseCompletionMonitor
{
    public const string CompletionMessage = "dsh-response-complete";

    // Harness keeps the stop button mounted across model and tool phases, while
    // data-streaming covers the assistant render itself. Watching both avoids
    // treating a tool transition as a completed reply.
    public const string Script = """
        (() => {
          const key = "__dshDesktopResponseMonitorV1";
          if (window[key]) return;

          const state = { armed: false, timer: 0, observer: null };
          const stopLabels = new Set(["停止生成", "stop generating"]);
          const isRunning = () => {
            if (document.querySelector('[data-streaming="true"]')) return true;
            return Array.from(document.querySelectorAll('button[aria-label]')).some((button) =>
              stopLabels.has((button.getAttribute("aria-label") || "").trim().toLowerCase()));
          };

          const scan = () => {
            if (isRunning()) {
              state.armed = true;
              if (state.timer) {
                clearTimeout(state.timer);
                state.timer = 0;
              }
              return;
            }

            if (!state.armed || state.timer) return;
            state.timer = window.setTimeout(() => {
              state.timer = 0;
              if (!state.armed || isRunning()) return;
              state.armed = false;
              window.chrome?.webview?.postMessage("dsh-response-complete");
            }, 800);
          };

          const start = () => {
            if (state.observer || !document.documentElement) return;
            state.observer = new MutationObserver(scan);
            state.observer.observe(document.documentElement, {
              subtree: true,
              childList: true,
              attributes: true,
              attributeFilter: ["data-streaming", "aria-label", "disabled"]
            });
            scan();
          };

          window[key] = state;
          if (document.documentElement) start();
          else document.addEventListener("DOMContentLoaded", start, { once: true });
        })();
        """;

    public static bool IsCompletionMessage(string? message) =>
        string.Equals(message, CompletionMessage, StringComparison.Ordinal);
}
