document.addEventListener("smartx:page-load", () => {
    const stream = document.getElementById("commandStream");
    const status = document.getElementById("commandStreamStatus");
    const refreshLink = document.getElementById("refreshCommandStream");

    if (!stream || !status || !refreshLink) {
        return;
    }

    let timer;
    let request;
    let stopped = false;
    for (const button of stream.querySelectorAll("button[data-pending]")) {
        button.disabled = false;
        delete button.dataset.pending;
    }
    const lifetime = new AbortController();
    document.addEventListener("smartx:page-unload", () => {
        stopped = true;
        window.clearTimeout(timer);
        request?.abort();
        lifetime.abort();
    }, { once: true });

    async function refresh() {
        window.clearTimeout(timer);
        if (request || stopped || document.hidden) {
            return;
        }

        request = new AbortController();
        const timeout = window.setTimeout(() => request?.abort(), 10000);
        stream.setAttribute("aria-busy", "true");

        try {
            const response = await fetch(stream.dataset.url, {
                signal: request.signal,
                cache: "no-store"
            });
            if (!response.ok) {
                throw new Error("Command stream unavailable.");
            }

            const html = await response.text();
            if (stopped || document.hidden) {
                return;
            }

            if (stream.contains(document.activeElement)) {
                status.textContent = "Updates paused while you interact with the stream.";
                return;
            }

            const scrollPositions = new Map(Array.from(stream.querySelectorAll(".table-shell"),
                table => [table.getAttribute("aria-label"), table.scrollLeft]));
            stream.innerHTML = html;
            for (const table of stream.querySelectorAll(".table-shell")) {
                table.scrollLeft = scrollPositions.get(table.getAttribute("aria-label")) ?? 0;
            }
            stream.dataset.loaded = "true";
            status.textContent = "Connected · Updates every 3 seconds.";
        } catch {
            if (!stopped && !document.hidden) {
                status.textContent = stream.dataset.loaded === "true"
                    ? "Connection interrupted. Showing the last successful snapshot. Retrying automatically…"
                    : "Unable to load the stream. Retrying automatically…";
            }
        } finally {
            window.clearTimeout(timeout);
            request = null;
            stream.setAttribute("aria-busy", "false");
            if (!stopped && !document.hidden) {
                timer = window.setTimeout(refresh, 3000);
            }
        }
    }

    refreshLink.addEventListener("click", event => {
        event.preventDefault();
        refresh();
    }, { signal: lifetime.signal });

    stream.addEventListener("submit", event => {
        if (!event.target.matches(".command-form")) {
            return;
        }
        if (stopped) {
            event.preventDefault();
            return;
        }
        stopped = true;
        window.clearTimeout(timer);
        request?.abort();
        for (const button of stream.querySelectorAll(".command-form button:not(:disabled)")) {
            button.dataset.pending = "true";
            button.disabled = true;
        }
    }, { signal: lifetime.signal });

    document.addEventListener("visibilitychange", () => {
        if (document.hidden) {
            window.clearTimeout(timer);
            request?.abort();
        } else {
            refresh();
        }
    }, { signal: lifetime.signal });

    window.addEventListener("pagehide", () => {
        stopped = true;
        window.clearTimeout(timer);
        request?.abort();
    }, { signal: lifetime.signal });

    window.addEventListener("pageshow", () => {
        stopped = false;
        refresh();
    }, { signal: lifetime.signal });

    status.textContent = "Connecting to automatic updates…";
    refresh();
});
