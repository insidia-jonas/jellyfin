/* Bounded cache for our own channel logos. Never patches image setters or ApiClient. */
(function () {
    if (window.FireTvLogos) { return; }
    var entries = new Map();
    var pending = {};
    var queue = [];
    var active = 0;
    var dbPromise;
    var MAX_BYTES = 8 * 1024 * 1024;

    function database() {
        if (!window.indexedDB) { return Promise.resolve(null); }
        if (!dbPromise) {
            dbPromise = new Promise(function (resolve) {
                var request;
                try { request = indexedDB.open("firetv-channel-logos", 1); } catch (e) { resolve(null); return; }
                request.onupgradeneeded = function () { request.result.createObjectStore("logos", { keyPath: "key" }); };
                request.onsuccess = function () { resolve(request.result); };
                request.onerror = request.onblocked = function () { resolve(null); };
            });
        }
        return dbPromise;
    }

    function read(key) {
        return database().then(function (db) {
            if (!db) { return null; }
            return new Promise(function (resolve) {
                try {
                    var request = db.transaction("logos").objectStore("logos").get(key);
                    request.onsuccess = function () {
                        var record = request.result || null;
                        // Blob URLs belong to one document and cannot survive a restart.
                        if (record) { delete record.objectUrl; }
                        resolve(record);
                    };
                    request.onerror = function () { resolve(null); };
                } catch (e) { resolve(null); }
            });
        });
    }

    function write(record) {
        database().then(function (db) {
            if (!db) { return; }
            try {
                var store = db.transaction("logos", "readwrite").objectStore("logos");
                store.put({ key: record.key, url: record.url, blob: record.blob, used: record.used });
                var request = store.getAll();
                request.onsuccess = function () {
                    var rows = request.result.sort(function (a, b) { return b.used - a.used; });
                    var bytes = 0;
                    rows.forEach(function (row, index) {
                        bytes += row.blob.size;
                        if (index >= 96 || bytes > MAX_BYTES) { store.delete(row.key); }
                    });
                };
            } catch (e) { /* Cache is optional, e.g. storage quota exhausted. */ }
        });
    }

    function remember(record) {
        var previous = entries.get(record.key);
        if (previous && previous.objectUrl) { URL.revokeObjectURL(previous.objectUrl); }
        record.objectUrl = URL.createObjectURL(record.blob);
        entries.delete(record.key);
        entries.set(record.key, record);
        var bytes = 0;
        entries.forEach(function (entry) { bytes += entry.blob.size; });
        while (entries.size > 32 || bytes > 3 * 1024 * 1024) {
            var key = entries.keys().next().value;
            var old = entries.get(key);
            bytes -= old.blob.size;
            URL.revokeObjectURL(old.objectUrl);
            entries.delete(key);
        }
        return record;
    }

    function drain() {
        while (active < 3 && queue.length) {
            (function (job) {
                active++;
                job.run().then(job.resolve, function () { job.resolve(null); }).then(function () { active--; drain(); });
            })(queue.shift());
        }
    }

    function load(key, url, token, isVisible) {
        if (pending[key]) { return pending[key]; }
        pending[key] = new Promise(function (resolve) {
            queue.push({ resolve: resolve, run: function () {
                if (!isVisible()) { return Promise.resolve(null); }
                var old = entries.get(key);
                return (old ? Promise.resolve(old) : read(key)).then(function (cached) {
                    if (cached && cached.url === url && Date.now() - cached.used < 7 * 86400000) {
                        return cached.objectUrl ? cached : remember(cached);
                    }
                    var headers = token ? { "X-Emby-Token": token } : {};
                    var controller = window.AbortController ? new AbortController() : null;
                    var timeout = window.setTimeout(function () { if (controller) { controller.abort(); } }, 6000);
                    return fetch(url, { headers: headers, credentials: "same-origin", cache: "force-cache", signal: controller ? controller.signal : undefined })
                        .then(function (response) {
                            if (!response.ok || !/^image\//i.test(response.headers.get("content-type") || "")) { throw new Error("No logo"); }
                            return response.blob();
                        }).then(function (blob) {
                            if (blob.size > 512 * 1024) { throw new Error("Logo too large"); }
                            var record = { key: key, url: url, blob: blob, used: Date.now() };
                            write(record);
                            return remember(record);
                        }).catch(function () { return cached ? (cached.objectUrl ? cached : remember(cached)) : null; })
                        .then(function (record) { window.clearTimeout(timeout); return record; });
                });
            } });
            while (queue.length > 32) { queue.shift().resolve(null); }
            drain();
        }).then(function (record) { delete pending[key]; return record; });
        return pending[key];
    }

    window.FireTvLogos = {
        mount: function (image, key, url, token) {
            if (!window.fetch || !URL.createObjectURL) { image.src = url; return; }
            image.setAttribute("data-logo-key", key);
            load(key, url, token, function () { return image.isConnected; }).then(function (record) {
                if (!image.isConnected || image.getAttribute("data-logo-key") !== key) { return; }
                if (record) { image.src = record.objectUrl; }
                else { image.src = url; }
            });
        }
    };
})();
