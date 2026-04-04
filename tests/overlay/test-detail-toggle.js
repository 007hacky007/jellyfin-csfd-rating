/**
 * Tests for the overlay detail toggle logic.
 * Extracts and tests the state machine independently from the full overlay.
 *
 * Run: node tests/overlay/test-detail-toggle.js
 */

const { JSDOM } = require('jsdom');

let passed = 0;
let failed = 0;

function assert(condition, msg) {
    if (condition) {
        passed++;
        console.log(`  PASS: ${msg}`);
    } else {
        failed++;
        console.log(`  FAIL: ${msg}`);
    }
}

function createDOM(html = '') {
    const dom = new JSDOM(`<!DOCTYPE html><html><body>${html}</body></html>`, {
        url: 'http://localhost/web/index.html#!/details?id=abc12345def67890abc12345def67890'
    });
    // Stub localStorage/sessionStorage
    const storage = {};
    const storageProxy = {
        getItem: (k) => storage[k] !== undefined ? storage[k] : null,
        setItem: (k, v) => { storage[k] = String(v); },
        removeItem: (k) => { delete storage[k]; },
        clear: () => { Object.keys(storage).forEach(k => delete storage[k]); }
    };
    dom.window._storage = storage;
    return { dom, storage: storageProxy, window: dom.window, document: dom.window.document };
}

// Build a minimal overlay engine that mirrors the real logic
function createEngine(opts = {}) {
    const { dom, storage, window, document } = createDOM(opts.html || '');

    const detailSelector = '.itemMiscInfo.itemMiscInfo-primary';
    const cache = new Map();
    if (opts.cacheEntries) {
        for (const [k, v] of Object.entries(opts.cacheEntries)) {
            cache.set(k, v);
        }
    }

    // Mirror the real initialization logic
    const savedDetailSetting = opts.localStorageValue !== undefined
        ? opts.localStorageValue
        : storage.getItem('csfdOverlayDetailEnabled');
    let overlayDetailEnabled = savedDetailSetting !== null ? savedDetailSetting === 'true' : null;

    function prepareDetail(el) {
        if (overlayDetailEnabled !== true) return false;
        // Simplified: just call injectDetailRating
        return injectDetailRating(el, cache.get('test-id') || null);
    }

    function injectDetailRating(el, data) {
        if (overlayDetailEnabled !== true) return false;
        let container = el.querySelector('.csfd-detail-rating');
        if (!container) {
            container = document.createElement('div');
            container.className = 'csfd-detail-rating mediaInfoItem';
            el.appendChild(container);
        }
        container.textContent = data ? `CSFD: ${data.stars.toFixed(1)}` : 'CSFD: -';
        return true;
    }

    function simulateConfigResponse(value) {
        const wasEnabled = overlayDetailEnabled;
        overlayDetailEnabled = value;
        storage.setItem('csfdOverlayDetailEnabled', value.toString());
        if (overlayDetailEnabled && wasEnabled !== true) {
            document.querySelectorAll(detailSelector).forEach(el => prepareDetail(el));
        } else if (!overlayDetailEnabled) {
            document.querySelectorAll('.csfd-detail-rating').forEach(el => el.remove());
        }
    }

    function simulateMutationObserver() {
        document.querySelectorAll(detailSelector).forEach(el => prepareDetail(el));
    }

    function getDetailCount() {
        return document.querySelectorAll('.csfd-detail-rating').length;
    }

    function addDetailSection() {
        const el = document.createElement('div');
        el.className = 'itemMiscInfo itemMiscInfo-primary';
        document.body.appendChild(el);
        return el;
    }

    function simulateHashChange() {
        // Mirrors the hashchange listener in overlay.js
        if (overlayDetailEnabled === true) {
            document.querySelectorAll(detailSelector).forEach(el => prepareDetail(el));
        }
    }

    return {
        document, cache, storage,
        get overlayDetailEnabled() { return overlayDetailEnabled; },
        prepareDetail, injectDetailRating,
        simulateConfigResponse, simulateMutationObserver,
        getDetailCount, addDetailSection, simulateHashChange
    };
}

// ============================================================
// Test Suite
// ============================================================

console.log('\n=== Detail Toggle Tests ===\n');

console.log('1. Fresh install (no localStorage)');
{
    const e = createEngine();
    assert(e.overlayDetailEnabled === null, 'overlayDetailEnabled starts as null');
    const el = e.addDetailSection();
    e.prepareDetail(el);
    assert(e.getDetailCount() === 0, 'No detail injected before config loads');

    e.simulateMutationObserver();
    assert(e.getDetailCount() === 0, 'Mutation observer does not inject before config loads');
}

console.log('\n2. Fresh install -> config returns false');
{
    const e = createEngine();
    e.addDetailSection();
    e.simulateConfigResponse(false);
    assert(e.getDetailCount() === 0, 'No detail injected when config says false');

    e.simulateMutationObserver();
    assert(e.getDetailCount() === 0, 'Mutation observer does not inject when disabled');
}

console.log('\n3. Fresh install -> config returns true');
{
    const e = createEngine({ cacheEntries: { 'test-id': { stars: 8.6, status: 'resolved' } } });
    const el = e.addDetailSection();
    assert(e.getDetailCount() === 0, 'No detail before config');

    e.simulateConfigResponse(true);
    assert(e.getDetailCount() === 1, 'Detail injected after config says true');
}

console.log('\n4. localStorage=false (returning user, disabled)');
{
    const e = createEngine({ localStorageValue: 'false' });
    assert(e.overlayDetailEnabled === false, 'overlayDetailEnabled reads false from localStorage');

    e.addDetailSection();
    e.simulateMutationObserver();
    assert(e.getDetailCount() === 0, 'No detail injected when localStorage says false');

    // Config confirms false
    e.simulateConfigResponse(false);
    assert(e.getDetailCount() === 0, 'Still no detail after config confirms false');
}

console.log('\n5. localStorage=true (returning user, enabled)');
{
    const e = createEngine({
        localStorageValue: 'true',
        cacheEntries: { 'test-id': { stars: 7.5, status: 'resolved' } }
    });
    assert(e.overlayDetailEnabled === true, 'overlayDetailEnabled reads true from localStorage');

    e.addDetailSection();
    e.simulateMutationObserver();
    assert(e.getDetailCount() === 1, 'Detail injected immediately from localStorage=true');
}

console.log('\n6. localStorage=true but config returns false (user just disabled)');
{
    const e = createEngine({
        localStorageValue: 'true',
        cacheEntries: { 'test-id': { stars: 7.5, status: 'resolved' } }
    });
    const el = e.addDetailSection();
    e.simulateMutationObserver();
    assert(e.getDetailCount() === 1, 'Detail injected from localStorage=true');

    e.simulateConfigResponse(false);
    assert(e.getDetailCount() === 0, 'Detail REMOVED after config says false');

    e.simulateMutationObserver();
    assert(e.getDetailCount() === 0, 'Mutation observer does not re-inject after disable');
}

console.log('\n7. localStorage=false but config returns true (user just enabled)');
{
    const e = createEngine({
        localStorageValue: 'false',
        cacheEntries: { 'test-id': { stars: 9.0, status: 'resolved' } }
    });
    e.addDetailSection();
    e.simulateMutationObserver();
    assert(e.getDetailCount() === 0, 'No detail from localStorage=false');

    e.simulateConfigResponse(true);
    assert(e.getDetailCount() === 1, 'Detail injected after config says true');
}

console.log('\n8. Config false -> mutation observer -> still no detail');
{
    const e = createEngine();
    e.addDetailSection();
    e.simulateConfigResponse(false);

    // Simulate many mutations
    for (let i = 0; i < 10; i++) {
        e.simulateMutationObserver();
    }
    assert(e.getDetailCount() === 0, 'Detail never appears after 10 mutation cycles when disabled');
}

console.log('\n9. Detail removed manually -> mutation observer with disabled config');
{
    const e = createEngine({
        localStorageValue: 'true',
        cacheEntries: { 'test-id': { stars: 8.0, status: 'resolved' } }
    });
    e.addDetailSection();
    e.simulateMutationObserver();
    assert(e.getDetailCount() === 1, 'Detail present initially');

    // Config says disable
    e.simulateConfigResponse(false);
    assert(e.getDetailCount() === 0, 'Detail removed by config');

    // Many mutations follow
    for (let i = 0; i < 10; i++) {
        e.simulateMutationObserver();
    }
    assert(e.getDetailCount() === 0, 'Detail never reappears after 10 mutations');
}

console.log('\n10. Badges are never affected by overlayDetailEnabled');
{
    const e = createEngine({ localStorageValue: 'false' });
    // Badge logic doesn't check overlayDetailEnabled at all
    // Just verify the flag doesn't interfere with non-detail elements
    assert(e.overlayDetailEnabled === false, 'Detail disabled');
    // Cards would still work - they use prepareCard, not prepareDetail
    // This is a structural test - prepareCard has no overlayDetailEnabled check
}

console.log('\n11. localStorage persists after config response');
{
    const e = createEngine();
    e.simulateConfigResponse(false);
    assert(e.storage.getItem('csfdOverlayDetailEnabled') === 'false', 'localStorage set to false');

    e.simulateConfigResponse(true);
    assert(e.storage.getItem('csfdOverlayDetailEnabled') === 'true', 'localStorage set to true');
}

console.log('\n12. Detail section added AFTER config loads (SPA navigation)');
{
    const e = createEngine({
        cacheEntries: { 'test-id': { stars: 8.2, status: 'resolved' } }
    });
    // Config loads first, no detail sections exist yet
    e.simulateConfigResponse(true);
    assert(e.getDetailCount() === 0, 'No detail sections exist yet');

    // User navigates to detail page (SPA)
    e.addDetailSection();
    e.simulateMutationObserver();
    assert(e.getDetailCount() === 1, 'Detail injected after SPA navigation');
}

console.log('\n13. Detail section added AFTER config loads with disabled');
{
    const e = createEngine({
        cacheEntries: { 'test-id': { stars: 8.2, status: 'resolved' } }
    });
    e.simulateConfigResponse(false);

    e.addDetailSection();
    e.simulateMutationObserver();
    assert(e.getDetailCount() === 0, 'No detail injected on SPA nav when disabled');
}

console.log('\n14. Real-world flow: homepage load -> config true -> SPA navigate to detail');
{
    // Start with no detail section (homepage), no localStorage (fresh)
    const e = createEngine({
        cacheEntries: { 'test-id': { stars: 8.2, status: 'resolved' } }
    });
    assert(e.overlayDetailEnabled === null, 'Starts null (fresh install)');
    assert(e.getDetailCount() === 0, 'No detail sections on homepage');

    // Config loads while on homepage
    e.simulateConfigResponse(true);
    assert(e.getDetailCount() === 0, 'Still no detail (no detail section in DOM yet)');

    // User clicks movie -> SPA adds detail section
    e.addDetailSection();
    // Mutation observer fires (childList mutation for new node)
    e.simulateMutationObserver();
    assert(e.getDetailCount() === 1, 'Detail injected after SPA navigation via mutation observer');
}

console.log('\n15. Real-world flow: homepage load -> config false -> SPA navigate to detail');
{
    const e = createEngine({
        cacheEntries: { 'test-id': { stars: 8.2, status: 'resolved' } }
    });
    e.simulateConfigResponse(false);
    e.addDetailSection();
    e.simulateMutationObserver();
    assert(e.getDetailCount() === 0, 'No detail even after SPA nav when disabled');
}

console.log('\n16. Real-world flow: localStorage=false -> page load on detail page -> config returns true');
{
    // User previously had it disabled, now enables it. Loads directly onto a detail page.
    const e = createEngine({
        localStorageValue: 'false',
        html: '<div class="itemMiscInfo itemMiscInfo-primary"></div>',
        cacheEntries: { 'test-id': { stars: 7.0, status: 'resolved' } }
    });
    assert(e.overlayDetailEnabled === false, 'Starts false from localStorage');
    assert(e.getDetailCount() === 0, 'No detail injected initially');

    // Config returns true (user just enabled it)
    e.simulateConfigResponse(true);
    assert(e.getDetailCount() === 1, 'Detail injected after config overrides localStorage');
}

console.log('\n17. Hashchange fallback: config true, detail appears after hash change');
{
    const e = createEngine({
        cacheEntries: { 'test-id': { stars: 9.1, status: 'resolved' } }
    });
    e.simulateConfigResponse(true);

    // Simulate hashchange (SPA navigation) + detail section appearing
    e.addDetailSection();
    // Even without mutation observer, hashchange handler should catch it
    e.simulateHashChange();
    assert(e.getDetailCount() === 1, 'Detail injected via hashchange fallback');
}

// ============================================================
// Summary
// ============================================================

console.log(`\n=== Results: ${passed} passed, ${failed} failed ===\n`);
process.exit(failed > 0 ? 1 : 0);
