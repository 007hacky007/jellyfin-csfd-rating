/* Jellyfin ČSFD Rating Overlay - injected client bundle */
(() => {
  const logPrefix = '[CsfdOverlay]';
  console.log(logPrefix, 'Script initializing...');

  function waitForApiClient(attempt = 0) {
    if (window.ApiClient) {
      console.log(logPrefix, 'ApiClient found, proceeding.');
      init();
      return;
    }
    if (attempt > 100) {
      console.warn(logPrefix, 'ApiClient not found after waiting; aborting');
      return;
    }
    setTimeout(() => waitForApiClient(attempt + 1), 100);
  }

  waitForApiClient();

  function init() {
  const apiBase = (window.ApiClient && window.ApiClient._serverAddress) || '';

  const sessionKey = 'csfdOverlayCache';
  // Expanded selector to catch more potential card types
  const cardSelector = '[data-id], [data-itemid], .card, .itemImageContainer';
  const placeholderText = '- ⭐️';
  const batchSize = 20;
  const pending = new Set();
  const rendered = new WeakSet();
  let flushTimer = null;

  const cache = new Map();
  try {
    const raw = sessionStorage.getItem(sessionKey);
    if (raw) {
      const parsed = JSON.parse(raw);
      for (const [k, v] of Object.entries(parsed)) {
        cache.set(k, v);
      }
    }
  } catch (err) {
    console.warn(logPrefix, 'Failed to restore cache', err);
  }

  const style = document.createElement('style');
  style.textContent = `
    .csfd-rating-container { position: relative; }
    .csfd-rating-badge {
      position: absolute;
      bottom: 6px;
      left: 50%;
      transform: translateX(-50%);
      background: rgba(20, 20, 20, 0.75);
      color: #fff;
      padding: 2px 6px;
      border-radius: 6px;
      font-size: 0.85rem;
      font-weight: 600;
      line-height: 1.2;
      pointer-events: none;
      box-shadow: 0 2px 6px rgba(0,0,0,0.35);
      z-index: 1000;
    }
    .csfd-rating-badge--placeholder {
      background: rgba(255, 255, 255, 0.2);
      color: #f5f5f5;
      font-style: italic;
    }
  `;
  document.head.appendChild(style);

  const intersectionObserver = new IntersectionObserver(entries => {
    entries.forEach(entry => {
      const el = entry.target;
      if (!entry.isIntersecting) return;
      intersectionObserver.unobserve(el);
      const id = getItemId(el);
      if (!id) return;
      if (cache.has(id)) {
        applyBadge(el, cache.get(id));
      } else {
        queueFetch(id);
      }
    });
  }, { rootMargin: '200px' });

  const mutationObserver = new MutationObserver(records => {
    records.forEach(record => {
      record.addedNodes.forEach(node => {
        if (!(node instanceof HTMLElement)) return;
        scanNode(node);
      });
    });
  });

  mutationObserver.observe(document.body, { childList: true, subtree: true });
  scanNode(document.body);

  function scanNode(root) {
    if (!(root instanceof HTMLElement)) return;
    if (matchesCard(root)) {
      prepareCard(root);
    }
    const found = root.querySelectorAll(cardSelector);
    if (found.length > 0) {
        // console.debug(logPrefix, 'Found cards:', found.length);
        found.forEach(el => prepareCard(el));
    }
  }

  function matchesCard(el) {
    return el.matches && el.matches(cardSelector);
  }

  function prepareCard(el) {
    if (rendered.has(el)) return;
    
    const id = getItemId(el);
    if (!id) {
        // Try to find ID in parent if not on element
        const parentWithId = el.closest('[data-id], [data-itemid]');
        if (parentWithId) {
             // If we found a parent with ID, maybe we should be processing the parent instead?
             // But let's just use that ID.
             // Actually, if we are processing .itemImageContainer, the ID is usually on the parent .card
        }
        return;
    }
    
    rendered.add(el);
    // console.debug(logPrefix, 'Preparing card', id);
    
    const container = ensureContainer(el);
    if (cache.has(id)) {
      applyBadge(container, cache.get(id));
    } else {
      renderPlaceholder(container);
    }
    intersectionObserver.observe(container);
  }

  function ensureContainer(el) {
    // If el is the image container, use it. If it's the card, find the image container.
    // We want the badge on the image.
    let target = el;
    if (el.classList.contains('card')) {
        const imgContainer = el.querySelector('.cardImageContainer, .itemImageContainer');
        if (imgContainer) target = imgContainer;
    }
    
    target.classList.add('csfd-rating-container');
    return target;
  }

  function getItemId(el) {
    let id = el.getAttribute('data-id') || el.getAttribute('data-itemid');
    if (!id) {
        const parent = el.closest('[data-id], [data-itemid]');
        if (parent) {
            id = parent.getAttribute('data-id') || parent.getAttribute('data-itemid');
        }
    }
    return id;
  }

  function queueFetch(id) {
    pending.add(id);
    if (pending.size >= batchSize) {
      flushPending();
    } else if (!flushTimer) {
      flushTimer = setTimeout(flushPending, 400);
    }
  }

  function flushPending() {
    if (pending.size === 0) return;
    const ids = Array.from(pending);
    pending.clear();
    clearTimeout(flushTimer);
    flushTimer = null;
    fetchBatch(ids);
  }

  async function fetchBatch(ids) {
    const token = window.ApiClient.accessToken ? window.ApiClient.accessToken() : window.ApiClient._accessToken;
    const url = apiBase.replace(/\/$/, '') + '/Plugins/CsfdRatingOverlay/csfd/items/batch';
    const headers = { 'Content-Type': 'application/json' };
    if (token) {
      headers['X-Emby-Token'] = token;
    }

    try {
      const res = await fetch(url, { method: 'POST', headers, body: JSON.stringify({ itemIds: ids }) });
      if (!res.ok) {
        console.warn(logPrefix, 'Batch fetch failed', res.status);
        return;
      }
      const data = await res.json();
      for (const id of ids) {
        if (data[id]) {
          cache.set(id, data[id]);
        }
      }
      persistCache();
      updateBadges(data);
    } catch (err) {
      console.warn(logPrefix, 'Batch fetch error', err);
    }
  }

  function persistCache() {
    const obj = Object.fromEntries(cache.entries());
    try {
      sessionStorage.setItem(sessionKey, JSON.stringify(obj));
    } catch (err) {
      // ignore
    }
  }

  function updateBadges(map) {
    document.querySelectorAll(cardSelector).forEach(el => {
      const id = getItemId(el);
      if (!id) return;
      if (map[id]) {
        applyBadge(el, map[id]);
      } else {
        renderPlaceholder(el);
      }
    });
  }

  function applyBadge(el, data) {
    if (!data) {
      renderPlaceholder(el);
      return;
    }

    const rawStatus = (data.status ?? data.Status ?? '').toString().toLowerCase();
    if (rawStatus && rawStatus !== 'resolved' && rawStatus !== '1') {
      renderPlaceholder(el);
      return;
    }

    const starsValue = typeof data.stars === 'number' ? data.stars : typeof data.Stars === 'number' ? data.Stars : null;
    const text = data.displayText || data.DisplayText || (starsValue !== null ? `${starsValue.toFixed(1)} ⭐️` : null);
    if (!text) {
      renderPlaceholder(el);
      return;
    }

    setBadge(el, text, false);
  }

  function renderPlaceholder(el) {
    setBadge(el, placeholderText, true);
  }

  function setBadge(el, text, isPlaceholder) {
    const container = ensureContainer(el);
    let badge = container.querySelector('.csfd-rating-badge');
    if (!badge) {
      badge = document.createElement('div');
      badge.className = 'csfd-rating-badge';
      container.appendChild(badge);
    }
    badge.textContent = text;
    badge.classList.toggle('csfd-rating-badge--placeholder', Boolean(isPlaceholder));
  }
  }
})();
