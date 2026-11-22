document.documentElement.classList.add('js');
document.addEventListener('DOMContentLoaded', function () {
  var N = 17, g = document.getElementById('qr'), html = '', r, c, i;
  if (g) {
    var finder = function (r, c) {
      var boxes = [[0, 0], [0, N - 7], [N - 7, 0]];
      for (var i = 0; i < 3; i++) {
        var rr = r - boxes[i][0], cc = c - boxes[i][1];
        if (rr >= 0 && rr < 7 && cc >= 0 && cc < 7) {
          return (rr === 0 || rr === 6 || cc === 0 || cc === 6 || (rr >= 2 && rr <= 4 && cc >= 2 && cc <= 4)) ? 1 : 0;
        }
      }
      return null;
    };
    for (r = 0; r < N; r++) for (c = 0; c < N; c++) {
      var v = finder(r, c);
      if (v === null) v = ((r * 7 + c * 13 + r * c) % 5 < 2) ? 1 : 0;
      html += v ? '<b class="k"></b>' : '<b></b>';
    }
    g.innerHTML = html;
  }
  var els = document.querySelectorAll('.reveal');
  if (!('IntersectionObserver' in window) || matchMedia('(prefers-reduced-motion: reduce)').matches) {
    for (i = 0; i < els.length; i++) els[i].classList.add('in');
    return;
  }
  var io = new IntersectionObserver(function (es) {
    es.forEach(function (e) { if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); } });
  }, { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });
  els.forEach(function (el) { io.observe(el); });
});

// The download buttons link to the latest release page (works without JavaScript). With JavaScript they go straight to the installer:
// the file name carries the version, so ask GitHub which file the latest release has. Any failure leaves the page link as it is.
document.addEventListener('DOMContentLoaded', function () {
  var PREFIX = 'https://github.com/bustrama/easy-mouse/releases/download/';
  var links = document.querySelectorAll('a[href$="/releases/latest"]');
  if (!links.length || !window.fetch) return;
  fetch('https://api.github.com/repos/bustrama/easy-mouse/releases/latest', { headers: { Accept: 'application/vnd.github+json' } })
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (rel) {
      if (!rel || !rel.assets) return;
      for (var i = 0; i < rel.assets.length; i++) {
        var a = rel.assets[i];
        if (/^EasyMouse-Setup-[0-9A-Za-z.-]+\.exe$/.test(a.name) && typeof a.browser_download_url === 'string' && a.browser_download_url.indexOf(PREFIX) === 0) {
          for (var j = 0; j < links.length; j++) links[j].href = a.browser_download_url;
          return;
        }
      }
    })
    .catch(function () { });
});
