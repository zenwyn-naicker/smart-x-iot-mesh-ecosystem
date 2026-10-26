(() => {
    const storageKey = "smartx.moduleState";
    let saved = { pages: {}, commandStreamUrl: null };
    let skipSave = false;
    try {
        const stored = JSON.parse(sessionStorage.getItem(storageKey));
        if (stored && typeof stored.pages === "object" && stored.pages !== null) {
            saved = stored;
        }
    } catch {
        saved = { pages: {}, commandStreamUrl: null };
    }

    function key(url) {
        const value = new URL(url, window.location.origin);
        return value.pathname.toLowerCase().replace(/\/index\/?$/, "").replace(/\/$/, "") + value.search;
    }

    function persist() {
        try {
            sessionStorage.setItem(storageKey, JSON.stringify(saved));
        } catch {
        }
    }

    function fields() {
        return Array.from(document.querySelectorAll(
            "#pageContent [data-preserve-state] input, #pageContent [data-preserve-state] select, " +
            "#pageContent [data-preserve-state] textarea, #pageContent input[data-preserve-state], " +
            "#pageContent select[data-preserve-state]"))
            .filter(field => field.id && !["hidden", "password", "file", "submit", "button"].includes(field.type));
    }

    window.smartXState = {
        save(url) {
            if (skipSave) {
                return;
            }
            const pageKey = key(url);
            delete saved.pages[pageKey];
            saved.pages[pageKey] = {
                fields: Object.fromEntries(fields().map(field => [field.id, field.value])),
                scrollY: window.scrollY,
                tables: Object.fromEntries(Array.from(document.querySelectorAll("#pageContent .table-shell[aria-label]"),
                    table => [table.getAttribute("aria-label"), table.scrollLeft]))
            };
            const keys = Object.keys(saved.pages);
            for (const oldKey of keys.slice(0, Math.max(0, keys.length - 20))) {
                delete saved.pages[oldKey];
            }
            persist();
        },
        restore(url) {
            skipSave = false;
            const pageKey = key(url);
            if (pageKey.split("?")[0] === "/commandstream") {
                saved.commandStreamUrl = new URL(url).pathname + new URL(url).search;
                persist();
            }
            const page = saved.pages[pageKey];
            if (!page || typeof page.fields !== "object" || page.fields === null) {
                return 0;
            }
            for (const field of fields()) {
                const value = page.fields[field.id];
                if (typeof value !== "string" || field instanceof HTMLSelectElement &&
                    !Array.from(field.options).some(option => option.value === value)) {
                    continue;
                }
                field.value = value;
                field.dataset.stateRestored = "true";
            }
            for (const table of document.querySelectorAll("#pageContent .table-shell[aria-label]")) {
                const position = page.tables?.[table.getAttribute("aria-label")];
                if (Number.isFinite(position)) {
                    table.scrollLeft = Math.max(0, position);
                }
            }
            return Number.isFinite(page.scrollY) ? Math.max(0, page.scrollY) : 0;
        },
        resolve(url) {
            if (key(url) === "/commandstream" && typeof saved.commandStreamUrl === "string" &&
                saved.commandStreamUrl.startsWith("/") && !saved.commandStreamUrl.startsWith("//") &&
                key(saved.commandStreamUrl).split("?")[0] === "/commandstream") {
                return new URL(saved.commandStreamUrl, window.location.origin).href;
            }
            return url;
        }
    };

    document.addEventListener("submit", event => {
        if (!event.defaultPrevented && event.target.matches("form[data-preserve-state]")) {
            delete saved.pages[key(window.location.href)];
            if (key(window.location.href).split("?")[0] === "/commandstream") {
                for (const pageKey of Object.keys(saved.pages)) {
                    if (pageKey.split("?")[0] === "/commandstream") {
                        delete saved.pages[pageKey];
                    }
                }
            }
            skipSave = true;
            persist();
        }
    });

    document.addEventListener("click", event => {
        if (!event.target.closest("[data-reset-state]")) {
            return;
        }
        for (const pageKey of Object.keys(saved.pages)) {
            if (pageKey.split("?")[0] === "/commandstream") {
                delete saved.pages[pageKey];
            }
        }
        saved.commandStreamUrl = null;
        skipSave = true;
        persist();
    });
})();
