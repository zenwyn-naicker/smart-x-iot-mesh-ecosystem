document.addEventListener("smartx:page-load", () => {
    const chart = document.getElementById("telemetryChart");

    if (!chart) {
        return;
    }

    const context = chart.getContext("2d");
    const themeColor = name =>
        getComputedStyle(document.documentElement)
            .getPropertyValue(name)
            .trim();
    const sensorFilter = document.getElementById("sensorFilter");
    const severityFilter =
        document.getElementById("severityFilter");

    let dashboard = null;
    const lifetime = new AbortController();
    let timer;
    let loading = false;
    const review = window.smartXReview(lifetime.signal);
    document.addEventListener("smartx:page-unload", () => {
        lifetime.abort();
        window.clearTimeout(timer);
    }, { once: true });

    async function refresh() {
        if (loading || lifetime.signal.aborted) {
            return;
        }
        loading = true;
        review.loading();
        try {
            const response = await fetch("/Dashboard/Data", {
                signal: lifetime.signal,
                headers: {
                    Accept: "application/json"
                }
            });

            if (!response.ok) {
                throw new Error("Dashboard refresh failed.");
            }

            dashboard = await response.json();
            if (lifetime.signal.aborted) {
                return;
            }

            renderMetrics();
            renderSensorFilter();
            renderSensors();
            renderIssues();
            review.update(dashboard);
            drawChart();

            document.getElementById("lastUpdated").textContent =
                `Updated ${new Date(
                    dashboard.generatedAt
                ).toLocaleTimeString()}`;
        } catch {
            if (!lifetime.signal.aborted) {
                document.getElementById("lastUpdated").textContent = "Unable to refresh dashboard";
                review.unavailable();
            }
        } finally {
            loading = false;
            if (!lifetime.signal.aborted) {
                timer = window.setTimeout(refresh, 3000);
            }
        }
    }

    function renderMetrics() {
        setText("totalCount", dashboard.totalSensors);
        setText("connectedCount", dashboard.connectedSensors);
        setText("warningCount", dashboard.warningSensors);
        setText(
            "disconnectedCount",
            dashboard.disconnectedSensors
        );
        setText("powerCount", dashboard.aggregatePowerWatts);
    }

    function renderSensorFilter() {
        const currentValue = sensorFilter.value;
        const knownIds = Array
            .from(sensorFilter.options)
            .map(option => option.value);

        for (const option of Array.from(sensorFilter.options)) {
            if (option.value && !dashboard.sensors.some(sensor => sensor.id === option.value)) {
                option.remove();
            }
        }

        for (const sensor of dashboard.sensors) {
            if (knownIds.includes(sensor.id)) {
                continue;
            }

            const option = document.createElement("option");
            option.value = sensor.id;
            option.textContent = sensor.deviceIdentifier;
            sensorFilter.appendChild(option);
        }

        if (dashboard.sensors.some(sensor => sensor.id === currentValue)) {
            sensorFilter.value = currentValue;
        } else if (dashboard.sensors.length > 0) {
            const numericSensor = dashboard.sensors.find(sensor =>
                dashboard.recentReadings.some(reading =>
                    reading.sensorId === sensor.id &&
                    reading.dataType !== "Actuator"
                )
            );

            sensorFilter.value =
                numericSensor?.id ?? dashboard.sensors[0].id;
        }
    }

    function renderSensors() {
        const body = document.getElementById("sensorRows");
        body.replaceChildren();

        if (dashboard.sensors.length === 0) {
            const row = document.createElement("tr");
            const cell = document.createElement("td");

            cell.colSpan = 5;
            cell.textContent = "No registered sensors.";
            row.appendChild(cell);
            body.appendChild(row);
            return;
        }

        for (const sensor of dashboard.sensors) {
            const row = document.createElement("tr");

            const deviceCell = document.createElement("td");
            deviceCell.appendChild(deviceLink(sensor.id, sensor.deviceIdentifier));
            row.appendChild(deviceCell);
            addCell(row, splitName(sensor.category));
            addCell(row, sensor.deploymentPath);

            const statusCell = document.createElement("td");
            const status = document.createElement("span");

            status.className =
                `status status--${sensor.status.toLowerCase()}`;
            status.textContent = sensor.status;

            statusCell.appendChild(status);
            row.appendChild(statusCell);

            addCell(
                row,
                sensor.lastHeartbeat
                    ? new Date(
                        sensor.lastHeartbeat
                    ).toLocaleString()
                    : "Not received"
            );

            body.appendChild(row);
        }
    }

    function renderIssues() {
        const list = document.getElementById("issueList");
        const severity = severityFilter.value;

        const issues = dashboard.activeIssues.filter(issue =>
            !severity || issue.severity === severity
        );

        list.replaceChildren();

        if (issues.length === 0) {
            const message = document.createElement("p");
            message.className = "muted-text";
            message.textContent = "No active issues.";
            list.appendChild(message);
            return;
        }

        for (const issue of issues) {
            const item = document.createElement("article");
            item.className = "issue-item";

            const header = document.createElement("div");
            header.className = "issue-header";

            const title = document.createElement("strong");
            title.textContent = issue.deviceIdentifier;

            const severityLabel = document.createElement("span");
            severityLabel.className =
                `severity severity--${issue.severity.toLowerCase()}`;
            severityLabel.textContent = issue.severity;

            header.append(title, severityLabel);

            const type = document.createElement("span");
            type.className = "issue-type";
            type.textContent = issue.issueType;

            const message = document.createElement("p");
            message.textContent = issue.message;

            const metadata = document.createElement("small");
            const occurrenceLabel = issue.occurrences === 1
                ? "occurrence"
                : "occurrences";

            metadata.textContent =
                `${issue.occurrences} ${occurrenceLabel} · ` +
                `Last seen ${new Date(
                    issue.lastSeen
                ).toLocaleTimeString()}`;

            item.append(
                header,
                type,
                message,
                metadata
            );

            const actions = document.createElement("div");
            actions.className = "issue-actions";
            const stream = document.createElement("a");
            stream.href = "/CommandStream?" + new URLSearchParams({
                Search: issue.deviceIdentifier,
                Alert: issue.issueType === "Disconnect" ? "Disconnected" : "Telemetry"
            });
            stream.dataset.navigation = "";
            stream.textContent = "Open command stream";
            actions.append(deviceLink(issue.sensorId, "View device"), stream);
            item.appendChild(actions);

            list.appendChild(item);
        }
    }

    function drawChart() {
        if (!dashboard) {
            return;
        }

        resizeCanvas();

        const sensorId = sensorFilter.value;
        const readings = dashboard.recentReadings
            .filter(reading =>
                reading.sensorId === sensorId &&
                reading.dataType !== "Actuator")
            .slice(-60);

        const emptyMessage =
            document.getElementById("chartEmpty");

        context.clearRect(
            0,
            0,
            chart.width,
            chart.height
        );

        if (readings.length < 2) {
            emptyMessage.hidden = false;
            return;
        }

        emptyMessage.hidden = true;

        const scale =
            window.devicePixelRatio || 1;

        const padding = {
            top: 24 * scale,
            right: 20 * scale,
            bottom: 30 * scale,
            left: 52 * scale
        };

        const values = readings.map(reading =>
            Number.parseFloat(reading.value)
        );

        const thresholds = readings
            .flatMap(reading => [
                reading.lowerThreshold,
                reading.upperThreshold
            ])
            .filter(value =>
                Number.isFinite(value)
            );

        const minimum = Math.min(
            ...values,
            ...thresholds
        );

        const maximum = Math.max(
            ...values,
            ...thresholds
        );

        const range = Math.max(maximum - minimum, 1);
        const chartWidth =
            chart.width - padding.left - padding.right;
        const chartHeight =
            chart.height - padding.top - padding.bottom;

        const x = index =>
            padding.left +
            (index / (readings.length - 1)) *
            chartWidth;

        const y = value =>
            padding.top +
            ((maximum - value) / range) *
            chartHeight;

        drawThresholdBand(
            readings,
            y,
            padding,
            chartWidth,
            scale
        );

        context.strokeStyle = themeColor("--border");
        context.lineWidth = scale;
        context.beginPath();
        context.moveTo(
            padding.left,
            padding.top + chartHeight
        );
        context.lineTo(
            padding.left + chartWidth,
            padding.top + chartHeight
        );
        context.stroke();

        context.strokeStyle = themeColor("--accent");
        context.lineWidth = 2.5 * scale;
        context.lineJoin = "round";

        let previousTimestamp = null;

        context.beginPath();

        readings.forEach((reading, index) => {
            const timestamp =
                new Date(reading.timestamp);

            const hasGap =
                previousTimestamp !== null &&
                timestamp - previousTimestamp > 15000;

            if (hasGap) {
                context.stroke();
                context.beginPath();
            }

            if (index === 0 || hasGap) {
                context.moveTo(x(index), y(values[index]));
            } else {
                context.lineTo(x(index), y(values[index]));
            }

            previousTimestamp = timestamp;
        });

        context.stroke();

        readings.forEach((reading, index) => {
            context.beginPath();
            context.fillStyle = reading.isAnomaly
                ? themeColor("--danger")
                : themeColor("--accent");

            context.arc(
                x(index),
                y(values[index]),
                reading.isAnomaly
                    ? 5 * scale
                    : 2.5 * scale,
                0,
                Math.PI * 2
            );

            context.fill();
        });

        context.fillStyle = themeColor("--muted");
        context.font = `${12 * scale}px Arial`;
        context.textAlign = "right";

        context.fillText(
            maximum.toFixed(1),
            padding.left - 8 * scale,
            padding.top + 4 * scale
        );

        context.fillText(
            minimum.toFixed(1),
            padding.left - 8 * scale,
            padding.top + chartHeight
        );
    }

    function drawThresholdBand(
        readings,
        y,
        padding,
        chartWidth,
        scale
    ) {
        const lower = readings.find(reading =>
            Number.isFinite(reading.lowerThreshold)
        )?.lowerThreshold;

        const upper = readings.find(reading =>
            Number.isFinite(reading.upperThreshold)
        )?.upperThreshold;

        if (
            !Number.isFinite(lower) ||
            !Number.isFinite(upper)
        ) {
            return;
        }

        const top = y(upper);
        const bottom = y(lower);

        context.fillStyle = themeColor("--accent-soft");
        context.fillRect(
            padding.left,
            top,
            chartWidth,
            Math.max(bottom - top, 2 * scale)
        );
    }

    function resizeCanvas() {
        const scale =
            window.devicePixelRatio || 1;

        const width =
            chart.parentElement.clientWidth;

        chart.width = width * scale;
        chart.height = 340 * scale;
        chart.style.width = `${width}px`;
        chart.style.height = "340px";
    }

    function deviceLink(sensorId, label) {
        const link = document.createElement("a");
        link.href = "/Sensors/Details/" + encodeURIComponent(sensorId);
        link.dataset.navigation = "";
        link.textContent = label;
        return link;
    }

    function addCell(row, value) {
        const cell = document.createElement("td");
        cell.textContent = value;
        row.appendChild(cell);
    }

    function setText(id, value) {
        document.getElementById(id).textContent =
            value;
    }

    function splitName(value) {
        return value.replace(
            /([a-z])([A-Z])/g,
            "$1 $2"
        );
    }

    sensorFilter.addEventListener(
        "change",
        drawChart,
        { signal: lifetime.signal }
    );

    severityFilter.addEventListener(
        "change",
        () => {
            if (dashboard) {
                renderIssues();
            }
        },
        { signal: lifetime.signal }
    );

    window.addEventListener(
        "resize",
        drawChart,
        { signal: lifetime.signal }
    );

    refresh();
});
