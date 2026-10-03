/* Remote navigation for detail actions, including injected Indexer/subtitle controls. */
(function () {
    'use strict';
    if (window.FireTvDetailNavigation) { return; }
    var targets = 'button,a[href],input,select,textarea,summary,[role="button"],[data-action][tabindex],.card[tabindex],.listItem[tabindex]';
    var lastEnter = false;
    function visible(el) {
        if (!el || el.disabled || el.getAttribute('aria-disabled') === 'true' || el.tabIndex < 0) { return false; }
        if (el.closest('[hidden],[inert],[aria-hidden="true"],.hide')) { return false; }
        var parent = el.parentElement;
        while (parent) {
            if (parent.tagName === 'DETAILS' && !parent.open && el !== parent.querySelector('summary')) { return false; }
            var style = getComputedStyle(parent);
            if (style.display === 'none' || style.visibility === 'hidden') { return false; }
            parent = parent.parentElement;
        }
        var box = el.getBoundingClientRect(), ownStyle = getComputedStyle(el);
        return box.width > 0 && box.height > 0 && ownStyle.display !== 'none' && ownStyle.visibility !== 'hidden';
    }
    function scope() {
        if (!/#\/(?:details|itemdetails)(?:[.?/]|$)/i.test(location.hash)) { return null; }
        var dialogs = Array.prototype.filter.call(document.querySelectorAll('[role="dialog"],dialog[open],.dialog'), function (d) {
            var box = d.getBoundingClientRect(); return box.width && box.height && !d.closest('[hidden],.hide');
        });
        return dialogs.length ? dialogs[dialogs.length - 1] : document;
    }
    function choose(elements, current, key) {
        var origin = current && current.getBoundingClientRect();
        if (!origin || (!origin.width && !origin.height) || current === document.body) { return elements[0]; }
        var vertical = key === 'ArrowDown' || key === 'ArrowUp', positive = key === 'ArrowDown' || key === 'ArrowRight';
        var cx = origin.left + origin.width / 2, cy = origin.top + origin.height / 2, best, bestScore = Infinity;
        elements.forEach(function (el) {
            if (el === current || el.contains(current)) { return; }
            var r = el.getBoundingClientRect(), dx = r.left + r.width / 2 - cx, dy = r.top + r.height / 2 - cy;
            var forward = (vertical ? dy : dx) * (positive ? 1 : -1);
            if (forward <= 2) { return; }
            var overlap = vertical ? r.right > origin.left + 2 && r.left < origin.right - 2 : r.bottom > origin.top + 2 && r.top < origin.bottom - 2;
            var cross = Math.abs(vertical ? dx : dy);
            var score = forward + cross * (overlap ? 0.15 : 2.5) + (overlap ? 0 : 120);
            if (score < bestScore) { best = el; bestScore = score; }
        });
        return best;
    }
    function stop(event) { event.preventDefault(); event.stopImmediatePropagation(); }
    window.addEventListener('keydown', function (event) {
        var root = scope(); if (!root || event.altKey || event.ctrlKey || event.metaKey) { return; }
        var key = event.key || ({37:'ArrowLeft',38:'ArrowUp',39:'ArrowRight',40:'ArrowDown',13:'Enter'})[event.keyCode];
        var current = document.activeElement;
        if (current && (current.matches('input,textarea') || current.isContentEditable)) { return; }
        // Enter opens the native select popup. While closed, D-pad can leave it for Search.
        if (key === 'Enter' && current && current.tagName === 'SELECT') { return; }
        if (key === 'Enter' && current && current.matches(targets) && visible(current)) {
            stop(event); lastEnter = true;
            if (!event.repeat) { current.click(); }
            return;
        }
        if (!/^Arrow(Up|Down|Left|Right)$/.test(key)) { return; }
        var elements = Array.prototype.filter.call(root.querySelectorAll(targets), visible);
        if (!elements.length) { return; }
        var next = choose(elements, current, key);
        stop(event);
        var selection = window.getSelection(); if (selection && selection.rangeCount) { selection.removeAllRanges(); }
        if (!next) { return; }
        next.focus({preventScroll:true});
        next.scrollIntoView({block:'nearest',inline:'nearest',behavior:'auto'});
        if (selection && selection.rangeCount) { selection.removeAllRanges(); }
    }, true);
    window.addEventListener('keyup', function (event) {
        if ((event.key === 'Enter' || event.keyCode === 13) && lastEnter) { lastEnter = false; stop(event); }
    }, true);
    window.FireTvDetailNavigation = { choose: choose };
})();
