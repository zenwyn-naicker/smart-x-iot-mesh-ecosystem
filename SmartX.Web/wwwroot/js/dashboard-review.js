(() => {
    const storageKey = "smartx.dashboardReview";
    let savedReview = null;
    let storageAvailable = true;
    let notice = "";

    function readReview() {
        if (!storageAvailable) {
            return;
        }

        let stored;
        try {
            stored = localStorage.getItem(storageKey);
        } catch {
            storageAvailable = false;
            return;
        }

        try {
            const value = stored ? JSON.parse(stored) : null;
            if (value && (value.version !== 1 || typeof value.instanceId !== "string" || !value.instanceId ||
                typeof value.reviewedAt !== "string" || !Number.isFinite(Date.parse(value.reviewedAt)) || !Array.isArray(value.issues) ||
                !value.issues.every(issue => issue && ["id", "sensorId", "deviceIdentifier", "issueType"]
                    .every(field => typeof issue[field] === "string" && issue[field].length > 0)))) {
                throw new Error("Invalid review.");
            }
            savedReview = value;
        } catch {
            clearReview("Your saved review could not be read. Save a new review to begin comparing issues.");
        }
    }

    function clearReview(message) {
        savedReview = null;
        notice = message;
        try {
            localStorage.removeItem(storageKey);
        } catch {
            storageAvailable = false;
        }
    }

    function key(issue) {
        return issue.sensorId + ":" + issue.issueType;
    }

    window.smartXReview = signal => {
        const button = document.getElementById("markReviewed");
        const status = document.getElementById("reviewStatus");
        const groups = document.getElementById("reviewGroups");
        let dashboard = null;
        let rendered = null;
        readReview();

        function render() {
            const storageMessage = storageAvailable
                ? "Saved reviews stay in this browser."
                : "Browser storage is unavailable; reviews are kept only for this visit.";
            status.textContent = (savedReview
                ? "Last reviewed " + new Date(savedReview.reviewedAt).toLocaleString() + ". "
                : (notice || "Save your first review to compare changes when you return.") + " ") + storageMessage;
            groups.hidden = !savedReview;
            if (!savedReview || !dashboard) {
                rendered = null;
                return;
            }

            const current = new Map(dashboard.activeIssues.map(issue => [key(issue), issue]));
            const previous = new Map(savedReview.issues.map(issue => [key(issue), issue]));
            const sensors = new Set(dashboard.sensors.map(sensor => sensor.id));
            const changes = {
                New: dashboard.activeIssues.filter(issue => previous.get(key(issue))?.id !== issue.id),
                Ongoing: dashboard.activeIssues.filter(issue => previous.get(key(issue))?.id === issue.id),
                Recovered: savedReview.issues.filter(issue => !current.has(key(issue)) && sensors.has(issue.sensorId))
            };
            const signature = JSON.stringify(Object.values(changes).map(issues =>
                issues.map(issue => [issue.id, issue.sensorId, issue.deviceIdentifier, issue.issueType])));
            if (signature === rendered) {
                return;
            }
            rendered = signature;

            for (const [name, issues] of Object.entries(changes)) {
                document.getElementById("review" + name + "Count").textContent = issues.length;
                const list = document.getElementById("review" + name + "Issues");
                list.replaceChildren();
                if (issues.length === 0) {
                    const empty = document.createElement("li");
                    empty.className = "muted-text";
                    empty.textContent = "No " + name.toLowerCase() + " issues.";
                    list.appendChild(empty);
                }
                for (const issue of issues) {
                    const item = document.createElement("li");
                    const link = document.createElement("a");
                    link.href = "/Sensors/Details/" + encodeURIComponent(issue.sensorId);
                    link.dataset.navigation = "";
                    link.textContent = issue.deviceIdentifier;
                    const type = document.createElement("small");
                    type.textContent = issue.issueType === "Disconnect" ? "Connection" : "Telemetry";
                    item.append(link, type);
                    list.appendChild(item);
                }
            }
        }

        function update(data) {
            dashboard = data;
            if (savedReview && savedReview.instanceId !== data.instanceId) {
                clearReview("Gateway data has restarted. Save a new review to begin comparing issues.");
            }
            button.disabled = false;
            render();
        }

        button.addEventListener("click", () => {
            if (!dashboard || button.disabled) {
                return;
            }
            savedReview = {
                version: 1,
                instanceId: dashboard.instanceId,
                reviewedAt: dashboard.generatedAt,
                issues: dashboard.activeIssues.map(issue => ({
                    id: issue.id,
                    sensorId: issue.sensorId,
                    deviceIdentifier: issue.deviceIdentifier,
                    issueType: issue.issueType
                }))
            };
            notice = "";
            try {
                localStorage.setItem(storageKey, JSON.stringify(savedReview));
            } catch {
                storageAvailable = false;
            }
            render();
        }, { signal });

        window.addEventListener("storage", event => {
            if (event.key === storageKey || event.key === null) {
                readReview();
                if (dashboard && savedReview && savedReview.instanceId !== dashboard.instanceId) {
                    groups.hidden = true;
                    button.disabled = true;
                    status.textContent = "Waiting for current data before comparing the updated review.";
                } else if (dashboard) {
                    render();
                }
            }
        }, { signal });

        return {
            update,
            loading() {
                button.disabled = true;
            },
            unavailable() {
                button.disabled = true;
                status.textContent = "Live updates are unavailable. The comparison may be out of date; your saved review is unchanged.";
            }
        };
    };
})();
