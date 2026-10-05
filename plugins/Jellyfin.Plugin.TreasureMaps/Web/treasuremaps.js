/* Treasure-Maps client enhancements (injected into the Jellyfin web client):
   - Movie details pages of Treasure-Maps titles get a proper "Releases" LIST with a
     Download button per release (instead of the generic children card row).
   - Downloads is a title+poster+progress list (not a grid of quality strings),
     filtered to Treasure-Maps grabs, with large tap targets for phones and TV.
   - Downloads show live SABnzbd status (progress %, speed, ETA, completed/failed).
   - Hover play overlays are hidden on Treasure-Maps channel pages (nothing is playable). */
(function () {
    'use strict';

    var pollTimer = null;
    var lastHash = null;
    var searchRequest = null;

    var style = document.createElement('style');
    style.textContent =
        '.tmChannelPage .cardOverlayFab-primary{display:none!important}' +
        '.tmChannelPage .btnPlayAll,.tmChannelPage .btnShuffle{display:none!important}' +
        '.tmCategoryPage .alphaPicker{display:none!important}' +
        '.tmCategoryPage .card{width:25%!important}' +
        '.tmCategoryPage .cardPadder{padding-bottom:60%!important}' +
        '.tmChannelPage .cardText-first{white-space:normal;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;line-height:1.35;min-height:2.7em}' +
        '.tmChannelPage .cardImageContainer{background-size:cover}' +
        '#tmArrRequest{margin:1em 0;display:flex;flex-wrap:wrap;gap:.75em;align-items:center}' +
        '#tmArrRequest[hidden]{display:none}' +
        '#tmArrRequest button:focus{outline:3px solid #fff;outline-offset:3px}' +
        '.tmLanguageGroup{margin:.65em 0;border:1px solid rgba(255,255,255,.2);border-radius:10px;padding:.4em .8em}' +
        '.tmLanguageGroup>summary{cursor:pointer;padding:.65em .2em;font-size:1.1em;font-weight:650;min-height:1.5em}' +
        '.tmLanguageGroup>summary:focus{outline:3px solid #00a4dc;outline-offset:3px}' +
        '.tmTitlePage .emby-scroller{max-width:100%;overflow:hidden}' +
        '.tmBrowseHeading{margin:1em 0 .6em;font-size:1.7em;font-weight:650}' +
        '.tmBrowsePages{display:flex;flex-wrap:wrap;gap:.6em;margin:0 0 1.2em}' +
        '.tmBrowsePages a{padding:.65em 1em;border:1px solid rgba(255,255,255,.3);border-radius:.5em;color:inherit;text-decoration:none}' +
        '.tmBrowsePages a:focus,.tmBrowsePages a:hover{outline:2px solid #74b9ff;background:rgba(255,255,255,.1)}' +
        '.tmLoadMessage{padding:1em 0;color:inherit;opacity:.85}' +
        '.tmRetry{margin-left:1em;padding:.6em 1em;border:1px solid currentColor;border-radius:.5em;background:transparent;color:inherit;cursor:pointer}' +
        '@media(max-width:700px){.tmCategoryPage .card{width:50%!important}}' +
        '#tmReleases{margin:1.2em 0;max-width:100%;box-sizing:border-box}' +
        '#tmReleases .tmRelRow{display:flex;flex-wrap:wrap;align-items:flex-start;gap:.55em .75em;' +
        'padding:.65em .8em;margin:.35em 0;border-radius:12px;background:rgba(255,255,255,.07);' +
        'border:1px solid rgba(255,255,255,.1);box-sizing:border-box;max-width:100%}' +
        '#tmReleases .tmRelRow:hover{background:rgba(255,255,255,.12)}' +
        '#tmReleases .tmRelName{flex:1 1 12rem;min-width:0;white-space:normal;overflow:visible;' +
        'overflow-wrap:anywhere;word-break:break-word;line-height:1.35}' +
        '#tmReleases .tmRelMeta{display:flex;flex:0 1 auto;flex-wrap:wrap;align-items:center;gap:.55em;' +
        'margin-left:auto;max-width:100%}' +
        '#tmReleases .tmRelStatus{flex:1 1 auto;font-size:.9em;opacity:.9;min-width:0;text-align:right}' +
        '#tmReleases .tmRelStatus:empty{display:none}' +
        '#tmReleases .tmDl,#tmArrRequest .tmDl{flex:0 0 auto;border:none;border-radius:999px;padding:.6em 1.1em;cursor:pointer;min-height:2.5em;' +
        'background:#0a84ff;color:#fff;font-weight:600;font-family:inherit;white-space:nowrap}' +
        '#tmReleases .tmDl:disabled,#tmArrRequest .tmDl:disabled{background:rgba(255,255,255,.18);cursor:default}' +
        '@media (max-width:700px){' +
        '#tmReleases .tmRelRow{flex-direction:column;align-items:stretch}' +
        '#tmReleases .tmRelName{flex:1 1 auto}' +
        '#tmReleases .tmRelMeta{margin-left:0;width:100%;justify-content:space-between}' +
        '#tmReleases .tmRelStatus{text-align:left}' +
        '}' +
        '.tmDetailDl{margin-left:.4em}' +
        '#tmDownloads{margin:.4em 0 1.6em;max-width:52rem;box-sizing:border-box}' +
        '#tmDownloads .tmDlRow{display:flex;align-items:stretch;gap:1rem;padding:1rem 1.1rem;' +
        'margin:.65em 0;border-radius:16px;background:rgba(255,255,255,.07);' +
        'border:1px solid rgba(255,255,255,.12);box-sizing:border-box;min-height:7.25rem;' +
        'cursor:pointer;-webkit-tap-highlight-color:transparent}' +
        '#tmDownloads .tmDlRow:hover,#tmDownloads .tmDlRow:focus{background:rgba(255,255,255,.13);outline:none}' +
        '#tmDownloads .tmDlPoster{flex:0 0 5.4rem;width:5.4rem;height:8.1rem;border-radius:10px;' +
        'background:rgba(0,0,0,.35) center/cover no-repeat;overflow:hidden}' +
        '#tmDownloads .tmDlBody{flex:1 1 auto;min-width:0;display:flex;flex-direction:column;justify-content:center;gap:.35em}' +
        '#tmDownloads .tmDlTitle{font-size:1.2em;font-weight:700;line-height:1.25;overflow-wrap:anywhere}' +
        '#tmDownloads .tmDlMeta{font-size:.92em;opacity:.85;overflow-wrap:anywhere}' +
        '#tmDownloads .tmDlBar{height:8px;border-radius:99px;background:rgba(255,255,255,.12);overflow:hidden}' +
        '#tmDownloads .tmDlBar>span{display:block;height:100%;width:0;background:#0a84ff;border-radius:99px}' +
        '#tmDownloads .tmDlActions{display:flex;flex-direction:column;gap:.5rem;align-self:center;flex:0 0 auto}' +
        '#tmDownloads .tmDlOpen,#tmDownloads .tmDlRemove{border:none;border-radius:999px;' +
        'min-height:2.75rem;padding:.55em 1.15em;font-weight:600;font-family:inherit;cursor:pointer}' +
        '#tmDownloads .tmDlOpen{background:#0a84ff;color:#fff}' +
        '#tmDownloads .tmDlRemove{background:transparent;color:#fff;border:1px solid rgba(255,255,255,.38)}' +
        '#tmDownloads .tmDlRemove:disabled{opacity:.45;cursor:default}' +
        '#tmSearchHits{margin:0 0 1.4em}' +
        '#tmSearchHits h2{font-size:1.15em;margin:0 0 .6em}' +
        '#tmSearchHits .tmSearchRow{display:flex;gap:.85em;overflow-x:auto;padding:.2em 0 0.6em}' +
        '#tmSearchHits .tmSearchCard{flex:0 0 7.2rem;width:7.2rem;text-decoration:none;color:inherit}' +
        '#tmSearchHits .tmSearchPoster{width:7.2rem;height:10.8rem;border-radius:10px;background:rgba(255,255,255,.08) center/cover no-repeat}' +
        '#tmSearchHits .tmSearchName{margin:.4em 0 0;font-size:.86em;line-height:1.25;overflow-wrap:anywhere}' +
        '.tmTitlePage .collectionItems,.tmTitlePage #childrenCollapsible,' +
        '.tmTitlePage #listChildrenCollapsible,.tmTitlePage .childrenItemsContainer,' +
        '.tmTitlePage .tmNativeChildren{display:none!important}' +
        '.tmDownloadsPage .itemsContainer,.tmDownloadsPage .alphaPicker{display:none!important}' +
        '.tmDownloadHero{display:flex;gap:1rem;align-items:center;margin:0 0 1em;flex-wrap:wrap}' +
        '.tmDownloadHero .tmDlBar{flex:1 1 12rem;height:10px;border-radius:99px;background:rgba(255,255,255,.12)}' +
        '.tmDownloadHero .tmDlBar>span{display:block;height:100%;background:#0a84ff;border-radius:99px}' +
        '.tmDownloadHero .tmDlRemove{border:1px solid rgba(255,255,255,.38);background:transparent;color:#fff;' +
        'border-radius:999px;min-height:2.5rem;padding:.45em 1.1em;font-weight:600;font-family:inherit;cursor:pointer}' +
        '#tmSubtitles{margin:1.2em 0 1.6em;max-width:52rem;box-sizing:border-box}' +
        '#tmSubtitles h2{font-size:1.15em;margin:0 0 .55em}' +
        '#tmSubtitles .tmSubBar{display:flex;flex-wrap:wrap;gap:.55em;align-items:center;margin:0 0 .75em}' +
        '#tmSubtitles .tmSubBar select,#tmSubtitles .tmSubBar button,#tmSubtitles .tmSubAi button,' +
        '#tmSubtitles .tmSubDl{border:none;border-radius:999px;min-height:2.4rem;padding:.4em 1em;' +
        'font-weight:600;font-family:inherit;cursor:pointer}' +
        '#tmSubtitles .tmSubBar select{background:rgba(255,255,255,.1);color:inherit;border:1px solid rgba(255,255,255,.2)}' +
        '#tmSubtitles .tmSubSearch,#tmSubtitles .tmSubGen,#tmSubtitles .tmSubDl{background:#0a84ff;color:#fff}' +
        '#tmSubtitles .tmSubSearch:disabled,#tmSubtitles .tmSubGen:disabled,#tmSubtitles .tmSubDl:disabled{opacity:.45;cursor:default}' +
        '#tmSubtitles .tmSubAi{padding:.85em 1em;margin:0 0 .65em;border-radius:14px;' +
        'background:rgba(10,132,255,.12);border:1px solid rgba(10,132,255,.35)}' +
        '#tmSubtitles .tmSubQuote{font-weight:700;line-height:1.35;margin:0 0 .35em}' +
        '#tmSubtitles .tmSubHint{opacity:.85;font-size:.92em;margin:0 0 .65em}' +
        '#tmSubtitles .tmSubRow{display:flex;flex-wrap:wrap;align-items:flex-start;gap:.55em .75em;' +
        'padding:.65em .8em;margin:.35em 0;border-radius:12px;background:rgba(255,255,255,.07);' +
        'border:1px solid rgba(255,255,255,.1)}' +
        '#tmSubtitles .tmSubName{flex:1 1 12rem;min-width:0;overflow-wrap:anywhere;line-height:1.35}' +
        '#tmSubtitles .tmSubMeta{font-size:.9em;opacity:.85}' +
        '#tmSubtitles .tmSubEmpty{opacity:.75;margin:.4em 0}' +
        '@media (max-width:700px){' +
        '#tmDownloads .tmDlRow{flex-wrap:wrap}' +
        '#tmDownloads .tmDlActions{width:100%}' +
        '#tmDownloads .tmDlOpen,#tmDownloads .tmDlRemove{width:100%}' +
        '}';
    document.head.appendChild(style);

    function releasePage() {
        stopPoll();
        document.querySelectorAll('#tmArrRequest').forEach(function (el) { el.remove(); });
        document.querySelectorAll('.tmChannelPage,.tmDownloadsPage,.tmTitlePage,.tmCategoryPage').forEach(function (page) {
            page.classList.remove('tmChannelPage', 'tmDownloadsPage', 'tmTitlePage', 'tmCategoryPage');
            page.querySelectorAll('.tmNativeChildren').forEach(function (el) { el.style.display = el.dataset.tmDisplay || ''; el.classList.remove('tmNativeChildren'); });
            page.querySelectorAll('#tmReleases,#tmDownloads,#tmDownloadHero,.tmLoadMessage,.tmBrowseHeading,.tmBrowsePages,#tmArrRequest').forEach(function (el) { el.remove(); });
            page.querySelectorAll('[data-tm-page-link]').forEach(function (el) { el.style.display = ''; el.removeAttribute('data-tm-page-link'); });
        });
    }

    function routeChanged() {
        if (location.hash === lastHash) { return; }
        lastHash = location.hash;
        releasePage();
        var route = lastHash;
        setTimeout(function () { if (route === location.hash) { onNavigate(); } }, 350);
    }
    ['pushState', 'replaceState'].forEach(function (name) {
        var original = history[name];
        history[name] = function () { var value = original.apply(this, arguments); routeChanged(); return value; };
    });
    window.addEventListener('hashchange', routeChanged);
    window.addEventListener('popstate', routeChanged);
    setInterval(routeChanged, 350);
    routeChanged();

    function api() { return window.ApiClient; }

    function onNavigate(attempt) {
        if (!api() || !api().getCurrentUserId()) {
            if ((attempt || 0) < 40) { setTimeout(function () { onNavigate((attempt || 0) + 1); }, 250); }
            return;
        }
        var hash = location.hash || '';
        if (searchRequest) { searchRequest.abort(); searchRequest = null; }
        var details = hash.match(/[#/]details\?id=([a-f0-9]{32})/i);
        var list = hash.match(/[#/]list\?parentId=([a-f0-9]{32})/i);

        if (details) {
            api().getItem(api().getCurrentUserId(), details[1]).then(function (item) {
                if (location.hash !== hash) { return; }
                if (isDownloadsFolder(item)) {
                    enhanceDownloadsList(item);
                } else if (isDownloadItem(item)) {
                    enhanceDownloadDetail(item);
                } else if (item.ChannelId && (item.Type === 'BoxSet' || item.Type === 'Season' || looksEpisodeOrSeason(item))) {
                    enhanceTitlePage(item);
                } else if (item.ProviderIds && item.ProviderIds.TreasureMaps) {
                    enhanceReleasePage(item);
                }
                if (item.Type === 'Movie' || item.Type === 'Series' || (item.ProviderIds && item.ProviderIds.TreasureMapsKind && item.Type === 'BoxSet')) { enhanceArrRequest(item); }
                if (item.Type === 'Movie' || item.Type === 'Episode') {
                    enhanceSubtitles(item);
                }
            }).catch(function () { });
        } else if (/search/i.test(hash) && searchQueryFromHash(hash)) {
            enhanceSearchPage(searchQueryFromHash(hash));
        } else if (list) {
            api().getItem(api().getCurrentUserId(), list[1]).then(function (item) {
                if (location.hash !== hash) { return; }
                if (isDownloadsFolder(item)) {
                    enhanceDownloadsList(item);
                    return;
                }
                if (isDownloadItem(item)) {
                    enhanceDownloadDetail(item);
                    return;
                }
                if (item.Type === 'Channel' || item.ChannelId) {
                    var page = visiblePage();
                    if (page) { page.classList.add('tmChannelPage'); }
                    retryEmptyChannelFolder(item);
                }
            }).catch(function () { });
        }
    }

    function retryEmptyChannelFolder(item) {
        if (!item || !item.Id || isDownloadsFolder(item) || isDownloadItem(item)) {
            return;
        }
        var route = location.hash;
        api().getItems(api().getCurrentUserId(), { ParentId: item.Id, Limit: 60, Fields: 'ProviderIds' }).then(function (result) {
            if (location.hash !== route) { return; }
            if (result && result.Items && result.Items.length) {
                whenReady('.itemsContainer', 16, function (page, container) {
                    var categories = result.Items.every(function (child) { return child.ProviderIds && child.ProviderIds.TreasureMapsCategory; });
                    page.classList.toggle('tmCategoryPage', categories);
                    page.classList.add('tmChannelPage');
                    if (!page.querySelector('.tmBrowseHeading')) {
                        var heading = document.createElement('h1');
                        heading.className = 'tmBrowseHeading';
                        heading.textContent = item.Name;
                        container.parentNode.insertBefore(heading, container);
                    }
                    var pages = result.Items.filter(function (child) { return child.ProviderIds && /^pg:/.test(child.ProviderIds.TreasureMapsCategory || ''); });
                    if (pages.length && !page.querySelector('.tmBrowsePages')) {
                        var nav = document.createElement('nav');
                        nav.className = 'tmBrowsePages'; nav.setAttribute('aria-label', 'Weitere Seiten');
                        pages.forEach(function (child) {
                            var link = document.createElement('a');
                            link.href = '#/list?parentId=' + child.Id;
                            link.textContent = child.Name.replace(/^Page /, 'Seite ');
                            nav.appendChild(link);
                        });
                        container.parentNode.insertBefore(nav, container);
                        whenReady('.card', 16, function () {
                            pages.forEach(function (child) {
                                page.querySelectorAll('.card[data-id="' + child.Id + '"]').forEach(function (card) { card.setAttribute('data-tm-page-link', '1'); card.style.display = 'none'; });
                            });
                        }, route);
                    }
                }, route);
                return;
            }
            window.setTimeout(function () {
                if (location.hash !== route) { return; }
                api().getItems(api().getCurrentUserId(), { ParentId: item.Id, Limit: 40 }).then(function (again) {
                    if (again && again.Items && again.Items.length && location.hash === route) {
                        location.reload();
                    }
                }).catch(function () { });
            }, 2500);
        }).catch(function () { });
    }

    function visiblePage() {
        var pages = document.querySelectorAll('.page:not(.hide)');
        return pages.length ? pages[pages.length - 1] : null;
    }

    function searchQueryFromHash(hash) {
        var m = (hash || '').match(/[?&](?:query|q)=([^&]+)/i);
        return m ? decodeURIComponent(m[1].replace(/\+/g, ' ')).trim() : '';
    }

    function enhanceSearchPage(query) {
        if (window.FireTvSmartSearch || !query || query.length < 2) { return; }
        var route = location.hash;
        var user = api().getCurrentUserId();
        whenReady('.searchResults,.padded-right,.verticalSection', 16, function (page) {
            if (route !== location.hash || user !== api().getCurrentUserId() || !page || page.getAttribute('data-tm-search') === query) { return; }
            page.setAttribute('data-tm-search', query);
            var url = api().getUrl('TreasureMaps/Search/Cards', { q: query });
            searchRequest = window.AbortController ? new AbortController() : null;
            var request = window.fetch && api().accessToken
                ? window.fetch(url, { signal: searchRequest && searchRequest.signal, headers: { Authorization: 'MediaBrowser Token="' + api().accessToken() + '"'  } }).then(function (response) {
                    if (!response.ok) { throw new Error('Search failed'); }
                    return response.json();
                })
                : api().ajax({ url: url, type: 'GET' });
            request
                .then(function (res) {
                    if (route !== location.hash || user !== api().getCurrentUserId()) { return; }
                    searchRequest = null;
                    if (!res || !res.ok) { page.removeAttribute('data-tm-search'); return; }
                    var items = res.items || [];
                    var old = page.querySelector('#tmSearchHits');
                    if (old) { old.remove(); }
                    if (!items.length) { return; }
                    var box = document.createElement('div');
                    box.id = 'tmSearchHits';
                    var h = document.createElement('h2');
                    h.textContent = 'Indexer';
                    box.appendChild(h);
                    var row = document.createElement('div');
                    row.className = 'tmSearchRow';
                    items.forEach(function (it) {
                        var a = document.createElement('a');
                        a.className = 'tmSearchCard';
                        a.href = '#/details?id=' + it.id;
                        var poster = document.createElement('div');
                        poster.className = 'tmSearchPoster';
                        poster.style.backgroundImage = 'url(' + api().getUrl('Items/' + it.id + '/Images/Primary') + ')';
                        var name = document.createElement('div');
                        name.className = 'tmSearchName';
                        name.textContent = it.year ? (it.name + ' (' + it.year + ')') : it.name;
                        a.appendChild(poster);
                        a.appendChild(name);
                        row.appendChild(a);
                    });
                    box.appendChild(row);
                    var anchor = page.querySelector('.searchResults, .verticalSection, .padded-right') || page;
                    anchor.insertBefore(box, anchor.firstChild);
                })
                .catch(function () {
                    if (route === location.hash && user === api().getCurrentUserId()) {
                        searchRequest = null; page.removeAttribute('data-tm-search');
                    }
                });
        });
    }

    function enhanceSubtitles(item) {
        if (!item || !item.Id || (item.Type !== 'Movie' && item.Type !== 'Episode')) { return; }
        if (item.ChannelId && !item.Path) { return; }
        whenReady('.detailSection,.detailPageSecondaryContainer', 16, function (page) {
            if (!page || page.getAttribute('data-tm-subs') === item.Id) { return; }
            page.setAttribute('data-tm-subs', item.Id);
            var old = page.querySelector('#tmSubtitles');
            if (old) { old.remove(); }

            var box = document.createElement('details');
            box.id = 'tmSubtitles';
            var h = document.createElement('summary');
            h.tabIndex = 0;
            h.textContent = 'Untertitel suchen & erstellen';
            box.appendChild(h);

            var bar = document.createElement('div');
            bar.className = 'tmSubBar';
            var sel = document.createElement('select');
            sel.setAttribute('aria-label', 'Sprache');
            [['de', 'Deutsch'], ['en', 'English'], ['es', 'Español'], ['fr', 'Français'], ['it', 'Italiano'], ['pl', 'Polski'], ['nl', 'Nederlands']].forEach(function (pair) {
                var opt = document.createElement('option');
                opt.value = pair[0];
                opt.textContent = pair[1];
                sel.appendChild(opt);
            });
            var searchBtn = document.createElement('button');
            searchBtn.type = 'button';
            searchBtn.className = 'tmSubSearch';
            searchBtn.textContent = 'Suchen';
            bar.appendChild(sel);
            bar.appendChild(searchBtn);
            box.appendChild(bar);

            var aiBox = document.createElement('div');
            aiBox.className = 'tmSubAi';
            var quoteEl = document.createElement('div');
            quoteEl.className = 'tmSubQuote';
            quoteEl.textContent = 'Kosten werden ermittelt…';
            var hintEl = document.createElement('div');
            hintEl.className = 'tmSubHint';
            hintEl.textContent = 'KI-Erzeugung startet erst, nachdem die groben Kosten bestätigt wurden.';
            var genBtn = document.createElement('button');
            genBtn.type = 'button';
            genBtn.className = 'tmSubGen';
            genBtn.textContent = 'KI erzeugen';
            genBtn.disabled = true;
            aiBox.appendChild(quoteEl);
            aiBox.appendChild(hintEl);
            aiBox.appendChild(genBtn);
            box.appendChild(aiBox);
            var jobStatus = document.createElement('div');
            jobStatus.className = 'tmSubJobStatus';
            jobStatus.setAttribute('role', 'status');
            jobStatus.hidden = true;
            var jobProgress = document.createElement('progress'); jobProgress.max = 100;
            var jobText = document.createElement('p');
            var jobsButton = document.createElement('button'); jobsButton.type = 'button';
            jobsButton.textContent = 'Alle Untertitelaufträge';
            jobsButton.addEventListener('click', function () { if (window.EvolutionSubtitleJobs) { window.EvolutionSubtitleJobs.open(); } });
            jobStatus.appendChild(jobProgress); jobStatus.appendChild(jobText); jobStatus.appendChild(jobsButton); box.appendChild(jobStatus);

            var list = document.createElement('div');
            list.className = 'tmSubList';
            box.appendChild(list);

            var anchor = page.querySelector('.detailSection, .detailPageSecondaryContainer') || page;
            anchor.appendChild(box);

            var lastQuote = null;
            var currentJobs = [], searchGeneration = 0;
            function updateJob(records) {
                currentJobs = records;
                var job = records.filter(function (j) { return j.itemId.replace(/-/g, '') === item.Id.replace(/-/g, '') && j.language === sel.value; })[0];
                jobStatus.hidden = !job;
                h.textContent = 'Untertitel suchen & erstellen' + (job && job.active ? ' · In Arbeit' : job && job.state === 'completed' ? ' · Fertig' : '');
                if (!job) { return; }
                jobText.textContent = job.message;
                jobProgress.value = job.percent;
                if (job.active) {
                    genBtn.disabled = true; genBtn.textContent = 'Auftrag läuft · ' + job.percent + ' %';
                    hintEl.textContent = 'Du kannst diesen Film verlassen. Der Server arbeitet weiter.';
                } else if (lastQuote && lastQuote.ok) {
                    genBtn.disabled = lastQuote.enabled === false;
                    genBtn.textContent = job.state === 'completed' ? 'Vorhandene verwenden' : 'Fortsetzen / erneut versuchen';
                }
            }
            if (window.EvolutionSubtitleJobs) { window.EvolutionSubtitleJobs.subscribe(box, updateJob); }

            function setQuote(res) {
                lastQuote = res && res.quote;
                if (!lastQuote || lastQuote.ok !== true || !lastQuote.summary || !Number.isFinite(lastQuote.totalUsd)) {
                    quoteEl.textContent = (lastQuote && lastQuote.message) || 'Keine gültige Kostenschätzung. Prüfe die KI-Konfiguration.';
                    hintEl.textContent = 'Die Erstellung ist ohne Kostenschätzung gesperrt.';
                    genBtn.disabled = true;
                    return;
                }
                quoteEl.textContent = lastQuote.summary || 'Keine Kostenschätzung.';
                hintEl.textContent = lastQuote.alreadyExists
                    ? 'Die vorhandene Datei wird wiederverwendet. Es entstehen keine neuen Kosten.'
                    : ((String(lastQuote.whisperModel || '').indexOf('grok-') === 0 ? 'Grok ' : 'Whisper ') + formatUsd(lastQuote.whisperUsd)
                        + (lastQuote.includesTranslation ? ' + Übersetzung ' + formatUsd(lastQuote.translationUsd) : '')
                        + ' · Startet erst nach Bestätigung.');
                genBtn.disabled = lastQuote.enabled === false;
                genBtn.textContent = lastQuote.alreadyExists ? 'Vorhandene verwenden' : 'KI erzeugen';
                if (res && res.aiEnabled === false) {
                    quoteEl.textContent = 'KI-Untertitel sind aus oder ohne API-Key.';
                    genBtn.disabled = true;
                }
                updateJob(currentJobs);
            }

            function formatUsd(n) {
                var v = Number(n || 0);
                return v.toFixed(2) + ' USD';
            }

            function renderHits(hits) {
                list.innerHTML = '';
                if (!hits || !hits.length) {
                    var empty = document.createElement('div');
                    empty.className = 'tmSubEmpty';
                    empty.textContent = 'Keine OpenSubtitles-Treffer. KI-Erzeugung steht oben bereit.';
                    list.appendChild(empty);
                    return;
                }
                hits.forEach(function (hit) {
                    var row = document.createElement('div');
                    row.className = 'tmSubRow';
                    var name = document.createElement('div');
                    name.className = 'tmSubName';
                    name.textContent = hit.name || 'OpenSubtitles';
                    var meta = document.createElement('div');
                    meta.className = 'tmSubMeta';
                    meta.textContent = [hit.hashMatch ? 'Exakte Datei' : null, hit.comment, hit.downloads ? hit.downloads + '×' : null]
                        .filter(Boolean).join(' · ');
                    var dl = document.createElement('button');
                    dl.type = 'button';
                    dl.className = 'tmSubDl';
                    dl.textContent = 'Übernehmen';
                    dl.addEventListener('click', function () {
                        dl.disabled = true;
                        dl.textContent = 'Lädt…';
                        api().ajax({
                            url: api().getUrl('TreasureMaps/Subtitles/Download', { itemId: item.Id, id: hit.id }),
                            type: 'POST', dataType: 'json'
                        }).then(function (res) {
                            dl.textContent = res && res.ok ? 'Gespeichert' : 'Fehler';
                            if (!res || !res.ok) {
                                dl.disabled = false;
                                hintEl.textContent = (res && res.message) || 'Download fehlgeschlagen.';
                            }
                        }, function () {
                            dl.disabled = false;
                            dl.textContent = 'Übernehmen';
                        });
                    });
                    row.appendChild(name);
                    row.appendChild(meta);
                    row.appendChild(dl);
                    list.appendChild(row);
                });
            }

            function search() {
                var ticket = ++searchGeneration, language = sel.value;
                lastQuote = null; genBtn.disabled = true; updateJob(currentJobs);
                searchBtn.disabled = true;
                quoteEl.textContent = 'Suche und Kostenschätzung…';
                api().ajax({
                    url: api().getUrl('TreasureMaps/Subtitles/Search', { itemId: item.Id, language: sel.value }),
                    type: 'GET', dataType: 'json'
                }).then(function (res) {
                    if (!box.isConnected || ticket !== searchGeneration || language !== sel.value) { return; }
                    searchBtn.disabled = false;
                    if (!res || !res.ok) {
                        quoteEl.textContent = (res && res.message) || 'Suche fehlgeschlagen.';
                        renderHits([]);
                        genBtn.disabled = true;
                        return;
                    }
                    setQuote(res);
                    renderHits(res.opensubtitles);
                }, function () {
                    if (!box.isConnected || ticket !== searchGeneration) { return; }
                    searchBtn.disabled = false;
                    quoteEl.textContent = 'Suche fehlgeschlagen.';
                });
            }

            searchBtn.addEventListener('click', search);
            sel.addEventListener('change', search);
            genBtn.addEventListener('click', function () {
                var q = lastQuote || {};
                if (genBtn.disabled || q.ok !== true || !Number.isFinite(q.totalUsd)) { return; }
                var line = q.summary || ('ca. ' + formatUsd(q.totalUsd));
                if (!window.confirm('KI-Untertitel im Hintergrund erstellen?\n\n' + line + '\n\nFertige Schritte werden wiederverwendet. Du kannst den Film danach verlassen.')) {
                    return;
                }
                genBtn.disabled = true;
                genBtn.textContent = 'Auftrag wird gestartet…';
                hintEl.textContent = 'Auftrag wird an den Server übergeben…';
                api().ajax({
                    url: api().getUrl('TreasureMaps/Subtitles/Generate', { itemId: item.Id, language: sel.value, force: 'false', confirmed: 'true', maxEstimatedUsd: q.totalUsd }),
                    type: 'POST', dataType: 'json'
                }).then(function (res) {
                    if (res && res.ok) {
                        if (res.job && window.EvolutionSubtitleJobs) { window.EvolutionSubtitleJobs.accept(res.job); }
                        else { genBtn.textContent = 'Fertig'; hintEl.textContent = 'Vorhandene Untertitel sind verfügbar. Es entstehen keine neuen Kosten.'; }
                    } else {
                        genBtn.disabled = false;
                        genBtn.textContent = 'KI erzeugen';
                        hintEl.textContent = (res && res.message) || 'Erzeugung fehlgeschlagen.';
                    }
                }, function () {
                    genBtn.disabled = false;
                    genBtn.textContent = 'KI erzeugen';
                    hintEl.textContent = 'Serverantwort fehlt. Prüfe „KI-Aufträge“, bevor du erneut startest.';
                    if (window.EvolutionSubtitleJobs) { window.EvolutionSubtitleJobs.refresh(); }
                });
            });

            var searched = false;
            box.addEventListener('toggle', function () {
                if (box.open && !searched) { searched = true; search(); }
            });
            preferredSubLang(function (lang) {
                if (lang && sel.querySelector('option[value="' + lang + '"]')) {
                    sel.value = lang;
                }
            });
        });
    }

    function preferredSubLang(cb) {
        api().getCurrentUser().then(function (user) {
            var raw = (user && user.Configuration && user.Configuration.SubtitleLanguagePreference) || '';
            var lang = String(raw).toLowerCase();
            if (lang.indexOf('de') === 0 || lang.indexOf('ger') === 0) { cb('de'); return; }
            if (lang.indexOf('en') === 0 || lang.indexOf('eng') === 0) { cb('en'); return; }
            if (lang.length >= 2) { cb(lang.slice(0, 2)); return; }
            cb('de');
        }).catch(function () { cb('de'); });
    }

    function whenReady(selector, tries, callback, route) {
        route = route === undefined ? location.hash : route;
        if (route !== location.hash) { return; }
        var page = visiblePage();
        var el = page && page.querySelector(selector);
        if (el) { callback(page, el); return; }
        if (tries > 0) { setTimeout(function () { whenReady(selector, tries - 1, callback, route); }, 350); }
        else if (page) { callback(page, page); }
    }

    function looksQuality(name) {
        return /^(?:\d{3,4}p|4k|uhd|sd)\b/i.test(name || '')
            || /\d{3,4}p\s*[·•]\s*(WEB|BLU|HDTV|CAM|TS)/i.test(name || '')
            || /start download/i.test(name || '');
    }

    function looksEpisodeOrSeason(item) {
        if (!item) { return false; }
        if (item.Type === 'Season') { return true; }
        return /^(?:S\d{2}E\d{2}|Season\s+\d|Staffel\s+\d|Other releases)/i.test(item.Name || '');
    }

    function isDownloadsFolder(item) {
        if (!item) { return false; }
        var name = item.Name || '';
        if (!/downloads/i.test(name)) { return false; }
        return !!(item.ChannelId || item.Type === 'Channel');
    }

    function isDownloadItem(item) {
        if (!item || !item.ChannelId) { return false; }
        var ext = String(item.ExternalId || item.Path || '');
        if (/DL::|dl::|dlinfo/i.test(ext)) { return true; }
        var overview = item.Overview || '';
        return item.Type === 'BoxSet' && /Download complete|Download failed|Downloading|SABnzbd|Treasure-Maps download|Indexer download/i.test(overview);
    }

    /* ---- Movie/show title page: replace the generic children POSTER GRID with a release LIST ----
       Newer jellyfin-web renders BoxSet children in #childrenCollapsible / .childrenItemsContainer
       (German heading: "Andere Inhalte"), not only .collectionItems. */
    function enhanceTitlePage(item) {
        if (isDownloadItem(item) && !hasReleaseChildrenHint(item)) {
            enhanceDownloadDetail(item);
            return;
        }

        var route = location.hash;
        var user = api().getCurrentUserId();
        function current() { return location.hash === route && api().getCurrentUserId() === user; }
        function message(text, retry) {
            whenReady('.detailPageSecondaryContainer', 16, function (page, anchor) {
                if (!current()) { return; }
                var state = page.querySelector('.tmLoadMessage');
                if (!state) { state = document.createElement('div'); state.className = 'tmLoadMessage'; state.setAttribute('role', 'status'); anchor.prepend(state); }
                state.textContent = text;
                if (retry) {
                    var button = document.createElement('button');
                    button.className = 'tmRetry'; button.textContent = 'Erneut laden';
                    button.onclick = function () { loadReleases(0); };
                    state.appendChild(button);
                }
            }, route);
        }
        function loadReleases(attempt) {
            if (!current()) { return; }
            message('Verfügbare Versionen werden geladen …', false);
            api().getItems(api().getCurrentUserId(), { ParentId: item.Id, Fields: 'ProviderIds,Overview' }).then(function (result) {
                if (!current()) { return; }
                var releases = pickReleases(result.Items || []);
                if (!releases.length) {
                    if (isDownloadItem(item)) { enhanceDownloadDetail(item); return; }
                    if ((attempt || 0) < 1) {
                        window.setTimeout(function () { loadReleases(1); }, 2500);
                    } else {
                        message('Aktuell sind keine Versionen verfügbar.', true);
                    }
                    return;
                }

                whenReady(childrenSelectors(), 28, function (page) {
                    if (!current()) { return; }
                    var state = page.querySelector('.tmLoadMessage');
                    if (state) { state.remove(); }
                    renderReleaseList(page, item, releases);
                }, route);
            }).catch(function () { if (current()) { message('Versionen konnten nicht geladen werden.', true); } });
        }
        loadReleases(0);
    }

    function pickReleases(items) {
        return items.filter(function (i) {
            if (!i || i.Type === 'Person') { return false; }
            if (looksEpisodeOrSeason(i)) { return true; }
            if (i.ProviderIds && i.ProviderIds.TreasureMaps) { return true; }
            return i.Type === 'Folder' || i.Type === 'BoxSet' || looksQuality(i.Name);
        });
    }

    function childrenSelectors() {
        return '.collectionItems, #childrenCollapsible, #listChildrenCollapsible, .childrenItemsContainer';
    }

    function findChildrenHost(page) {
        return page.querySelector('.collectionItems')
            || page.querySelector('#childrenCollapsible')
            || page.querySelector('#listChildrenCollapsible')
            || page.querySelector('.childrenItemsContainer')
            || page.querySelector('.detailPageSecondaryContainer .itemsContainer')
            || page.querySelector('.itemsContainer');
    }

    function hideNativeChildren(page) {
        page.classList.add('tmTitlePage', 'tmChannelPage');
        page.querySelectorAll(childrenSelectors()).forEach(function (el) {
            if (el.closest('#tmReleases') || el.closest('#tmDownloads')) { return; }
            if (!el.classList.contains('tmNativeChildren')) { el.dataset.tmDisplay = el.style.display; }
            el.classList.add('tmNativeChildren');
            el.style.display = 'none';
        });
        page.querySelectorAll('.verticalSection, .detailVerticalSection, section').forEach(function (sec) {
            if (sec.id === 'tmReleases' || sec.id === 'tmDownloads' || sec.querySelector('#tmReleases')) { return; }
            var heading = sec.querySelector('.sectionTitle, h2, h1');
            var text = heading ? (heading.textContent || '') : '';
            if (/andere inhalte|other items|items in this|more from|in this collection|weitere inhalte/i.test(text)) {
                if (!sec.classList.contains('tmNativeChildren')) { sec.dataset.tmDisplay = sec.style.display; }
                sec.classList.add('tmNativeChildren');
                sec.style.display = 'none';
            }
        });
    }

    function renderReleaseList(page, item, releases) {
        var old = page.querySelector('#tmReleases');
        if (old) { old.remove(); }

        var host = document.createElement('div');
        host.id = 'tmReleases';
        host.className = 'verticalSection detailVerticalSection';
        var title = document.createElement('h2');
        title.className = 'sectionTitle';
        var episodeLike = releases.filter(looksEpisodeOrSeason);
        var list = episodeLike.length ? episodeLike : releases;
        title.textContent = episodeLike.length
            ? (episodeLike.every(function (r) { return r.Type === 'Season' || /^Season\s+\d/i.test(r.Name || ''); }) ? 'Seasons' : 'Episodes')
            : 'Verfügbare Versionen';
        host.appendChild(title);
        if (episodeLike.length) { list.forEach(function (release) { host.appendChild(buildEpisodeRow(release)); }); }
        else { appendLanguageGroups(host, list, item.Name); }

        var children = findChildrenHost(page);
        var anchor = (children && (children.closest('#childrenCollapsible,#listChildrenCollapsible,.verticalSection,.detailVerticalSection') || children))
            || page.querySelector('.detailPageSecondaryContainer')
            || page.querySelector('.itemOverview')
            || page;
        if (anchor.parentNode && anchor !== page) {
            anchor.parentNode.insertBefore(host, anchor);
        } else {
            page.appendChild(host);
        }

        hideNativeChildren(page);
        var route = location.hash;
        var observer = new MutationObserver(function () {
            if (route !== location.hash) { observer.disconnect(); return; }
            hideNativeChildren(page);
        });
        observer.observe(page, { childList: true, subtree: true });
        setTimeout(function () { observer.disconnect(); }, 15000);

        refreshStatus();
        startPoll();
    }

    function hasReleaseChildrenHint(item) {
        var overview = item.Overview || '';
        return /release/i.test(overview);
    }

    /* ---- Release tile page: add a proper Download button next to the favorite button ---- */
    function enhanceReleasePage(item) {
        applyMovieTitle(item);
        whenReady('.mainDetailButtons', 14, function (page, buttons) {
            if (page.querySelector('.tmDetailDl')) { return; }
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'tmDl tmDetailDl';
            btn.textContent = '\u2B07 Download';
            var status = document.createElement('span');
            status.className = 'tmRelStatus';
            status.style.marginLeft = '.8em';
            var movieTitle = (item.ProviderIds && item.ProviderIds.TreasureMapsTitle) || item.OriginalTitle || '';
            btn.addEventListener('click', function () {
                grab(item.ProviderIds.TreasureMaps, item.ProviderIds.TreasureMapsKind || 'movie', item.Name, btn, status, null, item.Id, item.ImageTags && item.ImageTags.Primary, movieTitle);
            });
            buttons.appendChild(btn);
            buttons.appendChild(status);
        });
    }

    function applyMovieTitle(item) {
        var movieTitle = (item.ProviderIds && item.ProviderIds.TreasureMapsTitle) || item.OriginalTitle;
        if (!movieTitle || movieTitle === item.Name) { return; }
        whenReady('.itemName, h1.name, .nameContainer', 14, function (page, el) {
            var target = page.querySelector('.itemName') || el;
            if (!target || target.dataset.tmTitle === '1') { return; }
            target.dataset.tmTitle = '1';
            var quality = item.Name;
            target.textContent = movieTitle;
            if (quality && looksQuality(quality)) {
                var sub = document.createElement('div');
                sub.className = 'tmDlMeta';
                sub.style.opacity = '.8';
                sub.style.marginTop = '.25em';
                sub.textContent = quality;
                target.parentNode && target.parentNode.insertBefore(sub, target.nextSibling);
            }
        });
    }

    function buildEpisodeRow(item) {
        var row = document.createElement('div');
        row.className = 'tmRelRow';
        row.dataset.name = item.Name || '';

        var name = document.createElement('div');
        name.className = 'tmRelName';
        name.textContent = item.Name || '';
        name.title = item.Overview || item.Name || '';
        name.style.cursor = 'pointer';
        name.addEventListener('click', function () {
            location.hash = '#/details?id=' + item.Id + '&serverId=' + api().serverId();
        });

        var meta = document.createElement('div');
        meta.className = 'tmRelMeta';
        var open = document.createElement('button');
        open.type = 'button';
        open.className = 'tmDl';
        open.textContent = item.Type === 'Season' || /^Season\s+\d/i.test(item.Name || '') ? 'Open season' : 'Open episode';
        open.addEventListener('click', function () {
            location.hash = '#/details?id=' + item.Id + '&serverId=' + api().serverId();
        });
        var hint = document.createElement('div');
        hint.className = 'tmRelStatus';
        hint.textContent = item.Overview ? String(item.Overview).split('.')[0] : '';
        meta.appendChild(hint);
        meta.appendChild(open);

        row.appendChild(name);
        row.appendChild(meta);
        return row;
    }

    function enhanceArrRequest(item) {
        var route = location.hash;
        var endpoint = 'TreasureMaps/Requests/' + item.Id;
        whenReady('.detailSectionContent,.detailPageSecondaryContainer', 20, function (page, anchor) {
            if (route !== location.hash || page.querySelector('#tmArrRequest')) { return; }
            var box = document.createElement('div'); box.id = 'tmArrRequest';
            var button = document.createElement('button'); button.type = 'button'; button.className = 'tmDl'; button.disabled = true;
            var status = document.createElement('div'); status.setAttribute('role', 'status');
            box.appendChild(button); box.appendChild(status);
            var releases = anchor.querySelector('#tmReleases');
            if (releases) { releases.parentNode.insertBefore(box, releases); }
            else { anchor.appendChild(box); }
            function paint(raw) {
                if (route !== location.hash || !box.isConnected) { return; }
                var res = {}; Object.keys(raw || {}).forEach(function (key) { res[key.toLowerCase()] = raw[key]; });
                box.hidden = !res.enabled;
                button.textContent = (res.service === 'Sonarr' ? 'Serie anfordern · ' : 'Film anfordern · ') + res.service;
                button.disabled = !!(res.monitored || res.available);
                status.textContent = res.message || '';
                if (res.enabled && res.id && !res.available) { setTimeout(refresh, 15000); }
            }
            function refresh() {
                if (route !== location.hash || !box.isConnected) { return; }
                api().fetch({ url: api().getUrl(endpoint), type: 'GET', dataType: 'json' }).then(paint, function () { box.hidden = true; });
            }
            button.addEventListener('click', function () {
                button.disabled = true; status.textContent = 'Anforderung wird übergeben …';
                api().fetch({ url: api().getUrl(endpoint), type: 'POST', dataType: 'json' }).then(paint, function () {
                    if (route !== location.hash || !box.isConnected) { return; }
                    button.disabled = false; status.textContent = 'Anforderung fehlgeschlagen. Bitte erneut versuchen.';
                });
            });
            refresh();
        }, route);
    }

    function releaseLanguages(release) {
        var known = { de: 'Deutsch', en: 'Englisch', fr: 'Französisch', es: 'Spanisch', it: 'Italienisch', tr: 'Türkisch', ru: 'Russisch', ja: 'Japanisch', ko: 'Koreanisch', zh: 'Chinesisch', nl: 'Niederländisch', pl: 'Polnisch', pt: 'Portugiesisch' };
        var codes = ((release.ProviderIds || {}).TreasureMapsLanguages || '').split(',').filter(Boolean);
        var name = release.Name || '';
        if (!codes.length) {
            [['de', /🇩🇪|\bGerman\b|\bDeutsch\b/i], ['en', /🇬🇧|🇺🇸|\bEnglish\b/i], ['fr', /🇫🇷|\bFrench\b/i], ['es', /🇪🇸|\bSpanish\b/i], ['it', /🇮🇹|\bItalian\b/i], ['tr', /🇹🇷|\bTurkish\b/i], ['ja', /🇯🇵|\bJapanese\b/i]].forEach(function (entry) { if (entry[1].test(name)) { codes.push(entry[0]); } });
        }
        codes = codes.filter(function (code, i) { return codes.indexOf(code) === i; }).sort(function (a, b) { return (a === 'de' ? -1 : b === 'de' ? 1 : a.localeCompare(b)); });
        return { key: codes.join(','), label: codes.map(function (code) { return known[code] || code; }).join(' + ') || 'Sprache unbekannt' };
    }

    function appendLanguageGroups(host, releases, title) {
        var groups = {};
        releases.forEach(function (release) {
            var language = releaseLanguages(release);
            if (!groups[language.key]) { groups[language.key] = { language: language, items: [] }; }
            groups[language.key].items.push(release);
        });
        Object.keys(groups).sort(function (a, b) {
            var rank = function (key) { return key.indexOf('de') === 0 ? 0 : key.indexOf('en') === 0 ? 1 : key ? 2 : 3; };
            return rank(a) - rank(b) || groups[a].language.label.localeCompare(groups[b].language.label);
        }).forEach(function (key, index) {
            var group = groups[key];
            var details = document.createElement('details'); details.className = 'tmLanguageGroup'; details.open = index === 0;
            var summary = document.createElement('summary'); summary.tabIndex = 0;
            summary.textContent = group.language.label + ' · ' + group.items.length + (group.items.length === 1 ? ' Version' : ' Versionen');
            details.appendChild(summary);
            group.items.forEach(function (release) { details.appendChild(buildRow(release, title)); });
            host.appendChild(details);
        });
    }

    function buildRow(release, movieTitle) {
        var row = document.createElement('div');
        row.className = 'tmRelRow';
        row.dataset.name = release.Name || '';

        var name = document.createElement('div');
        name.className = 'tmRelName';
        name.textContent = release.Name || '';
        name.title = release.Name || '';
        name.style.cursor = 'pointer';
        name.addEventListener('click', function () {
            location.hash = '#/details?id=' + release.Id + '&serverId=' + api().serverId();
        });

        var status = document.createElement('div');
        status.className = 'tmRelStatus';

        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'tmDl';
        btn.textContent = '\u2B07 Download';
        var title = (release.ProviderIds && release.ProviderIds.TreasureMapsTitle) || movieTitle || '';
        btn.addEventListener('click', function () {
            grab(release.ProviderIds.TreasureMaps, release.ProviderIds.TreasureMapsKind || 'movie', release.Name, btn, status, row, release.Id, release.ImageTags && release.ImageTags.Primary, title);
        });

        var meta = document.createElement('div');
        meta.className = 'tmRelMeta';
        var origin = (release.ProviderIds || {}).EvolutionIndexer;
        if (origin) { var source = document.createElement('span'); source.className = 'tmIndexer'; source.textContent = origin; meta.appendChild(source); }
        meta.appendChild(status);
        meta.appendChild(btn);

        row.appendChild(name);
        row.appendChild(meta);
        return row;
    }

    function grab(guid, kind, name, btn, statusEl, row, itemId, imageTag, movieTitle) {
        var route = location.hash;
        btn.disabled = true;
        statusEl.textContent = 'Starting\u2026';
        var params = { type: kind, name: name };
        if (movieTitle) { params.title = movieTitle; }
        if (itemId && imageTag) {
            params.poster = api().getUrl('Items/' + itemId + '/Images/Primary', { tag: imageTag });
        }
        api().fetch({
            url: api().getUrl('TreasureMaps/Releases/' + guid + '/Grab', params),
            type: 'POST',
            dataType: 'json'
        }).then(function (res) {
            if (route !== location.hash || !btn.isConnected) { return; }
            if (res && res.ok) {
                if (row) { row.dataset.nzo = (res.nzoIds || []).join(','); }
                statusEl.textContent = 'Queued\u2026';
                startPoll();
            } else {
                btn.disabled = false;
                statusEl.textContent = '\u2717 ' + ((res && res.message) || 'failed');
            }
        }, function () {
            btn.disabled = false;
            statusEl.textContent = '\u2717 request failed';
        });
    }

    /* ---- Downloads folder: title list instead of a poster grid of quality strings ---- */
    function enhanceDownloadsList(folder) {
        var route = location.hash;
        whenReady('.itemsContainer, .padded-left, .pageTitle', 18, function (page) {
            page.classList.add('tmDownloadsPage', 'tmChannelPage');
            var old = page.querySelector('#tmDownloads');
            if (old) { old.remove(); }

            var host = document.createElement('div');
            host.id = 'tmDownloads';
            host.className = 'verticalSection';
            var heading = document.createElement('h2');
            heading.className = 'sectionTitle';
            heading.textContent = 'Downloads';
            host.appendChild(heading);
            var hint = document.createElement('div');
            hint.className = 'tmDlMeta';
            hint.style.margin = '0 0 .8em';
            hint.textContent = 'Downloads aus deinen Indexern. Eintrag auswählen, um Details zu öffnen.';
            host.appendChild(hint);

            var anchor = page.querySelector('.itemsContainer') || page.querySelector('.padded-left') || page;
            if (anchor.parentNode && anchor !== page) {
                anchor.parentNode.insertBefore(host, anchor);
            } else {
                page.appendChild(host);
            }

            // Status is authoritative and fast; catalog materialization must not delay it.
            fetchStatus().then(function (status) {
                if (route !== location.hash || !host.isConnected) { return; }
                host._tmStatus = status;
                renderDownloadRows(host, host._tmChildren || [], status);
                startPoll();
            }).catch(function () { if (route === location.hash && host.isConnected) { startPoll(); } });
            api().getItems(api().getCurrentUserId(), { ParentId: folder.Id, Fields: 'ProviderIds,Overview,PrimaryImageAspectRatio' }).then(function (result) {
                if (route !== location.hash || !host.isConnected) { return; }
                host._tmChildren = (result && result.Items) || [];
                renderDownloadRows(host, host._tmChildren, host._tmStatus);
            }).catch(function () { });
        });
    }

    function renderDownloadRows(host, children, status) {
        var signature = JSON.stringify([children, status]);
        if (host._tmSignature === signature) { return; }
        host._tmSignature = signature;
        var active = document.activeElement;
        var focusedRow = active && active.closest && active.closest('.tmDlRow');
        var focusedId = focusedRow && focusedRow.dataset.nzo;
        var focusedClass = focusedRow && active !== focusedRow ? active.className : '';
        host.querySelectorAll('.tmDlRow, .tmDlEmpty').forEach(function (n) { n.remove(); });
        var items = (status && status.items) || [];
        var rows = [];

        if (items.length) {
            items.forEach(function (entry) {
                var child = matchChild(children, entry);
                rows.push({ entry: entry, child: child });
            });
        } else if (!status || !status.ok) {
            children.forEach(function (child) {
                if (/no treasure-maps downloads|configure sabnzbd/i.test(child.Name || '')) {
                    return;
                }
                rows.push({ entry: null, child: child });
            });
        }

        if (!rows.length) {
            var empty = document.createElement('div');
            empty.className = 'tmDlMeta tmDlEmpty';
            empty.textContent = (status && status.message) || 'Noch keine Indexer-Downloads.';
            host.appendChild(empty);
            return;
        }

        var offset = 0;
        var token = (host.getAttribute('data-tm-paint') || '0') * 1 + 1;
        host.setAttribute('data-tm-paint', String(token));
        function paintChunk() {
            if (String(token) !== host.getAttribute('data-tm-paint')) {
                return;
            }
            var end = Math.min(offset + 16, rows.length);
            var i;
            for (i = offset; i < end; i++) {
                var row = buildDownloadRow(rows[i].child, rows[i].entry, status && status.speed);
                host.appendChild(row);
                if (focusedId && row.dataset.nzo === focusedId) {
                    var focusTarget = focusedClass ? row.getElementsByClassName(focusedClass)[0] : row;
                    (focusTarget || row).focus();
                }
            }
            offset = end;
            if (offset < rows.length && typeof window.requestAnimationFrame === 'function') {
                window.requestAnimationFrame(paintChunk);
            } else if (offset < rows.length) {
                window.setTimeout(paintChunk, 0);
            }
        }
        paintChunk();
    }

    function matchChild(children, entry) {
        var nzo = entry.id || '';
        var titleKey = normalize(entry.title || entry.name);
        for (var i = 0; i < children.length; i++) {
            var child = children[i];
            var ext = String(child.ExternalId || '');
            if (nzo && ext.indexOf(nzo) >= 0) { return child; }
            if (titleKey && normalize(child.Name) === titleKey) { return child; }
            if (child.ProviderIds && child.ProviderIds.TreasureMapsTitle && normalize(child.ProviderIds.TreasureMapsTitle) === titleKey) {
                return child;
            }
        }
        return null;
    }

    function buildDownloadRow(child, entry, speed) {
        var title = (entry && entry.title) || (child && child.ProviderIds && child.ProviderIds.TreasureMapsTitle) || (child && child.Name) || 'Download';
        var quality = (entry && entry.quality) || '';
        var row = document.createElement('div');
        row.className = 'tmDlRow';
        row.tabIndex = 0;
        if (entry && entry.id) { row.dataset.nzo = entry.id; }
        row.dataset.name = (entry && entry.name) || (child && child.Name) || '';

        var poster = document.createElement('div');
        poster.className = 'tmDlPoster';
        var cover = entry && entry.cover;
        if (!cover && child && child.ImageTags && child.ImageTags.Primary) {
            cover = api().getUrl('Items/' + child.Id + '/Images/Primary', { tag: child.ImageTags.Primary, maxHeight: 360 });
        }
        if (cover) { poster.style.backgroundImage = 'url("' + cover + '")'; }

        var body = document.createElement('div');
        body.className = 'tmDlBody';
        var name = document.createElement('div');
        name.className = 'tmDlTitle';
        name.textContent = title;
        var meta = document.createElement('div');
        meta.className = 'tmDlMeta';
        meta.textContent = statusText(entry, speed, quality);
        var bar = document.createElement('div');
        bar.className = 'tmDlBar';
        var fill = document.createElement('span');
        fill.style.width = Math.max(0, Math.min(100, Math.round((entry && entry.percent) || (entry && entry.status === 'Completed' ? 100 : 0)))) + '%';
        bar.appendChild(fill);
        body.appendChild(name);
        body.appendChild(meta);
        body.appendChild(bar);

        var actions = document.createElement('div');
        actions.className = 'tmDlActions';
        var open = document.createElement('button');
        open.type = 'button';
        open.className = 'tmDlOpen';
        open.textContent = 'Open';
        function go() {
            if (child && child.Id) {
                location.hash = '#/details?id=' + child.Id + '&serverId=' + api().serverId();
            }
        }
        open.addEventListener('click', function (ev) { ev.stopPropagation(); go(); });
        row.addEventListener('click', go);
        row.addEventListener('keydown', function (ev) {
            if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); go(); }
        });
        if (child && child.Id) { actions.appendChild(open); }

        var remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'tmDlRemove';
        remove.textContent = 'Remove';
        remove.addEventListener('click', function (ev) {
            ev.stopPropagation();
            removeDownload(row, entry && entry.id, title, remove);
        });
        actions.appendChild(remove);

        row.appendChild(poster);
        row.appendChild(body);
        row.appendChild(actions);
        return row;
    }

    function removeDownload(row, nzoId, title, button) {
        if (!api()) { return; }
        if (button) { button.disabled = true; }
        api().fetch({
            url: api().getUrl('TreasureMaps/Downloads/Remove', { nzoId: nzoId || '', title: title || '' }),
            type: 'POST',
            dataType: 'json'
        }).then(function (res) {
            if (!res || res.ok === false) {
                if (button) { button.disabled = false; }
                return;
            }
            if (row && row.parentNode) { row.parentNode.removeChild(row); }
            var host = document.getElementById('tmDownloads');
            if (host && !host.querySelector('.tmDlRow')) {
                var empty = document.createElement('div');
                empty.className = 'tmDlMeta tmDlEmpty';
                empty.textContent = 'Noch keine Indexer-Downloads.';
                host.appendChild(empty);
            }
        }, function () {
            if (button) { button.disabled = false; }
        });
    }

    function statusText(entry, speed, quality) {
        var bits = [];
        if (quality) { bits.push(quality); }
        if (!entry) { return bits.join(' \u00B7 ') || 'Queued'; }
        if (entry.status === 'Completed') { bits.push('\u2713 Downloaded'); }
        else if (entry.status === 'Failed') { bits.push('\u2717 Failed' + (entry.failMessage ? ': ' + entry.failMessage : '')); }
        else {
            bits.push('\u2B07 ' + Math.round(entry.percent || 0) + '%');
            if (speed) { bits.push(speed + 'B/s'); }
            if (entry.timeLeft) { bits.push(entry.timeLeft); }
        }
        return bits.join(' \u00B7 ');
    }

    function enhanceDownloadDetail(item) {
        applyMovieTitle(item);
        var page = visiblePage();
        if (page) { page.classList.add('tmChannelPage'); }
        whenReady('.itemName, .detailImageContainer, .mainDetailButtons', 16, function (pageEl) {
            if (pageEl.querySelector('#tmDownloadHero')) { refreshDownloadHero(); return; }
            var hero = document.createElement('div');
            hero.id = 'tmDownloadHero';
            hero.className = 'tmDownloadHero';
            var meta = document.createElement('div');
            meta.className = 'tmDlMeta';
            meta.id = 'tmDownloadHeroMeta';
            var bar = document.createElement('div');
            bar.className = 'tmDlBar';
            bar.innerHTML = '<span></span>';
            hero.appendChild(meta);
            hero.appendChild(bar);
            var remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'tmDlRemove';
            remove.textContent = 'Remove';
            remove.addEventListener('click', function (ev) {
                ev.stopPropagation();
                var title = (item.ProviderIds && item.ProviderIds.TreasureMapsTitle) || item.OriginalTitle || item.Name;
                var nzo = '';
                var ext = String(item.ExternalId || '').split('::');
                if (ext.length > 1) { nzo = ext[1]; }
                removeDownload(hero, nzo, title, remove);
            });
            hero.appendChild(remove);
            var buttons = pageEl.querySelector('.mainDetailButtons');
            var nameEl = pageEl.querySelector('.itemName') || pageEl.querySelector('h1');
            if (buttons && buttons.parentNode) {
                buttons.parentNode.insertBefore(hero, buttons);
            } else if (nameEl && nameEl.parentNode) {
                nameEl.parentNode.appendChild(hero);
            } else {
                pageEl.appendChild(hero);
            }
            refreshDownloadHero();
            startPoll();
        });

        function refreshDownloadHero() {
            fetchStatus().then(function (res) {
                var entry = matchDetailEntry(item, (res && res.items) || []);
                var meta = document.getElementById('tmDownloadHeroMeta');
                var fill = document.querySelector('#tmDownloadHero .tmDlBar>span');
                if (!meta) { return; }
                var title = (item.ProviderIds && item.ProviderIds.TreasureMapsTitle) || (entry && entry.title) || item.OriginalTitle || item.Name;
                applyMovieTitle({ Name: item.Name, OriginalTitle: title, ProviderIds: { TreasureMapsTitle: title } });
                meta.textContent = statusText(entry, res && res.speed, entry && entry.quality) +
                    '  \u2014 After it finishes, the title appears in Movies or TV Shows.';
                if (fill) {
                    fill.style.width = Math.max(0, Math.min(100, Math.round((entry && entry.percent) || (entry && entry.status === 'Completed' ? 100 : 0)))) + '%';
                }
            });
        }
    }

    function matchDetailEntry(item, items) {
        var ext = String(item.ExternalId || '');
        var titleKey = normalize((item.ProviderIds && item.ProviderIds.TreasureMapsTitle) || item.Name);
        for (var i = 0; i < items.length; i++) {
            if (items[i].id && ext.indexOf(items[i].id) >= 0) { return items[i]; }
            if (titleKey && (normalize(items[i].title) === titleKey || normalize(items[i].name) === titleKey)) {
                return items[i];
            }
        }
        return null;
    }

    /* ---- Live SABnzbd status ---- */
    function normalize(value) {
        return (value || '').toLowerCase().replace(/[^a-z0-9]+/g, '');
    }

    function fetchStatus() {
        return api().fetch({
            url: api().getUrl('TreasureMaps/Downloads/Status'),
            type: 'GET',
            dataType: 'json'
        }).then(function (res) { return res; }, function () { return { ok: false, items: [] }; });
    }

    function refreshStatus() {
        var page = visiblePage();
        if (!page) { return Promise.resolve(false); }
        var route = location.hash;
        var parent = currentParentId();
        var rows = page.querySelectorAll('.tmRelRow');
        var dlHost = page.querySelector('#tmDownloads');
        if ((!rows.length && !dlHost && !document.getElementById('tmDownloadHero')) || !api()) {
            return Promise.resolve(false);
        }

        return fetchStatus().then(function (res) {
            if (route !== location.hash || !res || !res.ok) { return false; }
            var anyActive = false;
            rows.forEach(function (row) {
                var entry = matchEntry(row, res.items || []);
                if (!entry) { return; }
                var statusEl = row.querySelector('.tmRelStatus');
                var btn = row.querySelector('.tmDl');
                if (entry.status === 'Completed') {
                    statusEl.textContent = '\u2713 Downloaded';
                    if (btn) { btn.disabled = true; }
                } else if (entry.status === 'Failed') {
                    statusEl.textContent = '\u2717 Failed' + (entry.failMessage ? ': ' + entry.failMessage : '');
                    if (btn) { btn.disabled = false; }
                } else {
                    anyActive = true;
                    var pct = Math.round(entry.percent || 0);
                    var text = '\u2B07 ' + pct + '%';
                    if (res.speed) { text += ' \u00B7 ' + res.speed + 'B/s'; }
                    if (entry.timeLeft) { text += ' \u00B7 ' + entry.timeLeft; }
                    statusEl.textContent = text;
                    if (btn) { btn.disabled = true; }
                }
            });

            if (dlHost) {
                dlHost._tmStatus = res;
                renderDownloadRows(dlHost, dlHost._tmChildren || [], res);
                anyActive = anyActive || (res.items || []).some(function (i) {
                    return i.status !== 'Completed' && i.status !== 'Failed';
                });
            }

            if (document.getElementById('tmDownloadHero')) {
                anyActive = true;
            }

            return anyActive;
        }, function () { return false; });
    }

    function currentParentId() {
        var list = (location.hash || '').match(/[#/]list\?parentId=([a-f0-9]{32})/i);
        var details = (location.hash || '').match(/[#/]details\?id=([a-f0-9]{32})/i);
        return (list && list[1]) || (details && details[1]) || '';
    }

    function matchEntry(row, items) {
        var nzo = (row.dataset.nzo || '').split(',').filter(Boolean);
        var wanted = normalize(row.dataset.name);
        var byName = null;
        for (var i = 0; i < items.length; i++) {
            var entry = items[i];
            if (nzo.length && entry.id && nzo.indexOf(entry.id) >= 0) { return entry; }
            if (!byName && wanted && (normalize(entry.name) === wanted || normalize(entry.title) === wanted)) {
                byName = entry;
            }
        }
        return byName;
    }

    function startPoll() {
        if (pollTimer) { return; }
        pollTimer = setInterval(function () {
            if (!document.querySelector('.tmRelRow') && !document.getElementById('tmDownloads') && !document.getElementById('tmDownloadHero')) {
                stopPoll();
                return;
            }
            refreshStatus();
        }, 3000);
    }

    function stopPoll() {
        if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    }
})();
