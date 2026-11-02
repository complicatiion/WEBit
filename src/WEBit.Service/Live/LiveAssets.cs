namespace WEBit.Service.Live;

/// <summary>Browser side of live reload, served under /__webit/ for sites with live reload.</summary>
internal static class LiveAssets
{
    /// <summary>Wrapper page: shows the site in a full-size frame and reloads it on changes.</summary>
    public const string LiveHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>WEBit Live</title>
          <style>
            html, body { margin: 0; height: 100%; background: #1B1D2E; }
            iframe { position: fixed; inset: 0; width: 100%; height: 100%; border: 0; background: #fff; }
            #badge { position: fixed; right: 14px; bottom: 14px; z-index: 10; display: flex; align-items: center; gap: 8px;
                     padding: 6px 12px; border-radius: 16px; background: rgba(32, 34, 54, .92); color: #F2F2ED;
                     font: 12px "Segoe UI", system-ui, sans-serif; box-shadow: 0 4px 16px rgba(0, 0, 0, .35); cursor: pointer;
                     user-select: none; transition: opacity .2s; }
            #badge i { width: 8px; height: 8px; border-radius: 4px; background: #7CD5C7; }
            #badge.flash i { background: #118AB2; }
            #badge.off i { background: #E5606B; }
            #badge.min span { display: none; }
            #badge b { padding: 0 2px 0 6px; border-left: 1px solid rgba(242, 242, 237, .25); font-weight: 600; }
            #badge.paused i { background: #F2B84B; }
          </style>
        </head>
        <body>
          <iframe id="site" title="Site"></iframe>
          <div id="badge" title="WEBit live reload – click to minimize"><i></i><span id="label">Live reload</span><b id="pause" title="Pause / resume">II</b></div>
          <script>
            (function () {
              var frame = document.getElementById('site');
              var badge = document.getElementById('badge');
              var label = document.getElementById('label');
              var pauseButton = document.getElementById('pause');
              var start = decodeURIComponent(location.hash.slice(1)) || '/';
              if (start.charAt(0) !== '/' || start.indexOf('//') === 0) start = '/';
              frame.src = start;
              var paused = false, interval = 0, delay = 0, nextAuto = 0, pending = null;
              badge.onclick = function () { badge.classList.toggle('min'); };

              // Pause: e.g. while filling in a form; no reloads until resumed.
              pauseButton.onclick = function (e) {
                e.stopPropagation();
                paused = !paused;
                if (paused) clearTimeout(pending);
                badge.classList.toggle('paused', paused);
                pauseButton.textContent = paused ? '\u25B6' : 'II';
                if (!paused) nextAuto = Date.now() + interval * 1000;
                updateLabel();
              };

              function updateLabel() {
                label.textContent = paused ? 'Paused' : interval > 0 ? 'Auto reload ' + interval + ' s' : 'Live reload';
              }

              // Keep the current page in the address bar (#/path) so a manual reload stays there.
              frame.addEventListener('load', function () {
                try {
                  var l = frame.contentWindow.location;
                  history.replaceState(null, '', '#' + l.pathname + l.search + l.hash);
                  document.title = frame.contentDocument.title || 'WEBit Live';
                } catch (e) { }
              });

              function refreshCss(doc, version) {
                var links = doc.querySelectorAll('link[rel~="stylesheet"]');
                for (var i = 0; i < links.length; i++) {
                  var href = links[i].getAttribute('href');
                  if (!href) continue;
                  links[i].setAttribute('href', href.replace(/([?&])__webit=\d+&?/, '$1').replace(/[?&]$/, '') +
                    (href.indexOf('?') < 0 ? '?' : '&') + '__webit=' + version);
                }
              }

              function reload(kind, version) {
                badge.classList.add('flash');
                setTimeout(function () { badge.classList.remove('flash'); }, 600);
                nextAuto = Date.now() + interval * 1000;
                try {
                  if (kind === 'css') refreshCss(frame.contentDocument, version);
                  else frame.contentWindow.location.reload();
                } catch (e) { frame.src = frame.src; }
              }

              var last = null;
              function poll() {
                fetch('/__webit/state.json?t=' + Date.now(), { cache: 'no-store' })
                  .then(function (r) { return r.ok ? r.json() : null; })
                  .then(function (s) {
                    badge.classList.remove('off');
                    if (!s) return;
                    if ((s.interval || 0) !== interval) { interval = s.interval || 0; nextAuto = Date.now() + interval * 1000; }
                    delay = s.delay || 0;
                    updateLabel();
                    if (last !== null && s.version !== last && !paused) {
                      // Wait after a change (builds); a newer change restarts the wait.
                      var kind = s.kind, version = s.version;
                      clearTimeout(pending);
                      pending = setTimeout(function () { reload(kind, version); }, delay);
                    }
                    last = s.version;
                    if (interval > 0 && !paused && Date.now() >= nextAuto) reload('full', s.version);
                  })
                  .catch(function () { badge.classList.add('off'); })
                  .then(function () { setTimeout(poll, 700); });
              }
              poll();
            })();
          </script>
        </body>
        </html>
        """;

    /// <summary>Snippet for direct use: &lt;script src="/__webit/livereload.js"&gt;&lt;/script&gt;.</summary>
    public const string LiveReloadJs = """
        /* WEBit live reload – reloads the page when files of the site change (and every N seconds if set for the site). */
        (function () {
          if (window.top !== window.self) return; // inside the WEBit Live frame
          var last = null, loaded = Date.now();
          function poll() {
            fetch('/__webit/state.json?t=' + Date.now(), { cache: 'no-store' })
              .then(function (r) { return r.ok ? r.json() : null; })
              .then(function (s) {
                if (!s) return;
                if (last !== null && s.version !== last) setTimeout(function () { location.reload(); }, s.delay || 0);
                else if (s.interval > 0 && Date.now() - loaded >= s.interval * 1000) location.reload();
                last = s.version;
              })
              .catch(function () { })
              .then(function () { setTimeout(poll, 700); });
          }
          poll();
        })();
        """;
}
