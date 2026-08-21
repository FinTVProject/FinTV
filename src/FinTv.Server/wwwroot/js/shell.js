(function () {
    const titles = {
        guide: 'TV Guide',
        channels: 'Channels',
        presets: 'Presets',
        lineups: 'Lineups',
        list: 'Lists',
        special: 'Special Presentation',
        commercials: 'Commercials',
        logos: 'Logo Sets',
        ebs: 'EBS',
        ai: 'AI',
        weather: 'Weather',
        news: 'News',
        general: 'General',
        setup: 'Live TV Setup',
        tasks: 'Tasks'
    };

    const subtitles = {
        guide: 'What\'s on now across FinTV channels',
        channels: 'Manage Live TV channels',
        presets: 'Create the Binarygeek119 ready-made lineup',
        lineups: 'Edit 24-hour schedules and playout',
        list: 'Register Jellyfin playlists as FinTV lists',
        special: 'Recurring blocks that override the normal lineup',
        commercials: 'Jellyfin and CommercialBrainz ad pools',
        logos: 'Channel bugs and logo sets',
        ebs: 'Off-air Emergency Broadcast System',
        ai: 'AI lineup generation and tagging',
        weather: 'WeatherStar live channels',
        news: 'Live RSS news channel',
        general: 'Server-wide FinTV settings',
        setup: 'Jellyfin Live TV tuner and guide URLs',
        tasks: 'Rebuild playouts and maintenance'
    };

    async function api(path, options) {
        options = options || {};
        const res = await fetch(path, Object.assign({ credentials: 'same-origin', headers: { accept: 'application/json' } }, options));
        if (options.body && !options.headers) {
            /* handled below */
        }
        if (res.status === 204) {
            return null;
        }
        const text = await res.text();
        const data = text ? JSON.parse(text) : null;
        if (!res.ok) {
            throw new Error((data && data.message) || res.statusText);
        }
        return data;
    }

    async function postJson(path, body) {
        const res = await fetch(path, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'content-type': 'application/json', accept: 'application/json' },
            body: JSON.stringify(body)
        });
        const text = await res.text();
        const data = text ? JSON.parse(text) : null;
        if (!res.ok) {
            throw new Error((data && data.message) || res.statusText);
        }
        return data;
    }

    function safeReturnPath(value) {
        if (!value || value[0] !== '/' || value[1] === '/') {
            return '/channels';
        }

        return value;
    }

    function syncDrawer(name) {
        const nav = document.getElementById('drawer-nav');
        if (!nav) {
            return;
        }

        nav.querySelectorAll('a').forEach((a) => a.classList.toggle('active', a.dataset.tab === name));
        const title = titles[name];
        if (title) {
            document.getElementById('page-title').textContent = title;
        }
        const subtitle = document.getElementById('page-subtitle');
        if (subtitle && subtitles[name]) {
            subtitle.textContent = subtitles[name];
        }
    }

    function restorePathAfterLogin() {
        const path = (location.pathname || '/').replace(/\/+$/, '') || '/';
        if (path !== '/login') {
            return;
        }

        const params = new URLSearchParams(location.search);
        const dest = safeReturnPath(params.get('ReturnUrl') || params.get('returnUrl') || '/channels');
        history.replaceState({}, '', dest);
    }

    function showLogin(needsSetup, userName) {
        document.getElementById('login-screen').classList.remove('hidden');
        document.getElementById('app-shell').classList.add('hidden');
        document.getElementById('auth-title').textContent = needsSetup ? 'Create admin' : 'Sign in';
        document.getElementById('login-submit').textContent = needsSetup ? 'Create account' : 'Sign in';
        document.getElementById('auth-subtitle').textContent = needsSetup
            ? 'First launch — choose a username and password for FinTV Server'
            : 'FinTV Server';
        const confirmField = document.getElementById('login-pass-confirm-field');
        const confirmInput = document.getElementById('login-pass-confirm');
        const passInput = document.getElementById('login-pass');
        confirmField.classList.toggle('hidden', !needsSetup);
        confirmInput.required = !!needsSetup;
        confirmInput.minLength = needsSetup ? 8 : 0;
        passInput.minLength = needsSetup ? 8 : 0;
        passInput.autocomplete = needsSetup ? 'new-password' : 'current-password';
        if (userName) {
            document.getElementById('topbar-user').textContent = userName;
        }

        const path = (location.pathname || '/').replace(/\/+$/, '') || '/';
        if (path === '/' || path === '/index.html') {
            history.replaceState({}, '', '/login');
        }
        document.title = (needsSetup ? 'Create admin' : 'Sign in') + ' · FinTV';
    }

    function showApp(userName) {
        document.getElementById('login-screen').classList.add('hidden');
        document.getElementById('app-shell').classList.remove('hidden');
        document.getElementById('topbar-user').textContent = userName || '';
        restorePathAfterLogin();
        buildDrawer();
        const page = document.getElementById('FinTVConfigPage');
        if (page && window.FinTV) {
            window.FinTV.init(page);
        }
        bindPathMappings();
    }

    function buildDrawer() {
        const nav = document.getElementById('drawer-nav');
        const tabs = document.querySelectorAll('#FinTVConfigPage .fintv-tabs .tab');
        nav.innerHTML = '';
        tabs.forEach((tab) => {
            const link = document.createElement('a');
            link.href = tab.getAttribute('href') || ('/' + tab.dataset.tab);
            link.textContent = tab.textContent.trim();
            link.dataset.tab = tab.dataset.tab;
            if (tab.classList.contains('active')) {
                link.classList.add('active');
            }
            nav.appendChild(link);
        });
        const current = window.FinTV && window.FinTV.tabFromPath
            ? window.FinTV.tabFromPath(location.pathname)
            : 'channels';
        syncDrawer(current);
    }

    function bindPathMappings() {
        const general = document.getElementById('tab-general') || document.getElementById('tab-setup');
        if (!general || document.getElementById('path-map-card')) {
            return;
        }
        const card = document.createElement('div');
        card.className = 'section-card';
        card.id = 'path-map-card';
        card.innerHTML = '<div class="section-header"><h3>Library path remaps</h3></div>' +
            '<p class="muted">Jellyfin path prefix → local FinTV mount prefix</p>' +
            '<textarea id="path-mappings" rows="6" placeholder="/data/media = /media"></textarea>' +
            '<div class="toolbar"><button type="button" id="btn-save-paths" class="raised button-submit">Save remaps</button>' +
            '<button type="button" id="btn-test-paths" class="raised">Test remaps</button></div>' +
            '<pre id="path-test-result"></pre>';
        general.appendChild(card);
        fetch('/api/settings/path-mappings', { credentials: 'same-origin' })
            .then((r) => r.json())
            .then((rows) => {
                document.getElementById('path-mappings').value = (rows || [])
                    .map((r) => r.jellyfinPrefix + ' = ' + r.localPrefix)
                    .join('\n');
            })
            .catch(() => { });
        document.getElementById('btn-save-paths').onclick = async () => {
            const mappings = document.getElementById('path-mappings').value.split('\n')
                .map((line) => line.split('='))
                .filter((p) => p.length >= 2)
                .map((p, i) => ({ jellyfinPrefix: p[0].trim(), localPrefix: p.slice(1).join('=').trim(), sortOrder: i }));
            await fetch('/api/settings/path-mappings', {
                method: 'PUT',
                credentials: 'same-origin',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify(mappings)
            });
        };
        document.getElementById('btn-test-paths').onclick = async () => {
            const res = await fetch('/api/settings/path-mappings/test', { method: 'POST', credentials: 'same-origin' });
            document.getElementById('path-test-result').textContent = JSON.stringify(await res.json(), null, 2);
        };
    }

    async function boot() {
        try {
            const status = await api('/api/auth/status');
            if (!status.authenticated) {
                showLogin(status.needsSetup);
            } else {
                showApp(status.userName);
            }

            document.getElementById('login-form').addEventListener('submit', async (e) => {
                e.preventDefault();
                const password = document.getElementById('login-pass').value;
                const confirm = document.getElementById('login-pass-confirm').value;
                const body = {
                    userName: document.getElementById('login-user').value,
                    password
                };
                const err = document.getElementById('login-error');
                try {
                    const needsSetup = status.needsSetup && !status.authenticated;
                    if (needsSetup && password !== confirm) {
                        throw new Error('Passwords do not match.');
                    }
                    const path = needsSetup ? '/api/auth/setup' : '/api/auth/login';
                    const result = await postJson(path, body);
                    showApp(result.userName);
                    err.textContent = '';
                } catch (ex) {
                    err.textContent = ex.message;
                }
            });
            document.getElementById('btn-logout').addEventListener('click', async () => {
                await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
                location.reload();
            });
            window.addEventListener('fintv-auth-required', () => showLogin(false));
            window.addEventListener('fintv-tabchange', (e) => {
                if (e.detail && e.detail.tab) {
                    syncDrawer(e.detail.tab);
                }
            });
        } catch (ex) {
            showLogin(true);
        }
    }

    document.addEventListener('DOMContentLoaded', boot);
})();
