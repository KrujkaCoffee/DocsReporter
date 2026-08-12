document.addEventListener('DOMContentLoaded', () => {
(() => {
  const state = {
    catalog: [],
    selectedKey: null,
    selectedSourceCode: null,
    schema: null,
    rootItems: [],
    children: new Map(),
    expanded: new Set(),
    selectedObjectId: null,
    page: 1,
    hasMore: false,
    searchMode: false
  };

  const $ = (id) => document.getElementById(id);
  const sourceSelect = $('referenceSource');
  const groupSelect = $('referenceGroup');
  const queryInput = $('referenceQuery');
  const pageSizeSelect = $('referencePageSize');
  const status = $('referenceStatus');
  const catalogRoot = $('referenceCatalog');
  const catalogMeta = $('referenceCatalogMeta');
  const title = $('referenceTitle');
  const meta = $('referenceMeta');
  const warnings = $('referenceWarnings');
  const tableRoot = $('referenceTable');
  const detail = $('referenceDetail');
  const detailMeta = $('referenceDetailMeta');
  const pageLabel = $('referencePageLabel');
  const prevButton = $('referencePrev');
  const nextButton = $('referenceNext');

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
    status.textContent = `${prefix}: ${message}`;
    status.classList.toggle('status-error', kind === 'error');
  }

  async function fetchJson(url, options = {}) {
    const response = await fetch(url, {
      credentials: 'include',
      headers: { 'Accept': 'application/json', ...(options.body ? { 'Content-Type': 'application/json' } : {}) },
      ...options
    });

    if (!response.ok) {
      const text = await response.text();
      throw new Error(`${response.status} ${response.statusText}${text ? `: ${text.slice(0, 500)}` : ''}`);
    }

    return response.status === 204 ? null : response.json();
  }

  function normalizeCatalogItem(raw) {
    return {
      key: get(raw, 'key', 'Key') || '',
      tableName: get(raw, 'tableName', 'TableName') || '',
      caption: get(raw, 'caption', 'Caption') || get(raw, 'tableName', 'TableName') || '',
      commonGroupGuid: get(raw, 'commonGroupGuid', 'CommonGroupGuid') || null,
      isHierarchical: Boolean(get(raw, 'isHierarchical', 'IsHierarchical')),
      sources: asArray(get(raw, 'sources', 'Sources')).map(source => ({
        sourceCode: get(source, 'sourceCode', 'SourceCode') || '',
        sourceDisplayName: get(source, 'sourceDisplayName', 'SourceDisplayName') || get(source, 'sourceCode', 'SourceCode') || '',
        groupId: Number(get(source, 'groupId', 'GroupId')),
        groupGuid: get(source, 'groupGuid', 'GroupGuid') || null,
        hierarchyType: get(source, 'hierarchyType', 'HierarchyType'),
        canSearch: Boolean(get(source, 'canSearch', 'CanSearch')),
        canOpenCard: Boolean(get(source, 'canOpenCard', 'CanOpenCard')),
        maxRowsPerPage: Number(get(source, 'maxRowsPerPage', 'MaxRowsPerPage') || 50)
      }))
    };
  }

  function normalizeSchema(raw) {
    return {
      sourceCode: get(raw, 'sourceCode', 'SourceCode') || '',
      sourceDisplayName: get(raw, 'sourceDisplayName', 'SourceDisplayName') || '',
      groupId: Number(get(raw, 'groupId', 'GroupId')),
      groupGuid: get(raw, 'groupGuid', 'GroupGuid') || null,
      tableName: get(raw, 'tableName', 'TableName') || '',
      caption: get(raw, 'caption', 'Caption') || '',
      hasHierarchy: Boolean(get(raw, 'hasHierarchy', 'HasHierarchy')),
      codeField: get(raw, 'codeField', 'CodeField') || null,
      nameField: get(raw, 'nameField', 'NameField') || null,
      maxRowsPerPage: Number(get(raw, 'maxRowsPerPage', 'MaxRowsPerPage') || 50),
      fields: asArray(get(raw, 'fields', 'Fields')).map(field => ({
        name: get(field, 'name', 'Name') || '',
        label: get(field, 'label', 'Label') || get(field, 'name', 'Name') || '',
        dataType: get(field, 'dataType', 'DataType') || '',
        isNullable: Boolean(get(field, 'isNullable', 'IsNullable')),
        isSystem: Boolean(get(field, 'isSystem', 'IsSystem')),
        isPrimaryDisplay: Boolean(get(field, 'isPrimaryDisplay', 'IsPrimaryDisplay')),
        isSearchable: Boolean(get(field, 'isSearchable', 'IsSearchable')),
        sortOrder: Number(get(field, 'sortOrder', 'SortOrder') || 0)
      })).sort((a, b) => a.sortOrder - b.sortOrder)
    };
  }

  function normalizeRow(raw) {
    return {
      objectId: Number(get(raw, 'objectId', 'ObjectId')),
      guid: get(raw, 'guid', 'Guid') || null,
      parentId: get(raw, 'parentId', 'ParentId') == null ? null : Number(get(raw, 'parentId', 'ParentId')),
      hasChildren: Boolean(get(raw, 'hasChildren', 'HasChildren')),
      objectCode: get(raw, 'objectCode', 'ObjectCode') || '',
      name: get(raw, 'name', 'Name') || '',
      docsUrl: get(raw, 'docsUrl', 'DocsUrl') || null,
      values: get(raw, 'values', 'Values') || {}
    };
  }

  function selectedCatalogItem() {
    return state.catalog.find(item => item.key === state.selectedKey) || null;
  }

  function selectedBinding() {
    const item = selectedCatalogItem();
    if (!item) return null;
    return item.sources.find(source => source.sourceCode === state.selectedSourceCode) || item.sources[0] || null;
  }

  function saveState() {
    localStorage.setItem('docsReporter.references', JSON.stringify({
      selectedKey: state.selectedKey,
      selectedSourceCode: state.selectedSourceCode,
      pageSize: pageSizeSelect.value
    }));
  }

  function loadState() {
    try {
      const raw = localStorage.getItem('docsReporter.references');
      return raw ? JSON.parse(raw) : {};
    } catch {
      return {};
    }
  }

  async function initialize() {
    const saved = loadState();
    setStatus('Получение доступных справочников…');
    const raw = await fetchJson('/api/reporter/references/catalog');
    state.catalog = asArray(raw).map(normalizeCatalogItem).filter(item => item.sources.length > 0);

    if (!state.catalog.length) {
      catalogRoot.innerHTML = '<div class="security-empty">Нет справочников с включенным флагом «Показывать в меню». Откройте управление и выполните начальную настройку.</div>';
      catalogMeta.textContent = '0 справочников';
      setStatus('Доступные справочники не найдены.', 'error');
      return;
    }

    state.selectedKey = state.catalog.some(item => item.key === saved.selectedKey)
      ? saved.selectedKey
      : state.catalog[0].key;
    const selected = selectedCatalogItem();
    state.selectedSourceCode = selected.sources.some(source => source.sourceCode === saved.selectedSourceCode)
      ? saved.selectedSourceCode
      : selected.sources[0].sourceCode;

    if (saved.pageSize && [...pageSizeSelect.options].some(option => option.value === String(saved.pageSize))) {
      pageSizeSelect.value = String(saved.pageSize);
    }

    renderCatalog();
    renderGroupSelect();
    renderSourceSelect();
    await loadSelectedReference();
  }

  function renderCatalog() {
    catalogMeta.textContent = `${state.catalog.length} справочников`;
    catalogRoot.innerHTML = state.catalog.map(item => {
      const active = item.key === state.selectedKey ? ' active' : '';
      const hierarchy = item.isHierarchical ? 'Иерархический' : 'Табличный';
      return `
        <button class="reference-catalog-item${active}" type="button" data-reference-key="${attr(item.key)}">
          <span class="reference-catalog-icon">${item.isHierarchical ? '⌘' : '▦'}</span>
          <span class="reference-catalog-text">
            <strong>${escapeHtml(item.caption)}</strong>
            <small>${escapeHtml(item.tableName)} · ${escapeHtml(hierarchy)} · ${item.sources.length} ист.</small>
          </span>
        </button>`;
    }).join('');

    catalogRoot.querySelectorAll('[data-reference-key]').forEach(button => {
      button.addEventListener('click', async () => {
        state.selectedKey = button.dataset.referenceKey;
        const item = selectedCatalogItem();
        state.selectedSourceCode = item?.sources[0]?.sourceCode || null;
        queryInput.value = '';
        state.page = 1;
        renderCatalog();
        renderGroupSelect();
        renderSourceSelect();
        saveState();
        await loadSelectedReference();
      });
    });
  }

  function renderGroupSelect() {
    groupSelect.innerHTML = state.catalog.map(item =>
      `<option value="${attr(item.key)}">${escapeHtml(item.caption)}</option>`
    ).join('');
    groupSelect.value = state.selectedKey || '';
  }

  function renderSourceSelect() {
    const item = selectedCatalogItem();
    sourceSelect.innerHTML = asArray(item?.sources).map(source =>
      `<option value="${attr(source.sourceCode)}">${escapeHtml(source.sourceDisplayName)} (${escapeHtml(source.sourceCode)})</option>`
    ).join('');
    sourceSelect.value = state.selectedSourceCode || '';
  }

  async function loadSelectedReference() {
    const binding = selectedBinding();
    if (!binding) return;

    setStatus('Загрузка структуры справочника…');
    state.schema = normalizeSchema(await fetchJson(
      `/api/reporter/references/sources/${encodeURIComponent(binding.sourceCode)}/groups/${binding.groupId}/schema`
    ));

    const allowedPageSize = Math.max(1, Math.min(100, state.schema.maxRowsPerPage || 50));
    [...pageSizeSelect.options].forEach(option => { option.disabled = Number(option.value) > allowedPageSize; });
    if (Number(pageSizeSelect.value) > allowedPageSize) pageSizeSelect.value = String(allowedPageSize);

    state.rootItems = [];
    state.children.clear();
    state.expanded.clear();
    state.selectedObjectId = null;
    state.page = 1;
    renderDetailEmpty();
    await loadItems();
  }

  async function loadItems() {
    const binding = selectedBinding();
    if (!binding || !state.schema) return;

    const query = queryInput.value.trim();
    state.searchMode = query.length > 0;
    setStatus(state.searchMode ? 'Поиск объектов…' : 'Загрузка объектов…');

    const params = new URLSearchParams({
      page: String(state.page),
      pageSize: String(Number(pageSizeSelect.value || 50))
    });
    if (query) params.set('query', query);

    const raw = await fetchJson(
      `/api/reporter/references/sources/${encodeURIComponent(binding.sourceCode)}/groups/${binding.groupId}/items?${params.toString()}`
    );

    state.rootItems = asArray(get(raw, 'items', 'Items')).map(normalizeRow);
    state.children.clear();
    state.expanded.clear();
    state.hasMore = Boolean(get(raw, 'hasMore', 'HasMore'));
    const warningRows = asArray(get(raw, 'warnings', 'Warnings'));
    warnings.innerHTML = warningRows.map(value => `<div>${escapeHtml(value)}</div>`).join('');

    renderTable();
    const count = state.rootItems.length;
    const mode = state.searchMode ? 'результатов поиска' : 'объектов';
    meta.textContent = `${count} ${mode} · ${state.schema.tableName} · ID ${state.schema.groupId}`;
    title.textContent = state.schema.caption;
    pageLabel.textContent = `Страница ${state.page}`;
    prevButton.disabled = state.page <= 1;
    nextButton.disabled = !state.hasMore;
    setStatus(`Загружено ${count} объектов из ${binding.sourceDisplayName}.`, 'ok');
    saveState();
  }

  function visibleColumns() {
    if (!state.schema) return [];
    const primary = state.schema.fields.filter(field => field.isPrimaryDisplay);
    const others = state.schema.fields.filter(field => !field.isPrimaryDisplay && !field.isSystem).slice(0, 3);
    return [...primary, ...others].filter((field, index, array) =>
      array.findIndex(other => other.name.toLowerCase() === field.name.toLowerCase()) === index
    ).slice(0, 5);
  }

  function flattenTree(items, depth = 0, output = []) {
    for (const item of items) {
      output.push({ item, depth });
      if (state.expanded.has(item.objectId)) {
        flattenTree(state.children.get(item.objectId) || [], depth + 1, output);
      }
    }
    return output;
  }

  function renderTable() {
    if (!state.rootItems.length) {
      tableRoot.innerHTML = '<div class="security-empty">Объекты не найдены.</div>';
      return;
    }

    const columns = visibleColumns();
    const rows = state.searchMode ? state.rootItems.map(item => ({ item, depth: 0 })) : flattenTree(state.rootItems);
    tableRoot.innerHTML = `
      <table class="reference-table">
        <thead>
          <tr>
            <th>Объект</th>
            ${columns.map(field => `<th>${escapeHtml(field.label)}</th>`).join('')}
          </tr>
        </thead>
        <tbody>
          ${rows.map(({ item, depth }) => renderRow(item, depth, columns)).join('')}
        </tbody>
      </table>`;

    tableRoot.querySelectorAll('[data-object-id]').forEach(row => {
      row.addEventListener('click', event => {
        if (event.target.closest('[data-expand-id]')) return;
        loadObject(Number(row.dataset.objectId)).catch(error => setStatus(error.message, 'error'));
      });
    });

    tableRoot.querySelectorAll('[data-expand-id]').forEach(button => {
      button.addEventListener('click', async event => {
        event.stopPropagation();
        const objectId = Number(button.dataset.expandId);
        await toggleNode(objectId);
      });
    });
  }

  function renderRow(item, depth, columns) {
    const active = state.selectedObjectId === item.objectId ? ' active' : '';
    const label = item.objectCode || item.name || `Объект ${item.objectId}`;
    const expand = state.schema?.hasHierarchy && item.hasChildren && !state.searchMode
      ? `<button class="reference-expand" type="button" data-expand-id="${item.objectId}" aria-label="Раскрыть">${state.expanded.has(item.objectId) ? '−' : '+'}</button>`
      : '<span class="reference-expand-placeholder"></span>';

    return `
      <tr class="reference-row${active}" data-object-id="${item.objectId}">
        <td>
          <div class="reference-object-cell" style="--tree-depth:${depth}">
            ${expand}
            <span>
              <strong>${escapeHtml(label)}</strong>
              <small>ID ${item.objectId}${item.name && item.name !== label ? ` · ${escapeHtml(item.name)}` : ''}</small>
            </span>
          </div>
        </td>
        ${columns.map(field => `<td>${escapeHtml(valueFor(item, field.name) || '—')}</td>`).join('')}
      </tr>`;
  }

  function valueFor(item, fieldName) {
    const values = item.values || {};
    if (Object.prototype.hasOwnProperty.call(values, fieldName)) return values[fieldName];
    const match = Object.keys(values).find(key => key.toLowerCase() === fieldName.toLowerCase());
    return match ? values[match] : null;
  }

  async function toggleNode(objectId) {
    if (state.expanded.has(objectId)) {
      state.expanded.delete(objectId);
      renderTable();
      return;
    }

    if (!state.children.has(objectId)) {
      const binding = selectedBinding();
      const params = new URLSearchParams({ parentId: String(objectId), page: '1', pageSize: '100' });
      setStatus(`Загрузка вложенных объектов для ID ${objectId}…`);
      const raw = await fetchJson(
        `/api/reporter/references/sources/${encodeURIComponent(binding.sourceCode)}/groups/${binding.groupId}/items?${params.toString()}`
      );
      state.children.set(objectId, asArray(get(raw, 'items', 'Items')).map(normalizeRow));
    }

    state.expanded.add(objectId);
    renderTable();
    setStatus(`Вложенные объекты ID ${objectId} загружены.`, 'ok');
  }

  async function loadObject(objectId) {
    const binding = selectedBinding();
    state.selectedObjectId = objectId;
    renderTable();
    detailMeta.textContent = `ID ${objectId}`;
    detail.innerHTML = '<div class="notice"><span class="loader">Загрузка параметров</span></div>';

    const raw = await fetchJson(
      `/api/reporter/references/sources/${encodeURIComponent(binding.sourceCode)}/groups/${binding.groupId}/items/${objectId}`
    );
    const object = normalizeRow(get(raw, 'object', 'Object'));
    renderObjectDetail(object);
  }

  function renderObjectDetail(object) {
    const fieldMap = new Map(asArray(state.schema?.fields).map(field => [field.name.toLowerCase(), field]));
    const entries = Object.entries(object.values || {})
      .map(([name, value]) => ({ field: fieldMap.get(name.toLowerCase()), name, value }))
      .sort((a, b) => (a.field?.sortOrder || 99999) - (b.field?.sortOrder || 99999));

    detailMeta.textContent = object.objectCode || object.name || `ID ${object.objectId}`;
    detail.innerHTML = `
      <div class="reference-detail-hero">
        <span class="reference-detail-id">ID ${object.objectId}</span>
        <h3>${escapeHtml(object.objectCode || object.name || `Объект ${object.objectId}`)}</h3>
        ${object.name && object.name !== object.objectCode ? `<p>${escapeHtml(object.name)}</p>` : ''}
        <div class="reference-detail-actions">
          ${object.docsUrl ? `<a class="primary-btn reference-link-button" href="${attr(object.docsUrl)}" target="_blank" rel="noreferrer">Открыть в T-FLEX</a>` : ''}
        </div>
      </div>
      <dl class="reference-property-list">
        ${entries.map(entry => `
          <div>
            <dt>${escapeHtml(entry.field?.label || entry.name)}</dt>
            <dd>${escapeHtml(entry.value || '—')}</dd>
          </div>`).join('')}
        <div><dt>UUID</dt><dd>${escapeHtml(object.guid || '—')}</dd></div>
        <div><dt>Родительский объект</dt><dd>${escapeHtml(object.parentId ?? '—')}</dd></div>
      </dl>`;
  }

  function renderDetailEmpty() {
    detailMeta.textContent = 'Объект не выбран';
    detail.innerHTML = '<div class="security-empty">Нажмите на строку, чтобы увидеть параметры.</div>';
  }

  groupSelect.addEventListener('change', async () => {
    state.selectedKey = groupSelect.value;
    const item = selectedCatalogItem();
    state.selectedSourceCode = item?.sources[0]?.sourceCode || null;
    queryInput.value = '';
    state.page = 1;
    renderCatalog();
    renderSourceSelect();
    saveState();
    await loadSelectedReference();
  });

  sourceSelect.addEventListener('change', async () => {
    state.selectedSourceCode = sourceSelect.value;
    state.page = 1;
    saveState();
    await loadSelectedReference();
  });

  $('referenceSearch').addEventListener('click', async () => {
    state.page = 1;
    await loadItems();
  });

  $('referenceReset').addEventListener('click', async () => {
    queryInput.value = '';
    state.page = 1;
    await loadItems();
  });

  queryInput.addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      state.page = 1;
      loadItems().catch(error => setStatus(error.message, 'error'));
    }
  });

  pageSizeSelect.addEventListener('change', async () => {
    state.page = 1;
    saveState();
    await loadItems();
  });

  prevButton.addEventListener('click', async () => {
    if (state.page <= 1) return;
    state.page -= 1;
    await loadItems();
  });

  nextButton.addEventListener('click', async () => {
    if (!state.hasMore) return;
    state.page += 1;
    await loadItems();
  });

  initialize().catch(error => {
    setStatus(error.message, 'error');
    tableRoot.innerHTML = `<div class="security-empty">${escapeHtml(error.message)}</div>`;
  });
})();
});
