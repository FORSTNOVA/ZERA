/**
 * ZERA Android App Engine & Bridge RPC
 * Offline bundled client for Android WebView
 */
(function () {
    'use strict';

    // ==========================================
    // 1. RPC & Android Bridge Integration
    // ==========================================
    let reqCounter = 0;
    const pendingRequests = new Map();
    let isBridgeAvailable = false;
    let isStatePolling = false;
    let isLoginPollInFlight = false;
    let pollTimer = null;
    let qrPollTimer = null;
    let lastImportedSharedText = '';
    let sharedImportGeneration = 0;
    let lastPlatformRender = '';
    let lastSettingsRender = '';
    let lastExplorerRender = '';
    let lastNativeTheme = '';
    let isSettingsDirty = false;
    const expandedJobIds = new Set();
    const taskCardElements = new Map(); // id -> { card, update(t, isBt), isBt }

    // Local cached state
    let appState = {
        platforms: [],
        platform: 0,
        accounts: [],
        activeAccount: null,
        tasks: [],
        torrentTasks: [],
        settings: { threads: 32, concurrent: 2, writeMode: 'SharedFile', theme: 'system', autoExport: false, floatingWindow: false },
        files: [],
        path: '',
        shareMode: false,
        viewToken: '',
        exportFolder: { selected: false, name: null, uri: null },
        exports: {},
        systemDark: false,
        overlayAllowed: false,
        logs: [],
        error: null,
        downloadDirectory: '',
        version: '3.0.0-preview.2'
    };

    let selectedFileIds = new Set();
    let currentMode = 'share'; // 'share' | 'drive' | 'direct' | 'torrent'
    let currentLoginMode = 'qr'; // 'qr' | 'password' | 'cookie' | 'webdav' | 'json'

    /**
     * Dispatch an RPC action to Android native bridge with timeout protection
     */
    function dispatch(action, payload = {}, timeoutMs = 15000) {
        // Picker, cloud transfer, and file export operations require extended timeouts
        if (action === 'system.pickExportFolder' || action === 'system.pickQrImage' || action === 'system.pickTorrent' || action === 'system.importSharedTorrent' || action.startsWith('cloud.') || action === 'account.capacity') {
            timeoutMs = Math.max(timeoutMs, 180000); // >= 180s
        } else if (action === 'system.exportTask' || action === 'system.saveFile' || action === 'download.add' || action === 'links.export') {
            timeoutMs = Math.max(timeoutMs, 1800000); // >= 30min
        }

        return new Promise((resolve, reject) => {
            if (!isBridgeAvailable) {
                reject(new Error('未检测到安卓运行环境'));
                return;
            }
            const id = 'rpc_' + (++reqCounter) + '_' + Date.now();
            const timer = setTimeout(() => {
                if (pendingRequests.has(id)) {
                    pendingRequests.delete(id);
                    reject(new Error(`请求超时 (${action})`));
                }
            }, timeoutMs);

            pendingRequests.set(id, { resolve, reject, action, timer });
            try {
                window.AndroidBridge.dispatch(JSON.stringify({ id, action, payload }));
            } catch (err) {
                clearTimeout(timer);
                pendingRequests.delete(id);
                reject(err);
            }
        });
    }

    // Exposed bridge callbacks called from Android Bridge
    window.zeraResolve = function (id, res) {
        const item = pendingRequests.get(id);
        if (!item) return;
        clearTimeout(item.timer);
        pendingRequests.delete(id);
        if (res && res.ok) {
            item.resolve(res.data);
        } else {
            const msg = (res && res.error) ? res.error : '操作未成功';
            item.reject(new Error(msg));
        }
    };

    window.zeraRefresh = function () {
        loadStateSilently();
    };

    window.zeraImportIntent = function () {
        lastImportedSharedText = '';
        checkSharedIntent();
    };

    // ==========================================
    // 2. Safe DOM & UI Utilities
    // ==========================================
    function $(id) {
        return document.getElementById(id);
    }

    function showToast(message, isError = false) {
        const toast = $('toast');
        if (!toast) return;
        toast.textContent = message;
        toast.style.background = isError ? 'rgba(224, 67, 73, 0.94)' : '';
        toast.classList.remove('hidden');
        clearTimeout(toast._timer);
        toast._timer = setTimeout(() => {
            toast.classList.add('hidden');
        }, 2400);
    }

    function formatBytes(bytes) {
        if (bytes === null || bytes === undefined || isNaN(bytes) || bytes < 0) return '0 B';
        if (bytes === 0) return '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        const val = bytes / Math.pow(k, i);
        return (i === 0 ? val.toString() : val.toFixed(val >= 100 ? 1 : 2)) + ' ' + sizes[i];
    }

    function formatSpeed(bytesPerSec) {
        if (!bytesPerSec || bytesPerSec <= 0) return '0.0 KB/s';
        return formatBytes(bytesPerSec) + '/s';
    }

    function createSvgElement(pathD, width = 20, height = 20) {
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('viewBox', '0 0 24 24');
        svg.setAttribute('width', width.toString());
        svg.setAttribute('height', height.toString());
        svg.setAttribute('fill', 'none');
        svg.setAttribute('stroke', 'currentColor');
        svg.setAttribute('stroke-width', '1.8');
        svg.setAttribute('stroke-linecap', 'round');
        svg.setAttribute('stroke-linejoin', 'round');
        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', pathD);
        svg.appendChild(path);
        return svg;
    }

    function applyTheme(theme) {
        const root = document.documentElement;
        let effective = theme;
        if (theme === 'system') {
            if (appState.systemDark !== undefined) {
                effective = appState.systemDark ? 'dark' : 'light';
            } else {
                effective = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
            }
        }
        root.setAttribute('data-theme', effective);
        if (window.AndroidBridge && effective !== lastNativeTheme) {
            lastNativeTheme = effective;
            window.AndroidBridge.dispatch(JSON.stringify({ id: 'theme_' + Date.now(), action: 'system.theme', payload: { dark: effective === 'dark' } }));
        }
    }
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
        if (appState.settings.theme === 'system') applyTheme('system');
    });

    function getSelectedSplit() {
        const active = document.querySelector('#torrent-split-pills .split-pill-btn.active');
        return active ? parseInt(active.getAttribute('data-split') || '1', 10) : 1;
    }

    // ==========================================
    // 3. Share Intent Auto-Import & QR Image Import
    // ==========================================
    async function checkSharedIntent() {
        if (!isBridgeAvailable) return;
        const generation = ++sharedImportGeneration;
        try {
            const res = await dispatch('system.sharedText', {});
            if (generation !== sharedImportGeneration) return;
            if (!res) return;

            const kind = res.kind || 'text';
            const text = (typeof res.text === 'string') ? res.text.trim() : '';

            if (kind === 'torrent') {
                promptImportSharedTorrent();
                return;
            }

            if (text && text !== lastImportedSharedText) {
                lastImportedSharedText = text;
                $('share-url-input').value = text;

                // Switch to Download tab
                const navDownload = document.querySelector('.app-nav [data-target="tab-download"]');
                if (navDownload) navDownload.click();

                // Switch to Share mode
                const modeShare = $('mode-share');
                if (modeShare) modeShare.click();

                showToast(kind === 'qr' ? '已从分享图片中识别二维码' : '已从外部分享导入链接');
                triggerShareParse(text, '');
            }
        } catch (e) {
            // silent ignore
        }
    }

    function promptImportSharedTorrent() {
        const wrap = document.createElement('div');
        const p = document.createElement('p');
        p.textContent = '检测到外部传入的种子文件，请确认是否导入到 BT 下载列表：';
        p.style.marginBottom = '12px';
        p.style.color = 'var(--text-secondary)';
        wrap.appendChild(p);

        const splitRow = document.createElement('div');
        splitRow.className = 'split-setting-row mb-8';
        const splitLabel = document.createElement('span');
        splitLabel.className = 'sub-label';
        splitLabel.textContent = '拆分任务数：';
        splitRow.appendChild(splitLabel);

        const select = document.createElement('select');
        select.className = 'app-select';
        select.style.width = '140px';
        [1, 2, 4, 8].forEach(n => {
            const opt = document.createElement('option');
            opt.value = n.toString();
            opt.textContent = n === 1 ? '普通单任务' : `${n} 任务拆分`;
            select.appendChild(opt);
        });
        splitRow.appendChild(select);
        wrap.appendChild(splitRow);

        const btnRow = document.createElement('div');
        btnRow.style.display = 'flex';
        btnRow.style.gap = '8px';
        btnRow.style.marginTop = '16px';

        const confirmBtn = document.createElement('button');
        confirmBtn.className = 'btn btn-primary flex-1';
        confirmBtn.textContent = '确认导入并下载';
        confirmBtn.addEventListener('click', async () => {
            closeModal();
            try {
                showToast('正在导入种子文件...');
                const split = parseInt(select.value, 10) || 1;
                const res = await dispatch('system.importSharedTorrent', { split });
                if (Array.isArray(res)) {
                    appState.torrentTasks = res;
                }
                showToast('种子任务已成功加入列表');
                const navTasks = document.querySelector('.app-nav [data-target="tab-tasks"]');
                if (navTasks) navTasks.click();
                loadStateSilently();
            } catch (err) {
                showToast(err.message || '导入种子失败', true);
            }
        });

        const cancelBtn = document.createElement('button');
        cancelBtn.className = 'btn btn-ghost flex-1';
        cancelBtn.textContent = '取消';
        cancelBtn.addEventListener('click', closeModal);

        btnRow.appendChild(confirmBtn);
        btnRow.appendChild(cancelBtn);
        wrap.appendChild(btnRow);

        openModal('导入共享种子', wrap);
    }

    async function pickQrImage() {
        try {
            showToast('请选择包含二维码的图片...');
            const res = await dispatch('system.pickQrImage', {});
            if (res && res.text) {
                const text = res.text.trim();
                if (!text) {
                    showToast('未能从图片中识别到二维码内容', true);
                    return;
                }
                // Never auto-navigate arbitrary decoded URLs or import credentials
                // Preview and insert into share-url-input for user share parsing
                $('share-url-input').value = text;
                const navDownload = document.querySelector('.app-nav [data-target="tab-download"]');
                if (navDownload) navDownload.click();
                const modeShare = $('mode-share');
                if (modeShare) modeShare.click();

                showToast('二维码内容已填入分享输入框');
                triggerShareParse(text, '');
            } else {
                showToast('未能识别二维码', true);
            }
        } catch (err) {
            showToast(err.message || '识别二维码图片失败', true);
        }
    }

    async function triggerShareParse(text, passcode = '') {
        if (!text) return;
        try {
            showToast('正在解析分享链接...');
            const res = await dispatch('share.parse', { text, passcode });
            if (res && res.files) {
                appState.files = res.files;
                appState.path = '';
            }
            selectedFileIds.clear();
            renderExplorer();
            showToast('解析成功，请选择要下载的文件');
        } catch (err) {
            showToast(err.message || '解析失败，请检查链接或提取码', true);
        }
    }

    // ==========================================
    // 4. State Rendering & Synchronization
    // ==========================================
    async function loadState(showIndicator = false) {
        if (!isBridgeAvailable || isStatePolling) return;
        isStatePolling = true;
        try {
            const [stateRes, torrentRes] = await Promise.allSettled([
                dispatch('getState'),
                dispatch('torrent.list')
            ]);

            if (stateRes.status === 'fulfilled' && stateRes.value) {
                handleStateUpdate(stateRes.value);
            } else if (stateRes.status === 'rejected' && showIndicator) {
                showToast(stateRes.reason?.message || '获取状态失败', true);
            }

            if (torrentRes.status === 'fulfilled' && Array.isArray(torrentRes.value)) {
                appState.torrentTasks = torrentRes.value;
                renderTasks();
            }
        } finally {
            isStatePolling = false;
        }
    }

    async function loadStateSilently() {
        if (!isBridgeAvailable || isStatePolling) return;
        isStatePolling = true;
        try {
            const [stateRes, torrentRes] = await Promise.allSettled([
                dispatch('getState'),
                dispatch('torrent.list')
            ]);

            if (stateRes.status === 'fulfilled' && stateRes.value) {
                handleStateUpdate(stateRes.value);
            }
            if (torrentRes.status === 'fulfilled' && Array.isArray(torrentRes.value)) {
                appState.torrentTasks = torrentRes.value;
                renderTasks();
            }
        } catch (e) {
            // silent ignore on periodic poll
        } finally {
            isStatePolling = false;
        }
    }

    function handleStateUpdate(data) {
        if ((data.platform !== undefined && data.platform !== appState.platform) || data.activeAccount !== appState.activeAccount || (data.path || '') !== appState.path || (data.viewToken !== undefined && data.viewToken !== appState.viewToken)) selectedFileIds.clear();
        appState.platforms = data.platforms || [];
        appState.platform = data.platform !== undefined ? data.platform : appState.platform;
        appState.accounts = data.accounts || [];
        appState.activeAccount = data.activeAccount;
        appState.tasks = data.tasks || [];
        appState.settings = data.settings || appState.settings;
        appState.files = data.files || [];
        appState.path = data.path || '';
        appState.shareMode = !!data.shareMode;
        appState.viewToken = data.viewToken || '';
        appState.exportFolder = data.exportFolder || appState.exportFolder;
        appState.exports = data.exports || appState.exports;
        appState.systemDark = data.systemDark !== undefined ? data.systemDark : appState.systemDark;
        appState.overlayAllowed = data.overlayAllowed !== undefined ? data.overlayAllowed : appState.overlayAllowed;
        appState.logs = data.logs || [];
        appState.downloadDirectory = data.downloadDirectory || '';
        appState.version = data.version || appState.version;

        // Apply theme from settings
        if (appState.settings && appState.settings.theme) {
            applyTheme(appState.settings.theme);
            updateThemeChoiceUi(appState.settings.theme);
        }

        renderHeader();
        renderPlatforms();
        const explorerKey = JSON.stringify([appState.platform, appState.path, appState.files, appState.shareMode, appState.viewToken]);
        if (explorerKey !== lastExplorerRender) { lastExplorerRender = explorerKey; renderExplorer(); }
        renderTasks();
        renderAccounts();
        renderSettings();
    }

    function renderHeader() {
        const activeAcc = appState.accounts ? appState.accounts.find(a => a.id === appState.activeAccount) : null;
        const nameEl = $('header-account-name');
        const pill = $('active-account-pill');

        if (activeAcc) {
            nameEl.textContent = activeAcc.name || '已登录';
            pill.classList.add('logged-in');
        } else {
            nameEl.textContent = '未登录';
            pill.classList.remove('logged-in');
        }
    }

    function renderPlatforms() {
        const renderKey = JSON.stringify([appState.platform, appState.platforms]);
        if (renderKey === lastPlatformRender) return;
        lastPlatformRender = renderKey;
        const container = $('platform-chips');
        container.replaceChildren();

        const platforms = appState.platforms || [];
        const currentP = platforms.find(p => p.id === appState.platform) || platforms[0];

        platforms.forEach(p => {
            const btn = document.createElement('button');
            btn.className = 'chip-btn' + (p.id === appState.platform ? ' active' : '');
            btn.setAttribute('role', 'radio');
            btn.setAttribute('aria-checked', p.id === appState.platform ? 'true' : 'false');

            const span = document.createElement('span');
            span.textContent = p.name;
            btn.appendChild(span);

            btn.addEventListener('click', async () => {
                if (p.id === appState.platform) return;
                try {
                    const newState = await dispatch('platform.select', { platform: p.id });
                    handleStateUpdate(newState);
                    selectedFileIds.clear();
                    showToast('已切换至 ' + p.name);
                } catch (err) {
                    showToast(err.message || '切换平台失败', true);
                }
            });
            container.appendChild(btn);
        });
        requestAnimationFrame(() => container.querySelector('.active')?.scrollIntoView({ block: 'nearest', inline: 'nearest' }));

        // Platform capability gating
        if (currentP) {
            // Mode visibility in Download tab
            const modeDriveBtn = $('mode-drive');
            const modeShareBtn = $('mode-share');
            if (modeDriveBtn) {
                const canDrive = currentP.drive !== false;
                modeDriveBtn.classList.toggle('hidden', !canDrive);
                if (!canDrive && currentMode === 'drive') {
                    if (modeShareBtn) modeShareBtn.click();
                }
            }
            if (modeShareBtn) {
                modeShareBtn.classList.toggle('hidden', currentP.share === false);
                if (currentP.share === false && currentMode === 'share' && currentP.drive && modeDriveBtn) modeDriveBtn.click();
            }

            // Anonymous / Share-only platform banner (e.g. Lanzou)
            const isAnonymousPlatform = (!currentP.drive && !currentP.qr && !currentP.password);
            const anonBanner = $('anonymous-platform-banner');
            if (anonBanner) {
                anonBanner.classList.toggle('hidden', !isAnonymousPlatform);
            }

            // Accounts tab capability gating
            $('account-platform-tag').textContent = currentP.name;

            const anonNotice = $('login-anonymous-notice');
            const loginModeBar = $('login-mode-bar');
            if (isAnonymousPlatform) {
                if (anonNotice) anonNotice.classList.remove('hidden');
                if (loginModeBar) loginModeBar.classList.add('hidden');
                hideAllLoginSubPanels();
            } else {
                if (anonNotice) anonNotice.classList.add('hidden');
                if (loginModeBar) loginModeBar.classList.remove('hidden');

                // Toggle tabs based on capabilities
                const tabQr = $('login-tab-qr');
                const tabPass = $('login-tab-pass');
                const tabCookie = $('login-tab-cookie');
                const tabWebdav = $('login-tab-webdav');
                const tabJson = $('login-tab-json');

                if (tabQr) tabQr.classList.toggle('hidden', !currentP.qr);
                if (tabPass) tabPass.classList.toggle('hidden', !currentP.password);
                if (tabCookie) tabCookie.classList.toggle('hidden', !currentP.cookie);
                if (tabWebdav) tabWebdav.classList.toggle('hidden', currentP.id !== 2);
                if (tabJson) tabJson.classList.toggle('hidden', !currentP.native);

                // If currently active login tab is now hidden, select first visible tab
                const activeTab = document.querySelector('.login-mode-pills .pill-btn.active');
                if (activeTab && activeTab.classList.contains('hidden')) {
                    const firstVisible = document.querySelector('.login-mode-pills .pill-btn:not(.hidden)');
                    if (firstVisible) firstVisible.click();
                } else if (activeTab) activeTab.click();
            }

            // Baidu speed note & membership query & capacity
            const activeAcc = appState.accounts ? appState.accounts.find(a => a.id === appState.activeAccount) : null;
            const btnCapacity = $('btn-account-capacity');
            if (btnCapacity) {
                btnCapacity.classList.toggle('hidden', !activeAcc);
            }

            if (currentP.id === 1) { // Baidu
                $('speed-note').classList.remove('hidden');
                $('btn-query-membership').classList.remove('hidden');
            } else {
                $('speed-note').classList.add('hidden');
                $('btn-query-membership').classList.add('hidden');
            }
        }
    }

    function hideAllLoginSubPanels() {
        $('login-panel-qr').classList.add('hidden');
        $('login-panel-pass').classList.add('hidden');
        $('login-panel-cookie').classList.add('hidden');
        $('login-panel-webdav').classList.add('hidden');
        $('login-panel-json').classList.add('hidden');
    }

    function renderExplorer() {
        const pathEl = $('current-path-text');
        pathEl.textContent = appState.path ? appState.path : '根目录';

        const listEl = $('file-list');
        const emptyEl = $('file-empty');
        const loadingEl = $('file-loading');
        loadingEl.classList.add('hidden');

        listEl.replaceChildren();

        const filterTerm = ($('file-filter-input').value || '').trim().toLowerCase();
        const files = (appState.files || []).filter(f => {
            if (!filterTerm) return true;
            return (f.name || '').toLowerCase().includes(filterTerm);
        });

        if (files.length === 0) {
            emptyEl.classList.remove('hidden');
        } else {
            emptyEl.classList.add('hidden');
            files.forEach(f => {
                const li = document.createElement('li');
                li.className = 'file-item' + (selectedFileIds.has(f.id) ? ' selected' : '');

                // Checkbox for ALL items (both files and directories support recursive download)
                const chk = document.createElement('input');
                chk.type = 'checkbox';
                chk.className = 'file-checkbox';
                chk.checked = selectedFileIds.has(f.id);
                chk.addEventListener('click', (e) => {
                    e.stopPropagation();
                    toggleFileSelection(f.id);
                });
                li.appendChild(chk);

                // Icon
                const iconSvg = f.directory
                    ? createSvgElement('M3 6.5 A1.5 1.5 0 0 1 4.5 5 H9 L11 7.5 H19.5 A1.5 1.5 0 0 1 21 9 V18 A1.5 1.5 0 0 1 19.5 19.5 H4.5 A1.5 1.5 0 0 1 3 18 Z', 18, 18)
                    : createSvgElement('M6 3.5 H14 L18.5 8 V20.5 H6 Z M14 3.5 V8 H18.5', 18, 18);
                iconSvg.classList.add('file-icon');
                if (f.directory) iconSvg.classList.add('folder');
                li.appendChild(iconSvg);

                // Name (Safely rendered with textContent)
                const nameSpan = document.createElement('span');
                nameSpan.className = 'file-name';
                nameSpan.textContent = f.name || '未命名';
                li.appendChild(nameSpan);

                // Size / Folder tag
                const sizeSpan = document.createElement('span');
                sizeSpan.className = 'file-size';
                sizeSpan.textContent = f.directory ? '文件夹' : formatBytes(f.size);
                li.appendChild(sizeSpan);

                // Click event: folder navigates; file toggles selection
                li.addEventListener('click', async () => {
                    if (f.directory) {
                        try {
                            loadingEl.classList.remove('hidden');
                            emptyEl.classList.add('hidden');
                            listEl.replaceChildren();
                            const res = await dispatch('browse', { id: f.id });
                            if (res && res.files) {
                                appState.files = res.files;
                                appState.path = res.path || f.name;
                                if (res.viewToken) appState.viewToken = res.viewToken;
                            }
                            selectedFileIds.clear();
                            renderExplorer();
                        } catch (err) {
                            showToast(err.message || '进入文件夹失败', true);
                            loadStateSilently();
                        }
                    } else {
                        toggleFileSelection(f.id);
                    }
                });

                listEl.appendChild(li);
            });
        }

        updateSelectionSummary();
    }

    function toggleFileSelection(fileId) {
        if (selectedFileIds.has(fileId)) {
            selectedFileIds.delete(fileId);
        } else {
            selectedFileIds.add(fileId);
        }
        renderExplorer();
    }

    function updateSelectionSummary() {
        const count = selectedFileIds.size;
        const textEl = $('selected-summary-text');
        const downloadBtn = $('btn-download-selected');
        const exportBtn = $('btn-export-links');

        if (count === 0) {
            textEl.textContent = '未选择文件';
            downloadBtn.disabled = true;
            exportBtn.disabled = true;
        } else {
            textEl.textContent = '已选择 ' + count + ' 项';
            downloadBtn.disabled = false;
            exportBtn.disabled = false;
        }

        // Update Quark Cloud Ops buttons visibility & state immediately on selection change
        const currentP = (appState.platforms || []).find(p => p.id === appState.platform);
        const hasCloudOps = !!(currentP && currentP.cloudOps);
        const isShareMode = appState.shareMode;

        const mkdirBtn = $('btn-cloud-mkdir');
        const transferBtn = $('btn-cloud-transfer');
        const deleteBtn = $('btn-cloud-delete');

        if (mkdirBtn) {
            mkdirBtn.classList.toggle('hidden', !hasCloudOps);
        }
        if (transferBtn) {
            // Transfer only available in shareMode
            transferBtn.classList.toggle('hidden', !(hasCloudOps && isShareMode));
            transferBtn.disabled = count === 0;
        }
        if (deleteBtn) {
            // Delete only available in drive mode (not shareMode)
            deleteBtn.classList.toggle('hidden', !(hasCloudOps && !isShareMode));
            deleteBtn.disabled = count === 0;
        }
    }

    function renderTasks() {
        const cloudTasks = appState.tasks || [];
        const btTasks = appState.torrentTasks || [];
        const listEl = $('task-list');
        const emptyEl = $('tasks-empty');
        const badgeEl = $('nav-task-badge');

        let totalSpeed = 0;
        let activeCount = 0;

        cloudTasks.forEach(t => {
            if (t.state === 'Downloading') {
                totalSpeed += (t.speed || 0);
                activeCount++;
            }
        });

        btTasks.forEach(t => {
            if (t.state === 'Downloading') {
                totalSpeed += (t.speed || 0);
                activeCount++;
            }
        });

        const totalTasksCount = cloudTasks.length + btTasks.length;

        $('metric-total-speed').textContent = formatSpeed(totalSpeed);
        $('metric-active-count').textContent = activeCount.toString();
        $('metric-total-count').textContent = totalTasksCount.toString();

        if (activeCount > 0) {
            badgeEl.textContent = activeCount > 99 ? '99+' : activeCount.toString();
            badgeEl.classList.remove('hidden');
        } else {
            badgeEl.classList.add('hidden');
        }

        if (totalTasksCount === 0) {
            emptyEl.classList.remove('hidden');
            taskCardElements.clear();
            listEl.replaceChildren();
            return;
        }
        emptyEl.classList.add('hidden');

        // Track current task IDs to remove stale elements
        const currentTaskIds = new Set();
        const allItems = [];

        cloudTasks.forEach(t => {
            currentTaskIds.add(t.id);
            allItems.push({ task: t, isBt: false });
        });
        btTasks.forEach(t => {
            currentTaskIds.add(t.id);
            allItems.push({ task: t, isBt: true });
        });

        // Remove cards that no longer exist
        for (const [id, entry] of taskCardElements.entries()) {
            if (!currentTaskIds.has(id)) {
                entry.card.remove();
                taskCardElements.delete(id);
            }
        }

        // Reconcile and update cards without full DOM teardown
        allItems.forEach(({ task, isBt }) => {
            let entry = taskCardElements.get(task.id);
            if (!entry || entry.isBt !== isBt) {
                const card = createTaskCard(task, isBt);
                taskCardElements.set(task.id, card._entry);
                listEl.appendChild(card);
            } else {
                entry.update(task);
            }
        });
    }

    function createTaskCard(initialTask, isBt = false) {
        const card = document.createElement('li');
        card.className = 'task-card';

        // Header
        const header = document.createElement('div');
        header.className = 'task-header';

        const titleWrap = document.createElement('div');
        titleWrap.className = 'task-title-wrap';

        const dot = document.createElement('span');
        dot.className = 'task-dot ' + (initialTask.state || '').toLowerCase();
        titleWrap.appendChild(dot);

        let btTag = null;
        if (isBt) {
            btTag = document.createElement('span');
            btTag.className = 'platform-badge';
            btTag.style.marginRight = '6px';
            titleWrap.appendChild(btTag);
        }

        const nameEl = document.createElement('span');
        nameEl.className = 'task-name';
        titleWrap.appendChild(nameEl);
        header.appendChild(titleWrap);

        const statusPill = document.createElement('span');
        statusPill.className = 'task-status-pill ' + (initialTask.state || '').toLowerCase();
        header.appendChild(statusPill);

        card.appendChild(header);

        // Progress bar
        const barWrap = document.createElement('div');
        barWrap.className = 'task-progress-bar-wrap';
        const bar = document.createElement('div');
        bar.className = 'task-progress-bar';
        barWrap.appendChild(bar);
        card.appendChild(barWrap);

        // Meta Row
        const metaRow = document.createElement('div');
        metaRow.className = 'task-meta-row';

        const speedEl = document.createElement('span');
        speedEl.className = 'task-speed';
        metaRow.appendChild(speedEl);

        const progressEl = document.createElement('span');
        metaRow.appendChild(progressEl);

        card.appendChild(metaRow);

        // Error message banner if any
        const errBox = document.createElement('div');
        errBox.className = 'task-error-text hidden';
        card.appendChild(errBox);

        // Worker section for BT
        let workerSection = null;
        let workerList = null;
        let workerToggleText = null;
        if (isBt) {
            workerSection = document.createElement('div');
            workerSection.className = 'workers-section hidden';

            const workerHeader = document.createElement('div');
            workerHeader.className = 'workers-header';

            const workerHeaderTitle = document.createElement('span');
            workerHeaderTitle.textContent = '拆分子任务';
            workerHeader.appendChild(workerHeaderTitle);

            workerToggleText = document.createElement('button');
            workerToggleText.className = 'workers-toggle-btn';
            workerHeader.appendChild(workerToggleText);

            workerHeader.addEventListener('click', () => {
                if (expandedJobIds.has(initialTask.id)) {
                    expandedJobIds.delete(initialTask.id);
                } else {
                    expandedJobIds.add(initialTask.id);
                }
                updateWorkersUi(card._currentTask);
            });

            workerList = document.createElement('ul');
            workerList.className = 'workers-list';

            workerSection.appendChild(workerHeader);
            workerSection.appendChild(workerList);
            card.appendChild(workerSection);
        }

        // Action buttons
        const actionsRow = document.createElement('div');
        actionsRow.className = 'task-actions-row';
        card.appendChild(actionsRow);

        let workersKey = '';
        function paintWorker(row, w) {
            const done = w.done || 0, total = w.total || 0;
            const pct = total > 0 ? Math.min(100, Math.round(done / total * 100)) : (w.state === 'Completed' ? 100 : 0);
            row.querySelector('.worker-progress').textContent = `${formatBytes(done)} / ${formatBytes(total)} (${pct}%) · ${w.peers || 0} 节点`;
            row.querySelector('.worker-speed').textContent = w.state === 'Downloading' ? formatSpeed(w.speed) : getTaskStateName(w.state);
        }
        function updateWorkersUi(t) {
            if (!isBt || !workerSection) return;
            const workers = t.workers || [];
            if (workers.length <= 1) {
                workerSection.classList.add('hidden');
                return;
            }

            workerSection.classList.remove('hidden');
            const isExpanded = expandedJobIds.has(t.id);
            workerToggleText.textContent = isExpanded ? '收起详情 ▴' : `展开详情 (${workers.length}个) ▾`;

            if (!isExpanded) {
                workerList.classList.add('hidden');
                return;
            }

            workerList.classList.remove('hidden');
            const isParentFinished = t.state === 'Completed' || t.state === 'Merging';
            const nextWorkersKey = JSON.stringify([isParentFinished, workers.map(w => [w.index, w.state])]);
            if (workersKey === nextWorkersKey && workerList.children.length === workers.length) {
                workers.forEach((w, i) => paintWorker(workerList.children[i], w));
                return;
            }
            workersKey = nextWorkersKey;
            workerList.replaceChildren();

            workers.forEach(w => {
                const li = document.createElement('li');
                li.className = 'worker-item';

                const info = document.createElement('div');
                info.className = 'worker-info';

                const badge = document.createElement('span');
                badge.className = 'worker-badge';
                badge.textContent = '#' + ((w.index ?? 0) + 1);
                info.appendChild(badge);

                const wDone = w.done || 0;
                const wTotal = w.total || 0;
                const wPct = wTotal > 0 ? Math.min(100, Math.round((wDone / wTotal) * 100)) : (w.state === 'Completed' ? 100 : 0);

                const prog = document.createElement('span');
                prog.className = 'worker-progress';
                prog.textContent = `${formatBytes(wDone)} / ${formatBytes(wTotal)} (${wPct}%) · ${w.peers || 0} 节点`;
                info.appendChild(prog);

                const spd = document.createElement('span');
                spd.className = 'worker-speed';
                spd.textContent = w.state === 'Downloading' ? formatSpeed(w.speed) : (w.state || '');
                info.appendChild(spd);

                li.appendChild(info);
                paintWorker(li, w);

                // Per-worker actions: hide if parent is completed or merging
                if (!isParentFinished) {
                    const actWrap = document.createElement('div');
                    actWrap.className = 'worker-actions';

                    if (w.state === 'Downloading') {
                        const pBtn = document.createElement('button');
                        pBtn.className = 'btn btn-secondary btn-sm';
                        pBtn.style.padding = '2px 6px';
                        pBtn.style.fontSize = '10px';
                        pBtn.textContent = '暂停';
                        pBtn.addEventListener('click', () => workerAction('torrent.worker.pause', t.id, w.index));
                        actWrap.appendChild(pBtn);
                    } else if (w.state === 'Paused' || w.state === 'Failed') {
                        const rBtn = document.createElement('button');
                        rBtn.className = 'btn btn-primary btn-sm';
                        rBtn.style.padding = '2px 6px';
                        rBtn.style.fontSize = '10px';
                        rBtn.textContent = '继续';
                        rBtn.addEventListener('click', () => workerAction('torrent.worker.resume', t.id, w.index));
                        actWrap.appendChild(rBtn);
                    }
                    li.appendChild(actWrap);
                }

                workerList.appendChild(li);
            });
        }

        let actionsKey = '';
        function update(t) {
            card._currentTask = t;
            const exportRecord = appState.exports?.[t.id];

            // Dot & Status
            dot.className = 'task-dot ' + (t.state || '').toLowerCase();
            statusPill.className = 'task-status-pill ' + (t.state || '').toLowerCase();
            statusPill.textContent = getTaskStateName(t.state);

            if (isBt && btTag) {
                btTag.textContent = t.split && t.split > 1 ? `BT 拆分 (${t.split}片)` : 'BT 任务';
            }

            nameEl.textContent = t.name || (isBt ? '正在解析种子元数据' : '下载任务');

            // Progress
            const total = t.total || 0;
            const done = t.done || 0;
            const percent = total > 0 ? Math.min(100, Math.round((done / total) * 100)) : (t.state === 'Completed' ? 100 : 0);
            bar.style.width = percent + '%';

            speedEl.textContent = t.state === 'Downloading' ? formatSpeed(t.speed) : (t.state === 'Completed' ? '已完成' : '');
            progressEl.textContent = formatBytes(done) + ' / ' + formatBytes(total) + ' (' + percent + '%)';

            // Error
            const errorText = t.error || (exportRecord?.state === 'Failed' ? exportRecord.error : '');
            if (errorText) {
                errBox.textContent = errorText;
                errBox.classList.remove('hidden');
            } else {
                errBox.classList.add('hidden');
            }

            // Workers
            if (isBt) {
                updateWorkersUi(t);
            }

            // Actions row rendering
            const nextActionsKey = JSON.stringify([t.state, exportRecord?.state]);
            if (actionsKey === nextActionsKey) return;
            actionsKey = nextActionsKey;
            actionsRow.replaceChildren();

            if (isBt) {
                // BT Actions
                if (t.state === 'Downloading') {
                    const pauseBtn = document.createElement('button');
                    pauseBtn.className = 'btn btn-secondary btn-sm';
                    pauseBtn.textContent = '暂停';
                    pauseBtn.addEventListener('click', () => btAction('torrent.pause', t.id));
                    actionsRow.appendChild(pauseBtn);
                } else if (t.state === 'Paused' || t.state === 'Failed') {
                    const resumeBtn = document.createElement('button');
                    resumeBtn.className = 'btn btn-primary btn-sm';
                    resumeBtn.textContent = '继续';
                    resumeBtn.addEventListener('click', () => btAction('torrent.resume', t.id));
                    actionsRow.appendChild(resumeBtn);
                }

                if (t.state === 'Completed') {
                    const openBtn = document.createElement('button');
                    openBtn.className = 'btn btn-ghost btn-sm';
                    openBtn.textContent = '打开/播放';
                    openBtn.addEventListener('click', async () => {
                        try {
                            await torrentFileAction('system.open', t.id);
                        } catch (err) {
                            showToast(err.message || '打开失败', true);
                        }
                    });
                    actionsRow.appendChild(openBtn);

                    const shareBtn = document.createElement('button');
                    shareBtn.className = 'btn btn-ghost btn-sm';
                    shareBtn.textContent = '导出分享';
                    shareBtn.addEventListener('click', async () => {
                        try {
                            await torrentFileAction('system.share', t.id);
                        } catch (err) {
                            showToast(err.message || '分享失败', true);
                        }
                    });
                    actionsRow.appendChild(shareBtn);

                    const saveAsBtn = document.createElement('button');
                    saveAsBtn.className = 'btn btn-ghost btn-sm';
                    saveAsBtn.textContent = '另存为';
                    saveAsBtn.addEventListener('click', async () => {
                        try {
                            await torrentSaveAsAction(t.id);
                        } catch (err) {
                            showToast(err.message || '另存为失败', true);
                        }
                    });
                    actionsRow.appendChild(saveAsBtn);

                    const exportBtn = document.createElement('button');
                    exportBtn.className = 'btn btn-secondary btn-sm';
                    if (exportRecord && exportRecord.state === 'Copying') {
                        exportBtn.textContent = '正在导出...';
                        exportBtn.disabled = true;
                    } else if (exportRecord && exportRecord.state === 'Completed') {
                        exportBtn.textContent = '已导出到公共目录';
                    } else {
                        exportBtn.textContent = '导出到所选目录';
                    }
                    exportBtn.addEventListener('click', async () => {
                        try {
                            showToast('正在复制完整任务到公共目录...');
                            const res = await dispatch('system.exportTask', { torrentId: t.id });
                            showToast(res.message || '导出成功');
                            loadStateSilently();
                        } catch (err) {
                            showToast(err.message || '导出失败，请检查目录权限', true);
                        }
                    });
                    actionsRow.appendChild(exportBtn);
                }

                const removeBtn = document.createElement('button');
                removeBtn.className = 'btn btn-danger-ghost btn-sm';
                removeBtn.textContent = t.state === 'Completed' ? '移除' : '取消';
                removeBtn.addEventListener('click', () => btAction('torrent.remove', t.id));
                actionsRow.appendChild(removeBtn);
            } else {
                // Cloud Task Actions
                if (t.state === 'Downloading' || t.state === 'Queued') {
                    const pauseBtn = document.createElement('button');
                    pauseBtn.className = 'btn btn-secondary btn-sm';
                    pauseBtn.textContent = '暂停';
                    pauseBtn.addEventListener('click', () => taskAction('task.pause', t.id));
                    actionsRow.appendChild(pauseBtn);
                } else if (t.state === 'Paused' || t.state === 'Failed') {
                    const resumeBtn = document.createElement('button');
                    resumeBtn.className = 'btn btn-primary btn-sm';
                    resumeBtn.textContent = '继续';
                    resumeBtn.addEventListener('click', () => taskAction('task.resume', t.id));
                    actionsRow.appendChild(resumeBtn);
                }

                if (t.state === 'Completed') {
                    const openBtn = document.createElement('button');
                    openBtn.className = 'btn btn-ghost btn-sm';
                    openBtn.textContent = '打开/播放';
                    openBtn.addEventListener('click', async () => {
                        try {
                            await dispatch('system.open', { id: t.id });
                        } catch (err) {
                            showToast(err.message || '打开失败', true);
                        }
                    });
                    actionsRow.appendChild(openBtn);

                    const shareBtn = document.createElement('button');
                    shareBtn.className = 'btn btn-ghost btn-sm';
                    shareBtn.textContent = '导出分享';
                    shareBtn.addEventListener('click', async () => {
                        try {
                            await dispatch('system.share', { id: t.id });
                        } catch (err) {
                            showToast(err.message || '分享失败', true);
                        }
                    });
                    actionsRow.appendChild(shareBtn);

                    const saveAsBtn = document.createElement('button');
                    saveAsBtn.className = 'btn btn-ghost btn-sm';
                    saveAsBtn.textContent = '另存为';
                    saveAsBtn.addEventListener('click', async () => {
                        try {
                            showToast('正在调起系统存储另存为...');
                            await dispatch('system.saveFile', { id: t.id });
                            showToast('文件已另存为');
                        } catch (err) {
                            showToast(err.message || '另存为失败', true);
                        }
                    });
                    actionsRow.appendChild(saveAsBtn);

                    const exportBtn = document.createElement('button');
                    exportBtn.className = 'btn btn-secondary btn-sm';
                    if (exportRecord && exportRecord.state === 'Copying') {
                        exportBtn.textContent = '正在导出...';
                        exportBtn.disabled = true;
                    } else if (exportRecord && exportRecord.state === 'Completed') {
                        exportBtn.textContent = '已导出到公共目录';
                    } else {
                        exportBtn.textContent = '导出到所选目录';
                    }
                    exportBtn.addEventListener('click', async () => {
                        try {
                            showToast('正在复制完整任务到公共目录...');
                            const res = await dispatch('system.exportTask', { id: t.id });
                            showToast(res.message || '导出成功');
                            loadStateSilently();
                        } catch (err) {
                            showToast(err.message || '导出失败，请检查目录权限', true);
                        }
                    });
                    actionsRow.appendChild(exportBtn);
                }

                // Delete local file button (only for finished/paused/failed tasks)
                if (t.state === 'Completed' || t.state === 'Cancelled' || t.state === 'Failed' || t.state === 'Paused') {
                    const deleteLocalBtn = document.createElement('button');
                    deleteLocalBtn.className = 'btn btn-danger-ghost btn-sm';
                    deleteLocalBtn.textContent = '删除文件';
                    deleteLocalBtn.title = '删除手机本地下载的文件并移除记录';
                    deleteLocalBtn.addEventListener('click', () => promptDeleteLocalTask(card._currentTask || t));
                    actionsRow.appendChild(deleteLocalBtn);
                }

                const removeBtn = document.createElement('button');
                removeBtn.className = 'btn btn-ghost btn-sm';
                removeBtn.textContent = t.state === 'Completed' || t.state === 'Cancelled' ? '移除记录' : '取消任务';
                removeBtn.title = '仅移除列表记录，保留本地下载文件';
                removeBtn.addEventListener('click', () => {
                    const cur = card._currentTask || t;
                    if (cur.state === 'Completed' || cur.state === 'Cancelled') {
                        taskAction('task.remove', cur.id);
                    } else {
                        taskAction('task.cancel', cur.id);
                    }
                });
                actionsRow.appendChild(removeBtn);
            }
        }

        update(initialTask);
        card._entry = { card, update, isBt };
        return card;
    }

    function promptDeleteLocalTask(task) {
        const wrap = document.createElement('div');
        const p1 = document.createElement('p');
        p1.textContent = `确认要彻底删除本地下载文件吗？`;
        p1.style.fontWeight = '600';
        p1.style.marginBottom = '6px';
        p1.style.color = 'var(--text-primary)';
        wrap.appendChild(p1);

        const p2 = document.createElement('p');
        p2.textContent = `目标文件：${task.name || '未知文件'}`;
        p2.style.marginBottom = '8px';
        p2.style.fontFamily = 'var(--font-mono)';
        p2.style.fontSize = '12px';
        p2.style.color = 'var(--text-secondary)';
        wrap.appendChild(p2);

        const pWarn = document.createElement('p');
        pWarn.textContent = '此操作将仅停止并从本地存储删除该任务文件，且无法撤销（普通移除则保留本地文件）。';
        pWarn.style.fontSize = '11.5px';
        pWarn.style.color = 'var(--danger)';
        pWarn.style.marginBottom = '16px';
        wrap.appendChild(pWarn);

        const btnRow = document.createElement('div');
        btnRow.style.display = 'flex';
        btnRow.style.gap = '8px';

        const confirmBtn = document.createElement('button');
        confirmBtn.className = 'btn btn-danger-ghost flex-1';
        confirmBtn.style.background = 'var(--danger)';
        confirmBtn.style.color = '#fff';
        confirmBtn.textContent = '确认彻底删除';
        confirmBtn.addEventListener('click', async () => {
            closeModal();
            try {
                showToast('正在删除本地文件...');
                const res = await dispatch('task.deleteLocal', { id: task.id, confirmed: true });
                handleStateUpdate(res);
                showToast('本地文件已删除');
            } catch (err) {
                showToast(err.message || '删除本地文件失败', true);
            }
        });

        const cancelBtn = document.createElement('button');
        cancelBtn.className = 'btn btn-ghost flex-1';
        cancelBtn.textContent = '取消';
        cancelBtn.addEventListener('click', closeModal);

        btnRow.appendChild(confirmBtn);
        btnRow.appendChild(cancelBtn);
        wrap.appendChild(btnRow);

        openModal('确认删除本地文件', wrap);
    }

    function getTaskStateName(state) {
        switch (state) {
            case 'Downloading': return '下载中';
            case 'Queued': return '排队中';
            case 'Paused': return '已暂停';
            case 'Completed': return '已完成';
            case 'Failed': return '下载失败';
            case 'Cancelling': return '正在取消';
            case 'Cancelled': return '已取消';
            case 'Merging': return '校验合并中';
            default: return state || '就绪';
        }
    }

    async function taskAction(action, id) {
        try {
            const newState = await dispatch(action, { id });
            handleStateUpdate(newState);
        } catch (err) {
            showToast(err.message || '操作失败', true);
        }
    }

    async function btAction(action, id) {
        try {
            const list = await dispatch(action, { id });
            if (Array.isArray(list)) {
                appState.torrentTasks = list;
                renderTasks();
            } else {
                loadStateSilently();
            }
        } catch (err) {
            showToast(err.message || 'BT 操作失败', true);
        }
    }

    async function workerAction(action, id, index) {
        try {
            const list = await dispatch(action, { id, index });
            if (Array.isArray(list)) {
                appState.torrentTasks = list;
                renderTasks();
            } else {
                loadStateSilently();
            }
        } catch (err) {
            showToast(err.message || '子任务操作失败', true);
        }
    }

    async function torrentSaveAsAction(id) {
        const result = await dispatch('torrent.files', { id });
        const files = result.files || [];
        if (!files.length) throw new Error('没有可导出的结果文件');
        const save = file => dispatch('system.saveFile', { torrentId: id, file: file.id });
        if (files.length === 1) return save(files[0]);
        const list = document.createElement('div');
        for (const file of files) {
            const button = document.createElement('button');
            button.className = 'btn btn-secondary btn-block mt-8';
            button.textContent = file.name + ' · ' + formatBytes(file.size);
            button.addEventListener('click', () => {
                closeModal();
                save(file).then(() => showToast('文件已另存为')).catch(error => showToast(error.message, true));
            });
            list.appendChild(button);
        }
        openModal('选择要另存为的文件', list);
    }

    async function torrentFileAction(action, id) {
        const result = await dispatch('torrent.files', { id });
        const files = result.files || [];
        if (!files.length) throw new Error('没有可导出的结果文件');
        const open = file => dispatch(action, { torrentId: id, file: file.id });
        if (files.length === 1) return open(files[0]);
        const list = document.createElement('div');
        for (const file of files) {
            const button = document.createElement('button');
            button.className = 'btn btn-secondary btn-block mt-8';
            button.textContent = file.name + ' · ' + formatBytes(file.size);
            button.addEventListener('click', () => {
                closeModal();
                open(file).catch(error => showToast(error.message, true));
            });
            list.appendChild(button);
        }
        openModal(action === 'system.share' ? '选择要导出的文件' : '选择要打开的文件', list);
    }

    function renderAccounts() {
        const activeAcc = appState.accounts ? appState.accounts.find(a => a.id === appState.activeAccount) : null;
        const avatarEl = $('account-avatar');
        const nameEl = $('account-name-text');
        const hintEl = $('account-hint-text');

        if (activeAcc) {
            const initial = (activeAcc.name || 'Z').trim().charAt(0).toUpperCase();
            avatarEl.textContent = initial;
            nameEl.textContent = activeAcc.name;
            hintEl.textContent = '已保存账号 · 可浏览此网盘';
        } else {
            avatarEl.textContent = '?';
            nameEl.textContent = '未登录任何账号';
            hintEl.textContent = '登录后可浏览网盘文件；直链和蓝奏云分享可直接使用';
        }

        // Saved accounts list
        const listEl = $('saved-accounts-list');
        listEl.replaceChildren();

        const accounts = appState.accounts || [];
        $('accounts-count').textContent = accounts.length.toString();

        if (accounts.length === 0) {
            const li = document.createElement('li');
            li.className = 'empty-hint';
            li.textContent = '暂无已保存的网盘账号';
            listEl.appendChild(li);
        } else {
            accounts.forEach(a => {
                const li = document.createElement('li');
                li.className = 'account-item' + (a.id === appState.activeAccount ? ' active' : '');

                const meta = document.createElement('div');
                meta.className = 'account-item-meta';

                const name = document.createElement('span');
                name.style.fontWeight = '600';
                name.textContent = a.name || '未命名账号';
                meta.appendChild(name);

                const platformName = (appState.platforms.find(p => p.id === a.platform) || {}).name || '';
                if (platformName) {
                    const tag = document.createElement('span');
                    tag.className = 'platform-badge';
                    tag.textContent = platformName;
                    meta.appendChild(tag);
                }
                li.appendChild(meta);

                const actions = document.createElement('div');
                actions.style.display = 'flex';
                actions.style.gap = '6px';

                if (a.id !== appState.activeAccount) {
                    const selectBtn = document.createElement('button');
                    selectBtn.className = 'btn btn-secondary btn-sm';
                    selectBtn.textContent = '切换';
                    selectBtn.addEventListener('click', async () => {
                        try {
                            const res = await dispatch('account.select', { id: a.id });
                            handleStateUpdate(res);
                            showToast('已切换至账号: ' + a.name);
                        } catch (err) {
                            showToast(err.message || '切换账号失败', true);
                        }
                    });
                    actions.appendChild(selectBtn);
                }

                const removeBtn = document.createElement('button');
                removeBtn.className = 'btn btn-danger-ghost btn-sm';
                removeBtn.textContent = '移除';
                removeBtn.addEventListener('click', async () => {
                    try {
                        const res = await dispatch('account.remove', { id: a.id });
                        handleStateUpdate(res);
                        showToast('已移除账号');
                    } catch (err) {
                        showToast(err.message || '移除失败', true);
                    }
                });
                actions.appendChild(removeBtn);

                li.appendChild(actions);
                listEl.appendChild(li);
            });
        }
    }

    function renderSettings() {
        const s = appState.settings || {};
        const exp = appState.exportFolder || {};

        // Avoid overwriting form elements while user is editing them
        if (!isSettingsDirty) {
            const renderKey = JSON.stringify([s.threads, s.concurrent, s.writeMode, s.autoExport, s.floatingWindow, exp.selected, exp.name]);
            if (renderKey !== lastSettingsRender) {
                lastSettingsRender = renderKey;
                if (s.threads) $('setting-threads').value = s.threads.toString();
                if (s.concurrent) $('setting-concurrent').value = s.concurrent.toString();

                const radios = document.getElementsByName('writeMode');
                for (let r of radios) {
                    r.checked = (r.value === (s.writeMode || 'SharedFile'));
                }

                const autoExportChk = $('setting-auto-export');
                if (autoExportChk) autoExportChk.checked = !!s.autoExport;

                const floatingChk = $('setting-floating-window');
                if (floatingChk) floatingChk.checked = !!s.floatingWindow;
            }
        }

        // Export folder UI
        const folderNameEl = $('export-folder-name');
        const clearFolderBtn = $('btn-clear-export-folder');
        if (folderNameEl) {
            if (exp.selected && exp.name) {
                folderNameEl.textContent = `已选择：${exp.name}`;
                folderNameEl.style.color = 'var(--primary)';
                if (clearFolderBtn) clearFolderBtn.classList.remove('hidden');
            } else {
                folderNameEl.textContent = '未选择（仅保存在应用私有存储）';
                folderNameEl.style.color = '';
                if (clearFolderBtn) clearFolderBtn.classList.add('hidden');
            }
        }

        // Render logs
        const logsEl = $('log-console');
        const logs = appState.logs || [];
        logsEl.textContent = logs.length > 0 ? logs.join('\n') : '暂无系统日志';
        logsEl.scrollTop = logsEl.scrollHeight;

        $('runtime-dir-text').textContent = '存储路径: ' + (appState.downloadDirectory || '默认应用存储');
        $('runtime-ver-text').textContent = '版本: ' + (appState.version || '3.0.0-preview.2');
    }

    function updateThemeChoiceUi(theme) {
        const buttons = document.querySelectorAll('.theme-choice-btn');
        buttons.forEach(btn => {
            if (btn.getAttribute('data-theme') === theme) {
                btn.classList.add('active');
            } else {
                btn.classList.remove('active');
            }
        });
    }

    // ==========================================
    // 5. Tab & Action Bindings
    // ==========================================
    function setupTabNavigation() {
        const navItems = document.querySelectorAll('.app-nav .nav-item');
        navItems.forEach(item => {
            item.addEventListener('click', () => {
                const targetId = item.getAttribute('data-target');
                navItems.forEach(n => n.classList.remove('active'));
                item.classList.add('active');

                document.querySelectorAll('.tab-pane').forEach(p => {
                    if (p.id === targetId) {
                        p.classList.add('active');
                    } else {
                        p.classList.remove('active');
                    }
                });
            });
        });

        // Mode switchers in Download tab
        const modeButtons = document.querySelectorAll('.mode-bar .mode-btn');
        modeButtons.forEach(btn => {
            btn.addEventListener('click', async () => {
                const mode = btn.getAttribute('data-mode');
                currentMode = mode;
                modeButtons.forEach(b => {
                    b.classList.remove('active');
                    b.setAttribute('aria-selected', 'false');
                });
                btn.classList.add('active');
                btn.setAttribute('aria-selected', 'true');

                $('panel-share').classList.toggle('hidden', mode !== 'share');
                $('panel-direct').classList.toggle('hidden', mode !== 'direct');
                $('panel-torrent').classList.toggle('hidden', mode !== 'torrent');
                $('panel-explorer').classList.toggle('hidden', mode !== 'share' && mode !== 'drive');

                if (mode === 'drive') {
                    // Navigate to drive root
                    try {
                        const res = await dispatch('browse', { id: '' });
                        if (res && res.files) {
                            appState.files = res.files;
                            appState.path = res.path || '';
                            if (res.viewToken) appState.viewToken = res.viewToken;
                        }
                        selectedFileIds.clear();
                        renderExplorer();
                    } catch (e) {
                        showToast(e.message || '请先在「账号」页登录或添加网盘授权', true);
                    }
                }
            });
        });

        // BT Split Pills
        const splitBtns = document.querySelectorAll('#torrent-split-pills .split-pill-btn');
        splitBtns.forEach(btn => {
            btn.addEventListener('click', () => {
                splitBtns.forEach(b => b.classList.remove('active'));
                btn.classList.add('active');
            });
        });

        // Login sub-tabs in Account tab
        const loginTabs = document.querySelectorAll('.login-mode-pills .pill-btn');
        loginTabs.forEach(tab => {
            tab.addEventListener('click', () => {
                const mode = tab.getAttribute('data-mode');
                currentLoginMode = mode;
                loginTabs.forEach(t => t.classList.remove('active'));
                tab.classList.add('active');

                $('login-panel-qr').classList.toggle('hidden', mode !== 'qr');
                $('login-panel-pass').classList.toggle('hidden', mode !== 'password');
                $('login-panel-cookie').classList.toggle('hidden', mode !== 'cookie');
                $('login-panel-webdav').classList.toggle('hidden', mode !== 'webdav');
                $('login-panel-json').classList.toggle('hidden', mode !== 'json');
            });
        });

        // Theme selector buttons in Settings tab
        const themeButtons = document.querySelectorAll('.theme-choice-btn');
        themeButtons.forEach(btn => {
            btn.addEventListener('click', async () => {
                const choice = btn.getAttribute('data-theme');
                updateThemeChoiceUi(choice);
                applyTheme(choice);
                try {
                    await saveSettings({ theme: choice });
                } catch (e) { }
            });
        });

        $('btn-go-download').addEventListener('click', () => {
            document.querySelector('.app-nav [data-target="tab-download"]').click();
        });
    }

    // ==========================================
    // 6. User Action Handlers
    // ==========================================
    function setupActions() {
        // Refresh button in header
        $('btn-refresh-state').addEventListener('click', () => {
            loadState(true);
            showToast('已同步最新状态');
        });

        // Import QR Image button in header
        const btnImportQr = $('btn-import-qr-image');
        if (btnImportQr) {
            btnImportQr.addEventListener('click', pickQrImage);
        }

        // Share Parse Action
        $('btn-parse-share').addEventListener('click', () => {
            const text = $('share-url-input').value.trim();
            const passcode = $('share-pass-input').value.trim();
            if (!text) {
                showToast('请输入分享链接', true);
                return;
            }
            triggerShareParse(text, passcode);
        });

        $('btn-clear-share').addEventListener('click', () => {
            $('share-url-input').value = '';
            $('share-pass-input').value = '';
        });

        // Direct Download Action
        $('btn-add-direct').addEventListener('click', async () => {
            const url = $('direct-url-input').value.trim();
            const name = $('direct-name-input').value.trim();
            if (!url) {
                showToast('请输入有效的直链 URL', true);
                return;
            }
            try {
                const res = await dispatch('direct.add', { url, name });
                handleStateUpdate(res);
                $('direct-url-input').value = '';
                $('direct-name-input').value = '';
                showToast('直链任务已加入队列');
                document.querySelector('.app-nav [data-target="tab-tasks"]').click();
            } catch (err) {
                showToast(err.message || '添加直链任务失败', true);
            }
        });

        // Torrent Actions
        $('btn-add-magnet').addEventListener('click', async () => {
            const magnet = $('torrent-magnet-input').value.trim();
            if (!magnet) {
                showToast('请输入磁力链接', true);
                return;
            }
            const split = getSelectedSplit();
            try {
                await dispatch('torrent.add', { magnet, split });
                $('torrent-magnet-input').value = '';
                showToast('磁力任务已提交');
                document.querySelector('.app-nav [data-target="tab-tasks"]').click();
                loadStateSilently();
            } catch (err) {
                showToast(err.message || '磁力下载失败，请检查链接', true);
            }
        });

        $('btn-pick-torrent').addEventListener('click', async () => {
            const split = getSelectedSplit();
            try {
                await dispatch('system.pickTorrent', { split });
                showToast('正在调起系统文件选择器...');
                document.querySelector('.app-nav [data-target="tab-tasks"]').click();
                loadStateSilently();
            } catch (err) {
                showToast(err.message || '调起种子选择器失败', true);
            }
        });

        // File Explorer Path Navigation
        $('btn-path-up').addEventListener('click', async () => {
            try {
                const res = await dispatch('browse', { id: '' });
                if (res && res.files) {
                    appState.files = res.files;
                    appState.path = res.path || '';
                    if (res.viewToken) appState.viewToken = res.viewToken;
                }
                selectedFileIds.clear();
                renderExplorer();
            } catch (err) {
                showToast(err.message || '返回上级失败', true);
            }
        });

        $('btn-refresh-files').addEventListener('click', async () => {
            try {
                const res = await dispatch('browse', { id: appState.path || '' });
                if (res && res.files) {
                    appState.files = res.files;
                    if (res.viewToken) appState.viewToken = res.viewToken;
                }
                renderExplorer();
                showToast('目录已刷新');
            } catch (err) {
                showToast(err.message || '刷新失败', true);
            }
        });

        $('file-filter-input').addEventListener('input', () => {
            renderExplorer();
        });

        // Select all files (both files and folders)
        $('btn-toggle-select-all').addEventListener('click', () => {
            const files = appState.files || [];
            if (selectedFileIds.size === files.length && files.length > 0) {
                selectedFileIds.clear();
            } else {
                files.forEach(f => selectedFileIds.add(f.id));
            }
            renderExplorer();
        });

        // Batch Download Action
        $('btn-download-selected').addEventListener('click', async () => {
            const ids = Array.from(selectedFileIds);
            if (ids.length === 0) return;
            try {
                showToast('正在添加至下载队列...');
                const res = await dispatch('download.add', { ids });
                handleStateUpdate(res);
                selectedFileIds.clear();
                renderExplorer();
                showToast('成功添加 ' + ids.length + ' 个下载任务');
                document.querySelector('.app-nav [data-target="tab-tasks"]').click();
            } catch (err) {
                showToast(err.message || '添加下载任务失败', true);
            }
        });

        // Batch Export Links
        $('btn-export-links').addEventListener('click', async () => {
            const ids = Array.from(selectedFileIds);
            if (ids.length === 0) return;

            const wrap = document.createElement('div');
            const p = document.createElement('p');
            p.textContent = '选择导出格式：直链含临时授权，请妥善保管。';
            p.style.marginBottom = '12px';
            p.style.color = 'var(--text-secondary)';
            wrap.appendChild(p);

            const formatRow = document.createElement('div');
            formatRow.className = 'form-group mb-8';

            const select = document.createElement('select');
            select.className = 'app-select mb-8';
            const formats = [
                { id: 'plain', name: '纯下载链接列表 (Plain URLs)' },
                { id: 'labeled', name: '文件名 + 下载链接 (Labeled)' },
                { id: 'curl', name: 'cURL 终端下载命令 (含 Headers)' },
                { id: 'aria2', name: 'Aria2 下载脚本格式' },
                { id: 'motrix', name: 'Motrix 任务协议格式' }
            ];
            formats.forEach(fmt => {
                const opt = document.createElement('option');
                opt.value = fmt.id;
                opt.textContent = fmt.name;
                select.appendChild(opt);
            });
            formatRow.appendChild(select);
            wrap.appendChild(formatRow);

            const resultArea = document.createElement('textarea');
            resultArea.className = 'app-textarea code-font mb-8';
            resultArea.rows = 7;
            resultArea.readOnly = true;
            resultArea.placeholder = '点击下方生成链接...';
            wrap.appendChild(resultArea);

            const btnRow = document.createElement('div');
            btnRow.style.display = 'flex';
            btnRow.style.gap = '8px';

            const generateBtn = document.createElement('button');
            generateBtn.className = 'btn btn-secondary flex-1';
            generateBtn.textContent = '生成对应格式';
            btnRow.appendChild(generateBtn);

            const copyBtn = document.createElement('button');
            copyBtn.className = 'btn btn-primary flex-1';
            copyBtn.textContent = '复制内容';
            copyBtn.disabled = true;
            btnRow.appendChild(copyBtn);

            wrap.appendChild(btnRow);

            async function runExport() {
                try {
                    showToast('正在获取直链导出格式...');
                    generateBtn.disabled = true;
                    const res = await dispatch('links.export', { ids, format: select.value });
                    const exportText = (res && res.text) ? res.text : '';
                    resultArea.value = exportText;
                    copyBtn.disabled = !exportText;
                    showToast('直链生成完成');
                } catch (err) {
                    showToast(err.message || '导出直链失败', true);
                } finally {
                    generateBtn.disabled = false;
                }
            }

            select.addEventListener('change', runExport);
            generateBtn.addEventListener('click', runExport);

            copyBtn.addEventListener('click', () => {
                const text = resultArea.value;
                if (!text) return;
                try {
                    navigator.clipboard.writeText(text);
                    showToast('已复制到剪贴板');
                } catch (e) {
                    resultArea.select();
                    document.execCommand('copy');
                    showToast('已复制');
                }
            });

            openModal('导出直链 (' + ids.length + ' 项)', wrap);
            // Run initial export
            runExport();
        });

        // Quark Cloud Ops: Mkdir, Transfer, Delete
        const btnCloudMkdir = $('btn-cloud-mkdir');
        if (btnCloudMkdir) {
            btnCloudMkdir.addEventListener('click', () => {
                const wrap = document.createElement('div');
                const p = document.createElement('p');
                p.textContent = '请输入要在夸克网盘中新建的文件夹完整路径（必须以 / 开头）：';
                p.style.marginBottom = '12px';
                p.style.color = 'var(--text-secondary)';
                wrap.appendChild(p);

                const input = document.createElement('input');
                input.type = 'text';
                input.className = 'app-input mb-8';
                input.placeholder = '/我的资源/新分类';
                input.value = (appState.path && appState.path.startsWith('/')) ? `${appState.path}/新建文件夹` : '/新建文件夹';
                wrap.appendChild(input);

                const btnRow = document.createElement('div');
                btnRow.style.display = 'flex';
                btnRow.style.gap = '8px';
                btnRow.style.marginTop = '12px';

                const confirmBtn = document.createElement('button');
                confirmBtn.className = 'btn btn-primary flex-1';
                confirmBtn.textContent = '确认创建';
                confirmBtn.addEventListener('click', async () => {
                    const path = input.value.trim();
                    if (!path.startsWith('/')) {
                        showToast('路径必须以 / 开头', true);
                        return;
                    }
                    closeModal();
                    try {
                        showToast('正在创建文件夹...');
                        const res = await dispatch('cloud.mkdir', { path });
                        showToast(res.message || '文件夹已创建');
                        // Refresh directory list
                        await loadStateSilently();
                    } catch (err) {
                        showToast(err.message || '新建文件夹失败', true);
                    }
                });

                const cancelBtn = document.createElement('button');
                cancelBtn.className = 'btn btn-ghost flex-1';
                cancelBtn.textContent = '取消';
                cancelBtn.addEventListener('click', closeModal);

                btnRow.appendChild(confirmBtn);
                btnRow.appendChild(cancelBtn);
                wrap.appendChild(btnRow);

                openModal('夸克网盘 · 新建目录', wrap);
            });
        }

        const btnCloudTransfer = $('btn-cloud-transfer');
        if (btnCloudTransfer) {
            btnCloudTransfer.addEventListener('click', () => {
                // Snapshot selected IDs and viewToken before opening modal confirmation
                const ids = Array.from(selectedFileIds);
                if (ids.length === 0) return;
                const viewToken = appState.viewToken;

                const wrap = document.createElement('div');
                const p = document.createElement('p');
                const names = ids.map(id => appState.files.find(file => file.id === id)?.name || id);
                p.textContent = `确认将 ${ids.length} 项分享文件转存到夸克：${names.slice(0, 10).join('、')}${names.length > 10 ? '…' : ''}`;
                p.style.marginBottom = '12px';
                p.style.color = 'var(--text-secondary)';
                wrap.appendChild(p);

                const pathLabel = document.createElement('label');
                pathLabel.className = 'sub-label';
                pathLabel.textContent = '保存目标路径（以 / 开头）：';
                wrap.appendChild(pathLabel);

                const pathInput = document.createElement('input');
                pathInput.type = 'text';
                pathInput.className = 'app-input mb-8';
                pathInput.value = '/ZERA';
                wrap.appendChild(pathInput);

                const dlLabel = document.createElement('label');
                dlLabel.className = 'checkbox-label mb-8';
                const dlChk = document.createElement('input');
                dlChk.type = 'checkbox';
                dlLabel.appendChild(dlChk);
                const dlSpan = document.createElement('span');
                dlSpan.textContent = '转存后立即作为「我的网盘」文件加入本地下载';
                dlLabel.appendChild(dlSpan);
                wrap.appendChild(dlLabel);

                const btnRow = document.createElement('div');
                btnRow.style.display = 'flex';
                btnRow.style.gap = '8px';
                btnRow.style.marginTop = '16px';

                const confirmBtn = document.createElement('button');
                confirmBtn.className = 'btn btn-primary flex-1';
                confirmBtn.textContent = '确认转存';
                confirmBtn.addEventListener('click', async () => {
                    const path = pathInput.value.trim() || '/ZERA';
                    const download = dlChk.checked;
                    closeModal();
                    try {
                        showToast('正在转存至夸克网盘...');
                        const res = await dispatch('cloud.transfer', {
                            ids,
                            path,
                            viewToken,
                            download
                        });
                        showToast(res.message || '转存已完成');
                        selectedFileIds.clear();
                        renderExplorer();
                        await loadStateSilently();
                    } catch (err) {
                        showToast(err.message || '转存失败', true);
                    }
                });

                const cancelBtn = document.createElement('button');
                cancelBtn.className = 'btn btn-ghost flex-1';
                cancelBtn.textContent = '取消';
                cancelBtn.addEventListener('click', closeModal);

                btnRow.appendChild(confirmBtn);
                btnRow.appendChild(cancelBtn);
                wrap.appendChild(btnRow);

                openModal(`夸克网盘 · 转存 (${ids.length} 项)`, wrap);
            });
        }

        const btnCloudDelete = $('btn-cloud-delete');
        if (btnCloudDelete) {
            btnCloudDelete.addEventListener('click', () => {
                // Snapshot selected IDs and viewToken before opening modal confirmation
                const ids = Array.from(selectedFileIds);
                if (ids.length === 0) return;
                const viewToken = appState.viewToken;

                // Gather selected file names
                const selectedNames = (appState.files || []).filter(f => ids.includes(f.id)).map(f => f.name || f.id);

                const wrap = document.createElement('div');
                const p = document.createElement('p');
                p.textContent = `确认要删除网盘中选中的 ${ids.length} 项文件/文件夹吗？`;
                p.style.marginBottom = '8px';
                p.style.fontWeight = '600';
                wrap.appendChild(p);

                const listDiv = document.createElement('div');
                listDiv.className = 'card card-sunken mb-8';
                listDiv.style.maxHeight = '120px';
                listDiv.style.overflowY = 'auto';
                listDiv.style.fontSize = '12px';
                listDiv.style.fontFamily = 'var(--font-mono)';
                listDiv.textContent = selectedNames.join('\n');
                wrap.appendChild(listDiv);

                const permLabel = document.createElement('label');
                permLabel.className = 'checkbox-label mb-8';
                permLabel.style.color = 'var(--danger)';
                const permChk = document.createElement('input');
                permChk.type = 'checkbox';
                permLabel.appendChild(permChk);
                const permSpan = document.createElement('span');
                permSpan.textContent = '彻底永久删除（无法恢复）';
                permLabel.appendChild(permSpan);
                wrap.appendChild(permLabel);

                const tip = document.createElement('p');
                tip.className = 'sub-label';
                tip.textContent = '默认移入夸克回收站，可在回收站恢复。勾选彻底永久删除则无法找回。';
                wrap.appendChild(tip);

                const btnRow = document.createElement('div');
                btnRow.style.display = 'flex';
                btnRow.style.gap = '8px';
                btnRow.style.marginTop = '16px';

                const confirmBtn = document.createElement('button');
                confirmBtn.className = 'btn btn-danger-ghost flex-1';
                confirmBtn.style.background = 'var(--danger)';
                confirmBtn.style.color = '#fff';
                confirmBtn.textContent = '确认删除';
                confirmBtn.addEventListener('click', async () => {
                    const permanent = permChk.checked;
                    closeModal();
                    try {
                        showToast(permanent ? '正在永久删除网盘文件...' : '正在将网盘文件移入回收站...');
                        const res = await dispatch('cloud.delete', {
                            ids,
                            permanent,
                            confirmed: true,
                            viewToken
                        });
                        showToast(res.message || '删除成功');
                        selectedFileIds.clear();
                        renderExplorer();
                        await loadStateSilently();
                    } catch (err) {
                        showToast(err.message || '网盘删除失败', true);
                    }
                });

                const cancelBtn = document.createElement('button');
                cancelBtn.className = 'btn btn-ghost flex-1';
                cancelBtn.textContent = '取消';
                cancelBtn.addEventListener('click', closeModal);

                btnRow.appendChild(confirmBtn);
                btnRow.appendChild(cancelBtn);
                wrap.appendChild(btnRow);

                openModal(`确认删除网盘文件 (${ids.length} 项)`, wrap);
            });
        }

        // Tasks Header Controls: Pause All, Resume All, Clear Completed
        $('btn-pause-all').addEventListener('click', async () => {
            try {
                showToast('正在暂停所有任务...');
                const res = await dispatch('task.pauseAll', {});
                handleStateUpdate(res);
                showToast('已暂停全部下载任务');
            } catch (err) {
                showToast(err.message || '暂停失败', true);
            }
        });

        const btnResumeAll = $('btn-resume-all');
        if (btnResumeAll) {
            btnResumeAll.addEventListener('click', async () => {
                try {
                    showToast('正在恢复所有任务...');
                    const res = await dispatch('task.resumeAll', {});
                    handleStateUpdate(res);
                    showToast('已恢复下载任务');
                } catch (err) {
                    showToast(err.message || '恢复失败', true);
                }
            });
        }

        $('btn-clear-completed').addEventListener('click', async () => {
            try {
                showToast('正在清理已完成与已取消任务...');
                const res = await dispatch('task.clearCompleted', {});
                handleStateUpdate(res);
                showToast('已清理完成项');
            } catch (err) {
                showToast(err.message || '清理失败', true);
            }
        });

        // Active Account Capacity Query
        const btnAccountCapacity = $('btn-account-capacity');
        if (btnAccountCapacity) {
            btnAccountCapacity.addEventListener('click', async () => {
                try {
                    showToast('正在查询网盘容量...');
                    const res = await dispatch('account.capacity', {});
                    const total = res.total || 0;
                    const used = res.used || 0;
                    const free = Math.max(0, total - used);
                    const pct = total > 0 ? ((used / total) * 100).toFixed(1) : 0;

                    const wrap = document.createElement('div');
                    const infoP = document.createElement('p');
                    infoP.innerHTML = `已用空间：<strong>${formatBytes(used)}</strong> / 总配额：<strong>${formatBytes(total)}</strong><br>剩余可用：<strong>${formatBytes(free)}</strong> (${pct}%)`;
                    infoP.style.lineHeight = '1.8';
                    infoP.style.marginBottom = '12px';
                    wrap.appendChild(infoP);

                    const closeBtn = document.createElement('button');
                    closeBtn.className = 'btn btn-primary btn-block';
                    closeBtn.textContent = '知道了';
                    closeBtn.addEventListener('click', closeModal);
                    wrap.appendChild(closeBtn);

                    openModal('当前网盘存储容量', wrap);
                } catch (err) {
                    showToast(err.message || '查询网盘容量失败', true);
                }
            });
        }

        // Baidu Membership Query
        $('btn-query-membership').addEventListener('click', async () => {
            try {
                showToast('正在查询百度网盘会员与流量状态...');
                const data = await dispatch('membership.query', {});
                const pre = document.createElement('pre');
                pre.className = 'log-console';
                pre.textContent = JSON.stringify(data, null, 2);
                openModal('百度网盘会员与配额信息', pre);
            } catch (err) {
                showToast(err.message || '会员查询失败', true);
            }
        });

        // QR Code Login Start / Cancel / Challenge / SMS
        $('btn-start-qr').addEventListener('click', async () => {
            try {
                showToast('正在获取二维码...');
                $('qr-placeholder').classList.add('hidden');
                $('qr-img-wrap').classList.add('hidden');
                $('qr-status-bar').classList.remove('hidden');
                $('qr-status-msg').textContent = '正在连接网盘认证服务...';
                $('btn-cancel-qr').classList.remove('hidden');
                $('qr-challenge-section').classList.add('hidden');

                const res = await dispatch('login.start', {
                    platform: appState.platform,
                    mode: 'qr'
                });

                handleLoginState(res);
                startQrPolling();
            } catch (err) {
                $('qr-placeholder').classList.remove('hidden');
                $('qr-status-bar').classList.add('hidden');
                $('btn-cancel-qr').classList.add('hidden');
                showToast(err.message || '获取二维码失败', true);
            }
        });

        $('btn-cancel-qr').addEventListener('click', async () => {
            stopQrPolling();
            try {
                await dispatch('login.cancel', {});
            } catch (e) { }
            $('qr-img-wrap').classList.add('hidden');
            $('qr-placeholder').classList.remove('hidden');
            $('qr-status-bar').classList.add('hidden');
            $('btn-cancel-qr').classList.add('hidden');
            $('qr-challenge-section').classList.add('hidden');
            showToast('已取消扫码登录');
        });

        $('btn-qr-submit-challenge').addEventListener('click', async () => {
            const code = $('qr-challenge-input').value.trim();
            if (!code) {
                showToast('请输入安全验证码', true);
                return;
            }
            try {
                showToast('正在提交验证...');
                const res = await dispatch('login.challenge', { code });
                handleLoginState(res);
                $('qr-challenge-input').value = '';
            } catch (err) {
                showToast(err.message || '验证码提交失败', true);
            }
        });

        $('btn-qr-resend-sms').addEventListener('click', async () => {
            try {
                showToast('正在请求重发短信...');
                const res = await dispatch('login.sms', {});
                handleLoginState(res);
                showToast('短信验证码已重发');
            } catch (err) {
                showToast(err.message || '重发短信失败', true);
            }
        });

        // Password Login Submit
        $('btn-submit-pass').addEventListener('click', async () => {
            const username = $('pass-username').value.trim();
            const password = $('pass-password').value;
            if (!username || !password) {
                showToast('请完整输入账号与密码', true);
                return;
            }
            try {
                showToast('正在登录验证...');
                const res = await dispatch('login.start', {
                    platform: appState.platform,
                    mode: 'password',
                    username,
                    password
                });

                if (res && res.state === 'NeedChallenge') {
                    $('challenge-box').classList.remove('hidden');
                    showToast(res.hint || '请输入短信验证码');
                } else if (res && res.state === 'Success') {
                    showToast('登录成功！已保存网盘凭据');
                    $('pass-password').value = '';
                    await loadState();
                } else {
                    showToast(res.hint || '正在处理登录...');
                }
            } catch (err) {
                showToast(err.message || '账号密码登录失败', true);
            }
        });

        // Cookie Import Submit
        $('btn-submit-cookie').addEventListener('click', async () => {
            const credential = $('cookie-text').value.trim();
            const name = $('cookie-alias').value.trim();
            if (!credential) {
                showToast('请粘贴 Cookie 串', true);
                return;
            }
            try {
                showToast('正在校验 Cookie 有效性...');
                const res = await dispatch('account.save', {
                    platform: appState.platform,
                    name,
                    credential
                });
                handleStateUpdate(res);
                $('cookie-text').value = '';
                showToast('Cookie 导入成功！已保存账号');
            } catch (err) {
                showToast(err.message || 'Cookie 校验失败，请检查是否已过期', true);
            }
        });

        // WebDAV Form Submit
        $('btn-submit-webdav').addEventListener('click', async () => {
            const name = $('webdav-name').value.trim();
            const endpoint = $('webdav-endpoint').value.trim();
            const username = $('webdav-user').value.trim();
            const password = $('webdav-pass').value;
            const allowHttp = $('webdav-allow-http').checked;

            if (!endpoint) {
                showToast('请填写 WebDAV 地址；匿名服务可留空账号与密码', true);
                return;
            }
            try {
                showToast('正在连接 WebDAV 服务器...');
                const res = await dispatch('account.webdav', {
                    name: name || 'WebDAV 网盘',
                    endpoint,
                    username,
                    password,
                    allowHttp
                });
                handleStateUpdate(res);
                $('webdav-pass').value = '';
                showToast('WebDAV 连接成功并已保存');
            } catch (err) {
                showToast(err.message || 'WebDAV 连接验证失败', true);
            }
        });

        // Advanced JSON Credential Submit
        $('btn-submit-json').addEventListener('click', async () => {
            const rawJson = $('json-credential-input').value.trim();
            const name = $('json-account-name').value.trim();
            if (!rawJson) {
                showToast('请输入 JSON 凭据数据', true);
                return;
            }
            try {
                // Ensure valid json
                JSON.parse(rawJson);
                showToast('正在验证高级凭据...');
                const res = await dispatch('account.save', {
                    platform: appState.platform,
                    name: name || '自定义高级账号',
                    credential: rawJson
                });
                handleStateUpdate(res);
                $('json-credential-input').value = '';
                showToast('高级凭据保存成功');
            } catch (err) {
                showToast('凭据格式或校验错误: ' + err.message, true);
            }
        });

        // Settings Dirty Tracking
        $('setting-threads').addEventListener('change', () => { isSettingsDirty = true; });
        $('setting-concurrent').addEventListener('change', () => { isSettingsDirty = true; });
        const radios = document.getElementsByName('writeMode');
        for (let r of radios) {
            r.addEventListener('change', () => { isSettingsDirty = true; });
        }

        // Export Folder Picker & Clear Actions
        const btnPickExportFolder = $('btn-pick-export-folder');
        if (btnPickExportFolder) {
            btnPickExportFolder.addEventListener('click', async () => {
                try {
                    showToast('正在调起系统目录选择器...');
                    const res = await dispatch('system.pickExportFolder', {});
                    if (res) {
                        appState.exportFolder = { selected: true, name: res.name, uri: res.uri };
                        renderSettings();
                        showToast(`已绑定公共导出目录：${res.name}`);
                    }
                } catch (err) {
                    showToast(err.message || '选择导出目录失败', true);
                }
            });
        }

        const btnClearExportFolder = $('btn-clear-export-folder');
        if (btnClearExportFolder) {
            btnClearExportFolder.addEventListener('click', async () => {
                try {
                    await dispatch('system.clearExportFolder', {});
                    appState.exportFolder = { selected: false, name: null, uri: null };
                    if (appState.settings) appState.settings.autoExport = false;
                    renderSettings();
                    showToast('已解绑公共导出目录');
                } catch (err) {
                    showToast(err.message || '清除导出目录失败', true);
                }
            });
        }

        // Auto Export Checkbox
        const autoExportChk = $('setting-auto-export');
        if (autoExportChk) {
            autoExportChk.addEventListener('change', async () => {
                const checked = autoExportChk.checked;
                if (checked && (!appState.exportFolder || !appState.exportFolder.selected)) {
                    autoExportChk.checked = false;
                    showToast('请先选择公共导出目录', true);
                    return;
                }
                try {
                    await saveSettings({ autoExport: checked });
                    showToast(checked ? '已开启完成自动导出' : '已关闭自动导出');
                } catch (err) {
                    autoExportChk.checked = !checked;
                    showToast(err.message || '保存自动导出失败', true);
                }
            });
        }

        // Floating Window Permission & Toggle
        const floatingChk = $('setting-floating-window');
        if (floatingChk) {
            floatingChk.addEventListener('change', async () => {
                const wantEnable = floatingChk.checked;
                if (wantEnable) {
                    try {
                        showToast('正在检查系统悬浮窗权限...');
                        const perm = await dispatch('system.overlayPermission', {});
                        if (!perm.granted) {
                            floatingChk.checked = false;
                            showToast('请在系统设置中允许 ZERA 显示在其他应用上层后再开启', true);
                            return;
                        }
                    } catch (err) {
                        floatingChk.checked = false;
                        showToast(err.message || '检查悬浮窗权限失败', true);
                        return;
                    }
                }

                try {
                    await saveSettings({ floatingWindow: wantEnable });
                    showToast(wantEnable ? '已开启桌面悬浮窗' : '已关闭桌面悬浮窗');
                } catch (err) {
                    floatingChk.checked = !wantEnable;
                    showToast(err.message || '保存悬浮窗设置失败', true);
                }
            });
        }

        // Settings Save
        $('btn-save-settings').addEventListener('click', async () => {
            const threads = parseInt($('setting-threads').value, 10);
            const concurrent = parseInt($('setting-concurrent').value, 10);
            let writeMode = 'SharedFile';
            const radios = document.getElementsByName('writeMode');
            for (let r of radios) {
                if (r.checked) writeMode = r.value;
            }
            try {
                await saveSettings({ threads, concurrent, writeMode });
                isSettingsDirty = false;
                showToast('设置已即时保存生效');
            } catch (err) {
                showToast(err.message || '保存设置失败', true);
            }
        });

        // Trackers Modal (Full object { manualText, autoUpdate, subscriptions })
        $('btn-open-trackers').addEventListener('click', async () => {
            try {
                showToast('正在获取 Tracker 配置...');
                const trackerConfig = await dispatch('tracker.get', {});
                showTrackerModal(trackerConfig);
            } catch (err) {
                showToast(err.message || '获取 Tracker 失败', true);
            }
        });

        // Clear Logs Action
        $('btn-clear-logs').addEventListener('click', () => {
            appState.logs = [];
            $('log-console').textContent = '已清空本地显示';
        });

        // Modal Close Action
        $('btn-modal-close').addEventListener('click', closeModal);
        $('modal-container').addEventListener('click', (e) => {
            if (e.target === $('modal-container')) closeModal();
        });
    }

    // ==========================================
    // 7. Tracker Management Modal
    // ==========================================
    function showTrackerModal(trackerConfig) {
        let currentConfig = {
            manualText: (trackerConfig && trackerConfig.manualText) || '',
            autoUpdate: trackerConfig && trackerConfig.autoUpdate !== undefined ? trackerConfig.autoUpdate : true,
            subscriptions: (trackerConfig && Array.isArray(trackerConfig.subscriptions)) ? trackerConfig.subscriptions : []
        };

        const wrap = document.createElement('div');

        // 1. Auto Update Checkbox
        const autoRow = document.createElement('div');
        autoRow.style.marginBottom = '12px';
        const autoLabel = document.createElement('label');
        autoLabel.className = 'checkbox-label';
        const autoChk = document.createElement('input');
        autoChk.type = 'checkbox';
        autoChk.checked = currentConfig.autoUpdate;
        autoChk.addEventListener('change', () => {
            currentConfig.autoUpdate = autoChk.checked;
        });
        const autoSpan = document.createElement('span');
        autoSpan.textContent = '启用自动更新订阅源 (推荐开启)';
        autoLabel.appendChild(autoChk);
        autoLabel.appendChild(autoSpan);
        autoRow.appendChild(autoLabel);
        wrap.appendChild(autoRow);

        // 2. Manual Trackers Textarea
        const desc = document.createElement('p');
        desc.className = 'input-label';
        desc.textContent = '手动自定义 Tracker 地址（每行一条）：';
        wrap.appendChild(desc);

        const textarea = document.createElement('textarea');
        textarea.className = 'app-textarea code-font';
        textarea.rows = 4;
        textarea.placeholder = 'udp://tracker.example.com:6969/announce\nhttp://tracker.example.com:80/announce';
        textarea.value = currentConfig.manualText;
        wrap.appendChild(textarea);

        // 3. Subscriptions Header
        const subHeader = document.createElement('div');
        subHeader.style.display = 'flex';
        subHeader.style.justifyContent = 'space-between';
        subHeader.style.alignItems = 'center';
        subHeader.style.marginTop = '16px';
        subHeader.style.marginBottom = '8px';

        const subTitle = document.createElement('span');
        subTitle.className = 'input-label';
        subTitle.textContent = 'Tracker 在线订阅列表';
        subHeader.appendChild(subTitle);

        const addSubBtn = document.createElement('button');
        addSubBtn.className = 'btn btn-secondary btn-sm';
        addSubBtn.textContent = '+ 添加订阅源';
        subHeader.appendChild(addSubBtn);
        wrap.appendChild(subHeader);

        // Subscriptions Container
        const subListContainer = document.createElement('div');
        subListContainer.style.maxHeight = '180px';
        subListContainer.style.overflowY = 'auto';
        subListContainer.style.marginBottom = '12px';
        wrap.appendChild(subListContainer);

        function renderSubscriptionsList() {
            subListContainer.replaceChildren();
            if (currentConfig.subscriptions.length === 0) {
                const emptyHint = document.createElement('div');
                emptyHint.className = 'empty-hint';
                emptyHint.textContent = '暂无订阅源，可点击上方「+ 添加订阅源」';
                subListContainer.appendChild(emptyHint);
                return;
            }

            currentConfig.subscriptions.forEach((sub, idx) => {
                const item = document.createElement('div');
                item.className = 'sub-item-card';

                const chk = document.createElement('input');
                chk.type = 'checkbox';
                chk.checked = sub.enabled !== false;
                chk.addEventListener('change', () => {
                    sub.enabled = chk.checked;
                });
                item.appendChild(chk);

                const info = document.createElement('div');
                info.className = 'sub-item-info';

                const nameEl = document.createElement('div');
                nameEl.className = 'sub-item-name';
                nameEl.textContent = sub.name || '未命名订阅';
                info.appendChild(nameEl);

                const urlEl = document.createElement('div');
                urlEl.className = 'sub-item-url';
                urlEl.textContent = sub.url || '';
                info.appendChild(urlEl);

                const summaryEl = document.createElement('div');
                summaryEl.className = 'sub-item-summary';
                summaryEl.textContent = sub.summary || (Array.isArray(sub.trackers) ? `${sub.trackers.length} 条 Tracker` : '有效');
                info.appendChild(summaryEl);

                item.appendChild(info);

                const delBtn = document.createElement('button');
                delBtn.className = 'btn btn-danger-ghost btn-sm';
                delBtn.textContent = '删除';
                delBtn.addEventListener('click', () => {
                    currentConfig.subscriptions.splice(idx, 1);
                    renderSubscriptionsList();
                });

                const editBtn = document.createElement('button');
                editBtn.className = 'btn btn-secondary btn-sm mr-4';
                editBtn.textContent = '编辑';
                editBtn.addEventListener('click', () => {
                    if (wrap.querySelector('.tracker-edit-form')) return;

                    const editForm = document.createElement('div');
                    editForm.className = 'card card-sunken mb-8 tracker-edit-form';

                    const nameInput = document.createElement('input');
                    nameInput.type = 'text';
                    nameInput.className = 'app-input mb-8';
                    nameInput.value = sub.name || '';
                    nameInput.placeholder = '订阅名称';

                    const urlInput = document.createElement('input');
                    urlInput.type = 'url';
                    urlInput.className = 'app-input mb-8';
                    urlInput.value = sub.url || '';
                    urlInput.placeholder = 'https://...';

                    const formBtns = document.createElement('div');
                    formBtns.style.display = 'flex';
                    formBtns.style.gap = '8px';

                    const confirmBtn = document.createElement('button');
                    confirmBtn.className = 'btn btn-primary btn-sm flex-1';
                    confirmBtn.textContent = '保存修改';
                    confirmBtn.addEventListener('click', () => {
                        const name = nameInput.value.trim();
                        const url = urlInput.value.trim();
                        if (!url) {
                            showToast('请输入有效的订阅 URL 地址', true);
                            return;
                        }
                        sub.name = name || '自定义订阅';
                        sub.url = url;
                        renderSubscriptionsList();
                        editForm.remove();
                    });

                    const cancelBtn = document.createElement('button');
                    cancelBtn.className = 'btn btn-ghost btn-sm';
                    cancelBtn.textContent = '取消';
                    cancelBtn.addEventListener('click', () => editForm.remove());

                    formBtns.appendChild(confirmBtn);
                    formBtns.appendChild(cancelBtn);

                    editForm.appendChild(nameInput);
                    editForm.appendChild(urlInput);
                    editForm.appendChild(formBtns);

                    item.parentElement.insertBefore(editForm, item.nextSibling);
                });

                item.appendChild(editBtn);
                item.appendChild(delBtn);

                subListContainer.appendChild(item);
            });
        }

        renderSubscriptionsList();

        // Add subscription dialog inline toggle
        addSubBtn.addEventListener('click', () => {
            if (wrap.querySelector('.tracker-add-form')) return;

            const addForm = document.createElement('div');
            addForm.className = 'card card-sunken mb-8 tracker-add-form';

            const nameInput = document.createElement('input');
            nameInput.type = 'text';
            nameInput.className = 'app-input mb-8';
            nameInput.placeholder = '订阅名称（如：TrackersList 综合源）';

            const urlInput = document.createElement('input');
            urlInput.type = 'url';
            urlInput.className = 'app-input mb-8';
            urlInput.placeholder = 'https://raw.githubusercontent.com/.../trackers_best.txt';

            const formBtns = document.createElement('div');
            formBtns.style.display = 'flex';
            formBtns.style.gap = '8px';

            const confirmBtn = document.createElement('button');
            confirmBtn.className = 'btn btn-primary btn-sm flex-1';
            confirmBtn.textContent = '确认添加';
            confirmBtn.addEventListener('click', () => {
                const name = nameInput.value.trim();
                const url = urlInput.value.trim();
                if (!url) {
                    showToast('请输入有效的订阅 URL 地址', true);
                    return;
                }
                currentConfig.subscriptions.push({
                    name: name || '自定义订阅',
                    url: url,
                    enabled: true,
                    trackers: [],
                    error: ''
                });
                renderSubscriptionsList();
                addForm.remove();
            });

            const cancelBtn = document.createElement('button');
            cancelBtn.className = 'btn btn-ghost btn-sm';
            cancelBtn.textContent = '取消';
            cancelBtn.addEventListener('click', () => addForm.remove());

            formBtns.appendChild(confirmBtn);
            formBtns.appendChild(cancelBtn);

            addForm.appendChild(nameInput);
            addForm.appendChild(urlInput);
            addForm.appendChild(formBtns);

            subListContainer.parentElement.insertBefore(addForm, subListContainer);
        });

        // 4. Modal Action Buttons Row
        const btnRow = document.createElement('div');
        btnRow.style.display = 'flex';
        btnRow.style.gap = '8px';
        btnRow.style.marginTop = '16px';

        const updateBtn = document.createElement('button');
        updateBtn.className = 'btn btn-secondary flex-1';
        updateBtn.textContent = '立即在线更新所有订阅';
        updateBtn.addEventListener('click', async () => {
            try {
                showToast('正在向网络更新所有订阅源...');
                updateBtn.disabled = true;
                const updated = await dispatch('tracker.update', {});
                if (updated) {
                    currentConfig = updated;
                    textarea.value = currentConfig.manualText || '';
                    autoChk.checked = currentConfig.autoUpdate !== false;
                    renderSubscriptionsList();
                    showToast('订阅更新完成');
                }
            } catch (e) {
                showToast(e.message || '更新订阅失败', true);
            } finally {
                updateBtn.disabled = false;
            }
        });
        btnRow.appendChild(updateBtn);

        const saveBtn = document.createElement('button');
        saveBtn.className = 'btn btn-primary flex-1';
        saveBtn.textContent = '保存全部配置';
        saveBtn.addEventListener('click', async () => {
            try {
                currentConfig.manualText = textarea.value.trim();
                showToast('正在保存 Tracker 配置...');
                await dispatch('tracker.save', currentConfig);
                showToast('Tracker 配置已成功保存');
                closeModal();
            } catch (e) {
                showToast(e.message || '保存失败', true);
            }
        });
        btnRow.appendChild(saveBtn);

        wrap.appendChild(btnRow);
        openModal('BT Tracker 服务器与订阅管理', wrap);
    }

    async function saveSettings(diff) {
        const current = appState.settings || {};
        const threads = diff.threads !== undefined ? diff.threads : (current.threads || 32);
        const concurrent = diff.concurrent !== undefined ? diff.concurrent : (current.concurrent || 2);
        const writeMode = diff.writeMode !== undefined ? diff.writeMode : (current.writeMode || 'SharedFile');
        const theme = diff.theme !== undefined ? diff.theme : (current.theme || 'system');

        const payload = { threads, concurrent, writeMode, theme };
        if (diff.autoExport !== undefined) payload.autoExport = diff.autoExport;
        else if (current.autoExport !== undefined) payload.autoExport = current.autoExport;

        if (diff.floatingWindow !== undefined) payload.floatingWindow = diff.floatingWindow;
        else if (current.floatingWindow !== undefined) payload.floatingWindow = current.floatingWindow;

        const res = await dispatch('settings.save', payload);
        handleStateUpdate(res);
    }

    // ==========================================
    // 8. QR Code & Challenge Polling
    // ==========================================
    function handleLoginState(res) {
        if (!res) return;

        if (res.image) {
            $('qr-img').src = 'data:image/png;base64,' + res.image;
            $('qr-img-wrap').classList.remove('hidden');
            $('qr-placeholder').classList.add('hidden');
        }

        const state = res.state || 'Waiting';
        const hint = res.hint || '';

        switch (state) {
            case 'Waiting':
                $('qr-status-msg').textContent = hint || '等待手机扫码确认...';
                $('qr-challenge-section').classList.add('hidden');
                break;
            case 'Scanned':
                $('qr-status-msg').textContent = hint || '已扫描二维码，请在手机上点击确认登录';
                $('qr-challenge-section').classList.add('hidden');
                break;
            case 'Captcha':
                $('qr-status-msg').textContent = hint || '需要输入图形验证码';
                $('qr-challenge-section').classList.remove('hidden');
                $('qr-challenge-label').textContent = '安全验证码 (Captcha)';
                $('qr-challenge-input').placeholder = '输入图形验证码';
                $('btn-qr-resend-sms').classList.add('hidden');
                $('qr-verification-link-row').classList.add('hidden');
                break;
            case 'SmsRequired':
            case 'SmsSent':
                $('qr-status-msg').textContent = hint || (state === 'SmsSent' ? '验证码已发送至手机' : '需要短信安全验证');
                $('qr-challenge-section').classList.remove('hidden');
                $('qr-challenge-label').textContent = '短信验证码';
                $('qr-challenge-input').placeholder = '输入收到的短信验证码';
                $('btn-qr-resend-sms').classList.remove('hidden');
                $('qr-verification-link-row').classList.add('hidden');
                break;
            case 'Verification':
                $('qr-status-msg').textContent = hint || '需要在网页中完成滑块验证';
                $('qr-challenge-section').classList.remove('hidden');
                if (res.verificationUrl) {
                    $('qr-verification-link-row').classList.remove('hidden');
                    $('qr-verification-link').href = res.verificationUrl;
                }
                break;
            case 'Success':
                stopQrPolling();
                $('qr-status-msg').textContent = '扫码登录成功！';
                $('qr-challenge-section').classList.add('hidden');
                $('btn-cancel-qr').classList.add('hidden');
                showToast('登录成功！已保存网盘账号');
                loadState();
                break;
            case 'Expired':
                stopQrPolling();
                $('qr-status-msg').textContent = hint || '二维码已过期，请重新获取';
                $('qr-challenge-section').classList.add('hidden');
                $('btn-cancel-qr').classList.add('hidden');
                break;
            case 'Cancelled':
                stopQrPolling();
                $('qr-status-msg').textContent = '登录已取消';
                $('qr-challenge-section').classList.add('hidden');
                $('btn-cancel-qr').classList.add('hidden');
                break;
            default:
                $('qr-status-msg').textContent = hint || state;
                break;
        }
    }

    function startQrPolling() {
        stopQrPolling();
        qrPollTimer = setInterval(async () => {
            if (isLoginPollInFlight) return;
            isLoginPollInFlight = true;
            try {
                const res = await dispatch('login.poll', {});
                handleLoginState(res);
            } catch (e) {
                // silent ignore
            } finally {
                isLoginPollInFlight = false;
            }
        }, 1500);
    }

    function stopQrPolling() {
        if (qrPollTimer) {
            clearInterval(qrPollTimer);
            qrPollTimer = null;
        }
    }

    // ==========================================
    // 9. Modal Window Controller
    // ==========================================
    function openModal(title, contentElement) {
        $('modal-title').textContent = title;
        const body = $('modal-content');
        if (body) {
            body.replaceChildren();
            if (contentElement) {
                body.appendChild(contentElement);
            }
        }
        $('modal-container').classList.remove('hidden');
    }

    function closeModal() {
        $('modal-container').classList.add('hidden');
        const body = $('modal-content');
        if (body) body.replaceChildren();
    }

    // ==========================================
    // 10. Lifecycle & Initialization
    // ==========================================
    function init() {
        setupTabNavigation();
        setupActions();

        // Check if running inside Android WebView bridge
        if (typeof window.AndroidBridge !== 'undefined' && typeof window.AndroidBridge.dispatch === 'function') {
            isBridgeAvailable = true;
            $('browser-fallback').classList.add('hidden');
            loadState();
            checkSharedIntent();

            // Background polling: poll every 1500ms when tab is visible
            pollTimer = setInterval(() => {
                if (document.visibilityState === 'visible') {
                    loadStateSilently();
                }
            }, 1500);
        } else {
            // Browser-only preview: show clear fallback notice without generating fake mock data
            isBridgeAvailable = false;
            $('browser-fallback').classList.remove('hidden');
            // Disable native action triggers in browser preview
            document.querySelectorAll('button:not(#btn-modal-close)').forEach(b => {
                b.title = '请在安卓客户端中打开使用';
            });
        }
    }

    // Launch when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
