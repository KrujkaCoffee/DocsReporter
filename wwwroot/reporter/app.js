document.addEventListener('DOMContentLoaded', () => {
(() => {
  const PROJECT_CARD_GROUP_ID = 891;
  const FALLBACK_SOURCES = [
    { sourceId: 0, code: 'tdocs', displayName: 'TDOCS', baseDocsUrl: null, isEnabled: true },
    { sourceId: 0, code: 'docs', displayName: 'DOCS', baseDocsUrl: null, isEnabled: true },
    { sourceId: 0, code: 'pow', displayName: 'POW', baseDocsUrl: null, isEnabled: true }
  ];

  const state = {
    sources: [],
    selectedSources: new Set(),
    results: [],
    activeResultKey: null,
    activeCard: null,
    activeTab: 'card',
    loading: false,
    lastQuery: ''
  };

  const $ = (id) => document.getElementById(id);
  const sourceList = $('sourceList');
  const searchInput = $('searchInput');
  const searchBtn = $('searchBtn');
  const searchResults = $('searchResults');
  const resultMeta = $('resultMeta');
  const statusLog = $('statusLog');
  const depthSelect = $('depthSelect');
  const fileDepthSelect = $('fileDepthSelect');
  const pageSizeSelect = $('pageSizeSelect');
  const manualSourceInput = $('manualSourceInput');

  function get(obj, ...names) {
    if (obj == null) return undefined;
    for (const name of names) {
      if (Object.prototype.hasOwnProperty.call(obj, name)) return obj[name];
    }
    return undefined;
  }

  function asArray(value) {
    return Array.isArray(value) ? value : [];
  }

  function escapeHtml(value) {
    return String(value ?? '')
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#039;');
  }

  function attr(value) {
    return escapeHtml(value).replaceAll('`', '&#096;');
  }

  function setStatus(message, kind = 'info') {
    const prefix = kind === 'error' ? 'Ошибка' : kind === 'ok' ? 'Готово' : 'Статус';
    statusLog.textContent = `${prefix}: ${message}`;
  }

  function setBusy(isBusy, label = 'Найти') {
    state.loading = isBusy;
    searchBtn.disabled = isBusy;
    searchBtn.textContent = isBusy ? 'Идет поиск…' : label;
  }

  async function fetchJson(url, options = {}) {
    const response = await fetch(url, {
      credentials: 'include',
      headers: { 'Accept': 'application/json' },
      ...options
    });

    if (!response.ok) {
      let details = '';
      try {
        const text = await response.text();
        details = text ? `: ${text.slice(0, 300)}` : '';
      } catch { /* ignore */ }
      throw new Error(`${response.status} ${response.statusText}${details}`);
    }

    if (response.status === 204) return null;
    return response.json();
  }

  function loadLocalState() {
    try {
      const raw = localStorage.getItem('docsReporter.ui');
      if (!raw) return;
      const saved = JSON.parse(raw);
      if (Array.isArray(saved.selectedSources)) {
        state.selectedSources = new Set(saved.selectedSources.filter(Boolean));
      }
      if (saved.query) searchInput.value = saved.query;
      if (saved.depth) depthSelect.value = String(saved.depth);
      if (saved.fileDepth) fileDepthSelect.value = String(saved.fileDepth);
    } catch { /* ignore broken local storage */ }
  }

  function saveLocalState() {
    const payload = {
      selectedSources: [...state.selectedSources],
      query: searchInput.value.trim(),
      depth: depthSelect.value,
      fileDepth: fileDepthSelect.value
    };
    localStorage.setItem('docsReporter.ui', JSON.stringify(payload));
  }

  function normalizeSource(src) {
    return {
      sourceId: get(src, 'sourceId', 'SourceId') ?? 0,
      code: get(src, 'code', 'Code') ?? '',
      displayName: get(src, 'displayName', 'DisplayName') ?? get(src, 'code', 'Code') ?? '',
      baseDocsUrl: get(src, 'baseDocsUrl', 'BaseDocsUrl') ?? null,
      isEnabled: get(src, 'isEnabled', 'IsEnabled') ?? true
    };
  }

  async function loadSources() {
    sourceList.innerHTML = '<div class="notice"><span class="loader">Загрузка источников</span></div>';
    try {
      const sources = await fetchJson('/api/reporter/ui/sources').catch(() => fetchJson('/api/reporter/sources'));
      state.sources = asArray(sources).map(normalizeSource).filter(s => s.code);
      if (!state.sources.length) throw new Error('API вернул пустой список источников.');
      setStatus(`Источников: ${state.sources.length}`, 'ok');
    } catch (error) {
      state.sources = FALLBACK_SOURCES;
      setStatus(`Не удалось получить список источников, включен fallback: ${error.message}`, 'error');
    }

    if (!state.selectedSources.size && state.sources.length) {
      state.selectedSources.add(state.sources[0].code);
    }
    renderSources();
    saveLocalState();
  }

  function renderSources() {
    const template = $('sourceTemplate');
    sourceList.innerHTML = '';

    for (const source of state.sources) {
      const node = template.content.firstElementChild.cloneNode(true);
      const checkbox = node.querySelector('input');
      const text = node.querySelector('.source-text');
      const health = node.querySelector('.source-health');
      checkbox.checked = state.selectedSources.has(source.code);
      text.textContent = source.displayName || source.code;
      text.title = source.code;
      health.textContent = source.code;
      node.classList.toggle('active', checkbox.checked);
      node.addEventListener('change', () => {
        if (checkbox.checked) state.selectedSources.add(source.code);
        else state.selectedSources.delete(source.code);
        node.classList.toggle('active', checkbox.checked);
        saveLocalState();
      });
      node.addEventListener('dblclick', () => checkHealth(source.code, health));
      sourceList.appendChild(node);
    }
  }

  async function checkHealth(sourceCode, healthElement) {
    healthElement.textContent = '…';
    healthElement.className = 'source-health';
    try {
      const health = await fetchJson(`/api/reporter/sources/${encodeURIComponent(sourceCode)}/health`);
      healthElement.textContent = 'ok';
      healthElement.classList.add('ok');
      setStatus(`${sourceCode}: ${get(health, 'database', 'Database') ?? 'online'}`, 'ok');
    } catch (error) {
      healthElement.textContent = '!';
      healthElement.classList.add('fail');
      setStatus(`${sourceCode}: ${error.message}`, 'error');
    }
  }

  function getSelectedSources() {
    const selected = [...state.selectedSources].filter(Boolean);
    return selected.length ? selected : state.sources.slice(0, 1).map(s => s.code);
  }

  function normalizeSearchItem(item, sourceCode) {
    const objectId = get(item, 'objectId', 'ObjectId');
    return {
      sourceCode: get(item, 'sourceCode', 'SourceCode') ?? sourceCode,
      objectId,
      guid: get(item, 'guid', 'Guid'),
      objectCode: get(item, 'objectCode', 'ObjectCode') ?? '',
      name: get(item, 'name', 'Name') ?? '',
      docsUrl: get(item, 'docsUrl', 'DocsUrl') ?? null,
      key: `${get(item, 'sourceCode', 'SourceCode') ?? sourceCode}:${objectId}`
    };
  }

  async function search() {
    const query = searchInput.value.trim();
    const selectedSources = getSelectedSources();
    const pageSize = Number(pageSizeSelect.value || 50);
    state.lastQuery = query;
    state.results = [];
    state.activeResultKey = null;
    saveLocalState();

    if (!query) {
      searchResults.className = 'results-list empty-state';
      searchResults.textContent = 'Введите шифр или часть наименования.';
      resultMeta.textContent = 'Нет данных';
      return;
    }

    setBusy(true);
    searchResults.className = 'results-list';
    searchResults.innerHTML = '<div class="notice"><span class="loader">Поиск по выбранным источникам</span></div>';
    resultMeta.textContent = selectedSources.join(', ');

    const tasks = selectedSources.map(async (sourceCode) => {
      const url = `/api/reporter/sources/${encodeURIComponent(sourceCode)}/project-cards/search?query=${encodeURIComponent(query)}&page=1&pageSize=${pageSize}`;
      try {
        const data = await fetchJson(url);
        return { sourceCode, ok: true, items: asArray(data).map(x => normalizeSearchItem(x, sourceCode)) };
      } catch (error) {
        return { sourceCode, ok: false, error: error.message, items: [] };
      }
    });

    const responses = await Promise.all(tasks);
    state.results = responses.flatMap(x => x.items);
    renderSearchResults(responses);
    const failed = responses.filter(x => !x.ok);
    if (failed.length) {
      setStatus(`Поиск завершен частично. Ошибки: ${failed.map(x => x.sourceCode).join(', ')}`, 'error');
    } else {
      setStatus(`Найдено: ${state.results.length}`, 'ok');
    }
    setBusy(false);
  }

  function renderSearchResults(responses) {
    const total = responses.reduce((acc, r) => acc + r.items.length, 0);
    resultMeta.textContent = `${total} найдено`;

    if (!total && responses.every(r => r.ok)) {
      searchResults.className = 'results-list empty-state';
      searchResults.textContent = 'Ничего не найдено. Попробуйте другой шифр или включите другой source.';
      return;
    }

    const html = responses.map(response => {
      const title = `<div class="result-group-title"><span>${escapeHtml(response.sourceCode)}</span><span>${response.ok ? response.items.length + ' шт.' : 'ошибка'}</span></div>`;
      if (!response.ok) {
        return `${title}<div class="notice">${escapeHtml(response.error)}</div>`;
      }
      if (!response.items.length) {
        return `${title}<div class="notice">Нет совпадений.</div>`;
      }
      return title + response.items.map(item => renderResultCard(item)).join('');
    }).join('');

    searchResults.className = 'results-list';
    searchResults.innerHTML = html;
    searchResults.querySelectorAll('[data-result-key]').forEach(node => {
      node.addEventListener('click', () => {
        const key = node.getAttribute('data-result-key');
        const item = state.results.find(x => x.key === key);
        if (item) openCard(item);
      });
    });
  }

  function renderResultCard(item) {
    const code = item.objectCode || `Object ${item.objectId}`;
    const name = item.name || 'Без наименования';
    return `
      <button type="button" class="result-card ${state.activeResultKey === item.key ? 'active' : ''}" data-result-key="${attr(item.key)}">
        <span class="result-main">
          <span class="result-code">${escapeHtml(code)}</span>
          <span class="result-name">${escapeHtml(name)}</span>
          <span class="result-sub">objectId: ${escapeHtml(item.objectId)}${item.guid ? ' · ' + escapeHtml(item.guid) : ''}</span>
        </span>
        <span class="badge">${escapeHtml(item.sourceCode)}</span>
      </button>`;
  }

  async function openCard(item) {
    state.activeResultKey = item.key;
    renderSearchResults(groupCurrentResults());
    const depth = Number(depthSelect.value || 2);
    const fileDepth = Number(fileDepthSelect.value || 4);
    const detailsRoot = $('detailsRoot');
    detailsRoot.className = 'details-empty';
    detailsRoot.innerHTML = '<div class="notice"><span class="loader">Загрузка карточки и файловых категорий</span></div>';

    try {
      const url = `/api/reporter/sources/${encodeURIComponent(item.sourceCode)}/project-cards/${encodeURIComponent(item.objectId)}/full-card?depth=${depth}&fileDepth=${fileDepth}`;
      const card = await fetchJson(url);
      state.activeCard = card;
      state.activeTab = 'card';
      renderDetails();
      const cardDto = normalizeCard(card);
      setStatus(`Открыта карточка ${cardDto.card.objectCode || cardDto.card.objectId}`, 'ok');
      updateUrl(item);
    } catch (error) {
      detailsRoot.className = 'details-empty';
      detailsRoot.innerHTML = `<div class="empty-card"><div class="empty-icon">!</div><h2>Не удалось открыть карточку</h2><p>${escapeHtml(error.message)}</p></div>`;
      setStatus(error.message, 'error');
    }
  }

  function groupCurrentResults() {
    const map = new Map();
    for (const item of state.results) {
      if (!map.has(item.sourceCode)) map.set(item.sourceCode, []);
      map.get(item.sourceCode).push(item);
    }
    return [...map].map(([sourceCode, items]) => ({ sourceCode, ok: true, items }));
  }

  function normalizePreview(preview) {
    return {
      groupId: get(preview, 'groupId', 'GroupId'),
      tableName: get(preview, 'tableName', 'TableName'),
      objectId: get(preview, 'objectId', 'ObjectId'),
      guid: get(preview, 'guid', 'Guid'),
      objectCode: get(preview, 'objectCode', 'ObjectCode'),
      name: get(preview, 'name', 'Name'),
      docsUrl: get(preview, 'docsUrl', 'DocsUrl')
    };
  }


  function normalizeProperty(raw) {
    return {
      code: get(raw, 'code', 'Code') ?? '',
      label: get(raw, 'label', 'Label') ?? '',
      value: get(raw, 'value', 'Value') ?? '',
      group: get(raw, 'group', 'Group') ?? 'Карточка',
      sortOrder: get(raw, 'sortOrder', 'SortOrder') ?? 0
    };
  }

  function normalizeCard(card) {
    return {
      sourceCode: get(card, 'sourceCode', 'SourceCode'),
      groupId: get(card, 'groupId', 'GroupId') ?? PROJECT_CARD_GROUP_ID,
      card: normalizePreview(get(card, 'card', 'Card') ?? {}),
      properties: asArray(get(card, 'properties', 'Properties')).map(normalizeProperty),
      relations: asArray(get(card, 'relations', 'Relations')),
      fileCategories: asArray(get(card, 'fileCategories', 'FileCategories')),
      requestedDepth: get(card, 'requestedDepth', 'RequestedDepth'),
      effectiveDepth: get(card, 'effectiveDepth', 'EffectiveDepth'),
      requestedFileDepth: get(card, 'requestedFileDepth', 'RequestedFileDepth'),
      effectiveFileDepth: get(card, 'effectiveFileDepth', 'EffectiveFileDepth'),
      technicalTrace: get(card, 'technicalTrace', 'TechnicalTrace') ?? {}
    };
  }

  function renderDetails() {
    if (!state.activeCard) return;
    const dto = normalizeCard(state.activeCard);
    const card = dto.card;
    const title = card.objectCode || card.name || `Object ${card.objectId}`;

    const detailsRoot = $('detailsRoot');
    detailsRoot.className = 'details-root';
    detailsRoot.innerHTML = `
      <div class="card-hero">
        <div class="card-yellow-strip">${escapeHtml(title)}</div>
        <div class="card-hero-meta">
          <span>${escapeHtml(dto.sourceCode)} · groupId ${escapeHtml(dto.groupId)} · objectId ${escapeHtml(card.objectId)}</span>
          ${card.docsUrl ? `<a class="docs-link" href="${attr(card.docsUrl)}" target="_blank" rel="noreferrer">Открыть в DOCs</a>` : ''}
        </div>
      </div>
      <div class="tabbar">
        ${tabButton('card', 'Карточка')}
        ${tabButton('files', `Файлы (${countFiles(dto.fileCategories)})`)}
        ${tabButton('relations', `Связи (${dto.relations.length})`)}
        ${tabButton('tech', 'Технически')}
      </div>
      <div id="tabContent" class="tab-content"></div>`;

    detailsRoot.querySelectorAll('[data-tab]').forEach(btn => {
      btn.addEventListener('click', () => {
        state.activeTab = btn.getAttribute('data-tab') || 'card';
        renderDetails();
      });
    });

    const tabContent = $('tabContent');
    if (state.activeTab === 'files') tabContent.innerHTML = renderFiles(dto.fileCategories);
    else if (state.activeTab === 'relations') tabContent.innerHTML = renderRelations(dto.relations);
    else if (state.activeTab === 'tech') tabContent.innerHTML = renderTech(dto);
    else tabContent.innerHTML = renderCardTab(dto);
  }

  function tabButton(tab, title) {
    return `<button type="button" class="tab-button ${state.activeTab === tab ? 'active' : ''}" data-tab="${attr(tab)}">${escapeHtml(title)}</button>`;
  }

  function countFiles(categories) {
    return categories.reduce((acc, c) => acc + Number(get(c, 'fileCount', 'FileCount') || 0), 0);
  }

  function renderCardTab(dto) {
    const card = dto.card;
    const propertyMap = new Map(dto.properties.map(p => [p.code, p]));
    const prop = (code) => propertyMap.get(code)?.value;
    const objectCode = prop('object') || prop('productCode') || card.objectCode || '';
    const parts = splitProjectCode(objectCode || '');
    const projectNumber = prop('projectNumber') || parts.project || '—';
    const positionNumber = prop('positionNumber') || parts.position || '—';
    const displayName = prop('name') || card.name || '—';
    const comment = prop('comment') || prop('variantComment') || '';
    const relationGroups = groupRelations(dto.relations);
    return `
      <div class="info-grid">
        ${infoCell('Шифр изделия', objectCode || parts.prefix || '—', 'green')}
        ${infoCell('Номер проекта', projectNumber, 'blue')}
        ${infoCell('Номер позиции', positionNumber, 'orange')}
        ${infoCell('Наименование', displayName, 'red')}
      </div>
      <div class="card-comment">
        <div class="card-comment-title">Комментарий</div>
        <div class="card-comment-body">${escapeHtml(comment)}</div>
      </div>
      <div class="summary-strip">
        ${metric(dto.fileCategories.length, 'категорий файлов')}
        ${metric(countFiles(dto.fileCategories), 'файлов')}
        ${metric(dto.relations.length, 'связей')}
        ${metric(dto.properties.length, 'полей')}
      </div>
      ${renderProperties(dto.properties)}
      <div class="relation-group">
        <div class="relation-head">
          <div><div class="relation-title">Краткая карточка</div><div class="relation-meta">основные идентификаторы</div></div>
        </div>
        <table class="table-like">
          <tbody>
            <tr><th>Source</th><td>${escapeHtml(dto.sourceCode)}</td></tr>
            <tr><th>Group</th><td>${escapeHtml(dto.groupId)} / ${escapeHtml(card.tableName || 'Kartochka_proekta')}</td></tr>
            <tr><th>ObjectID</th><td>${escapeHtml(card.objectId)}</td></tr>
            <tr><th>GUID</th><td>${escapeHtml(card.guid || '—')}</td></tr>
            <tr><th>DOCs URL</th><td>${card.docsUrl ? `<a class="docs-link" href="${attr(card.docsUrl)}" target="_blank" rel="noreferrer">${escapeHtml(card.docsUrl)}</a>` : '—'}</td></tr>
          </tbody>
        </table>
      </div>
      ${Object.keys(relationGroups).length ? `<div class="notice">Быстрые группы связей: ${escapeHtml(Object.keys(relationGroups).join(', '))}</div>` : ''}
    `;
  }

  function renderProperties(properties) {
    if (!properties.length) {
      return '<div class="notice">Список полей пока пуст. API вернул только preview карточки.</div>';
    }

    const groups = new Map();
    for (const property of properties) {
      if (!groups.has(property.group)) groups.set(property.group, []);
      groups.get(property.group).push(property);
    }

    return [...groups.entries()].map(([group, items]) => `
      <div class="relation-group">
        <div class="relation-head">
          <div><div class="relation-title">${escapeHtml(group)}</div><div class="relation-meta">${items.length} полей</div></div>
        </div>
        <table class="table-like">
          <tbody>
            ${items.map(item => `<tr><th>${escapeHtml(item.label)}</th><td>${escapeHtml(item.value || '—')}</td></tr>`).join('')}
          </tbody>
        </table>
      </div>`).join('');
  }

  function splitProjectCode(code) {
    const value = String(code || '').trim();
    const match = value.match(/^([А-ЯA-ZЁ]{1,4})[.\-_\s]?(\d+)(?:[.\-_\s]?(\d+))?/i);
    if (!match) return { prefix: value, project: '', position: '' };
    return { prefix: match[1], project: match[2], position: match[3] || '' };
  }

  function infoCell(label, value, colorClass = '') {
    return `<div class="info-cell"><div class="info-label ${attr(colorClass)}">${escapeHtml(label)}</div><div class="info-value">${escapeHtml(value)}</div></div>`;
  }

  function metric(value, label) {
    return `<div class="metric"><div class="metric-value">${escapeHtml(value)}</div><div class="metric-label">${escapeHtml(label)}</div></div>`;
  }

  function renderFiles(categoriesRaw) {
    const categories = asArray(categoriesRaw);
    if (!categories.length) return '<div class="notice">Файловые категории не найдены. Проверьте bootstrap relations и связи Link_891_16 / DocumentFiles.</div>';

    return categories.map(categoryRaw => {
      const category = normalizeCategory(categoryRaw);
      const rootsHtml = category.roots.length
        ? category.roots.map(node => renderTreeNode(node, 0)).join('')
        : category.flatFiles.length
          ? category.flatFiles.map(node => renderTreeNode(node, 0)).join('')
          : '<div class="notice">В категории нет файлов.</div>';
      return `
        <div class="category-card">
          <div class="category-head">
            <div>
              <div class="category-title">${escapeHtml(category.title || category.code)}</div>
              <div class="category-meta">${escapeHtml(category.relationTable || 'relation auto')} · roots: ${category.rootCount}</div>
            </div>
            <span class="badge">${category.fileCount} файлов</span>
          </div>
          <div class="tree">${rootsHtml}</div>
        </div>`;
    }).join('');
  }

  function normalizeCategory(raw) {
    return {
      code: get(raw, 'code', 'Code') ?? '',
      title: get(raw, 'title', 'Title') ?? '',
      relationTable: get(raw, 'relationTable', 'RelationTable') ?? '',
      rootCount: get(raw, 'rootCount', 'RootCount') ?? 0,
      fileCount: get(raw, 'fileCount', 'FileCount') ?? 0,
      roots: asArray(get(raw, 'roots', 'Roots')).map(normalizeFileNode),
      flatFiles: asArray(get(raw, 'flatFiles', 'FlatFiles')).map(normalizeFileNode)
    };
  }

  function normalizeFileNode(raw) {
    return {
      sourceCode: get(raw, 'sourceCode', 'SourceCode') ?? '',
      objectId: get(raw, 'objectId', 'ObjectId'),
      version: get(raw, 'version', 'Version'),
      guid: get(raw, 'guid', 'Guid'),
      parentId: get(raw, 'parentId', 'ParentId'),
      name: get(raw, 'name', 'Name') ?? `Object ${get(raw, 'objectId', 'ObjectId')}`,
      extension: get(raw, 'extension', 'Extension') ?? '',
      nodeKind: get(raw, 'nodeKind', 'NodeKind') ?? 'file',
      stage: get(raw, 'stage', 'Stage') ?? '',
      downloadUrl: get(raw, 'downloadUrl', 'DownloadUrl') ?? '',
      children: asArray(get(raw, 'children', 'Children')).map(normalizeFileNode)
    };
  }

  function renderTreeNode(node, level) {
    const isFile = String(node.nodeKind || '').toLowerCase() === 'file';
    const icon = isFile ? fileIcon(node.extension) : '📁';
    const name = node.downloadUrl && isFile
      ? `<a class="file-link" href="${attr(node.downloadUrl)}" target="_blank" rel="noreferrer">${escapeHtml(node.name)}</a>`
      : `<span class="node-name">${escapeHtml(node.name)}</span>`;
    const meta = [node.stage, node.version ? `ред. ${node.version}` : null, node.extension].filter(Boolean).join(' · ');
    const children = node.children.length
      ? `<div class="node-children">${node.children.map(child => renderTreeNode(child, level + 1)).join('')}</div>`
      : '';

    return `
      <div class="tree-node" style="--level:${level}">
        <div class="node-line">
          <span>${icon}</span>
          ${name}
          ${meta ? `<span class="node-meta">${escapeHtml(meta)}</span>` : ''}
        </div>
        ${children}
      </div>`;
  }

  function fileIcon(ext) {
    const e = String(ext || '').toLowerCase();
    if (['pdf'].includes(e)) return '📕';
    if (['grb', 'dwg', 'dxf', 'step', 'stp'].includes(e)) return '📐';
    if (['doc', 'docx', 'rtf'].includes(e)) return '📄';
    if (['xls', 'xlsx', 'csv'].includes(e)) return '📊';
    if (['zip', 'rar', '7z'].includes(e)) return '🗜️';
    return '📄';
  }

  function normalizeRelation(raw) {
    const targetRaw = get(raw, 'target', 'Target');
    return {
      relationTable: get(raw, 'relationTable', 'RelationTable') ?? '',
      displayName: get(raw, 'displayName', 'DisplayName') ?? '',
      categoryCode: get(raw, 'categoryCode', 'CategoryCode') ?? '',
      categoryTitle: get(raw, 'categoryTitle', 'CategoryTitle') ?? '',
      direction: get(raw, 'direction', 'Direction') ?? '',
      masterId: get(raw, 'masterId', 'MasterId'),
      slaveId: get(raw, 'slaveId', 'SlaveId'),
      target: targetRaw ? normalizePreview(targetRaw) : null
    };
  }

  function groupRelations(relationsRaw) {
    const groups = {};
    for (const relRaw of asArray(relationsRaw)) {
      const rel = normalizeRelation(relRaw);
      const key = rel.categoryTitle || rel.displayName || rel.relationTable || 'Связь';
      if (!groups[key]) groups[key] = [];
      groups[key].push(rel);
    }
    return groups;
  }

  function renderRelations(relationsRaw) {
    const groups = groupRelations(relationsRaw);
    const names = Object.keys(groups);
    if (!names.length) return '<div class="notice">Связи не найдены или не разрешены политикой RelationAccessPolicy.</div>';

    return names.map(name => {
      const rows = groups[name].map(rel => {
        const target = rel.target;
        const targetLabel = target ? `${target.objectCode || ''} ${target.name || ''}`.trim() || `Object ${target.objectId}` : '—';
        return `<tr>
          <td>${escapeHtml(rel.direction)}</td>
          <td>${escapeHtml(rel.relationTable)}</td>
          <td>${escapeHtml(rel.masterId)}</td>
          <td>${escapeHtml(rel.slaveId)}</td>
          <td>${target?.docsUrl ? `<a class="docs-link" href="${attr(target.docsUrl)}" target="_blank" rel="noreferrer">${escapeHtml(targetLabel)}</a>` : escapeHtml(targetLabel)}</td>
        </tr>`;
      }).join('');
      return `
        <div class="relation-group">
          <div class="relation-head">
            <div>
              <div class="relation-title">${escapeHtml(name)}</div>
              <div class="relation-meta">${groups[name].length} связей</div>
            </div>
          </div>
          <table class="table-like">
            <thead><tr><th>Напр.</th><th>Таблица связи</th><th>MasterID</th><th>SlaveID</th><th>Цель</th></tr></thead>
            <tbody>${rows}</tbody>
          </table>
        </div>`;
    }).join('');
  }

  function renderTech(dto) {
    return `
      <div class="summary-strip">
        ${metric(dto.requestedDepth ?? '—', 'requested depth')}
        ${metric(dto.effectiveDepth ?? '—', 'effective depth')}
        ${metric(dto.requestedFileDepth ?? '—', 'requested file depth')}
        ${metric(dto.effectiveFileDepth ?? '—', 'effective file depth')}
      </div>
      <div class="tech-box">
        <div class="relation-head">
          <div><div class="relation-title">Technical trace</div><div class="relation-meta">для диагностики discovery/relations</div></div>
        </div>
        <pre class="raw-json">${escapeHtml(JSON.stringify(dto.technicalTrace, null, 2))}</pre>
      </div>
      <div class="tech-box">
        <div class="relation-head">
          <div><div class="relation-title">Raw card DTO</div><div class="relation-meta">как пришло из API</div></div>
        </div>
        <pre class="raw-json">${escapeHtml(JSON.stringify(state.activeCard, null, 2))}</pre>
      </div>`;
  }

  function updateUrl(item) {
    const url = new URL(window.location.href);
    url.searchParams.set('source', item.sourceCode);
    url.searchParams.set('objectId', item.objectId);
    if (state.lastQuery) url.searchParams.set('q', state.lastQuery);
    history.replaceState(null, '', url.toString());
  }

  function addManualSource() {
    const code = manualSourceInput.value.trim();
    if (!code) return;
    if (!state.sources.some(s => s.code.toLowerCase() === code.toLowerCase())) {
      state.sources.push({ sourceId: 0, code, displayName: code, baseDocsUrl: null, isEnabled: true });
    }
    state.selectedSources.add(code);
    manualSourceInput.value = '';
    renderSources();
    saveLocalState();
  }

  async function openFromQueryParams() {
    const url = new URL(window.location.href);
    const q = url.searchParams.get('q');
    const source = url.searchParams.get('source');
    const objectId = url.searchParams.get('objectId');
    if (q) searchInput.value = q;
    if (source) state.selectedSources.add(source);
    renderSources();
    if (source && objectId) {
      await openCard({ sourceCode: source, objectId, objectCode: q || '', name: '', key: `${source}:${objectId}` });
    } else if (q) {
      await search();
    }
  }

  function bindEvents() {
    searchBtn.addEventListener('click', search);
    searchInput.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') search();
    });
    $('reloadSourcesBtn').addEventListener('click', loadSources);
    $('selectAllSourcesBtn').addEventListener('click', () => {
      const allSelected = state.sources.every(s => state.selectedSources.has(s.code));
      if (allSelected) state.selectedSources.clear();
      else state.sources.forEach(s => state.selectedSources.add(s.code));
      renderSources();
      saveLocalState();
    });
    $('addManualSourceBtn').addEventListener('click', addManualSource);
    manualSourceInput.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') addManualSource();
    });
    depthSelect.addEventListener('change', saveLocalState);
    fileDepthSelect.addEventListener('change', saveLocalState);
    pageSizeSelect.addEventListener('change', saveLocalState);
    $('toggleThemeBtn').addEventListener('click', () => {
      document.body.classList.toggle('high-contrast');
      localStorage.setItem('docsReporter.highContrast', document.body.classList.contains('high-contrast') ? '1' : '0');
    });
  }

    async function init() {
    await loadSources();
    bindEvents();
    loadLocalState();
    if (localStorage.getItem('docsReporter.highContrast') === '1') document.body.classList.add('high-contrast');
    
    await openFromQueryParams();
  }

  init().catch(error => {
      setStatus(error.message, 'error');
      console.log(error)
  });
})();


});