using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListRatings.Web;

/// <summary>
/// Injects a small JavaScript snippet into Jellyfin Web (index.html) that replaces the rating star icon
/// with the logo of the rating provider that was actually applied by this plugin.
/// </summary>
internal static class WebUiInjector
{
    private const string MarkerStart = "<!-- MDBListRatings:rating-source-icons:start -->";
    private const string MarkerEnd = "<!-- MDBListRatings:rating-source-icons:end -->";

    public static void TryInject(IApplicationPaths applicationPaths, Guid pluginId, ILogger logger)
    {
        try
        {
            var webPath = TryGetWebPath(applicationPaths);
            if (string.IsNullOrWhiteSpace(webPath))
            {
                logger.LogDebug("MDBListRatings: WebPath is not available. Skipping Jellyfin Web UI injection.");
                return;
            }

            var indexPath = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexPath))
            {
                logger.LogDebug("MDBListRatings: index.html not found at {IndexPath}. Skipping injection.", indexPath);
                return;
            }

            var html = File.ReadAllText(indexPath);
            var injection = BuildInjectionBlock(pluginId);

            // If a previous version was injected, replace it in-place so updates take effect.
            var startIdx = html.IndexOf(MarkerStart, StringComparison.Ordinal);
            if (startIdx >= 0)
            {
                var endIdx = html.IndexOf(MarkerEnd, startIdx, StringComparison.Ordinal);
                if (endIdx > startIdx)
                {
                    endIdx += MarkerEnd.Length;
                    html = html.Substring(0, startIdx)
                        + injection
                        + html.Substring(endIdx);

                    File.WriteAllText(indexPath, html);
                    logger.LogInformation("MDBListRatings: updated Web UI rating icon replacer in index.html. Refresh your browser to apply.");
                    return;
                }
                // If markers are malformed, fall through and re-inject at the end.
            }

            var insertPos = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (insertPos < 0)
            {
                // Fallback: append.
                html += Environment.NewLine + injection + Environment.NewLine;
            }
            else
            {
                html = html.Insert(insertPos, injection + Environment.NewLine);
            }

            File.WriteAllText(indexPath, html);
            logger.LogInformation("MDBListRatings: injected Web UI rating icon replacer into index.html. Refresh your browser to apply.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MDBListRatings: failed to inject Web UI script into index.html.");
        }
    }

    private static string? TryGetWebPath(IApplicationPaths applicationPaths)
    {
        // Jellyfin exposes WebPath on the concrete IApplicationPaths implementation.
        // We use reflection to avoid compile-time dependency on a specific interface version.
        var prop = applicationPaths.GetType().GetProperty("WebPath");
        return prop?.GetValue(applicationPaths) as string;
    }

    private static string BuildInjectionBlock(Guid pluginId)
    {
        // NOTE: this script is intentionally self-contained and runs only when ProviderIds contain our keys.
        // It does not modify server state or call any external APIs.
        // Use a C# raw string literal so the embedded JS can contain quotes without escaping.
        var js = """
(function(){
  try {
    if (window.__mdbListRatingsIconPatchApplied) return;
    window.__mdbListRatingsIconPatchApplied = true;

    var PLUGIN_ID = '__PLUGIN_ID__';
    var PROVIDER_KEY = 'MdbListCommunitySource';
    var APPLIED_CLASS = 'mdblist-rating-icon-applied';
    var IMG_CLASS = 'mdblist-rating-icon-img';
    var STAR_SHRINK_CLASS = 'mdblist-star-shrink';
    var STYLE_ID = 'mdblist-rating-icon-style';

    var ICONS = {
      imdb:               'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/IMDb.png',
      tmdb:               'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/TMDB.png',
      tomatoes:           'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/Rotten_Tomatoes.png',
      tomatoes_rotten:    'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/Rotten_Tomatoes_rotten.png',
      tomatoes_certified: 'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/rotten-tomatoes-certified.png',
      audience:           'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/Rotten_Tomatoes_positive_audience.png',
      audience_rotten:    'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/Rotten_Tomatoes_negative_audience.png',
      rotten_ver:         'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/roten_tomatoes_ver.png',
      metacritic:         'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/Metacritic.png',
      metacriticms:       'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/metacriticms.png',
      metacriticus:       'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/mus2.png',
      rogerebert:         'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/Roger_Ebert.png',
      trakt:              'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/Trakt.png',
      letterboxd:         'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/letterboxd.png',
      kinopoisk:          'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/kinopoisk.png',
      myanimelist:        'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/mal.png',
      anilist:            'https://cdn.jsdelivr.net/gh/Druidblack/jellyfin_ratings@main/logo/anilist.png'
    };

    function asset(name){
      try {
        if (window.ApiClient && window.ApiClient.getUrl) {
          return window.ApiClient.getUrl('Plugins/MdbListRatings/Assets/' + name, {});
        }
      } catch (e) {}
      return '/Plugins/MdbListRatings/Assets/' + name;
    }

    function localizeIconUrl(url){
      try {
        var s = String(url || '');
        var m = s.match(/\/logo\/([^\/?#]+)(?:[?#].*)?$/i);
        if (!m) return url;
        return asset(m[1]);
      } catch(e){
        return url;
      }
    }

    // Replace CDN URLs with local plugin-served assets.
    try {
      for (var k in ICONS) {
        if (!Object.prototype.hasOwnProperty.call(ICONS, k)) continue;
        ICONS[k] = localizeIconUrl(ICONS[k]);
      }
    } catch (e) {}


    // Fallback icons (data-uri SVG) for unknown sources.
    var _fallbackIcons = Object.create(null);
    function getFallbackIconUrl(source){
      try {
        var key = String(source || '?').toLowerCase();
        if (_fallbackIcons[key]) return _fallbackIcons[key];
        var label = (key || '?').replace(/[^a-z0-9]/g,'').slice(0,3).toUpperCase();
        if (!label) label = '?';
        var svg = '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64">'
          + '<rect x="0" y="0" width="64" height="64" rx="12" ry="12" fill="#6b7280"/>'
          + '<text x="32" y="40" text-anchor="middle" font-family="Arial, sans-serif" font-size="22" font-weight="700" fill="white">'
          + label + '</text></svg>';
        var uri = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
        _fallbackIcons[key] = uri;
        return uri;
      } catch (e) {
        return null;
      }
    }

    function getIconUrlForSource(source, rating0to10){
      try {
        var iconKey = toIconKey(source, rating0to10);
        var url = iconKey ? ICONS[iconKey] : null;
        return url || getFallbackIconUrl(source);
      } catch (e) {
        return getFallbackIconUrl(source);
      }
    }

    function ensureStyle(){
      if (document.getElementById(STYLE_ID)) return;
      var style = document.createElement('style');
      style.id = STYLE_ID;
      style.textContent = ''+
        '.' + IMG_CLASS + '{'+
        '  display:inline-block !important;'+
        '  height:1.45em !important;'+
        '  width:auto !important;'+
        '  margin-left:0.18em !important;'+
        '  margin-right:0.35em !important;'+
        '  vertical-align:-0.22em !important;'+
        '  object-fit:contain !important;'+
        '  max-width:3.2em !important;'+
        '}'+
        // If we shrink the star, remove the left margin before the icon.
        '.' + STAR_SHRINK_CLASS + ' + .' + IMG_CLASS + '{'+
        '  margin-left:0 !important;'+
        '}'+
        // Make the original star effectively invisible when icons are enabled.
        '.' + STAR_SHRINK_CLASS + '{'+
        '  font-size:0 !important;'+
        '  width:0 !important;'+
        '  margin:0 !important;'+
        '  padding:0 !important;'+
        '  line-height:0 !important;'+
        '  overflow:hidden !important;'+
        '}'+
        '.' + IMG_CLASS + '[hidden]{display:none !important;}' +
        // Details-page feature: hide default ratings and show all cached ratings.
        '.mdblist-hide-default-rating{display:none !important;}' +
        '.mdblist-allratings-container{display:flex;flex-wrap:wrap;align-items:center;gap:0.8em;}' +
        '.mdblist-allratings-item{display:inline-flex;align-items:center;gap:0.25em;}' +
        '.mdblist-allratings-icon{display:inline-block;height:1.35em;width:auto;vertical-align:-0.22em;object-fit:contain;max-width:3.2em;}' +
        '.mdblist-allratings-link{display:inline-flex;align-items:center;text-decoration:none;}' +
        '.mdblist-allratings-link:hover{text-decoration:none;}' +
        '.mdblist-allratings-link img{cursor:pointer;}';
      document.head.appendChild(style);
    }

    // Load plugin configuration once to check whether icons are enabled.
    var _iconsEnabled = null;
    var _iconsEnabledPromise = null;
    function ensureIconsEnabled(){
      if (_iconsEnabled !== null) return Promise.resolve(_iconsEnabled);
      if (_iconsEnabledPromise) return _iconsEnabledPromise;

      // If ApiClient isn't ready or doesn't expose getPluginConfiguration, default to enabled.
      if (!hasApiClient() || !window.ApiClient.getPluginConfiguration) {
        _iconsEnabled = true;
        return Promise.resolve(true);
      }

      _iconsEnabledPromise = window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function(cfg){
        // Default: enabled.
        var v = (cfg && (cfg.EnableWebRatingSourceIcon !== false));
        _iconsEnabled = !!v;
        return _iconsEnabled;
      }).catch(function(){
        _iconsEnabled = true;
        return true;
      }).finally(function(){
        _iconsEnabledPromise = null;
      });

      return _iconsEnabledPromise;
    }

    // --- Details page: show cached ratings from MDBList (web-only) ---
    // We cache the settings to avoid repeated configuration requests.
    var _allRatingsSettings = null;
    var _allRatingsSettingsPromise = null;

    function normalizeAllRatingsMode(v){
      try {
        if (typeof v === 'number') return (v === 1) ? 'custom' : 'all';
        var s = String(v || '').toLowerCase();
        return (s === 'custom' || s === 'selected') ? 'custom' : 'all';
      } catch (e) {
        return 'all';
      }
    }

    function parseSourcesList(raw){
      if (!raw) return [];
      var parts = String(raw)
        .split(/[\n,;]+/)
        .map(function(x){ return (x || '').trim().toLowerCase(); })
        .filter(Boolean);
      var seen = Object.create(null);
      var out = [];
      for (var i=0;i<parts.length;i++) {
        var p = parts[i];
        if (!seen[p]) { seen[p] = true; out.push(p); }
      }
      return out;
    }

    function ensureAllRatingsSettings(){
      if (_allRatingsSettings !== null) return Promise.resolve(_allRatingsSettings);
      if (_allRatingsSettingsPromise) return _allRatingsSettingsPromise;

      // If ApiClient isn't ready or doesn't expose getPluginConfiguration, default to disabled.
      if (!hasApiClient() || !window.ApiClient.getPluginConfiguration){
        _allRatingsSettings = { enabled: false, mode: 'all', order: [], extras: { tc:false, rv:false, mc:false, al:false }, clickable: false };
        return Promise.resolve(_allRatingsSettings);
      }

      _allRatingsSettingsPromise = window.ApiClient.getPluginConfiguration(PLUGIN_ID).then(function(cfg){
        var enabled = !!(cfg && (cfg.EnableWebAllRatingsFromCache === true));
        var mode = normalizeAllRatingsMode(cfg ? (cfg.WebAllRatingsMode || cfg.webAllRatingsMode) : null);
        var orderRaw = cfg ? (cfg.WebAllRatingsOrderCsv || cfg.webAllRatingsOrderCsv || cfg.WebAllRatingsOrder || cfg.webAllRatingsOrder || '') : '';
        var order = parseSourcesList(orderRaw);
        var extras = {
          tc: !!(cfg && (cfg.EnableWebExtraTomatoesCertified === true)),
          rv: !!(cfg && (cfg.EnableWebExtraRottenVerified === true)),
          mc: !!(cfg && (cfg.EnableWebExtraMetacriticMustSee === true)),
          al: !!(cfg && (cfg.EnableWebExtraAniList === true))
        };
        _allRatingsSettings = { enabled: enabled, mode: mode, order: order, extras: extras, clickable: !!(cfg && (cfg.EnableWebClickableRatingIcons === true)) };
        return _allRatingsSettings;
      }).catch(function(){
        _allRatingsSettings = { enabled: false, mode: 'all', order: [], extras: { tc:false, rv:false, mc:false, al:false }, clickable: false };
        return _allRatingsSettings;
      }).finally(function(){
        _allRatingsSettingsPromise = null;
      });

      return _allRatingsSettingsPromise;
    }

    function hasApiClient(){
      return typeof window.ApiClient !== 'undefined' && window.ApiClient && typeof window.ApiClient.getUrl === 'function';
    }

    function getUserId(){
      try {
        if (!hasApiClient()) return null;
        if (typeof window.ApiClient.getCurrentUserId === 'function') return window.ApiClient.getCurrentUserId();
        if (window.ApiClient._serverInfo && window.ApiClient._serverInfo.UserId) return window.ApiClient._serverInfo.UserId;
        if (window.ApiClient._currentUserId) return window.ApiClient._currentUserId;
      } catch (e) {}
      return null;
    }

    function ajaxJson(url){
      if (!window.ApiClient) return Promise.reject(new Error('ApiClient not ready'));
      if (window.ApiClient.getJSON) return window.ApiClient.getJSON(url);
      return window.ApiClient.ajax({ url: url, type: 'GET', dataType: 'json' });
    }

    function parseItemIdFromHref(href){
      if (!href) return null;
      var m = /[?&]id=([^&]+)/i.exec(href);
      if (!m) return null;
      try { return decodeURIComponent(m[1]); } catch(e) { return m[1]; }
    }

    function findItemId(el){
      // Walk up for common data-* attributes.
      var p = el;
      while (p && p !== document.documentElement) {
        if (p.getAttribute) {
          var did = p.getAttribute('data-id') || p.getAttribute('data-itemid') || p.getAttribute('data-item-id');
          if (did) return did;
        }
        p = p.parentElement;
      }

      // Try closest card anchor.
      var card = el.closest && el.closest('.card');
      if (card) {
        var a = card.querySelector('a[href*="id="]');
        if (a) {
          var id1 = parseItemIdFromHref(a.getAttribute('href'));
          if (id1) return id1;
        }
      }

      // Try any closest anchor.
      if (el.closest) {
        var a2 = el.closest('a[href*="id="]');
        if (a2) {
          var id2 = parseItemIdFromHref(a2.getAttribute('href'));
          if (id2) return id2;
        }
      }

      // Details page: parse from hash.
      var id3 = parseItemIdFromHref(window.location.hash);
      return id3;
    }

    function isLikelyRatingStar(el){
      // Avoid user-rating widgets.
      if (el.closest && (el.closest('.starRating') || el.closest('.ratingcontrol') || el.closest('.ratingControl') || el.closest('.ratingStars'))) {
        return false;
      }
      return true;
    }

    var itemCache = Object.create(null); // itemId -> { source: string|null, community: number|null }
    var inflight = Object.create(null);

    function toIconKey(source, communityRating0to10){
      if (!source) return null;
      var s = String(source).toLowerCase();
      if (s === 'metacriticuser') return 'metacriticus';
      if (s === 'metacriticms') return 'metacriticms';
      if (s === 'tomatoes') {
        var score = (typeof communityRating0to10 === 'number') ? (communityRating0to10 * 10) : null;
        if (score !== null && score > 0 && score < 60) return 'tomatoes_rotten';
        return 'tomatoes';
      }
      if (s === 'popcorn') {
        var score2 = (typeof communityRating0to10 === 'number') ? (communityRating0to10 * 10) : null;
        if (score2 !== null && score2 > 0 && score2 < 60) return 'audience_rotten';
        return 'audience';
      }
      return s;
    }

    function applyIconToEl(el, itemId, source, communityRating){
      var iconKey = toIconKey(source, communityRating);
      var url = iconKey ? ICONS[iconKey] : null;
      if (!url) return;

      ensureStyle();

      // If an icon image is already right after the star, just update it.
      var next = el && el.nextElementSibling;
      if (next && next.classList && next.classList.contains(IMG_CLASS)) {
        try {
          if (next.getAttribute('data-mdblist-icon') !== iconKey) next.setAttribute('data-mdblist-icon', iconKey);
          if (next.getAttribute('data-mdblist-itemid') !== itemId) next.setAttribute('data-mdblist-itemid', itemId);
          if (next.title !== iconKey) next.title = iconKey;
          if (next.alt !== iconKey) next.alt = iconKey;
          if (next.src !== url) next.src = url;
        } catch (e) {}
        // Ensure the star is visually hidden when icons are enabled.
        try { el.classList.add(STAR_SHRINK_CLASS); } catch (e) {}
        return;
      }

      // Insert after the star. Do NOT hide or replace the star (test mode).
      var img = document.createElement('img');
      img.className = IMG_CLASS;
      img.setAttribute('data-mdblist-icon', iconKey);
      img.setAttribute('data-mdblist-itemid', itemId);
      img.alt = iconKey;
      img.title = iconKey;
      img.loading = 'lazy';
      img.decoding = 'async';
      try { img.referrerPolicy = 'no-referrer'; } catch (e) {}

      img.onerror = function(){
        try { img.remove(); } catch (e) {}
      };

      img.src = url;

      try {
        el.insertAdjacentElement('afterend', img);
      } catch (e) {
        try { el.parentNode && el.parentNode.insertBefore(img, el.nextSibling); } catch (e2) {}
      }

      // Option enabled: visually hide/shrink the star so only the rating icon is visible.
      try { el.classList.add(STAR_SHRINK_CLASS); } catch (e) {}
    }

    // --- Details page: hide default ratings and render all cached ratings (web-only) ---
    var DETAILS_CONTAINER_CLASS = 'mdblist-allratings-custom';
    var DETAILS_HIDE_CLASS = 'mdblist-hide-default-rating';
    var _detailsLastItemId = null;
    var _detailsInflightId = null;

    function isDetailsPage(){
      var h = (window.location && window.location.hash) ? window.location.hash : '';
      h = String(h).toLowerCase();
      return h.indexOf('#/details') === 0 || h.indexOf('#!/details') === 0 || h.indexOf('details') >= 0;
    }

    function isElementVisible(el){
      if (!el) return false;
      try {
        var st = window.getComputedStyle(el);
        if (st && (st.display === 'none' || st.visibility === 'hidden' || st.opacity === '0')) return false;
        var r = el.getClientRects && el.getClientRects();
        if (r && r.length) return true;
        return el.offsetParent !== null;
      } catch (e) {
        return true;
      }
    }

    function getActiveDetailsBox(){
      var boxes = document.querySelectorAll('.itemMiscInfo.itemMiscInfo-primary');
      if (!boxes || !boxes.length) return null;

      for (var i=0;i<boxes.length;i++) {
        var b = boxes[i];
        if (!b) continue;
        if (!isElementVisible(b)) continue;
        if (b.querySelector('div.starRatingContainer.mediaInfoItem') || b.querySelector('div.mediaInfoItem.mediaInfoCriticRating')) return b;
      }

      // Fallback: any box that contains rating elements.
      for (var j=0;j<boxes.length;j++) {
        var b2 = boxes[j];
        if (!b2) continue;
        if (b2.querySelector('div.starRatingContainer.mediaInfoItem') || b2.querySelector('div.mediaInfoItem.mediaInfoCriticRating')) return b2;
      }

      return boxes[0];
    }

    function removeCustomFromBox(box){
      if (!box) return;
      try {
        var existing = box.querySelectorAll('.' + DETAILS_CONTAINER_CLASS);
        for (var i=0;i<existing.length;i++) {
          try { existing[i].remove(); } catch(e2) {}
        }
      } catch (e) {}
    }

    function unhideDefaultInBox(box){
      if (!box) return;
      try {
        var hidden = box.querySelectorAll('.' + DETAILS_HIDE_CLASS);
        for (var i=0;i<hidden.length;i++) {
          try { hidden[i].classList.remove(DETAILS_HIDE_CLASS); } catch(e2) {}
        }
      } catch (e) {}
    }

    function clearDetailsCustom(){
      // Remove from all pages (Jellyfin keeps old pages in DOM, hidden).
      try {
        var all = document.querySelectorAll('.' + DETAILS_CONTAINER_CLASS);
        for (var i=0;i<all.length;i++) {
          try { all[i].remove(); } catch(e2) {}
        }
      } catch (e) {}

      try {
        var hidden = document.querySelectorAll('.' + DETAILS_HIDE_CLASS);
        for (var j=0;j<hidden.length;j++) {
          try { hidden[j].classList.remove(DETAILS_HIDE_CLASS); } catch(e3) {}
        }
      } catch (e) {}

      _detailsLastItemId = null;
      _detailsInflightId = null;
    }

    function parseItemIdFromHash(){
      return parseItemIdFromHref(window.location && window.location.hash ? window.location.hash : '');
    }

    function hideDefaultRatingsBlocks(box){
      try {
        var starDiv = box ? box.querySelector('div.starRatingContainer.mediaInfoItem') : null;
        if (starDiv) starDiv.classList.add(DETAILS_HIDE_CLASS);
      } catch (e) {}
      try {
        var criticDiv = box ? box.querySelector('div.mediaInfoItem.mediaInfoCriticRating') : null;
        if (criticDiv) criticDiv.classList.add(DETAILS_HIDE_CLASS);
      } catch (e) {}
    }

    function formatCachedRating(r){
      if (!r) return null;
      var v = (r.value !== undefined && r.value !== null) ? Number(r.value) : null;
      var s = (r.score !== undefined && r.score !== null) ? Number(r.score) : null;

      if (v !== null && !isNaN(v)) {
        if (v <= 10) return { text: v.toFixed(1), rating0to10: v };
        if (v <= 100) return { text: String(Math.round(v)), rating0to10: v / 10 };
      }

      if (s !== null && !isNaN(s)) {
        if (s <= 10) return { text: s.toFixed(1), rating0to10: s };
        if (s <= 100) return { text: String(Math.round(s)), rating0to10: s / 10 };
      }

      return null;
    }

    
    function isDigitsOnly(s){
      try { return /^[0-9]+$/.test(String(s||'')); } catch (e) { return false; }
    }

    function normalizeLeadingSlash(p){
      if (p === undefined || p === null) return null;
      var s = String(p).trim();
      if (!s) return null;
      if (s.indexOf('http://') === 0 || s.indexOf('https://') === 0) return s;
      if (s[0] !== '/') s = '/' + s;
      return s;
    }

    function buildProviderHref(src, rating, ctx){
      try {
        src = String(src||'').toLowerCase();
        var ids = (ctx && (ctx.ids || ctx.Ids)) || {};
        var imdbId = ids.imdb || ids.Imdb || null;
        var tmdbId = ids.tmdb || ids.Tmdb || null;

        var rawUrl = rating ? (rating.url || rating.Url) : null;
        var url = normalizeLeadingSlash(rawUrl);

        // IMDb
        if (src === 'imdb') {
          if (imdbId) return 'https://www.imdb.com/title/' + String(imdbId).trim();
          return null;
        }

        // TMDb
        if (src === 'tmdb') {
          if (tmdbId) {
            var kind = (ctx && ctx.contentType === 'show') ? 'tv' : 'movie';
            return 'https://www.themoviedb.org/' + kind + '/' + String(tmdbId).trim();
          }
          return null;
        }

        // Trakt (use imdb id)
        if (src === 'trakt') {
          if (imdbId) {
            var kind2 = (ctx && ctx.contentType === 'show') ? 'shows' : 'movies';
            return 'https://trakt.tv/' + kind2 + '/' + String(imdbId).trim();
          }
          return null;
        }

        // RottenTomatoes (tomatoes + popcorn use same url field)
        if (src === 'tomatoes' || src === 'popcorn') {
          if (!url) return null;
          if (isDigitsOnly(url)) return null;
          // url is usually /m/... or /tv/...
          if (url.indexOf('http://') === 0 || url.indexOf('https://') === 0) return url;
          return 'https://www.rottentomatoes.com' + url;
        }

        // Metacritic
        if (src === 'metacritic') {
          if (!url) return null;
          if (isDigitsOnly(url)) return null;
          if (url.indexOf('http://') === 0 || url.indexOf('https://') === 0) return url;
          // MDBList gives "/slug" most of the time; we need /movie/slug or /tv/slug
          if (url.indexOf('/movie/') === 0 || url.indexOf('/tv/') === 0) {
            return 'https://www.metacritic.com' + url;
          }
          var prefix = (ctx && ctx.contentType === 'show') ? '/tv' : '/movie';
          return 'https://www.metacritic.com' + prefix + url;
        }

        // Letterboxd
        if (src === 'letterboxd') {
          if (!url) return null;
          if (isDigitsOnly(url)) return null;
          if (url.indexOf('http://') === 0 || url.indexOf('https://') === 0) return url;
          return 'https://letterboxd.com' + url;
        }

        // Roger Ebert
        if (src === 'rogerebert') {
          var s = rawUrl;
          if (s === undefined || s === null) return null;
          s = String(s).trim();
          if (!s) return null;
          if (s.indexOf('http://') === 0 || s.indexOf('https://') === 0) return s;
          // sample: "brothers-film-review-2024"
          s = s.replace(/^\/+/, '');
          if (!s) return null;
          return 'https://www.rogerebert.com/reviews/' + s;
        }

        return null;
      } catch (e) {
        return null;
      }
    }

function buildAllRatingsContainer(ratings, itemId, settings, ctx){
      var container = document.createElement('div');
      container.className = 'mediaInfoItem mdblist-allratings-container ' + DETAILS_CONTAINER_CLASS;
      try { container.setAttribute('data-mdblist-itemid', itemId); } catch (e) {}

      // Determine what to show and in what order.
      var list = ratings || [];
      try {
        if (settings && settings.mode === 'custom' && settings.order && settings.order.length) {
          var map = Object.create(null);
          for (var m=0;m<list.length;m++) {
            var rr = list[m];
            if (!rr) continue;
            var ssrc = (rr.source || rr.Source || '').toString().toLowerCase();
            if (ssrc && !map[ssrc]) map[ssrc] = rr;
          }
          var ordered = [];
          for (var o=0;o<settings.order.length;o++) {
            var key = settings.order[o];
            if (key && map[key]) ordered.push(map[key]);
          }
          list = ordered;
        }
      } catch (e) {
        // ignore
      }

      for (var i=0;i<list.length;i++) {
        var r = list[i];
        if (!r) continue;
        var src = (r.source || r.Source || '').toString().toLowerCase();
        if (!src) continue;

        var fmt = formatCachedRating(r);
        if (!fmt) continue;

        var iconUrl = getIconUrlForSource(src, fmt.rating0to10);
        if (!iconUrl) iconUrl = getFallbackIconUrl(src) || ICONS.tmdb;

        var item = document.createElement('span');
        item.className = 'mdblist-allratings-item';
        item.title = src;

        var img = document.createElement('img');
        img.className = 'mdblist-allratings-icon';
        img.alt = src;
        img.title = src;
        img.loading = 'lazy';
        img.decoding = 'async';
        try { img.referrerPolicy = 'no-referrer'; } catch (e) {}
        if (iconUrl) img.src = iconUrl;

        // Optionally make the ICON clickable (open provider page) when URL/IDs are available.
        if (settings && settings.clickable === true) {
          var href = buildProviderHref(src, r, ctx);
          if (href) {
            var a = document.createElement('a');
            a.className = 'mdblist-allratings-link';
            a.href = href;
            a.target = '_blank';
            a.rel = 'nofollow noopener noreferrer';
            a.appendChild(img);
            item.appendChild(a);
          } else {
            item.appendChild(img);
          }
        } else {
          item.appendChild(img);
        }

        var txt = document.createElement('span');
        txt.textContent = fmt.text;
        item.appendChild(txt);

        container.appendChild(item);
      }

      return container;
    }

    function fetchItemDetailsForTmdb(itemId){
      try {
        var userId = getUserId();
        if (!userId) return Promise.resolve(null);
        var url = window.ApiClient.getUrl('Users/' + userId + '/Items/' + itemId, { Fields: 'ProviderIds,ProductionYear,OriginalTitle' });
        return ajaxJson(url).then(function(item){
          if (!item) return null;
          var p = item.ProviderIds || item.providerIds || {};
          var tmdbId = p.Tmdb || p.tmdb || p.TMDb || p.TMDB || null;
          if (!tmdbId) return null;
          var type = (item.Type || item.type || '').toString();
          var contentType = null;
          if (type === 'Movie') contentType = 'movie';
          else if (type === 'Series') contentType = 'show';
          else return null;
          var title = (item.OriginalTitle || item.originalTitle || item.Name || item.name || '') + '';
          var year = (item.ProductionYear || item.productionYear || null);
          return { contentType: contentType, tmdbId: String(tmdbId), title: title, year: year ? Number(year) : null };
        });
      } catch (e) {
        return Promise.resolve(null);
      }
    }

    function fetchCachedRatingsByTmdb(contentType, tmdbId){
      try {
        var url = window.ApiClient.getUrl('Plugins/MdbListRatings/CachedByTmdb', { type: contentType, tmdbId: tmdbId });
        return ajaxJson(url);
      } catch (e) {
        return Promise.resolve(null);
      }
    }

    function fetchWebExtrasByTmdb(contentType, tmdbId, title, year, want){
      try {
        var url = window.ApiClient.getUrl('Plugins/MdbListRatings/WebExtrasByTmdb', {
          type: contentType,
          tmdbId: tmdbId,
          title: title || '',
          year: year || 0,
          tc: (want && want.tc) ? 1 : 0,
          rv: (want && want.rv) ? 1 : 0,
          mc: (want && want.mc) ? 1 : 0,
          al: (want && want.al) ? 1 : 0
        });
        return ajaxJson(url);
      } catch (e) {
        return Promise.resolve(null);
      }
    }


    function runDetailsAllRatings(settings){
      if (!isDetailsPage()) {
        // If the user navigated away from details, clean up any injected elements.
        if (_detailsLastItemId !== null) clearDetailsCustom();
        return;
      }

      var itemId = parseItemIdFromHash();
      if (!itemId) return;

      var box = getActiveDetailsBox();
      if (!box) return;

      // If the feature is disabled, ensure we restore the original UI.
      if (!settings || settings.enabled !== true) {
        try { removeCustomFromBox(box); } catch (e) {}
        try { unhideDefaultInBox(box); } catch (e) {}
        _detailsLastItemId = null;
        _detailsInflightId = null;
        return;
      }

      // If the item changed (still on details), reset the UI in the active box.
      if (_detailsLastItemId && _detailsLastItemId !== itemId) {
        removeCustomFromBox(box);
        unhideDefaultInBox(box);
      }

      // Prevent repeated fetch/render for the same item in the ACTIVE details box.
      var existing = null;
      try { existing = box.querySelector('.' + DETAILS_CONTAINER_CLASS + '[data-mdblist-itemid="' + itemId + '"]'); } catch (e) {}
      if (_detailsLastItemId === itemId && existing) return;
      if (_detailsInflightId === itemId) return;

      // Wait until Jellyfin has rendered the built-in rating blocks inside this details box.
      var anchor = box.querySelector('div.mediaInfoItem.mediaInfoCriticRating') || box.querySelector('div.starRatingContainer.mediaInfoItem');
      if (!anchor) return;

      _detailsInflightId = itemId;

      fetchItemDetailsForTmdb(itemId).then(function(info){
        if (!info) return null;
        return fetchCachedRatingsByTmdb(info.contentType, info.tmdbId).then(function(resp){
          return { info: info, resp: resp };
        });
      }).then(function(pack){
        if (!pack || !pack.resp) return;

        var resp = pack.resp;
        var hasCache = (resp.hasCache === true) || (resp.HasCache === true);
        var ratings = resp.ratings || resp.Ratings || null;
        if (!hasCache || !ratings || !ratings.length) return;

        // Decide which web-only extras are needed (to avoid unnecessary network calls).
        var ex = (settings && settings.extras) ? settings.extras : { tc:false, rv:false, mc:false, al:false };

        function hasSrc(list, key){
          try {
            key = String(key||'').toLowerCase();
            for (var i=0;i<(list||[]).length;i++){
              var s = (list[i] && (list[i].source || list[i].Source) || '').toString().toLowerCase();
              if (s === key) return true;
            }
          } catch (e) {}
          return false;
        }

        function inOrder(st, key){
          try {
            if (!st || !st.order || !st.order.length) return false;
            key = String(key||'').toLowerCase();
            return st.order.indexOf(key) >= 0;
          } catch (e) { return false; }
        }

        // For tomatoes/popcorn/metacritic we only fetch extras if that source will actually be shown.
        var showTomatoes = hasSrc(ratings, 'tomatoes') && (settings.mode !== 'custom' || inOrder(settings, 'tomatoes'));
        var showPopcorn  = hasSrc(ratings, 'popcorn')  && (settings.mode !== 'custom' || inOrder(settings, 'popcorn'));
        var showMeta     = hasSrc(ratings, 'metacritic') && (settings.mode !== 'custom' || inOrder(settings, 'metacritic'));

        var want = {
          tc: !!ex.tc && showTomatoes,
          rv: !!ex.rv && showPopcorn,
          mc: !!ex.mc && showMeta,
          // AniList: in "all" mode show always (if enabled); in "custom" mode only when listed.
          al: !!ex.al && (settings.mode !== 'custom' || inOrder(settings, 'anilist'))
        };

        var needExtras = want.tc || want.rv || want.mc || want.al;

        var extrasPromise = needExtras
          ? fetchWebExtrasByTmdb(pack.info.contentType, pack.info.tmdbId, pack.info.title, pack.info.year, want)
          : Promise.resolve(null);

        return extrasPromise.then(function(extras){

          // Add AniList as a virtual rating (web-only). Not saved anywhere.
          if (extras && want.al && extras.anilistScore !== undefined && extras.anilistScore !== null) {
            try {
              var v = Number(extras.anilistScore);
              if (!isNaN(v) && v > 0) {
                // Clone ratings array so we don't mutate cached objects.
                ratings = ratings.slice();
                ratings.push({ source: 'anilist', value: v, score: v });
              }
            } catch (e) {}
          }

          // Build and inject into the ACTIVE details box.
          ensureStyle();
          removeCustomFromBox(box);

          var ids = resp.ids || resp.Ids || null;
          var container = buildAllRatingsContainer(ratings, itemId, settings, { contentType: pack.info.contentType, ids: ids });
          if (!container || !container.children || container.children.length === 0) return;

          hideDefaultRatingsBlocks(box);

          // Insert right after the critic rating block if present, otherwise after the star rating block.
          var insertAfter = box.querySelector('div.mediaInfoItem.mediaInfoCriticRating') ||
                           box.querySelector('div.starRatingContainer.mediaInfoItem') ||
                           anchor;
          try {
            insertAfter.insertAdjacentElement('afterend', container);
          } catch (e) {
            try { insertAfter.parentNode && insertAfter.parentNode.insertBefore(container, insertAfter.nextSibling); } catch (e2) {}
          }

          function findIcon(key){
            try { return container.querySelector('.mdblist-allratings-item[title=\"' + key + '\"] img'); } catch (e) { return null; }
          }

          // Replace base icons with badge-specific icons when applicable.
          if (extras && want.tc && extras.rtCriticsCertified === true) {
            var imgTc = findIcon('tomatoes');
            if (imgTc) imgTc.src = ICONS.tomatoes_certified;
          }
          if (extras && want.rv && extras.rtAudienceVerified === true) {
            var imgRv = findIcon('popcorn');
            if (imgRv) imgRv.src = ICONS.rotten_ver;
          }
          if (extras && want.mc && extras.metacriticMustSee === true) {
            var imgMc = findIcon('metacritic');
            if (imgMc) imgMc.src = ICONS.metacriticms;
          }

          _detailsLastItemId = itemId;
        });
      }).catch(function(){
        // ignore
      }).finally(function(){
        if (_detailsInflightId === itemId) _detailsInflightId = null;
      });
    }

    function collectTargets(root){
      var scope = root || document;
      var nodes = scope.querySelectorAll ? scope.querySelectorAll('span.starIcon.star') : [];
      var targets = [];
      for (var i=0;i<nodes.length;i++) {
        var el = nodes[i];
        if (!el || !el.classList) continue;
        if (!isLikelyRatingStar(el)) continue;
        var itemId = findItemId(el);
        if (!itemId) continue;

        // Skip if we already inserted an icon right after this star.
        var next = el.nextElementSibling;
        if (next && next.classList && next.classList.contains(IMG_CLASS)) continue;

        targets.push({ el: el, itemId: itemId });
      }
      return targets;
    }

    function fetchBatch(itemIds){
      var userId = getUserId();
      if (!userId) return Promise.resolve();
      var ids = itemIds.filter(function(x){ return x && !itemCache[x] && !inflight[x]; });
      if (ids.length === 0) return Promise.resolve();

      // Mark as inflight.
      ids.forEach(function(id){ inflight[id] = true; });

      var url = window.ApiClient.getUrl('Users/' + userId + '/Items', { Ids: ids.join(','), Fields: 'ProviderIds' });
      return ajaxJson(url).then(function(res){
        var items = (res && (res.Items || res.items)) || [];
        for (var i=0;i<items.length;i++) {
          var it = items[i];
          if (!it || !it.Id) continue;
          var pids = it.ProviderIds || it.ProviderID || it.ProviderIdsMap || it.ProviderIds; // tolerate variants
          var src = null;
          if (pids && typeof pids === 'object') {
            src = pids[PROVIDER_KEY] || pids[PROVIDER_KEY.toLowerCase()] || null;
          }
          itemCache[it.Id] = {
            source: src,
            community: (typeof it.CommunityRating === 'number') ? it.CommunityRating : null
          };
        }
      }).catch(function(){
        // ignore
      }).finally(function(){
        ids.forEach(function(id){ delete inflight[id]; });
      });
    }

    var scanTimer = null;
    function scheduleScan(){
      if (scanTimer) clearTimeout(scanTimer);
      scanTimer = setTimeout(runScan, 200);
    }

    function runScan(){
      scanTimer = null;
      if (!hasApiClient()) {
        scheduleScan();
        return;
      }

      // Details page feature (independent from the star-icon feature).
      ensureAllRatingsSettings().then(function(st){
        runDetailsAllRatings(st);
      });

      ensureIconsEnabled().then(function(en){
        if (!en) return;
        runScanEnabled();
      });
    }

    function runScanEnabled(){

      var targets = collectTargets(document);
      if (targets.length === 0) return;

      // Unique item ids.
      var uniq = Object.create(null);
      var ids = [];
      for (var i=0;i<targets.length;i++) {
        var id = targets[i].itemId;
        if (!uniq[id]) { uniq[id] = true; ids.push(id); }
      }

      // Fetch unknown items in chunks (to avoid very long URLs).
      var chunks = [];
      var chunkSize = 50;
      for (var c=0; c<ids.length; c+=chunkSize) {
        chunks.push(ids.slice(c, c+chunkSize));
      }

      var p = Promise.resolve();
      chunks.forEach(function(ch){
        p = p.then(function(){ return fetchBatch(ch); });
      });

      p.then(function(){
        // Apply icons.
        for (var i=0;i<targets.length;i++) {
          var t = targets[i];
          var info = itemCache[t.itemId];
          if (!info || !info.source) continue;
          applyIconToEl(t.el, t.itemId, info.source, info.community);
        }
      });
    }

    // Run on navigation and DOM changes.
    document.addEventListener('viewshow', scheduleScan);
    document.addEventListener('pageshow', scheduleScan);
    window.addEventListener('hashchange', scheduleScan);

    var mo = new MutationObserver(function(){ scheduleScan(); });
    mo.observe(document.documentElement, { childList: true, subtree: true });

    // Initial scan.
    scheduleScan();
  } catch (e) {
    // ignore
  }
	})();
""";

        js = js.Replace("__PLUGIN_ID__", pluginId.ToString("D"));

        return MarkerStart + Environment.NewLine
            + "<script>" + Environment.NewLine
            + js + Environment.NewLine
            + "</script>" + Environment.NewLine
            + MarkerEnd;
    }
}
