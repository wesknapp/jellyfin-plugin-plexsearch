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
            // Cards use Jellyfin's own card classes, so they are the same size as Jellyfin's and Seerr's cards.
            + '#' + SECTION_ID + ' h2{display:flex;align-items:center;gap:.5em;}'
            + '#' + SECTION_ID + ' .ps-badge{font-size:.6em;padding:.15em .5em;border-radius:4px;background:#e5a00d;color:#1f1f1f;font-weight:700;}'
            + '#' + SECTION_ID + ' .ps-row{display:flex;flex-wrap:nowrap;overflow-x:auto;scrollbar-width:thin;}'
            + '#' + SECTION_ID + ' .ps-row>.card{flex:0 0 auto;color:inherit;text-decoration:none;}'
            + '#' + SECTION_ID + ' .ps-poster{display:flex;align-items:center;justify-content:center;text-align:center;'
            + 'padding:.5em;box-sizing:border-box;font-size:.9em;background-color:rgba(127,127,127,.25);}'
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
        // Sit after Jellyfin's own results but above Jellyfin Enhanced's "Discover on Seerr" section.
        var seerr = host.querySelector(':scope > .jellyseerr-section');
        if (seerr) {
            if (section.parentNode !== host || section.nextElementSibling !== seerr) host.insertBefore(section, seerr);
        } else if (section.parentNode !== host || host.lastElementChild !== section) {
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
        section.innerHTML = heading(serverName)
            + '<div class="ps-msg padded-left padded-right">' + esc(msg) + '</div>';
    }

    function heading(serverName) {
        return '<h2 class="sectionTitle sectionTitle-cards padded-left padded-right">On Plex <span class="ps-badge">'
            + esc(serverName || 'PLEX') + '</span></h2>';
    }

    // On phones and tablets, open the Plex app instead of app.plex.tv.
    var UA = navigator.userAgent || '';
    var IS_IOS = /iPad|iPhone|iPod/.test(UA) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    var IS_ANDROID = /Android/i.test(UA);

    function plexLink(r) {
        var m = /\/server\/([^/?#]+)\//.exec(r.webUrl || '');
        if (!m || !r.ratingKey || !(IS_IOS || IS_ANDROID)) return r.webUrl;
        var query = 'metadataKey=' + encodeURIComponent('/library/metadata/' + r.ratingKey) + '&server=' + m[1];
        if (IS_IOS) return 'plex://preplay/?' + query;
        // Chrome on Android: open the Plex app, or app.plex.tv if it isn't installed.
        return 'intent://preplay/?' + query + '#Intent;scheme=plex;package=com.plexapp.android;S.browser_fallback_url='
            + encodeURIComponent(r.webUrl) + ';end';
    }

    function renderResults(data) {
        freeBlobs();
        var section = getSection();
        if (!section) return;
        if (!data.results.length) {
            renderMessage(data.serverName, 'No matches on Plex.');
            return;
        }
        var html = heading(data.serverName) + '<div class="ps-row itemsContainer padded-left padded-right">';
        data.results.forEach(function (r, i) {
            var sub = (r.year ? r.year + ' · ' : '') + (r.type === 'show' ? 'TV' : 'Movie')
                + (r.libraryTitle ? ' · ' + r.libraryTitle : '');
            var link = plexLink(r);
            html += '<a class="card overflowPortraitCard card-hoverable" href="' + esc(link) + '"'
                + (link === r.webUrl ? ' target="_blank" rel="noopener"' : '') + ' title="Open in Plex">'
                + '<div class="cardBox cardBox-bottompadded"><div class="cardScalable">'
                + '<div class="cardPadder cardPadder-overflowPortrait"></div>'
                + '<div class="cardImageContainer coveredImage cardContent ps-poster" data-ps-img="' + i + '">' + esc(r.title) + '</div>'
                + '</div>'
                + '<div class="cardText cardTextCentered cardText-first"><bdi>' + esc(r.title) + '</bdi></div>'
                + '<div class="cardText cardTextCentered cardText-secondary"><bdi>' + esc(sub) + '</bdi></div>'
                + '</div></a>';
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
        removeSection();
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
            // Jellyfin may re-render the results area, and Seerr loads late; keep our section in place.
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
