/**
 * Integration tests for overlay.js - runs the REAL code with mocked browser APIs.
 * Uses jsdom's runScripts:'dangerously' to execute the overlay IIFE in a sandboxed DOM.
 *
 * Run: node tests/overlay/test-real-overlay.js
 */

const { JSDOM } = require('jsdom');
const fs = require('fs');
const path = require('path');

const overlaySource = fs.readFileSync(
    path.join(__dirname, '../../src/Jellyfin.Plugin.CsfdRatingOverlay/Web/overlay.js'),
    'utf8'
);

let passed = 0;
let failed = 0;
let currentTest = '';

function assert(condition, msg) {
    if (condition) {
        passed++;
        console.log(`  PASS: ${msg}`);
    } else {
        failed++;
        console.log(`  FAIL: ${msg} [in test: ${currentTest}]`);
    }
}

const TEST_ID_RAW = '6e7b826c78742b420b884c5c248d1979';
const TEST_ID_DASHED = '6e7b826c-7874-2b42-0b88-4c5c248d1979';
const TEST_RATING = { ItemId: TEST_ID_DASHED, Status: 'Resolved', Percent: 74, Stars: 7.4, DisplayText: '7.4', CsfdId: '1643979' };

/**
 * Create a jsdom environment and run overlay.js in it.
 */
async function createEnv(opts = {}) {
    const url = opts.url || 'http://localhost/web/index.html#!/home';
    const dom = new JSDOM(`<!DOCTYPE html><html><head></head><body>${opts.bodyHtml || ''}</body></html>`, {
        url,
        pretendToBeVisual: true,
        runScripts: 'dangerously',
        resources: 'usable'
    });
    const { window } = dom;
    const { document } = window;

    let configResolve = null;
    const configPromise = opts.delayConfig ? new Promise(r => { configResolve = r; }) : null;

    // Mock fetch
    const fetchCalls = [];
    window.fetch = async function(fetchUrl, fetchOpts) {
        fetchCalls.push({ url: fetchUrl, opts: fetchOpts });

        if (fetchUrl.includes('client-config')) {
            if (opts.delayConfig && configPromise) {
                await configPromise;
            }
            if (opts.configResponse === null || opts.configResponse === undefined) {
                return { ok: false, status: 500 };
            }
            return {
                ok: true,
                json: async () => ({
                    clientCacheVersion: 1,
                    overlayDetailEnabled: typeof opts.configResponse === 'object' ? opts.configResponse.overlayDetailEnabled : opts.configResponse,
                    overlayPosterEnabled: typeof opts.configResponse === 'object' ? opts.configResponse.overlayPosterEnabled : true,
                    detailIconStyle: typeof opts.configResponse === 'object' ? opts.configResponse.detailIconStyle || 'None' : 'None'
                })
            };
        }

        if (fetchUrl.includes('/items/batch')) {
            // Jellyfin 10.11 and 12 accept this scheme with legacy auth disabled.
            // Reject legacy token headers so a regression cannot be hidden by the mock.
            if (fetchOpts.headers.Authorization !== 'MediaBrowser Token="test-token"') {
                return { ok: false, status: 401 };
            }
            const body = JSON.parse(fetchOpts.body);
            const result = {};
            for (const id of body.itemIds) {
                if (id === TEST_ID_DASHED) {
                    result[id] = TEST_RATING;
                }
            }
            return { ok: true, json: async () => result };
        }

        return { ok: false, status: 404 };
    };

    // Mock ApiClient
    window.ApiClient = opts.apiClient || {
        _serverAddress: 'http://localhost',
        _accessToken: 'test-token',
        accessToken: () => 'test-token'
    };

    // Mock IntersectionObserver
    window.IntersectionObserver = class {
        constructor(callback) { this._callback = callback; }
        observe(el) {
            this._callback([{ target: el, isIntersecting: true }]);
        }
        unobserve() {}
        disconnect() {}
    };

    // Pre-set localStorage
    if (opts.localStorageDetailEnabled !== undefined && opts.localStorageDetailEnabled !== null) {
        window.localStorage.setItem('csfdOverlayDetailEnabled', opts.localStorageDetailEnabled);
    }

    // Pre-set session cache
    if (opts.sessionCache) {
        window.sessionStorage.setItem('csfdOverlayCache_v2', JSON.stringify(opts.sessionCache));
    }

    // Execute overlay.js via a script element (safe within jsdom sandbox)
    const script = document.createElement('script');
    script.textContent = overlaySource;
    document.head.appendChild(script);

    async function flush(ms = 100) {
        await new Promise(r => setTimeout(r, ms));
    }

    function resolveConfig() {
        if (configResolve) configResolve();
    }

    function getDetailCount() {
        return document.querySelectorAll('.csfd-detail-rating').length;
    }

    function getDetailText() {
        const el = document.querySelector('.csfd-detail-rating');
        return el ? el.textContent : null;
    }

    function getDetailIcon() {
        return document.querySelector('.csfd-detail-rating img');
    }

    function getBadgeCount() {
        return document.querySelectorAll('.csfd-rating-badge').length;
    }

    function addDetailSection() {
        const el = document.createElement('div');
        el.className = 'itemMiscInfo itemMiscInfo-primary';
        document.body.appendChild(el);
        return el;
    }

    function addMovieCard(id) {
        const wrapper = document.createElement('div');
        wrapper.setAttribute('data-type', 'Movie');
        wrapper.setAttribute('data-id', id);
        const card = document.createElement('div');
        card.className = 'card';
        const scalable = document.createElement('div');
        scalable.className = 'cardScalable';
        const imgContainer = document.createElement('div');
        imgContainer.className = 'cardImageContainer';
        scalable.appendChild(imgContainer);
        card.appendChild(scalable);
        wrapper.appendChild(card);
        document.body.appendChild(wrapper);
        return wrapper;
    }

    function navigateToDetail(id) {
        const rawId = id.replace(/-/g, '');
        window.location.hash = `#!/details?id=${rawId}&serverId=abc123`;
        const event = new window.Event('hashchange');
        window.dispatchEvent(event);
    }

    return {
        dom, window, document, fetchCalls,
        flush, resolveConfig,
        getDetailCount, getDetailText, getDetailIcon, getBadgeCount,
        addDetailSection, addMovieCard, navigateToDetail
    };
}

async function runTests() {

    console.log('\n=== Real Overlay.js Integration Tests ===\n');

    // ----------------------------------------------------------
    currentTest = '1. Script initializes and exposes clearCsfdCache';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({ configResponse: true });
        await env.flush();
        assert(typeof env.window.clearCsfdCache === 'function', 'clearCsfdCache is exposed');
    }

    for (const tokenSource of ['accessToken', '_accessToken']) {
        currentTest = `Authenticated requests using ${tokenSource}, legacy auth disabled`;
        console.log(`\n${currentTest}`);
        const apiClient = { _serverAddress: 'http://localhost/jellyfin' };
        apiClient[tokenSource] = tokenSource === 'accessToken' ? () => 'test-token' : 'test-token';
        const env = await createEnv({
            apiClient,
            configResponse: true,
            url: 'http://localhost/jellyfin/web/index.html#/details?id=' + TEST_ID_RAW,
            bodyHtml: '<div class="mainDetailButtons"><button class="btnPlaystate" data-type="Movie"></button></div>'
                + '<div class="itemMiscInfo itemMiscInfo-primary"></div>'
                + '<div class="card" data-type="Movie" data-id="' + TEST_ID_RAW + '">'
                + '<div class="cardScalable"><a class="cardImageContainer"></a></div></div>'
        });
        await env.flush(1000);
        const configRequest = env.fetchCalls.find(call => call.url.includes('client-config'));
        const batchRequest = env.fetchCalls.find(call => call.url.includes('/items/batch'));
        for (const [name, request] of [['config', configRequest], ['batch', batchRequest]]) {
            assert(request?.opts.headers.Authorization === 'MediaBrowser Token="test-token"', `${name} sends modern authorization`);
            assert(request?.url.startsWith('http://localhost/jellyfin/Plugins/'), `${name} preserves the server base URL`);
            assert(request && !Object.keys(request.opts.headers).some(key => /^X-(Emby|MediaBrowser)-/i.test(key)), `${name} does not rely on legacy headers`);
        }
        assert(env.getDetailText()?.includes('7.4'), 'Uncached detail rating loads with legacy auth disabled');
        assert(env.document.querySelector('.csfd-rating-badge')?.textContent.includes('7.4'), 'Uncached poster rating loads with legacy auth disabled');
    }

    currentTest = 'Anonymous client config before login';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: false,
            apiClient: { _serverAddress: 'http://localhost', accessToken: () => null }
        });
        await env.flush();
        const request = env.fetchCalls.find(call => call.url.includes('client-config'));
        assert(request && !('Authorization' in request.opts.headers), 'Missing token does not send an invalid authorization header');
        assert(env.window.localStorage.getItem('csfdOverlayDetailEnabled') === 'false', 'Anonymous configuration still loads');
    }

    // ----------------------------------------------------------
    currentTest = '2. Config=false, no localStorage: no detail on detail page';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: false,
            url: 'http://localhost/web/index.html#!/details?id=' + TEST_ID_RAW + '&serverId=abc',
            bodyHtml: '<div class="itemMiscInfo itemMiscInfo-primary"></div>',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        assert(env.getDetailCount() === 0, 'No detail element injected');
    }

    // ----------------------------------------------------------
    currentTest = '3. Config=true, no localStorage: detail injected on detail page';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: { overlayDetailEnabled: true, overlayPosterEnabled: true, detailIconStyle: 'None' },
            url: 'http://localhost/web/index.html#!/details?id=' + TEST_ID_RAW + '&serverId=abc',
            bodyHtml: '<div class="itemMiscInfo itemMiscInfo-primary"></div>',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        assert(env.getDetailCount() === 1, 'Detail element injected');
        assert(env.getDetailText() !== null && env.getDetailText().includes('7.4'), 'Shows correct rating');
    }

    // ----------------------------------------------------------
    currentTest = '4. Detail-only mode disables poster badges but keeps detail rating';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: { overlayDetailEnabled: true, overlayPosterEnabled: false, detailIconStyle: 'LogoSocial' },
            url: 'http://localhost/web/index.html#!/details?id=' + TEST_ID_RAW + '&serverId=abc',
            bodyHtml: '<div class="itemMiscInfo itemMiscInfo-primary"></div>',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        env.addMovieCard(TEST_ID_DASHED);
        await env.flush();
        assert(env.getDetailCount() === 1, 'Detail row still renders in detail-only mode');
        assert(env.getBadgeCount() === 0, 'Poster badge is not rendered when poster overlays are disabled');
        assert(env.getDetailIcon() && env.getDetailIcon().getAttribute('src').includes('logo-social.png'), 'Configured detail icon is rendered from local asset');
    }

    // ----------------------------------------------------------
    currentTest = '4. Config=false, localStorage=true: detail removed after config';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: false,
            localStorageDetailEnabled: 'true',
            url: 'http://localhost/web/index.html#!/details?id=' + TEST_ID_RAW + '&serverId=abc',
            bodyHtml: '<div class="itemMiscInfo itemMiscInfo-primary"></div>',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        assert(env.getDetailCount() === 0, 'Detail removed after config says false');
        assert(env.window.localStorage.getItem('csfdOverlayDetailEnabled') === 'false', 'localStorage updated');
    }

    // ----------------------------------------------------------
    currentTest = '5. Config=true, localStorage=false: detail injected after config overrides';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: true,
            localStorageDetailEnabled: 'false',
            url: 'http://localhost/web/index.html#!/details?id=' + TEST_ID_RAW + '&serverId=abc',
            bodyHtml: '<div class="itemMiscInfo itemMiscInfo-primary"></div>',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        assert(env.getDetailCount() === 1, 'Detail injected after config overrides localStorage');
        assert(env.window.localStorage.getItem('csfdOverlayDetailEnabled') === 'true', 'localStorage updated');
    }

    // ----------------------------------------------------------
    currentTest = '6. SPA nav: homepage -> detail page, config=true';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: true,
            url: 'http://localhost/web/index.html#!/home',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        assert(env.getDetailCount() === 0, 'No detail on homepage');

        env.addDetailSection();
        env.navigateToDetail(TEST_ID_DASHED);
        await env.flush(600);
        assert(env.getDetailCount() === 1, 'Detail injected after SPA nav');
    }

    // ----------------------------------------------------------
    currentTest = '7. SPA nav: homepage -> detail page, config=false';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: false,
            url: 'http://localhost/web/index.html#!/home',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();

        env.addDetailSection();
        env.navigateToDetail(TEST_ID_DASHED);
        await env.flush(600);
        assert(env.getDetailCount() === 0, 'No detail after SPA nav when disabled');
    }

    // ----------------------------------------------------------
    currentTest = '8. Delayed config=true: detail page before config arrives';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: true,
            delayConfig: true,
            url: 'http://localhost/web/index.html#!/details?id=' + TEST_ID_RAW + '&serverId=abc',
            bodyHtml: '<div class="itemMiscInfo itemMiscInfo-primary"></div>',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        assert(env.getDetailCount() === 0, 'No detail before config arrives');

        env.resolveConfig();
        await env.flush();
        assert(env.getDetailCount() === 1, 'Detail injected after delayed config');
    }

    // ----------------------------------------------------------
    currentTest = '9. Delayed config=false: no detail even after config';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: false,
            delayConfig: true,
            url: 'http://localhost/web/index.html#!/details?id=' + TEST_ID_RAW + '&serverId=abc',
            bodyHtml: '<div class="itemMiscInfo itemMiscInfo-primary"></div>',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        env.resolveConfig();
        await env.flush();
        assert(env.getDetailCount() === 0, 'No detail when delayed config says false');
    }

    // ----------------------------------------------------------
    currentTest = '10. Badges work regardless of detail toggle';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: false,
            url: 'http://localhost/web/index.html#!/home',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        env.addMovieCard(TEST_ID_DASHED);
        await env.flush(500);
        assert(env.getBadgeCount() >= 1, 'Badge injected even with detail disabled');
    }

    // ----------------------------------------------------------
    currentTest = '11. Config fetch fails: localStorage=true shows detail';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: null,
            localStorageDetailEnabled: 'true',
            url: 'http://localhost/web/index.html#!/details?id=' + TEST_ID_RAW + '&serverId=abc',
            bodyHtml: '<div class="itemMiscInfo itemMiscInfo-primary"></div>',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        assert(env.getDetailCount() === 1, 'Detail shows from localStorage when fetch fails');
    }

    // ----------------------------------------------------------
    currentTest = '12. Config fetch fails: localStorage=false hides detail';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: null,
            localStorageDetailEnabled: 'false',
            url: 'http://localhost/web/index.html#!/details?id=' + TEST_ID_RAW + '&serverId=abc',
            bodyHtml: '<div class="itemMiscInfo itemMiscInfo-primary"></div>',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        assert(env.getDetailCount() === 0, 'No detail when fetch fails and localStorage=false');
    }

    // ----------------------------------------------------------
    currentTest = '13. BoxSet detail page never renders CSFD detail rating';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: true,
            url: 'http://localhost/web/index.html#!/details?id=' + TEST_ID_RAW + '&serverId=abc',
            bodyHtml: '<div class="itemDetailPage"><div class="mainDetailButtons"><button class="btnPlaystate" data-type="BoxSet"></button></div><div class="itemMiscInfo itemMiscInfo-primary"></div></div>',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        assert(env.getDetailCount() === 0, 'Collection detail page stays clean');
    }

    // ----------------------------------------------------------
    currentTest = '14. Movie detail page still renders even with child item sections';
    console.log(`\n${currentTest}`);
    {
        const env = await createEnv({
            configResponse: true,
            url: 'http://localhost/web/index.html#!/details?id=' + TEST_ID_RAW + '&serverId=abc',
            bodyHtml: '<div class="itemDetailPage" data-type="Movie"><div class="childrenItemsContainer"><div class="items"></div></div><div class="itemMiscInfo itemMiscInfo-primary"></div></div>',
            sessionCache: { [TEST_ID_DASHED]: TEST_RATING }
        });
        await env.flush();
        assert(env.getDetailCount() === 1, 'Movie detail page still renders rating');
        assert(env.getDetailText() !== null && env.getDetailText().includes('7.4'), 'Movie detail page shows correct rating');
    }

    // ============================================================
    console.log(`\n=== Results: ${passed} passed, ${failed} failed ===\n`);
    process.exit(failed > 0 ? 1 : 0);
}

runTests().catch(e => { console.error(e); process.exit(1); });
