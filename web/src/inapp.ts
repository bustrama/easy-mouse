// In-app browsers (QR scanners, social apps, the Google app) keep their own storage and are closed or suspended
// without warning, so a phone paired inside one loses its credentials or its connection. The page asks to be
// opened in the real browser instead. Detection is by user agent and is best effort: an iOS SFSafariViewController
// looks exactly like Safari and cannot be recognised.

const KNOWN_APPS = /FBAN|FBAV|FB_IAB|Instagram|Line\/|MicroMessenger|Snapchat|TikTok|musical_ly|BytedanceWebview|Twitter|LinkedInApp|Pinterest|KAKAOTALK|GSA\//;

export function isInAppBrowser(ua: string): boolean {
  if (KNOWN_APPS.test(ua)) return true;
  // iOS WKWebView: a mobile WebKit user agent without the "Safari/" token that the real Safari and Chrome send.
  if (/iPhone|iPad|iPod/.test(ua) && /AppleWebKit/.test(ua) && !/Safari\//.test(ua)) return true;
  // Android WebView marks itself with "; wv)".
  return /Android/.test(ua) && /; wv\)/.test(ua);
}
