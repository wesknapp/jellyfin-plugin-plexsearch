/* Plex Search for Jellyfin: adds an "On Plex" section to the Jellyfin search page. */
(function () {
    'use strict';
    if (window.__plexSearchLoaded) return;
    window.__plexSearchLoaded = true;

    var SECTION_ID = 'plexSearchSection';
    var lastQuery = null;
    var requestSeq = 0;
    var debounceTimer = null;
    var blobUrls = [];

    // ---------- helpers ----------

    function norm(o) {
        if (Array.isArray(o)) return o.map(norm);
        if (o && typeof o === 'object') {
            var r = {};
            Object.keys(o).forEach(function (k) { r[k.charAt(0).toLowerCase() + k.slice(1)] = norm(o[k]); });
            return r;
        }
        return o;
    }

    function authHeaders() {
        return { 'Authorization': 'MediaBrowser Token="' + window.ApiClient.accessToken() + '"' };
    }

    function apiGet(path, asBlob) {
        return fetch(window.ApiClient.getUrl('PlexSearch/' + path), { headers: authHeaders() }).then(function (r) {
            if (!r.ok) { var e = new Error('HTTP ' + r.status); e.status = r.status; throw e; }
            return asBlob ? r.blob() : r.json().then(norm);
        });
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function isSearchPage() {
        return /^#\/search(\.html)?(\?|$)/i.test(window.location.hash || '');
    }

    function currentQuery() {
        var hash = window.location.hash || '';
        var i = hash.indexOf('?');
        var q = i >= 0 ? new URLSearchParams(hash.slice(i + 1)).get('query') : null;
        if (!q) {
            // Fall back to the search box in case the URL hasn't updated yet.
            var input = visible(document.querySelectorAll('input[type="search"], .searchfields-txtSearch'));
            q = input ? input.value : '';
        }
        return (q || '').trim();
    }

    function visible(nodes) {
        for (var i = 0; i < nodes.length; i++) {
            if (nodes[i].offsetParent !== null) return nodes[i];
        }
        return null;
    }

    function findHost() {
        var results = visible(document.querySelectorAll('.searchResults'));
        if (results) return results;
        var pages = document.querySelectorAll('.page:not(.hide), .mainAnimatedPage:not(.hide)');
        for (var i = pages.length - 1; i >= 0; i--) {
            if (pages[i].querySelector('input[type="search"]') && pages[i].offsetParent !== null) return pages[i];
        }
        return visible(pages);
    }

    // ---------- rendering ----------

    function ensureStyles() {
        if (document.getElementById('plexSearchStyles')) return;
        var css = ''
            + '#' + SECTION_ID + '{padding:1em 3.3% 2em;}'
            + '#' + SECTION_ID + ' h2{margin:.2em 0 .6em;font-size:1.3em;display:flex;align-items:center;gap:.5em;}'
            + '#' + SECTION_ID + ' .ps-badge{font-size:.6em;padding:.15em .5em;border-radius:4px;background:#e5a00d;color:#1f1f1f;font-weight:700;}'
            + '#' + SECTION_ID + ' .ps-row{display:flex;gap:1em;overflow-x:auto;padding-bottom:.5em;scroll-snap-type:x proximity;}'
            + '#' + SECTION_ID + ' .ps-card{flex:0 0 auto;width:9em;cursor:pointer;text-decoration:none;color:inherit;scroll-snap-align:start;}'
            + '#' + SECTION_ID + ' .ps-poster{width:9em;height:13.5em;border-radius:6px;background:rgba(127,127,127,.25) center/cover no-repeat;'
            + 'display:flex;align-items:center;justify-content:center;font-size:.8em;text-align:center;overflow:hidden;}'
            + '#' + SECTION_ID + ' .ps-title{margin-top:.4em;font-size:.9em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}'
            + '#' + SECTION_ID + ' .ps-sub{font-size:.75em;opacity:.7;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}'
            + '#' + SECTION_ID + ' .ps-msg{opacity:.7;}';
        var style = document.createElement('style');
        style.id = 'plexSearchStyles';
        style.textContent = css;
        document.head.appendChild(style);
    }

    function getSection() {
        var host = findHost();
        if (!host) return null;
        var section = document.getElementById(SECTION_ID);
        if (!section) {
            section = document.createElement('div');
            section.id = SECTION_ID;
            section.className = 'verticalSection';
        }
        if (section.parentNode !== host || host.lastElementChild !== section) {
            host.appendChild(section);
        }
        return section;
    }

    function removeSection() {
        var s = document.getElementById(SECTION_ID);
        if (s) s.remove();
    }

    function freeBlobs() {
        blobUrls.forEach(function (u) { URL.revokeObjectURL(u); });
        blobUrls = [];
    }

    function renderMessage(serverName, msg) {
        var section = getSection();
        if (!section) return;
        section.innerHTML = '<h2>On Plex <span class="ps-badge">' + esc(serverName || 'PLEX') + '</span></h2>'
            + '<div class="ps-msg">' + esc(msg) + '</div>';
    }

    function renderResults(data) {
        freeBlobs();
        var section = getSection();
        if (!section) return;
        if (!data.results.length) {
            renderMessage(data.serverName, 'No matches on Plex.');
            return;
        }
        var html = '<h2>On Plex <span class="ps-badge">' + esc(data.serverName) + '</span></h2><div class="ps-row">';
        data.results.forEach(function (r, i) {
            var sub = (r.year ? r.year + ' · ' : '') + (r.type === 'show' ? 'TV' : 'Movie')
                + (r.libraryTitle ? ' · ' + r.libraryTitle : '');
            html += '<a class="ps-card" href="' + esc(r.webUrl) + '" target="_blank" rel="noopener" title="Open in Plex">'
                + '<div class="ps-poster" data-ps-img="' + i + '">' + esc(r.title) + '</div>'
                + '<div class="ps-title">' + esc(r.title) + '</div>'
                + '<div class="ps-sub">' + esc(sub) + '</div></a>';
        });
        html += '</div>';
        section.innerHTML = html;

        data.results.forEach(function (r, i) {
            if (!r.imagePath) return;
            apiGet('Image?width=240&height=360&path=' + encodeURIComponent(r.imagePath), true).then(function (blob) {
                var url = URL.createObjectURL(blob);
                blobUrls.push(url);
                var el = section.querySelector('[data-ps-img="' + i + '"]');
                if (el) { el.style.backgroundImage = 'url("' + url + '")'; el.textContent = ''; }
            }).catch(function () { /* keep the title placeholder */ });
        });
    }

    // ---------- search loop ----------

    function runSearch(query) {
        var seq = ++requestSeq;
        renderMessage('', 'Searching Plex…');
        apiGet('Search?query=' + encodeURIComponent(query)).then(function (data) {
            if (seq !== requestSeq) return;
            if (!data.configured) { removeSection(); return; }
            renderResults(data);
        }).catch(function () {
            if (seq !== requestSeq) return;
            renderMessage('', 'Plex unavailable right now.');
        });
    }

    function tick() {
        if (!window.ApiClient || !isSearchPage()) {
            if (lastQuery !== null) { lastQuery = null; requestSeq++; freeBlobs(); removeSection(); }
            return;
        }
        var q = currentQuery();
        if (q === lastQuery) {
            // Jellyfin may re-render the results area; keep our section attached at the bottom.
            if (q.length >= 2 && document.getElementById(SECTION_ID)) getSection();
            return;
        }
        lastQuery = q;
        clearTimeout(debounceTimer);
        if (q.length < 2) { requestSeq++; removeSection(); return; }
        debounceTimer = setTimeout(function () { runSearch(q); }, 400);
    }

    ensureStyles();
    window.addEventListener('hashchange', tick);
    document.addEventListener('input', function (e) {
        if (e.target && e.target.matches && e.target.matches('input[type="search"], .searchfields-txtSearch')) setTimeout(tick, 50);
    }, true);
    setInterval(tick, 700);
    tick();
})();
