/* Background work belongs to the server; this small observer survives SPA navigation. */
(function () {
    'use strict';
    if (window.EvolutionSubtitleJobs) { return; }
    var jobs = [], listeners = [], account = '', generation = 0, busy = false, timer, trigger, dialog, lastFocus, serviceError = '';
    function api() { return window.ApiClient; }
    function identity() {
        var a = api();
        return a && a.getCurrentUserId && a.getCurrentUserId() ? (a.serverId ? a.serverId() : '') + ':' + a.getCurrentUserId() : '';
    }
    function request(route, params, method) {
        return api().ajax({url:api().getUrl('TreasureMaps/Subtitles/' + route, params || {}),type:method || 'GET',dataType:'json'});
    }
    function el(tag, className, text) {
        var node = document.createElement(tag); node.className = className || ''; if (text) { node.textContent = text; } return node;
    }
    function button(label, fn) {
        var node = el('button', '', label); node.type = 'button'; node.addEventListener('click',fn); return node;
    }
    function close() {
        if (dialog) { dialog.remove(); dialog = null; }
        if (lastFocus && lastFocus.isConnected) { lastFocus.focus(); }
    }
    function notify() {
        listeners = listeners.filter(function (listener) {
            if (!listener.node.isConnected) { return false; }
            listener.fn(jobs); return true;
        });
        mount();
        if (trigger) {
            var active = jobs.filter(function (j) { return j.active; }).length;
            trigger.textContent = 'Untertitelaufträge' + (active ? ' · ' + active : '');
            trigger.setAttribute('aria-label','Untertitelaufträge' + (active ? ', ' + active + ' aktiv' : ''));
        }
        render();
    }
    function mount() {
        if (!account || identity() !== account) { return; }
        // Jellyfin keeps the legacy header mounted but hidden on React routes.
        var header = document.querySelector('header.MuiAppBar-root .MuiToolbar-root') || document.querySelector('.headerRight');
        if (!header) { return; }
        if (!trigger) { trigger = button('Untertitelaufträge', open); trigger.className = 'evolution-jobs-trigger'; }
        if (trigger.parentElement !== header) { header.appendChild(trigger); }
    }
    function poll() {
        clearTimeout(timer);
        var key = identity();
        if (key !== account) {
            account = key; generation++; busy = false; jobs = []; listeners = [];
            close(); if (trigger) { trigger.remove(); trigger = null; }
        }
        if (!key || document.hidden || busy) { timer = setTimeout(poll, 2000); return; }
        var ticket = generation; busy = true;
        request('Jobs').then(function (res) {
            if (ticket !== generation || identity() !== account) { return; }
            jobs = (res && res.jobs) || []; serviceError = (res && res.error) || ''; notify();
        }, function () {
            if (dialog && ticket === generation) { dialog.querySelector('.evolution-jobs-note').textContent = 'Server derzeit nicht erreichbar. Die Aufträge laufen auf dem Server weiter.'; }
        }).then(function () {
            if (ticket !== generation) { return; }
            busy = false; timer = setTimeout(poll, dialog || jobs.some(function (j) { return j.active; }) ? 3000 : 20000);
        });
    }
    function render() {
        if (!dialog) { return; }
        var list = dialog.querySelector('.evolution-jobs-list');
        dialog.querySelector('.evolution-jobs-note').textContent = serviceError || 'Läuft auf deinem Server weiter – auch wenn du einen anderen Film öffnest oder die App schließt.';
        if (!jobs.length) {
            list.textContent = 'Noch keine Untertitelaufträge. Starte sie beim Film unter „Untertitel“. '; return;
        }
        var empty = list.querySelector('.evolution-job');
        if (!empty) { list.textContent = ''; }
        jobs.slice(0,20).forEach(function (job) {
            var row = list.querySelector('[data-job="' + job.id + '"]');
            if (!row) {
                row = el('article','evolution-job'); row.dataset.job = job.id;
                row.appendChild(el('strong','evolution-job-title'));
                row.appendChild(el('span','evolution-job-state'));
                var progress = el('progress'); progress.max = 100; progress.setAttribute('aria-label','Verarbeitete Schritte'); row.appendChild(progress);
                row.appendChild(el('p','evolution-job-message'));
                row.appendChild(button('Zum Film',function () { close(); location.hash = '#/details?id=' + job.itemId; }));
                var cancel = button('Abbrechen',function () {
                    if (!window.confirm(job.kind === 'sync' ? 'Lokalen Tonspur-Abgleich abbrechen?' : 'Untertitelauftrag abbrechen? Bereits verarbeitete Schritte werden gesichert. Anbieter-Kosten können bereits angefallen sein.')) { return; }
                    cancel.disabled = true;
                    request('Jobs/' + job.id + '/Cancel',{},'POST').then(poll,function () { cancel.disabled = false; });
                });
                cancel.className = 'evolution-job-cancel'; row.appendChild(cancel);
                var retry = button('Fortsetzen / erneut versuchen',function () { restart(job, retry); });
                retry.className = 'evolution-job-retry'; row.appendChild(retry);
                list.appendChild(row);
            }
            row.querySelector('.evolution-job-title').textContent = (job.title || 'Film') + ' · ' + (job.language || 'und').toUpperCase() + (job.kind === 'sync' ? ' · Tonspur-Abgleich' : '');
            var states = {queued:'Warteschlange',running:'In Arbeit',cancelling:'Wird abgebrochen',cancelled:'Abgebrochen',interrupted:'Unterbrochen',failed:'Fehlgeschlagen',completed:'Fertig'};
            row.querySelector('.evolution-job-state').textContent = (states[job.state] || job.state) + (job.active ? ' · ' + job.percent + ' % der Schritte' : '');
            row.querySelector('progress').value = job.percent;
            row.querySelector('.evolution-job-message').textContent = job.message;
            row.querySelector('.evolution-job-cancel').hidden = !job.active;
            row.querySelector('.evolution-job-cancel').disabled = job.state === 'cancelling';
            row.querySelector('.evolution-job-retry').hidden = job.active || job.state === 'completed';
        });
        Array.prototype.forEach.call(list.querySelectorAll('.evolution-job'),function (row) {
            if (!jobs.slice(0,20).some(function (j) { return j.id === row.dataset.job; })) { row.remove(); }
        });
    }
    function restart(job, btn) {
        if (job.kind === 'sync') { close(); location.hash = '#/details?id=' + job.itemId; return; }
        var ticket = generation; btn.disabled = true;
        request('Quote',{itemId:job.itemId,language:job.language}).then(function (q) {
            if (ticket !== generation || !q || !q.ok || !Number.isFinite(q.totalUsd)) { throw new Error('Keine gültige Kostenschätzung.'); }
            if (!window.confirm('Untertitel fortsetzen?\n\n' + q.summary + '\n\nGesicherte Schritte werden wiederverwendet. Die Schätzung gilt für eine vollständige Erstellung; tatsächliche Restkosten können geringer sein.')) { return; }
            return request('Generate',{itemId:job.itemId,language:job.language,force:false,confirmed:true,maxEstimatedUsd:q.totalUsd},'POST').then(poll);
        }).catch(function () { if (btn.isConnected) { btn.textContent = 'Derzeit nicht möglich · erneut versuchen'; } })
            .then(function () { btn.disabled = false; });
    }
    function open() {
        if (dialog) { return; }
        lastFocus = document.activeElement;
        dialog = el('section','evolution-jobs-dialog'); dialog.id = 'evolution-subtitle-jobs';
        dialog.setAttribute('role','dialog'); dialog.setAttribute('aria-modal','true'); dialog.setAttribute('aria-labelledby','evolution-jobs-heading');
        var h = el('h2','','Deine Untertitelaufträge'); h.id = 'evolution-jobs-heading'; dialog.appendChild(h);
        var exit = button('Schließen',close); exit.className = 'evolution-jobs-close'; dialog.appendChild(exit);
        dialog.appendChild(el('p','evolution-jobs-note'));
        dialog.appendChild(el('div','evolution-jobs-list'));
        document.body.appendChild(dialog); render(); exit.focus(); poll();
    }
    window.addEventListener('keydown',function (e) {
        if (!dialog) { return; }
        if (e.key === 'Escape' || e.key === 'BrowserBack') { e.preventDefault(); e.stopImmediatePropagation(); close(); }
        if (e.key === 'Tab') {
            var buttons = Array.prototype.filter.call(dialog.querySelectorAll('button'),function (b) { return !b.hidden && !b.disabled; });
            var index = buttons.indexOf(document.activeElement);
            e.preventDefault(); buttons[(index + (e.shiftKey ? -1 : 1) + buttons.length) % buttons.length].focus();
        }
    },true);
    window.EvolutionSubtitleJobs = {
        open:open, close: function () { if (!dialog) { return false; } close(); return true; }, refresh:poll,
        subscribe:function (node, fn) {
            var key = identity();
            if (key !== account) { account = key; generation++; busy = false; jobs = []; listeners = []; close(); }
            listeners.push({node:node,fn:fn}); fn(jobs);
        },
        accept:function (job) { if (job) { jobs = [job].concat(jobs.filter(function (j) { return j.id !== job.id; })); notify(); } poll(); }
    };
    document.addEventListener('visibilitychange',function () { if (!document.hidden) { poll(); } });
    var mountTimer;
    new MutationObserver(function () {
        if (mountTimer) { return; }
        mountTimer = setTimeout(function () { mountTimer = null; mount(); },250);
    }).observe(document.documentElement,{childList:true,subtree:true});
    timer = setTimeout(poll,1000);
})();
