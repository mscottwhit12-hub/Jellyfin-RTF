/* Race to Finish web client (v0.1).
 * Adds a header button that opens the Option D popup.
 * Written for old TV browsers too (Samsung Tizen): no optional chaining, no ?? operator.
 */
(function () {
  'use strict';

  if (window.__rtfLoaded) { return; }
  window.__rtfLoaded = true;

  var state = { open: false, tab: 'challenges', user: null, config: { useBlur: true }, lastFocus: null };

  var ICON_TROPHY = '<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M8 21h8M12 17v4M7 4h10v5a5 5 0 0 1-10 0V4z"></path><path d="M17 5h3v2a3 3 0 0 1-3 3M7 5H4v2a3 3 0 0 0 3 3"></path></svg>';
  var ICON_CLOSE = '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"></path></svg>';
  var ICON_FLAG = '<svg width="38" height="38" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M5 22V3M5 4h13l-3 4 3 4H5"></path></svg>';
  var RING = '<svg class="rtf-ring" width="44" height="44" viewBox="0 0 44 44" aria-hidden="true"><circle cx="22" cy="22" r="20" fill="none" stroke="rgba(255,255,255,0.16)" stroke-width="2.5"></circle></svg>';

  function esc(text) {
    return String(text == null ? '' : text).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function api() { return window.ApiClient || null; }

  function signedInUserId() {
    var client = api();
    try { return client && client.getCurrentUserId ? client.getCurrentUserId() : null; } catch (e) { return null; }
  }

  // ---------- Who may use the plugin ----------
  // Accounts hidden from the login screen get nothing. (The server enforces this too from v0.2 on.)
  function loadUser() {
    var client = api();
    var id = signedInUserId();
    if (!client || !id) { return Promise.resolve(null); }
    if (state.user && state.user.Id === id) { return Promise.resolve(state.user); }
    return client.getCurrentUser().then(function (user) {
      state.user = user;
      return user;
    }, function () { return null; });
  }

  function allowed(user) {
    return !!(user && !(user.Policy && user.Policy.IsHidden));
  }

  function loadConfig() {
    var client = api();
    if (!client) { return; }
    fetch(client.getUrl('RaceToFinish/ClientConfig')).then(function (r) { return r.json(); }).then(function (cfg) {
      state.config = cfg || state.config;
    }, function () { /* keep defaults */ });
  }

  // ---------- Header button ----------
  function isVisible(el) {
    if (!el) { return false; }
    var rect = el.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0 && window.getComputedStyle(el).visibility !== 'hidden';
  }

  // Where the button goes, in order of preference. Only a header that is actually on screen counts:
  // Jellyfin 12's modern layout keeps the old header in the page but hidden, which is what hid the
  // button in v0.1.0.
  function findHeaderSlot() {
    // Jellyfin 12 modern layout: the MUI toolbar group holding SyncPlay, Cast and Search.
    // Matched by aria-controls/href, which do not change with the interface language.
    var anchor = document.querySelector(
      '.MuiToolbar-root [aria-controls="app-remote-play-menu"], ' +
      '.MuiToolbar-root [aria-controls="app-sync-play-menu"], ' +
      '.MuiToolbar-root a[href$="#/search"], .MuiToolbar-root a[href*="/search"]');
    if (anchor && isVisible(anchor.parentElement)) { return anchor.parentElement; }

    // Legacy layouts (desktop-legacy, mobile-legacy, TV).
    var legacy = document.querySelector('.skinHeader .headerRight');
    if (isVisible(legacy)) { return legacy; }

    // Last resort in the modern layout: next to the avatar button.
    var avatarButton = document.querySelector('.MuiToolbar-root button[aria-controls="app-user-menu"]');
    if (avatarButton && isVisible(avatarButton.parentElement)) { return avatarButton.parentElement; }

    return null;
  }

  // The header is hidden while a video plays; a floating button must not sit on top of the picture.
  function isPlayingVideo() {
    return location.hash.indexOf('/video') !== -1 || !!document.querySelector('.videoPlayerContainer');
  }

  function makeButton(floating) {
    var btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'rtf-headerbtn' + (floating ? ' rtf-floatbtn' : '');
    btn.setAttribute('aria-label', 'Open Challenges');
    btn.setAttribute('title', 'Challenges');
    btn.innerHTML = (floating ? '' : RING) + ICON_TROPHY;
    btn.addEventListener('click', function (e) { e.preventDefault(); e.stopPropagation(); openPopup(); });
    return btn;
  }

  var floatTimer = null;

  function ensureButton() {
    var id = signedInUserId();
    var existing = document.querySelector('.rtf-headerbtn');
    if (!id) {
      if (existing) { existing.parentNode.removeChild(existing); }
      return;
    }

    loadUser().then(function (user) {
      var current = document.querySelector('.rtf-headerbtn');
      if (!allowed(user)) {
        if (current) { current.parentNode.removeChild(current); }
        return;
      }

      var slot = findHeaderSlot();
      if (slot) {
        if (current && current.parentNode === slot) { return; }
        if (current) { current.parentNode.removeChild(current); }
        slot.insertBefore(makeButton(false), slot.firstChild);
        return;
      }

      var floating = current && current.classList.contains('rtf-floatbtn');
      if (isPlayingVideo()) {
        if (floating) { current.parentNode.removeChild(current); }
        return;
      }

      // A button left in a header that is no longer shown is useless; the floating one replaces it.
      if (current && !floating && !isVisible(current)) {
        current.parentNode.removeChild(current);
        current = null;
      }

      // No visible header (theme or Jellyfin layout change). Wait a moment, then use a floating
      // button so the plugin is always reachable.
      if (!current && !floatTimer) {
        floatTimer = setTimeout(function () {
          floatTimer = null;
          if (!findHeaderSlot() && !isPlayingVideo() && !document.querySelector('.rtf-headerbtn')) {
            document.body.appendChild(makeButton(true));
          }
        }, 4000);
      }
    });
  }

  var scheduled = false;
  function scheduleEnsure() {
    if (scheduled) { return; }
    scheduled = true;
    setTimeout(function () { scheduled = false; ensureButton(); }, 250);
  }

  // ---------- Popup ----------
  function meBlock() {
    var u = state.user || {};
    return '<div class="rtf-me">' + avatar(u, 40) +
      '<span class="rtf-me-text"><span class="rtf-me-name">' + esc(u.Name) + '</span>' +
      '<span class="rtf-me-title">No title yet</span></span></div>';
  }

  function avatar(user, size) {
    var client = api();
    var initial = esc((user && user.Name ? user.Name : '?').charAt(0).toUpperCase());
    if (client && user && user.Id && user.PrimaryImageTag) {
      var url = client.getUrl('Users/' + user.Id + '/Images/Primary', { tag: user.PrimaryImageTag, maxHeight: size * 2, quality: 90 });
      return '<span class="rtf-avatar" style="background-image:url(\'' + esc(url) + '\')" aria-hidden="true"></span>';
    }
    return '<span class="rtf-avatar" aria-hidden="true">' + initial + '</span>';
  }

  var TABS = [
    { id: 'challenges', label: 'Challenges' },
    { id: 'hall', label: '<span class="rtf-long">Hall of Champions</span><span class="rtf-short">Champions</span>' },
    { id: 'profiles', label: 'Profiles' },
    { id: 'activity', label: '<span class="rtf-long">My Activity</span><span class="rtf-short">Mine</span>' }
  ];

  function shell() {
    var tabs = TABS.map(function (t) {
      return '<button type="button" role="tab" class="rtf-tab" data-tab="' + t.id + '" aria-selected="' + (state.tab === t.id) + '">' + t.label + '</button>';
    }).join('');
    return '<div class="rtf-panel" role="dialog" aria-modal="true" aria-label="Race to Finish">' +
      '<div class="rtf-top">' + meBlock() +
      '<div class="rtf-tabs" role="tablist">' + tabs + '</div>' +
      '<div class="rtf-topright"><button type="button" class="rtf-iconbtn" data-close aria-label="Close">' + ICON_CLOSE + '</button></div>' +
      '</div>' +
      '<div class="rtf-body" data-body></div>' +
      '<div class="rtf-foot">Race to Finish ' + esc(state.config.version || '') + '</div>' +
      '</div>';
  }

  function emptyState(title, text) {
    return '<div class="rtf-empty"><span class="rtf-empty-icon">' + ICON_FLAG + '</span>' +
      '<h2>' + esc(title) + '</h2><p>' + esc(text) + '</p></div>';
  }

  function renderBody(root) {
    var body = root.querySelector('[data-body]');
    if (state.tab === 'challenges') {
      body.innerHTML = '<h1 class="rtf-h1">Active challenges</h1>' +
        emptyState('No challenges yet', 'When a race starts it shows up here. Admins start one from Dashboard → Plugins → Race to Finish.');
    } else if (state.tab === 'hall') {
      body.innerHTML = '<h1 class="rtf-h1">Hall of Champions</h1>' +
        emptyState('No champions yet', 'Finished races and their winners are kept here for good.');
    } else if (state.tab === 'profiles') {
      body.innerHTML = '<h1 class="rtf-h1">Profiles</h1><div class="rtf-sub">Everyone shown on the login screen can play.</div><div class="rtf-people" data-people></div>';
      renderPeople(body.querySelector('[data-people]'));
    } else {
      renderActivity(body);
    }
  }

  // ---------- My Activity (private to the signed-in user) ----------
  // A plain check that watches are being recorded. The real stats page comes later.
  function authHeader() {
    var client = api();
    var token = client && client.accessToken ? client.accessToken() : '';
    return 'MediaBrowser Client="Race to Finish", Device="Web", DeviceId="rtf-web", Version="1", Token="' + token + '"';
  }

  function apiFetch(method, path, body) {
    var client = api();
    var options = { method: method, headers: { 'Authorization': authHeader(), 'X-Emby-Token': client.accessToken() } };
    if (body !== undefined) {
      options.headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(body);
    }
    return fetch(client.getUrl(path), options).then(function (response) {
      if (!response.ok) { throw new Error('HTTP ' + response.status); }
      var type = response.headers.get('content-type') || '';
      return type.indexOf('json') !== -1 ? response.json() : response.blob();
    });
  }

  function formatWhen(iso) {
    if (!iso) { return 'Date unknown'; }
    var d = new Date(iso);
    if (isNaN(d.getTime())) { return 'Date unknown'; }
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' }) + ' · ' +
      d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  }

  function itemLabel(row) {
    if (row.type === 'Episode' && row.seriesTitle) {
      var code = (row.season != null ? 'S' + row.season : '') + (row.episode != null ? 'E' + row.episode : '');
      return { main: row.seriesTitle, sub: (code ? code + ' · ' : '') + row.title };
    }
    return { main: row.title, sub: row.year ? String(row.year) : 'Movie' };
  }

  function activityRow(row) {
    var label = itemLabel(row);
    var imported = row.source && row.source !== 'live';
    var status = row.finished
      ? '<span class="rtf-chip rtf-chip-done">Finished</span>'
      : '<span class="rtf-chip">' + Math.round((row.percent || 0) * 100) + '% watched</span>';
    var source = imported ? '<span class="rtf-chip rtf-chip-old">Imported</span>' : '';
    var detail = imported && row.source === 'imported-userdata'
      ? 'From Jellyfin\'s watch history' + (row.playCount > 1 ? ' · played ' + row.playCount + ' times' : '')
      : Math.round(row.watchedMinutes) + ' min' + (row.device ? ' · ' + esc(row.device) : '');
    // Focusable so a TV remote can move down the list (and scroll it).
    return '<li class="rtf-act" tabindex="0">' +
      '<span class="rtf-act-text"><span class="rtf-act-title">' + esc(label.main) + '</span>' +
      '<span class="rtf-act-sub">' + esc(label.sub) + '</span></span>' +
      '<span class="rtf-act-when">' + esc(formatWhen(row.endedUtc || row.startedUtc)) + '<br><span class="rtf-act-sub">' + detail + '</span></span>' +
      '<span class="rtf-act-chips">' + status + source + '</span></li>';
  }

  function renderActivity(body) {
    body.innerHTML = '<h1 class="rtf-h1">My Activity</h1><div class="rtf-sub">Only you can see this page.</div><div class="rtf-activity" data-activity><div class="rtf-sub">Loading…</div></div>';
    var target = body.querySelector('[data-activity]');
    Promise.all([apiFetch('GET', 'RaceToFinish/Me'), apiFetch('GET', 'RaceToFinish/Me/Activity?limit=25')]).then(function (results) {
      if (!state.open || state.tab !== 'activity') { return; }
      var me = results[0], rows = results[1] || [];
      var list = rows.length
        ? '<ul class="rtf-acts">' + rows.map(activityRow).join('') + '</ul>'
        : emptyState('Nothing recorded yet', 'Watch something for at least a minute and it shows up here.');
      target.innerHTML =
        '<div class="rtf-stats">' +
          (me.liveMinutes < 60 ? stat(me.liveMinutes, me.liveMinutes === 1 ? 'minute recorded' : 'minutes recorded') : stat(me.liveHours, 'hours recorded')) +
          stat(me.liveSessions, 'watches recorded') +
          stat(me.importedSessions, 'imported from before') +
        '</div>' +
        '<div class="rtf-card rtf-privacy">' +
          '<span class="rtf-privacy-text"><span class="rtf-card-title">Share my viewing stats with the server admin</span>' +
          '<span class="rtf-act-sub">Off by default. When on, the admin can see this activity. Race standings are always visible to everyone, but only for that race\'s movies. ' +
          'Note: whoever runs the server can technically read the raw data files.</span></span>' +
          '<button type="button" role="switch" class="rtf-switch" data-share aria-checked="' + (me.shareWithAdmin ? 'true' : 'false') + '" aria-label="Share my viewing stats with the server admin"><span></span></button>' +
        '</div>' +
        '<h2 class="rtf-h2">Recent</h2>' + list +
        '<div class="rtf-actions">' +
          '<button type="button" class="rtf-btn" data-export>Download my data</button>' +
          '<button type="button" class="rtf-btn rtf-btn-danger" data-delete>Delete my viewing data</button>' +
          '<span class="rtf-act-sub rtf-delete-note">Deleting removes everything recorded and imported about you, and stops older history being imported again.</span>' +
        '</div>';
      wireActivity(target);
    }, function () {
      target.innerHTML = emptyState('Could not load your activity', 'Try again in a moment. If it keeps happening, ask your admin to check the plugin is up to date.');
    });
  }

  function stat(value, label) {
    return '<div class="rtf-stat"><span class="rtf-stat-value">' + esc(value) + '</span><span class="rtf-stat-label">' + esc(label) + '</span></div>';
  }

  function wireActivity(target) {
    var share = target.querySelector('[data-share]');
    share.addEventListener('click', function () {
      var next = share.getAttribute('aria-checked') !== 'true';
      share.disabled = true;
      apiFetch('PUT', 'RaceToFinish/Me/Privacy', { shareWithAdmin: next }).then(function (r) {
        share.setAttribute('aria-checked', r.shareWithAdmin ? 'true' : 'false');
      }).then(function () { share.disabled = false; }, function () { share.disabled = false; });
    });

    target.querySelector('[data-export]').addEventListener('click', function () {
      apiFetch('GET', 'RaceToFinish/Me/Export').then(function (data) {
        var blob = data instanceof Blob ? data : new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
        var link = document.createElement('a');
        link.href = URL.createObjectURL(blob);
        link.download = 'race-to-finish-my-data.json';
        document.body.appendChild(link);
        link.click();
        setTimeout(function () { URL.revokeObjectURL(link.href); link.parentNode.removeChild(link); }, 1000);
      });
    });

    // Two presses to delete, no browser dialog (dialogs don't work well on TVs).
    var del = target.querySelector('[data-delete]');
    del.addEventListener('click', function () {
      if (!del.getAttribute('data-armed')) {
        del.setAttribute('data-armed', '1');
        del.textContent = 'Press again to delete everything';
        setTimeout(function () {
          if (del.getAttribute('data-armed')) { del.removeAttribute('data-armed'); del.textContent = 'Delete my viewing data'; }
        }, 5000);
        return;
      }
      del.disabled = true;
      apiFetch('DELETE', 'RaceToFinish/Me/Data').then(function () {
        var root = document.querySelector('.rtf-root');
        if (root) { renderBody(root); }
      });
    });
  }

  function renderPeople(container) {
    var client = api();
    if (!client || !client.getPublicUsers) { return; }
    client.getPublicUsers().then(function (users) {
      if (!state.open) { return; }
      container.innerHTML = (users || []).map(function (u) {
        return '<button type="button" class="rtf-person">' + avatar(u, 52) +
          '<span><span class="rtf-person-name">' + esc(u.Name) + '</span><br><span class="rtf-person-title">No title yet</span></span></button>';
      }).join('');
    });
  }

  function selectTab(root, id) {
    state.tab = id;
    var tabs = root.querySelectorAll('.rtf-tab');
    for (var i = 0; i < tabs.length; i++) {
      tabs[i].setAttribute('aria-selected', String(tabs[i].getAttribute('data-tab') === id));
    }
    renderBody(root);
  }

  function openPopup() {
    if (state.open) { return; }
    state.open = true;
    state.lastFocus = document.activeElement;

    var root = document.createElement('div');
    root.className = 'rtf-root' + (state.config.useBlur === false ? ' rtf-solid' : '');
    root.innerHTML = shell();
    document.body.appendChild(root);

    root.addEventListener('click', function (e) {
      if (e.target === root) { closePopup(); return; }
      var tab = e.target.closest ? e.target.closest('[data-tab]') : null;
      if (tab) { selectTab(root, tab.getAttribute('data-tab')); return; }
      if (e.target.closest && e.target.closest('[data-close]')) { closePopup(); }
    });

    renderBody(root);
    var first = root.querySelector('.rtf-tab[aria-selected="true"]');
    if (first) { first.focus(); }
    window.addEventListener('keydown', onKeyDown, true);
  }

  function closePopup() {
    var root = document.querySelector('.rtf-root');
    if (root) { root.parentNode.removeChild(root); }
    state.open = false;
    window.removeEventListener('keydown', onKeyDown, true);
    if (state.lastFocus && state.lastFocus.focus) { try { state.lastFocus.focus(); } catch (e) { /* gone */ } }
  }

  // ---------- Keyboard and TV remote ----------
  // Runs in the capture phase so Jellyfin's own navigation never sees these keys while the popup is open.
  var BACK_KEYS = { Escape: 1, Esc: 1, Backspace: 1, GoBack: 1, BrowserBack: 1 };

  function onKeyDown(e) {
    if (!state.open) { return; }
    var root = document.querySelector('.rtf-root');
    if (!root) { return; }
    var typing = e.target && (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA');

    if (BACK_KEYS[e.key] || e.keyCode === 10009 || e.keyCode === 461) {   // 10009 Samsung, 461 LG
      if (e.key === 'Backspace' && typing) { return; }
      stop(e);
      closePopup();
      return;
    }

    var dir = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down', Left: 'left', Right: 'right', Up: 'up', Down: 'down' }[e.key];
    if (dir && !typing) {
      stop(e);
      moveFocus(root, dir);
      return;
    }

    if (e.key === 'Tab') {   // keep focus inside the popup
      var items = focusables(root);
      if (!items.length) { return; }
      var index = items.indexOf(document.activeElement);
      var next = e.shiftKey ? (index <= 0 ? items.length - 1 : index - 1) : (index + 1) % items.length;
      stop(e);
      items[next].focus();
    }
  }

  function stop(e) {
    e.preventDefault();
    e.stopPropagation();
    if (e.stopImmediatePropagation) { e.stopImmediatePropagation(); }
  }

  function focusables(root) {
    var list = root.querySelectorAll('button, a[href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
    var out = [];
    for (var i = 0; i < list.length; i++) {
      if (!list[i].disabled && list[i].offsetParent !== null) { out.push(list[i]); }
    }
    return out;
  }

  // Spatial navigation: pick the closest focusable in the pressed direction.
  function moveFocus(root, dir) {
    var items = focusables(root);
    var current = document.activeElement;
    if (items.indexOf(current) === -1) { if (items[0]) { items[0].focus(); } return; }

    var a = current.getBoundingClientRect();
    var ax = a.left + a.width / 2, ay = a.top + a.height / 2;
    var best = null, bestScore = Infinity;

    items.forEach(function (el) {
      if (el === current) { return; }
      var b = el.getBoundingClientRect();
      var bx = b.left + b.width / 2, by = b.top + b.height / 2;
      var dx = bx - ax, dy = by - ay;
      var ok = (dir === 'left' && dx < -4) || (dir === 'right' && dx > 4) || (dir === 'up' && dy < -4) || (dir === 'down' && dy > 4);
      if (!ok) { return; }
      var along = (dir === 'left' || dir === 'right') ? Math.abs(dx) : Math.abs(dy);
      var across = (dir === 'left' || dir === 'right') ? Math.abs(dy) : Math.abs(dx);
      var score = along + across * 2.5;
      if (score < bestScore) { bestScore = score; best = el; }
    });

    if (best) {
      best.focus();
      if (best.scrollIntoView) { best.scrollIntoView({ block: 'nearest' }); }
    }
  }

  // ---------- Start ----------
  function start() {
    loadConfig();
    ensureButton();
    // Jellyfin re-renders the header when navigating; put the button back when that happens.
    new MutationObserver(scheduleEnsure).observe(document.body, { childList: true, subtree: true });
    document.addEventListener('viewshow', scheduleEnsure);
    window.addEventListener('hashchange', scheduleEnsure);
  }

  function waitForClient(tries) {
    if (api()) { start(); return; }
    if (tries > 120) { return; }   // ~60 s; Jellyfin never initialised
    setTimeout(function () { waitForClient(tries + 1); }, 500);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', function () { waitForClient(0); });
  } else {
    waitForClient(0);
  }
})();
