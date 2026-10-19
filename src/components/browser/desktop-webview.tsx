import { useEffect, useRef } from "react";
import { useBrowserStore } from "@/lib/browser/store";

type WebviewEl = HTMLElement & {
  src: string;
  getURL?: () => string;
};

function isWebView2Host() {
  return typeof window !== "undefined" && window.spartanDesktop?.host === "webview2";
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
  if (isWebView2Host()) {
    return <WebView2Slot tabId={tabId} url={url} reloadKey={reloadKey} />;
  }
  return <ElectronWebview tabId={tabId} url={url} reloadKey={reloadKey} />;
}

function WebView2Slot({ tabId, url, reloadKey }: { tabId: string; url: string; reloadKey: number }) {
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
      const data = (event as Event & { data?: { type?: string; tabId?: string; url?: string; title?: string } }).data;
      if (!data || data.tabId !== tabId) return;
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

function ElectronWebview({
  tabId,
  url,
  reloadKey,
}: {
  tabId: string;
  url: string;
  reloadKey: number;
}) {
  const hostRef = useRef<HTMLDivElement>(null);
  const viewRef = useRef<WebviewEl | null>(null);
  const lastUrl = useRef(url);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;

    const view = document.createElement("webview") as WebviewEl;
    view.setAttribute("partition", "persist:spartan");
    view.setAttribute("allowpopups", "true");
    view.setAttribute(
      "useragent",
      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
    );
    view.setAttribute("src", url);
    view.style.width = "100%";
    view.style.height = "100%";
    view.style.border = "0";
    view.style.display = "flex";
    view.style.flex = "1";
    host.replaceChildren(view);
    viewRef.current = view;
    lastUrl.current = url;

    const syncUrl = (next: string) => {
      if (!next || next === lastUrl.current) return;
      lastUrl.current = next;
      useBrowserStore.getState().navigateTab(tabId, next);
    };

    const onNavigate = (event: Event) => {
      const next = (event as Event & { url?: string }).url ?? view.getURL?.();
      if (next) syncUrl(next);
    };
    const onTitle = (event: Event) => {
      const title = (event as Event & { title?: string }).title?.trim();
      if (title) useBrowserStore.getState().setTabTitle(title, tabId);
    };

    view.addEventListener("did-navigate", onNavigate);
    view.addEventListener("did-navigate-in-page", onNavigate);
    view.addEventListener("page-title-updated", onTitle);

    return () => {
      view.removeEventListener("did-navigate", onNavigate);
      view.removeEventListener("did-navigate-in-page", onNavigate);
      view.removeEventListener("page-title-updated", onTitle);
      viewRef.current = null;
      host.replaceChildren();
    };
    // Recreate only on explicit reload.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reloadKey, tabId]);

  useEffect(() => {
    const view = viewRef.current;
    if (!view || url === lastUrl.current) return;
    lastUrl.current = url;
    view.setAttribute("src", url);
  }, [url]);

  return <div ref={hostRef} className="h-full w-full bg-white" />;
}
