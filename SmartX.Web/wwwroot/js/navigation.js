(() => {
    const main = document.getElementById("pageContent");
    const status = document.getElementById("navigationStatus");
    let pending;
    let currentUrl = window.location.href;
    const moduleState = window.smartXState;
    window.history.scrollRestoration = "manual";

    function updateNavigation() {
        const path = window.location.pathname.toLowerCase().replace(/\/index\/?$/, "").replace(/\/$/, "") || "/";
        for (const link of document.querySelectorAll(".nav-links a")) {
            const target = new URL(link.href).pathname.toLowerCase();
            if (target === path || target !== "/" && path.startsWith(`${target}/`)) {
                link.setAttribute("aria-current", "page");
            } else {
                link.removeAttribute("aria-current");
            }
        }
    }

    async function navigate(url, fromHistory = false) {
        moduleState.save(currentUrl);
        if (!fromHistory) {
            url = moduleState.resolve(url);
        }
        pending?.abort();
        const request = new AbortController();
        pending = request;
        const timeout = window.setTimeout(() => request.abort(), 15000);
        status.hidden = false;
        status.textContent = "Opening page…";
        main.setAttribute("aria-busy", "true");

        try {
            const response = await fetch(url, { signal: request.signal, cache: "no-store" });
            if (!response.ok || !response.headers.get("content-type")?.includes("text/html")) {
                throw new Error("Page unavailable.");
            }
            const page = new DOMParser().parseFromString(await response.text(), "text/html");
            const content = page.getElementById("pageContent");
            if (!content || pending !== request || request.signal.aborted) {
                throw new Error("Page unavailable.");
            }

            document.dispatchEvent(new Event("smartx:page-unload"));
            main.replaceChildren(...content.childNodes);
            document.title = page.title;
            if (!fromHistory) {
                window.history.pushState(null, "", response.url);
            }
            currentUrl = window.location.href;
            const scrollY = moduleState.restore(currentUrl);
            updateNavigation();
            window.jQuery?.validator?.unobtrusive?.parse(main);
            document.dispatchEvent(new Event("smartx:page-load"));
            status.hidden = true;
            const heading = main.querySelector("h1");
            heading?.setAttribute("tabindex", "-1");
            heading?.focus({ preventScroll: true });
            window.scrollTo(0, scrollY);
        } catch {
            if (pending === request) {
                if (fromHistory) {
                    window.history.replaceState(null, "", currentUrl);
                }
                status.textContent = "Unable to open that page. Your current page is still available. Try the link again.";
            }
        } finally {
            window.clearTimeout(timeout);
            if (pending === request) {
                pending = null;
                main.setAttribute("aria-busy", "false");
            }
        }
    }

    document.addEventListener("click", event => {
        const link = event.target.closest(".app-navigation a, .hero-actions a, .pillar a, a[data-navigation]");
        if (!link || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey ||
            event.shiftKey || event.altKey || link.target || link.hasAttribute("download")) {
            return;
        }
        const url = new URL(link.href);
        if (url.origin !== window.location.origin || url.hash) {
            return;
        }
        event.preventDefault();
        navigate(url.href);
    });

    window.addEventListener("popstate", () => navigate(window.location.href, true));
    window.addEventListener("pagehide", () => {
        moduleState.save(currentUrl);
        pending?.abort();
        pending = null;
        main.setAttribute("aria-busy", "false");
        status.hidden = true;
        document.dispatchEvent(new Event("smartx:page-unload"));
    });
    window.addEventListener("pageshow", event => {
        if (event.persisted) {
            moduleState.restore(currentUrl);
            document.dispatchEvent(new Event("smartx:page-load"));
        }
    });
    updateNavigation();
    const scrollY = moduleState.restore(currentUrl);
    document.dispatchEvent(new Event("smartx:page-load"));
    window.scrollTo(0, scrollY);
})();
