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
  // Restrict selector to only cards and image containers to avoid buttons/links
  const cardSelector = '.card, .itemImageContainer, .cardImageContainer';
  const detailSelector = '.itemMiscInfo.itemMiscInfo-primary';
  const placeholderText = '- ⭐️';
  const batchSize = 20;
  const pending = new Set();
  const rendered = new WeakMap();
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
      left: 6px;
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
      if (record.type === 'childList') {
        record.addedNodes.forEach(node => {
          if (!(node instanceof HTMLElement)) return;
          scanNode(node);
        });
      } else if (record.type === 'attributes') {
        const el = record.target;
        if (matchesCard(el)) {
            prepareCard(el);
        }
        // Also check children if the attribute change was on a wrapper
        const found = el.querySelectorAll(cardSelector);
        found.forEach(c => prepareCard(c));
      }
    });
  });

  mutationObserver.observe(document.body, { 
      childList: true, 
      subtree: true, 
      attributes: true, 
      attributeFilter: ['data-id', 'data-itemid'] 
  });
  scanNode(document.body);

  function scanNode(root) {
    if (!(root instanceof HTMLElement)) return;
    if (matchesCard(root)) {
      prepareCard(root);
    }
    if (root.matches && root.matches(detailSelector)) {
        prepareDetail(root);
    }

    const found = root.querySelectorAll(cardSelector);
    if (found.length > 0) {
        // console.debug(logPrefix, 'Found cards:', found.length);
        found.forEach(el => prepareCard(el));
    }

    const foundDetails = root.querySelectorAll(detailSelector);
    foundDetails.forEach(el => prepareDetail(el));
  }

  function matchesCard(el) {
    return el.matches && el.matches(cardSelector);
  }

  function isValidType(el) {
    let node = el;
    while (node && node !== document.body) {
        if (node.getAttribute) {
            const type = node.getAttribute('data-type') || node.getAttribute('data-itemtype');
            if (type) {
                const lower = type.toLowerCase();
                return lower === 'movie' || lower === 'series';
            }
        }
        node = node.parentElement;
    }
    return false;
  }

  function prepareCard(el) {
    // Skip if this element is a button or inside a button/text container
    if (el.tagName === 'BUTTON' || el.closest('button') || el.closest('.cardText')) {
        return;
    }

    if (!isValidType(el)) return;

    const id = getItemId(el);
    if (!id) {
        return;
    }

    if (rendered.get(el) === id) return;
    rendered.set(el, id);
    
    const container = ensureContainer(el);
    if (!container) return; // Should not happen if ensureContainer works right, but safety first

    if (cache.has(id)) {
      applyBadge(container, cache.get(id));
    } else {
      renderPlaceholder(container);
    }
    intersectionObserver.observe(container);
  }

  function ensureContainer(el) {
    // Try to find the card scalable wrapper first (permanent visibility)
    const card = el.closest('.card');
    if (card) {
        const scalable = card.querySelector('.cardScalable');
        if (scalable) {
            scalable.classList.add('csfd-rating-container');
            return scalable;
        }

        // Fallback to image container
        const imgContainer = card.querySelector('.cardImageContainer, .itemImageContainer');
        if (imgContainer) {
            imgContainer.classList.add('csfd-rating-container');
            return imgContainer;
        }
    }
    
    // If we are not in a card (unlikely with current selectors), or fallback failed
    // Just use el if it's a container type
    if (el.classList.contains('cardImageContainer') || 
        el.classList.contains('itemImageContainer')) {
        el.classList.add('csfd-rating-container');
        return el;
    }
    
    return null;
  }

  function getItemId(el) {
    // Check if it is the detail info container
    if (el.matches && el.matches(detailSelector)) {
        const hash = window.location.hash;
        if (hash && hash.includes('/details')) {
             const params = new URLSearchParams(hash.split('?')[1]);
             return normalizeId(params.get('id'));
        }
    }

    let id = el.getAttribute('data-id') || el.getAttribute('data-itemid');
    if (!id) {
        const parent = el.closest('[data-id], [data-itemid]');
        if (parent) {
            id = parent.getAttribute('data-id') || parent.getAttribute('data-itemid');
        }
    }
    return normalizeId(id);
  }

  function normalizeId(id) {
    if (!id) return id;
    if (id.length === 32 && /^[0-9a-fA-F]+$/.test(id)) {
        return `${id.substring(0,8)}-${id.substring(8,12)}-${id.substring(12,16)}-${id.substring(16,20)}-${id.substring(20)}`;
    }
    return id;
  }

  function prepareDetail(el) {
    const id = getItemId(el);
    if (!id) return;

    if (rendered.get(el) === id) return;
    rendered.set(el, id);

    if (cache.has(id)) {
        injectDetailRating(el, cache.get(id));
    } else {
        injectDetailRating(el, null);
        queueFetch(id);
    }
  }

  function injectDetailRating(el, data) {
      let span = el.querySelector('.csfd-detail-rating');
      if (!span) {
          span = document.createElement('span');
          span.className = 'csfd-detail-rating';
          span.style.marginLeft = '1em';
          span.style.marginRight = '1em';
          el.appendChild(span);
      }

      if (!data) {
          span.textContent = 'CSFD: - ⭐️';
          return;
      }
      
      const rawStatus = (data.status ?? data.Status ?? '').toString().toLowerCase();
      if (rawStatus && rawStatus !== 'resolved' && rawStatus !== '1') {
          span.textContent = 'CSFD: - ⭐️';
          return;
      }

      const starsValue = typeof data.stars === 'number' ? data.stars : typeof data.Stars === 'number' ? data.Stars : null;
      const text = starsValue !== null ? `CSFD: ⭐️ ${starsValue.toFixed(1)}` : 'CSFD: - ⭐️';
      span.textContent = text;
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
      }
    });

    document.querySelectorAll(detailSelector).forEach(el => {
        const id = getItemId(el);
        if (id && map[id]) {
            injectDetailRating(el, map[id]);
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
