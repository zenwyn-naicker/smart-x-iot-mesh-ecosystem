document.addEventListener("smartx:page-load", () => {
    const input = document.getElementById("attachmentFile");
    if (!input) {
        return;
    }

    const error = document.getElementById("attachmentError");
    const lifetime = new AbortController();
    document.addEventListener("smartx:page-unload", () => lifetime.abort(), { once: true });

    function validate() {
        const file = input.files[0];
        let message = "";
        if (!file || file.size === 0) {
            message = "Choose a non-empty file.";
        } else if (file.size > Number(input.dataset.maxSize)) {
            message = "Files may not exceed 10 MB.";
        } else if (!input.accept.split(",").some(extension => file.name.toLowerCase().endsWith(extension))) {
            message = "This file type is not permitted.";
        }
        input.setCustomValidity(message);
        error.textContent = message;
        return !message;
    }

    input.addEventListener("change", validate, { signal: lifetime.signal });
    input.form.addEventListener("submit", event => {
        if (!validate()) {
            event.preventDefault();
            input.reportValidity();
        }
    }, { signal: lifetime.signal });
});
