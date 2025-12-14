const pluginId = 'b9643a4b-5b92-4f09-94c4-45ce6bfc57e9';

const defaults = {
    enabled: true,
    overlayInjectionEnabled: true,
    requestDelayMs: 2000,
    maxRetries: 5,
    cooldownMinMinutes: 10
};

function setFields(view, config) {
    view.querySelector('#chkEnabled').checked = config.Enabled ?? defaults.enabled;
    view.querySelector('#chkOverlayInjectionEnabled').checked = config.OverlayInjectionEnabled ?? defaults.overlayInjectionEnabled;
    view.querySelector('#txtRequestDelayMs').value = config.RequestDelayMs ?? defaults.requestDelayMs;
    view.querySelector('#txtMaxRetries').value = config.MaxRetries ?? defaults.maxRetries;
    view.querySelector('#txtCooldownMinMinutes').value = config.CooldownMinMinutes ?? defaults.cooldownMinMinutes;
}

function wireAction(view, selector, path) {
    const button = view.querySelector(selector);
    if (!button) {
        return;
    }

    button.addEventListener('click', () => {
        Dashboard.showLoadingMsg();
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(`Plugins/CsfdRatingOverlay/${path}`),
            dataType: 'json'
        }).then(() => Dashboard.hideLoadingMsg(), () => Dashboard.hideLoadingMsg());
    });
}

export default function (view) {
    const form = view.querySelector('.csfdConfigForm');
    const resetButton = view.querySelector('.btnResetDefaults');

    view.addEventListener('viewshow', () => {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId)
            .then(config => {
                setFields(view, config);
                Dashboard.hideLoadingMsg();
            }, () => Dashboard.hideLoadingMsg());
    });

    if (form) {
        form.addEventListener('submit', (e) => {
            e.preventDefault();
            Dashboard.showLoadingMsg();
            ApiClient.getPluginConfiguration(pluginId).then(config => {
                config.Enabled = view.querySelector('#chkEnabled').checked;
                config.OverlayInjectionEnabled = view.querySelector('#chkOverlayInjectionEnabled').checked;
                config.RequestDelayMs = parseInt(view.querySelector('#txtRequestDelayMs').value, 10) || defaults.requestDelayMs;
                config.MaxRetries = parseInt(view.querySelector('#txtMaxRetries').value, 10) || defaults.maxRetries;
                config.CooldownMinMinutes = parseInt(view.querySelector('#txtCooldownMinMinutes').value, 10) || defaults.cooldownMinMinutes;

                ApiClient.updatePluginConfiguration(pluginId, config)
                    .then(Dashboard.processPluginConfigurationUpdateResult, Dashboard.hideLoadingMsg)
                    .then(() => Dashboard.hideLoadingMsg());
            }, () => Dashboard.hideLoadingMsg());
        });
    }

    if (resetButton) {
        resetButton.addEventListener('click', () => {
            setFields(view, {
                Enabled: defaults.enabled,
                OverlayInjectionEnabled: defaults.overlayInjectionEnabled,
                RequestDelayMs: defaults.requestDelayMs,
                MaxRetries: defaults.maxRetries,
                CooldownMinMinutes: defaults.cooldownMinMinutes
            });
        });
    }

    wireAction(view, '#backfillButton', 'csfd/actions/backfill');
    wireAction(view, '#retryNotFoundButton', 'csfd/actions/retry-notfound');
    wireAction(view, '#clearCacheButton', 'csfd/actions/reset-cache');
}
