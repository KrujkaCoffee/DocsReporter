document.addEventListener('DOMContentLoaded', () => {
(() => {
  const FALLBACK_SOURCES = [
    { code: 'tdocs', displayName: 'TDOCS' },
    { code: 'docs', displayName: 'DOCS' },
    { code: 'pow', displayName: 'POW' }
  ];

  const state = {
    sources: [],
    roles: [],
    activeTab: 'groups',
    groups: [],
    relations: [],
    selectedGroupId: null,
    selectedRelationGroupId: null
  };

  const $ = (id) => document.getElementById(id);
  const sourceSelect = $('adminSource');
  const roleSelect = $('adminRole');
  const queryInput = $('adminQuery');
  const enabledOnlyInput = $('adminEnabledOnly');
  const status = $('adminStatus');
  const list = $('adminList');
  const listTitle = $('adminListTitle');
  const listMeta = $('adminListMeta');
  const editor = $('adminEditor');
  const editorTitle = $('adminEditorTitle');
  const editorMeta = $('adminEditorMeta');
  const overview = $('adminOverview');

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

  function bool(value) {
    return value === true || value === 'true' || value === 1;
  }

  function setStatus(message, kind = 'info') {
    const prefix = kind === 'error' ? 'Ошибка' : kind === 'ok' ? 'Готово' : 'Статус';
    status.textContent = `${prefix}: ${message}`;
    status.classList.toggle('status-error', kind === 'error');
  }

  async function fetchJson(url, options = {}) {
    const response = await fetch(url, {
      credentials: 'include',
      headers: {
        'Accept': 'application/json',
        ...(options.body ? { 'Content-Type': 'application/json' } : {})
      },
      ...options
    });

    if (!response.ok) {
      let details = '';
      try {
        const text = await response.text();
        details = text ? `: ${text.slice(0, 500)}` : '';
      } catch { /* ignore */ }
      throw new Error(`${response.status} ${response.statusText}${details}`);
    }

    if (response.status === 204) return null;
    return response.json();
  }

  function sourceCode() {
    return sourceSelect.value || 'tdocs';
  }

  function roleCode() {
    return roleSelect.value || 'ProjectCardViewer';
  }

  function saveLocalState() {
    localStorage.setItem('docsReporter.admin', JSON.stringify({
      sourceCode: sourceCode(),
      roleCode: roleCode(),
      activeTab: state.activeTab,
      query: queryInput.value,
      enabledOnly: enabledOnlyInput.checked
    }));
  }

  function loadLocalState() {
    try {
      const raw = localStorage.getItem('docsReporter.admin');
      if (!raw) return {};
      const saved = JSON.parse(raw);
      state.activeTab = saved.activeTab || 'groups';
      queryInput.value = saved.query || '';
      enabledOnlyInput.checked = Boolean(saved.enabledOnly);
      return saved;
    } catch {
      return {};
    }
  }

  async function loadInitial() {
    const saved = loadLocalState();
    setStatus('Загрузка источников и ролей…');

    try {
      state.sources = asArray(await fetchJson('/api/reporter/admin/sources'));
    } catch (error) {
      state.sources = FALLBACK_SOURCES;
      setStatus(`Источники из админ API не получены, fallback: ${error.message}`, 'error');
    }

    try {
      state.roles = asArray(await fetchJson('/api/reporter/admin/roles'));
    } catch (error) {
      state.roles = [{ code: 'ProjectCardViewer', displayName: 'ProjectCardViewer' }];
      setStatus(`Роли из админ API не получены, fallback: ${error.message}`, 'error');
    }

    renderSelects(saved);
    renderTabs();
    await refreshAll();
  }

  function renderSelects(saved) {
    sourceSelect.innerHTML = state.sources.map(src => {
      const code = get(src, 'code', 'Code') || '';
      const title = get(src, 'displayName', 'DisplayName') || code;
      return `<option value="${attr(code)}">${escapeHtml(title)} (${escapeHtml(code)})</option>`;
    }).join('');

    roleSelect.innerHTML = state.roles.map(role => {
      const code = get(role, 'code', 'Code') || '';
      const title = get(role, 'displayName', 'DisplayName') || code;
      return `<option value="${attr(code)}">${escapeHtml(title)}</option>`;
    }).join('');

    if (saved.sourceCode && [...sourceSelect.options].some(option => option.value === saved.sourceCode)) {
      sourceSelect.value = saved.sourceCode;
    }
    if (saved.roleCode && [...roleSelect.options].some(option => option.value === saved.roleCode)) {
      roleSelect.value = saved.roleCode;
    }
  }

  function renderTabs() {
    document.querySelectorAll('.admin-tab').forEach(button => {
      button.classList.toggle('active', button.dataset.adminTab === state.activeTab);
    });
  }

  async function refreshAll() {
    saveLocalState();
    renderTabs();
    await Promise.allSettled([loadOverview(), loadList()]);
  }

  async function loadOverview() {
    try {
      const data = await fetchJson(`/api/reporter/admin/overview?sourceCode=${encodeURIComponent(sourceCode())}&roleCode=${encodeURIComponent(roleCode())}`);
      const cards = [
        ['Security', get(data, 'securityMode', 'SecurityMode')],
        ['Sources', get(data, 'sourceCount', 'SourceCount')],
        ['Groups', `${get(data, 'enabledGroupPolicyCount', 'EnabledGroupPolicyCount')}/${get(data, 'totalGroupCount', 'TotalGroupCount')}`],
        ['Relations', `${get(data, 'enabledRelationPolicyCount', 'EnabledRelationPolicyCount')}/${get(data, 'totalRelationCount', 'TotalRelationCount')}`]
      ];
      overview.innerHTML = cards.map(([label, value]) => `
        <div class="admin-overview-card">
          <span>${escapeHtml(label)}</span>
          <strong>${escapeHtml(value)}</strong>
        </div>
      `).join('');
    } catch (error) {
      overview.innerHTML = `<div class="security-warnings">${escapeHtml(error.message)}</div>`;
    }
  }

  async function loadList() {
    const query = queryInput.value.trim();
    const enabled = enabledOnlyInput.checked ? 'true' : 'false';
    setStatus('Загрузка настроек…');

    if (state.activeTab === 'groups') {
      const data = await fetchJson(`/api/reporter/admin/sources/${encodeURIComponent(sourceCode())}/groups?roleCode=${encodeURIComponent(roleCode())}&query=${encodeURIComponent(query)}&enabledOnly=${enabled}&take=300`);
      state.groups = asArray(data);
      listTitle.textContent = 'Справочники';
      listMeta.textContent = `${state.groups.length} строк`;
      renderGroupList();
    } else {
      const data = await fetchJson(`/api/reporter/admin/sources/${encodeURIComponent(sourceCode())}/relations?roleCode=${encodeURIComponent(roleCode())}&query=${encodeURIComponent(query)}&enabledOnly=${enabled}&take=300`);
      state.relations = asArray(data);
      listTitle.textContent = 'Связи';
      listMeta.textContent = `${state.relations.length} строк`;
      renderRelationList();
    }

    setStatus('Настройки загружены.', 'ok');
  }

  function renderGroupList() {
    if (!state.groups.length) {
      list.innerHTML = '<div class="security-empty">Справочники не найдены.</div>';
      editor.innerHTML = '<div class="security-empty">Выберите справочник.</div>';
      return;
    }

    list.innerHTML = state.groups.map(group => {
      const groupId = get(group, 'groupId', 'GroupId');
      const title = get(group, 'caption', 'Caption') || get(group, 'tableName', 'TableName');
      const table = get(group, 'tableName', 'TableName');
      const enabled = bool(get(group, 'isEnabled', 'IsEnabled'));
      const inMenu = bool(get(group, 'canSeeInMenu', 'CanSeeInMenu'));
      const active = String(state.selectedGroupId || '') === String(groupId);
      return `
        <button type="button" class="admin-row ${active ? 'active' : ''}" data-group-id="${attr(groupId)}">
          <span class="admin-row-main">
            <strong>${escapeHtml(title)}</strong>
            <small>${escapeHtml(table)} · PK ${escapeHtml(groupId)}</small>
          </span>
          <span class="admin-badges">
            ${enabled ? '<span class="admin-badge ok">on</span>' : '<span class="admin-badge muted">off</span>'}
            ${inMenu ? '<span class="admin-badge">menu</span>' : ''}
          </span>
        </button>`;
    }).join('');

    list.querySelectorAll('[data-group-id]').forEach(button => {
      button.addEventListener('click', () => {
        state.selectedGroupId = Number(button.dataset.groupId);
        const group = state.groups.find(item => Number(get(item, 'groupId', 'GroupId')) === state.selectedGroupId);
        renderGroupEditor(group);
        renderGroupList();
      });
    });

    const selected = state.groups.find(item => Number(get(item, 'groupId', 'GroupId')) === state.selectedGroupId) || state.groups[0];
    if (selected) {
      state.selectedGroupId = Number(get(selected, 'groupId', 'GroupId'));
      renderGroupEditor(selected);
    }
  }

  function renderRelationList() {
    if (!state.relations.length) {
      list.innerHTML = '<div class="security-empty">Связи не найдены.</div>';
      editor.innerHTML = '<div class="security-empty">Выберите связь.</div>';
      return;
    }

    list.innerHTML = state.relations.map(rel => {
      const relationGroupId = get(rel, 'relationGroupId', 'RelationGroupId');
      const title = get(rel, 'categoryTitle', 'CategoryTitle') || get(rel, 'displayName', 'DisplayName') || get(rel, 'relationCaption', 'RelationCaption') || get(rel, 'relationTable', 'RelationTable');
      const table = get(rel, 'relationTable', 'RelationTable');
      const enabled = bool(get(rel, 'isEnabled', 'IsEnabled'));
      const tree = bool(get(rel, 'showInTree', 'ShowInTree'));
      const active = String(state.selectedRelationGroupId || '') === String(relationGroupId);
      return `
        <button type="button" class="admin-row ${active ? 'active' : ''}" data-relation-id="${attr(relationGroupId)}">
          <span class="admin-row-main">
            <strong>${escapeHtml(title)}</strong>
            <small>${escapeHtml(table)} · PK ${escapeHtml(relationGroupId)}</small>
          </span>
          <span class="admin-badges">
            ${enabled ? '<span class="admin-badge ok">on</span>' : '<span class="admin-badge muted">off</span>'}
            ${tree ? '<span class="admin-badge">tree</span>' : ''}
          </span>
        </button>`;
    }).join('');

    list.querySelectorAll('[data-relation-id]').forEach(button => {
      button.addEventListener('click', () => {
        state.selectedRelationGroupId = Number(button.dataset.relationId);
        const relation = state.relations.find(item => Number(get(item, 'relationGroupId', 'RelationGroupId')) === state.selectedRelationGroupId);
        renderRelationEditor(relation);
        renderRelationList();
      });
    });

    const selected = state.relations.find(item => Number(get(item, 'relationGroupId', 'RelationGroupId')) === state.selectedRelationGroupId) || state.relations[0];
    if (selected) {
      state.selectedRelationGroupId = Number(get(selected, 'relationGroupId', 'RelationGroupId'));
      renderRelationEditor(selected);
    }
  }

  function checkbox(name, label, checked) {
    return `<label class="checkbox-field admin-check"><input name="${attr(name)}" type="checkbox" ${checked ? 'checked' : ''} /><span>${escapeHtml(label)}</span></label>`;
  }

  function numberField(name, label, value, min, max) {
    return `<label><span>${escapeHtml(label)}</span><input name="${attr(name)}" type="number" min="${attr(min)}" max="${attr(max)}" value="${attr(value ?? 0)}" /></label>`;
  }

  function textField(name, label, value) {
    return `<label><span>${escapeHtml(label)}</span><input name="${attr(name)}" type="text" value="${attr(value ?? '')}" /></label>`;
  }

  function renderGroupEditor(group) {
    if (!group) return;
    const groupId = get(group, 'groupId', 'GroupId');
    const title = get(group, 'caption', 'Caption') || get(group, 'tableName', 'TableName');
    editorTitle.textContent = title;
    editorMeta.textContent = `${get(group, 'tableName', 'TableName')} · PK ${groupId}`;
    editor.innerHTML = `
      <div class="admin-editor-grid">
        ${checkbox('isEnabled', 'Политика включена', bool(get(group, 'isEnabled', 'IsEnabled')))}
        ${checkbox('canSeeInMenu', 'Показывать в меню', bool(get(group, 'canSeeInMenu', 'CanSeeInMenu')))}
        ${checkbox('canSearch', 'Разрешить поиск', bool(get(group, 'canSearch', 'CanSearch')))}
        ${checkbox('canOpenCard', 'Разрешить карточку', bool(get(group, 'canOpenCard', 'CanOpenCard')))}
        ${checkbox('canExport', 'Экспорт', bool(get(group, 'canExport', 'CanExport')))}
        ${numberField('maxObjectDepth', 'Глубина объектов', get(group, 'maxObjectDepth', 'MaxObjectDepth'), 0, 3)}
        ${numberField('maxFileTreeDepth', 'Глубина файлов', get(group, 'maxFileTreeDepth', 'MaxFileTreeDepth'), 0, 6)}
        ${numberField('maxRowsPerPage', 'Строк на страницу', get(group, 'maxRowsPerPage', 'MaxRowsPerPage'), 1, 500)}
        ${numberField('maxRelatedObjects', 'Связанных объектов', get(group, 'maxRelatedObjects', 'MaxRelatedObjects'), 1, 5000)}
      </div>
      <div class="admin-editor-actions">
        <button class="primary-btn" type="submit">Сохранить</button>
        <button id="adminOpenRelations" class="ghost-btn" type="button">Связи этого справочника</button>
      </div>
      <div class="admin-technical">
        Type: ${escapeHtml(get(group, 'type', 'Type'))}; VisibleInTflex: ${escapeHtml(get(group, 'visibleInTflex', 'VisibleInTflex'))}; Policy: ${bool(get(group, 'isPolicyConfigured', 'IsPolicyConfigured')) ? 'configured' : 'not configured'}
      </div>
    `;

    editor.onsubmit = async (event) => {
      event.preventDefault();
      await saveGroup(groupId);
    };
    $('adminOpenRelations')?.addEventListener('click', async () => {
      state.activeTab = 'relations';
      queryInput.value = '';
      saveLocalState();
      renderTabs();
      await loadRelationsByGroup(groupId);
    });
  }

  function renderRelationEditor(rel) {
    if (!rel) return;
    const relationGroupId = get(rel, 'relationGroupId', 'RelationGroupId');
    const table = get(rel, 'relationTable', 'RelationTable');
    const title = get(rel, 'categoryTitle', 'CategoryTitle') || get(rel, 'displayName', 'DisplayName') || get(rel, 'relationCaption', 'RelationCaption') || table;
    editorTitle.textContent = title;
    editorMeta.textContent = `${table} · PK ${relationGroupId}`;
    editor.innerHTML = `
      <div class="admin-editor-grid">
        ${checkbox('isEnabled', 'Политика включена', bool(get(rel, 'isEnabled', 'IsEnabled')))}
        ${checkbox('canTraverse', 'Разрешить обход связи', bool(get(rel, 'canTraverse', 'CanTraverse')))}
        ${checkbox('showInCard', 'Показывать в карточке', bool(get(rel, 'showInCard', 'ShowInCard')))}
        ${checkbox('showInTree', 'Показывать в дереве файлов', bool(get(rel, 'showInTree', 'ShowInTree')))}
        ${textField('displayName', 'Название', get(rel, 'displayName', 'DisplayName') || get(rel, 'relationCaption', 'RelationCaption') || table)}
        ${textField('categoryCode', 'Код категории', get(rel, 'categoryCode', 'CategoryCode') || 'relation')}
        ${textField('categoryTitle', 'Заголовок категории', get(rel, 'categoryTitle', 'CategoryTitle') || title)}
        <label><span>Направление</span><select name="directionMode">
          ${['Both', 'MasterToSlave', 'SlaveToMaster'].map(value => `<option value="${value}" ${String(get(rel, 'directionMode', 'DirectionMode') || 'Both') === value ? 'selected' : ''}>${value}</option>`).join('')}
        </select></label>
        ${numberField('maxDepth', 'Глубина связи', get(rel, 'maxDepth', 'MaxDepth'), 0, 3)}
        ${numberField('maxItems', 'Лимит элементов', get(rel, 'maxItems', 'MaxItems'), 1, 5000)}
      </div>
      <div class="admin-editor-actions">
        <button class="primary-btn" type="submit">Сохранить</button>
      </div>
      <div class="admin-technical">
        ${escapeHtml(get(rel, 'masterTableName', 'MasterTableName') || get(rel, 'masterGroupId', 'MasterGroupId') || '?')} → ${escapeHtml(get(rel, 'slaveTableName', 'SlaveTableName') || get(rel, 'slaveGroupId', 'SlaveGroupId') || '?')}; Policy: ${bool(get(rel, 'isPolicyConfigured', 'IsPolicyConfigured')) ? 'configured' : 'not configured'}
      </div>
    `;

    editor.onsubmit = async (event) => {
      event.preventDefault();
      await saveRelation(relationGroupId);
    };
  }

  function formBool(name) {
    return editor.querySelector(`[name="${name}"]`)?.checked ?? false;
  }

  function formInt(name) {
    const value = Number(editor.querySelector(`[name="${name}"]`)?.value || 0);
    return Number.isFinite(value) ? value : 0;
  }

  function formText(name) {
    return editor.querySelector(`[name="${name}"]`)?.value?.trim() || null;
  }

  async function saveGroup(groupId) {
    const payload = {
      isEnabled: formBool('isEnabled'),
      canSeeInMenu: formBool('canSeeInMenu'),
      canSearch: formBool('canSearch'),
      canOpenCard: formBool('canOpenCard'),
      canExport: formBool('canExport'),
      maxObjectDepth: formInt('maxObjectDepth'),
      maxFileTreeDepth: formInt('maxFileTreeDepth'),
      maxRowsPerPage: formInt('maxRowsPerPage'),
      maxRelatedObjects: formInt('maxRelatedObjects')
    };

    await fetchJson(`/api/reporter/admin/sources/${encodeURIComponent(sourceCode())}/groups/${encodeURIComponent(groupId)}/policy?roleCode=${encodeURIComponent(roleCode())}`, {
      method: 'PUT',
      body: JSON.stringify(payload)
    });
    setStatus('Политика справочника сохранена.', 'ok');
    await refreshAll();
  }

  async function saveRelation(relationGroupId) {
    const payload = {
      isEnabled: formBool('isEnabled'),
      displayName: formText('displayName'),
      categoryCode: formText('categoryCode'),
      categoryTitle: formText('categoryTitle'),
      canTraverse: formBool('canTraverse'),
      showInCard: formBool('showInCard'),
      showInTree: formBool('showInTree'),
      maxDepth: formInt('maxDepth'),
      maxItems: formInt('maxItems'),
      directionMode: formText('directionMode')
    };

    await fetchJson(`/api/reporter/admin/sources/${encodeURIComponent(sourceCode())}/relations/${encodeURIComponent(relationGroupId)}/policy?roleCode=${encodeURIComponent(roleCode())}`, {
      method: 'PUT',
      body: JSON.stringify(payload)
    });
    setStatus('Политика связи сохранена.', 'ok');
    await refreshAll();
  }

  async function loadRelationsByGroup(groupId) {
    setStatus('Загрузка связей справочника…');
    const data = await fetchJson(`/api/reporter/admin/sources/${encodeURIComponent(sourceCode())}/relations?roleCode=${encodeURIComponent(roleCode())}&groupId=${encodeURIComponent(groupId)}&enabledOnly=false&take=300`);
    state.relations = asArray(data);
    listTitle.textContent = 'Связи справочника';
    listMeta.textContent = `${state.relations.length} строк`;
    renderRelationList();
    setStatus('Связи загружены.', 'ok');
  }

  async function runDiscovery() {
    setStatus('Discovery ParameterGroups…');
    const data = await fetchJson(`/api/reporter/admin/discovery/${encodeURIComponent(sourceCode())}/parameter-groups`, { method: 'POST' });
    setStatus(`Discovery: groups ${get(data, 'groupsUpserted', 'GroupsUpserted')}, relations ${get(data, 'relationsUpserted', 'RelationsUpserted')}`, 'ok');
    await refreshAll();
  }

  async function runBootstrap() {
    setStatus('Bootstrap ProjectCardViewer…');
    const data = await fetchJson(`/api/reporter/admin/bootstrap/project-card-viewer/${encodeURIComponent(sourceCode())}`, { method: 'POST' });
    setStatus(`Bootstrap: groups ${get(data, 'groupPoliciesUpserted', 'GroupPoliciesUpserted')}, relations ${get(data, 'relationPoliciesUpserted', 'RelationPoliciesUpserted')}`, 'ok');
    await refreshAll();
  }

  document.querySelectorAll('.admin-tab').forEach(button => {
    button.addEventListener('click', async () => {
      state.activeTab = button.dataset.adminTab || 'groups';
      saveLocalState();
      await refreshAll();
    });
  });

  $('adminRefresh').addEventListener('click', refreshAll);
  $('adminDiscover').addEventListener('click', () => runDiscovery().catch(error => setStatus(error.message, 'error')));
  $('adminBootstrap').addEventListener('click', () => runBootstrap().catch(error => setStatus(error.message, 'error')));
  sourceSelect.addEventListener('change', refreshAll);
  roleSelect.addEventListener('change', refreshAll);
  enabledOnlyInput.addEventListener('change', refreshAll);
  queryInput.addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      refreshAll();
    }
  });

  loadInitial().catch(error => setStatus(error.message, 'error'));
})();
});
