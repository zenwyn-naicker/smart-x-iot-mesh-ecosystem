document.addEventListener("smartx:page-load", () => {
    const page = document.getElementById("topologyPage");
    if (!page) {
        return;
    }

    const status = document.getElementById("topologyStatus");
    const lifetime = new AbortController();
    let timer;
    let request;
    let stopped = false;

    function pause() {
        window.clearTimeout(timer);
        request?.abort();
    }

    async function refresh() {
        window.clearTimeout(timer);
        if (stopped || document.hidden || request) {
            return;
        }

        request = new AbortController();
        const timeout = window.setTimeout(() => request?.abort(), 10000);
        try {
            const response = await fetch(page.dataset.url, { signal: request.signal, cache: "no-store" });
            if (!response.ok) {
                throw new Error("Topology unavailable.");
            }
            const snapshot = new DOMParser().parseFromString(await response.text(), "text/html");
            const regions = Array.from(page.querySelectorAll("[data-topology-region]"));
            const updates = regions.map(region => snapshot.querySelector(
                `[data-topology-region="${region.dataset.topologyRegion}"]`));
            if (updates.some(region => !region)) {
                throw new Error("Topology unavailable.");
            }
            if (stopped || document.hidden || request.signal.aborted) {
                return;
            }
            if (regions.some(region => region.contains(document.activeElement))) {
                status.textContent = "Updates paused while you interact with the network.";
                return;
            }

            regions.forEach((region, index) => {
                const positions = Array.from(region.querySelectorAll(".table-shell, .network-shell"),
                    element => element.scrollLeft);
                region.replaceChildren(...updates[index].childNodes);
                region.querySelectorAll(".table-shell, .network-shell").forEach((element, position) => {
                    element.scrollLeft = positions[position] ?? 0;
                });
            });
            for (const select of page.querySelectorAll("select")) {
                const updated = snapshot.getElementById(select.id);
                if (!updated || select === document.activeElement) {
                    continue;
                }
                const value = select.value;
                select.replaceChildren(...updated.childNodes);
                if (Array.from(select.options).some(option => option.value === value)) {
                    select.value = value;
                }
            }
            status.textContent = "Connected · Updates every 5 seconds.";
        } catch {
            if (!stopped && !document.hidden) {
                status.textContent = "Connection interrupted. Showing the last successful snapshot. Retrying automatically…";
            }
        } finally {
            window.clearTimeout(timeout);
            request = null;
            if (!stopped && !document.hidden) {
                timer = window.setTimeout(refresh, 5000);
            }
        }
    }

    document.getElementById("refreshTopology").addEventListener("click", event => {
        event.preventDefault();
        refresh();
    }, { signal: lifetime.signal });

    page.addEventListener("submit", () => {
        stopped = true;
        pause();
    }, { signal: lifetime.signal });

    document.addEventListener("visibilitychange", () => {
        if (document.hidden) {
            pause();
        } else {
            refresh();
        }
    }, { signal: lifetime.signal });

    document.addEventListener("smartx:page-unload", () => {
        stopped = true;
        pause();
        lifetime.abort();
    }, { once: true, signal: lifetime.signal });

    status.textContent = "Connected · Updates every 5 seconds.";
    timer = window.setTimeout(refresh, 5000);
});
