document.addEventListener("smartx:page-load", () => {

    const sensorSelect =
        document.getElementById("sensorSelect");

    const valueInput =
        document.getElementById("telemetryValue");

    const valueHelp =
        document.getElementById("valueHelp");
    const valueError = document.querySelector('[data-valmsg-for="Value"]');

    if (!sensorSelect) {
        return;
    }
    const lifetime = new AbortController();
    document.addEventListener("smartx:page-unload", () => lifetime.abort(), { once: true });

    function validateValue(showError = false) {
        const category = sensorSelect.options[sensorSelect.selectedIndex]?.dataset.category;
        const value = valueInput.value.trim();
        let message = "";

        if (value && category === "Environmental" &&
            (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/.test(value) ||
                !Number.isFinite(Math.fround(Number(value))))) {
            message = "Enter a finite decimal value.";
        } else if (value && category === "PowerConsumption" &&
            (!/^[+-]?\d+$/.test(value) || Number(value) < -2147483648 || Number(value) > 2147483647)) {
            message = "Enter a valid whole-number wattage.";
        } else if (value && category === "Actuator" && !/^(true|false)$/i.test(value)) {
            message = "Enter true or false.";
        }

        valueInput.setCustomValidity(message);
        if (showError) {
            valueError.textContent = message;
            valueError.classList.toggle("field-validation-error", Boolean(message));
            valueError.classList.toggle("field-validation-valid", !message);
        }
    }

    function updateHelp() {
        const option =
            sensorSelect.options[sensorSelect.selectedIndex];

        const category = option?.dataset.category;

        if (category === "Environmental") {
            valueInput.placeholder = "Example: 23.5";
            valueHelp.textContent =
                "Enter a decimal temperature in °C.";
        } else if (category === "PowerConsumption") {
            valueInput.placeholder = "Example: 1450";
            valueHelp.textContent =
                "Enter a whole-number power value in watts.";
        } else if (category === "Actuator") {
            valueInput.placeholder = "true or false";
            valueHelp.textContent =
                "Enter true for on or false for off.";
        } else {
            valueInput.placeholder =
                "Select a sensor first";
            valueHelp.textContent =
                "The expected type changes with the sensor.";
        }
        valueInput.inputMode = category === "Environmental" || category === "PowerConsumption"
            ? "decimal" : "text";
        validateValue();
    }

    sensorSelect.addEventListener("change", () => {
        updateHelp();
        validateValue(true);
    }, { signal: lifetime.signal });
    valueInput.addEventListener("input", () => validateValue(true), { signal: lifetime.signal });
    valueInput.form.addEventListener("submit", event => {
        validateValue(true);
        if (!valueInput.checkValidity()) {
            event.preventDefault();
            valueInput.reportValidity();
        }
    }, { signal: lifetime.signal });
    updateHelp();

});
