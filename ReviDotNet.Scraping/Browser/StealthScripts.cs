// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// The browser-stealth evasion catalog, ported from <c>puppeteer-extra-plugin-stealth</c>. These are
/// injected on every new document (before navigation) so the page never observes the un-patched
/// values. They cover the JS fingerprint layer (<c>navigator.webdriver</c>, <c>chrome.runtime</c>,
/// plugins/mimeTypes, WebGL vendor/renderer, permissions consistency, iframe contentWindow, window
/// dimensions, hardware hints) and — crucially — mask <c>Function.prototype.toString</c> so the patches
/// themselves don't betray the override.
///
/// Note: these address the <em>JS</em> layer only. The authentic TLS/HTTP-2 fingerprint comes from
/// driving a real browser; the CDP <c>Runtime.enable</c> automation-protocol leak is addressed
/// separately by connecting to a non-leaky driver over CDP (see <see cref="BrowserWebFetcher"/> and the
/// scraping report).
/// </summary>
public static class StealthScripts
{
    /// <summary>The combined evasion script to inject via <c>EvaluateExpressionOnNewDocumentAsync</c>.</summary>
    public static string NavigatorEvasions => Script;

    private const string Script = """
        (() => {
          // --- toString masking: make patched natives report as [native code] ---------------------
          const patchToString = (fn, name) => {
            try {
              const native = `function ${name}() { [native code] }`;
              const handler = {
                apply: function (target, ctx, args) {
                  if (ctx === Function.prototype.toString) return 'function toString() { [native code] }';
                  if (typeof ctx === 'function' && ctx[Symbol.for('revi-native')]) return native;
                  return target.apply(ctx, args);
                }
              };
              Function.prototype.toString = new Proxy(Function.prototype.toString, handler);
            } catch (e) {}
          };
          const markNative = (fn, name) => { try { fn[Symbol.for('revi-native')] = name; } catch (e) {} return fn; };

          // --- navigator.webdriver: delete from the prototype ------------------------------------
          try {
            Object.defineProperty(Navigator.prototype, 'webdriver', { get: () => false, configurable: true });
            delete Object.getPrototypeOf(navigator).webdriver;
          } catch (e) {}

          // --- window.chrome runtime mock --------------------------------------------------------
          try {
            if (!window.chrome) window.chrome = {};
            window.chrome.runtime = window.chrome.runtime || {
              connect: () => {}, sendMessage: () => {}, onMessage: { addListener: () => {} }, id: undefined
            };
            window.chrome.loadTimes = window.chrome.loadTimes || function () { return {}; };
            window.chrome.csi = window.chrome.csi || function () { return {}; };
          } catch (e) {}

          // --- navigator.plugins / mimeTypes (non-empty, realistic) ------------------------------
          try {
            const make = (arr) => { const o = Object.create(arr.constructor ? arr.constructor.prototype : Object.prototype); arr.forEach((v, i) => o[i] = v); o.length = arr.length; return o; };
            const plugins = [
              { name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer', description: 'Portable Document Format' },
              { name: 'Chrome PDF Viewer', filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai', description: '' },
              { name: 'Native Client', filename: 'internal-nacl-plugin', description: '' }
            ];
            Object.defineProperty(navigator, 'plugins', { get: () => make(plugins), configurable: true });
            Object.defineProperty(navigator, 'mimeTypes', { get: () => make([{ type: 'application/pdf' }, { type: 'application/x-google-chrome-pdf' }]), configurable: true });
          } catch (e) {}

          // --- navigator.languages ---------------------------------------------------------------
          try {
            Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'], configurable: true });
          } catch (e) {}

          // --- hardware hints --------------------------------------------------------------------
          try { Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => 8, configurable: true }); } catch (e) {}
          try { Object.defineProperty(navigator, 'deviceMemory', { get: () => 8, configurable: true }); } catch (e) {}

          // --- permissions consistency (Notification) --------------------------------------------
          try {
            const orig = window.navigator.permissions && window.navigator.permissions.query;
            if (orig) {
              const patched = (parameters) => parameters && parameters.name === 'notifications'
                ? Promise.resolve({ state: Notification.permission })
                : orig(parameters);
              window.navigator.permissions.query = markNative(patched, 'query');
            }
          } catch (e) {}

          // --- WebGL vendor/renderer spoof (UNMASKED_VENDOR_WEBGL / UNMASKED_RENDERER_WEBGL) ------
          try {
            const spoof = (proto) => {
              if (!proto) return;
              const getParameter = proto.getParameter;
              proto.getParameter = markNative(function (p) {
                if (p === 37445) return 'Intel Inc.';
                if (p === 37446) return 'Intel Iris OpenGL Engine';
                return getParameter.apply(this, arguments);
              }, 'getParameter');
            };
            spoof(window.WebGLRenderingContext && WebGLRenderingContext.prototype);
            spoof(window.WebGL2RenderingContext && WebGL2RenderingContext.prototype);
          } catch (e) {}

          // --- iframe.contentWindow (avoid null on srcdoc) ---------------------------------------
          try {
            const desc = Object.getOwnPropertyDescriptor(HTMLIFrameElement.prototype, 'contentWindow');
            if (desc && desc.get) {
              Object.defineProperty(HTMLIFrameElement.prototype, 'contentWindow', {
                get: markNative(function () { const w = desc.get.call(this); return w || window; }, 'get contentWindow')
              });
            }
          } catch (e) {}

          // --- window outer dimensions -----------------------------------------------------------
          try {
            if (window.outerWidth === 0) window.outerWidth = window.innerWidth;
            if (window.outerHeight === 0) window.outerHeight = window.innerHeight + 85;
          } catch (e) {}

          patchToString(Function.prototype.toString, 'toString');
        })();
        """;
}
