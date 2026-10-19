import { useEffect, useRef } from "react";
import { useBrowserStore } from "@/lib/browser/store";

function hostFrom(url: string) {
  try {
    return new URL(url).hostname;
  } catch {
    return "";
  }
}

function postHost(message: Record<string, unknown>) {
  const bridge = (window as unknown as { chrome?: { webview?: { postMessage: (m: unknown) => void } } }).chrome
    ?.webview;
  bridge?.postMessage(message);
}

export function DesktopWebview({
  tabId,
  url,
  reloadKey,
}: {
  tabId: string;
  url: string;
  title: string;
  reloadKey: number;
}) {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const send = () => {
      const box = el.getBoundingClientRect();
      let node: HTMLElement | null = el;
      let visible = box.width > 2 && box.height > 2;
      while (node && visible) {
        const style = getComputedStyle(node);
        if (style.display === "none" || style.visibility === "hidden") visible = false;
        node = node.parentElement;
      }
      postHost({
        type: "slot",
        tabId,
        url,
        reloadKey,
        visible,
        x: Math.round(box.x),
        y: Math.round(box.y),
        w: Math.round(box.width),
        h: Math.round(box.height),
      });
    };
    send();
    const observer = new ResizeObserver(send);
    observer.observe(el);
    const timer = window.setInterval(send, 400);
    const onMessage = (event: Event) => {
      const data = (event as Event & { data?: { type?: string; tabId?: string; url?: string; title?: string; username?: string; password?: string } }).data;
      if (!data) return;
      if (data.type === "login-offer" && data.username && data.password) {
        const host = data.url ? hostFrom(data.url) : "";
        if (host) useBrowserStore.getState().offerSave(host, data.username, data.password);
        return;
      }
      if (data.tabId !== tabId) return;
      if (data.type === "guest-nav" && data.url) useBrowserStore.getState().navigateTab(tabId, data.url);
      if (data.type === "guest-title" && data.title) useBrowserStore.getState().setTabTitle(data.title, tabId);
    };
    const chrome = (window as unknown as { chrome?: { webview?: EventTarget } }).chrome?.webview;
    chrome?.addEventListener("message", onMessage);
    return () => {
      observer.disconnect();
      window.clearInterval(timer);
      chrome?.removeEventListener("message", onMessage);
      postHost({ type: "slot-close", tabId });
    };
  }, [tabId, url, reloadKey]);

  return <div ref={ref} className="h-full w-full bg-white" />;
}
