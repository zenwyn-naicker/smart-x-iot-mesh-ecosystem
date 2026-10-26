document.addEventListener("smartx:page-load", () => {
    const status = document.getElementById("simulationStatus");
    const rate = document.getElementById("readingsPerSecond");

    if (!status) {
        return;
    }

    let initialLoad = true;
    const lifetime = new AbortController();
    let timer;
    document.addEventListener("smartx:page-unload", () => {
        lifetime.abort();
        window.clearTimeout(timer);
    }, { once: true });

    async function refresh() {
        try {
            const response = await fetch("/Dashboard/SimulationStatus", { signal: lifetime.signal });

            if (!response.ok) {
                throw new Error("Simulation status unavailable.");
            }

            const simulation = await response.json();
            if (lifetime.signal.aborted) {
                return;
            }
            status.textContent = simulation.error || (simulation.isRunning
                ? `Running at up to ${simulation.readingsPerSecond} readings per device per second.`
                : "Simulation stopped.");

            if (initialLoad && !rate.dataset.stateRestored && document.activeElement !== rate) {
                rate.value = simulation.readingsPerSecond;
            }

            initialLoad = false;
        } catch {
            if (!lifetime.signal.aborted) {
                status.textContent = "Unable to check simulation status.";
            }
        } finally {
            if (!lifetime.signal.aborted) {
                timer = window.setTimeout(refresh, 3000);
            }
        }
    }

    refresh();
});
