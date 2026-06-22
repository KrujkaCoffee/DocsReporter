document.addEventListener('DOMContentLoaded', () => {
  const state = {
    sources: [],
    current: null,
    preview: null
  };

  const $ = (id) => document.getElementById(id);

  function get(obj, ...names) {
    if (obj == null) return undefined;

    for (const name of names) {
      if (Object.prototype.hasOwnProperty.call(obj, name)) {
        return obj[name];
      }
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

  async function fetchJson(url) {
    const response = await fetch(url, {
      credentials: 'include',
      headers: {
        Accept: 'application/json'
      }
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');

      throw new Error(
        `${response.status} ${response.statusText}`
        + (text ? `: ${text.slice(0, 300)}` : '')
      );
    }

    return response.json();
  }

  function setStatus(message, kind = 'info') {
    const node = $('securityStatus');

    node.textContent = message;
    node.classList.toggle(
      'security-error',
      kind === 'error'
    );
    node.classList.toggle(
      'security-ok',
      kind === 'ok'
    );
  }

  function row(label, value) {
    return `
      <div class="security-kv">
        <span>${escapeHtml(label)}</span>
        <strong>${escapeHtml(value ?? '—')}</strong>
      </div>`;
  }

  async function loadSources() {
    const raw = await fetchJson('/api/reporter/ui/sources')
      .catch(() => fetchJson('/api/reporter/sources'));

    state.sources = asArray(raw)
      .map(source => ({
        code: get(source, 'code', 'Code'),
        displayName:
          get(source, 'displayName', 'DisplayName')
          || get(source, 'code', 'Code')
      }))
      .filter(source => source.code);

    const select = $('securitySource');

    select.innerHTML = state.sources
      .map(source => `
        <option value="${escapeHtml(source.code)}">
          ${escapeHtml(source.displayName)}
          (${escapeHtml(source.code)})
        </option>`)
      .join('');
  }

  async function loadCurrent() {
    const sourceCodes = state.sources
      .map(source => source.code)
      .join(',');

    state.current = await fetchJson(
      `/api/reporter/security/me?sources=${encodeURIComponent(sourceCodes)}`
    );

    renderPrincipal();
  }

  function renderPrincipal() {
    const dto = state.current || {};
    const roles = asArray(get(dto, 'appRoles', 'AppRoles'));

    $('securityPrincipal').innerHTML = [
      row('Login', get(dto, 'login', 'Login')),
      row('SID', get(dto, 'sid', 'Sid')),
      row(
        'Аутентифицирован',
        get(dto, 'isAuthenticated', 'IsAuthenticated')
          ? 'да'
          : 'нет'
      ),
      row(
        'Authentication type',
        get(dto, 'authenticationType', 'AuthenticationType')
      ),
      row(
        'Debug identity',
        get(dto, 'isDebugIdentity', 'IsDebugIdentity')
          ? 'да'
          : 'нет'
      ),
      row('AppUserId', get(dto, 'appUserId', 'AppUserId')),
      row(
        'Роли',
        roles.length ? roles.join(', ') : '—'
      ),
      row(
        'Security mode',
        get(dto, 'securityMode', 'SecurityMode')
      )
    ].join('');
  }

  function selectedIdentity(sourceCode) {
    return asArray(get(state.current, 'sources', 'Sources'))
      .find(item =>
        String(get(item, 'sourceCode', 'SourceCode'))
          .toLowerCase()
        === sourceCode.toLowerCase()
      );
  }

  async function refreshPreview() {
    const sourceCode = $('securitySource').value;
    const referenceId = Number(
      $('securityReferenceId').value || 891
    );
    const objectRaw = $('securityObjectId').value.trim();
    const objectPart = objectRaw
      ? `?objectId=${encodeURIComponent(objectRaw)}`
      : '';

    if (!sourceCode) {
      setStatus('Нет доступных источников.', 'error');
      return;
    }

    setStatus(
      `Чтение ${sourceCode}: reference ${referenceId}…`
    );

    try {
      state.preview = await fetchJson(
        `/api/reporter/security/sources/`
        + `${encodeURIComponent(sourceCode)}`
        + `/references/${referenceId}/preview`
        + objectPart
      );

      renderPreview();

      setStatus(
        `Готово: `
        + `${get(state.preview, 'totalRows', 'TotalRows') ?? 0}`
        + ` строк xAccessRights.`,
        'ok'
      );
    } catch (error) {
      setStatus(error.message, 'error');
    }
  }

  function renderPreview() {
    const dto = state.preview || {};
    const sourceCode = $('securitySource').value;

    const identity =
      get(dto, 'identity', 'Identity')
      || selectedIdentity(sourceCode)
      || {};

    const hierarchy = asArray(
      get(identity, 'hierarchy', 'Hierarchy')
    );

    const policy =
      get(dto, 'appPolicy', 'AppPolicy')
      || {};

    const reference =
      get(dto, 'reference', 'Reference')
      || {};

    const rights = asArray(
      get(dto, 'rows', 'Rows')
    );

    const warnings = asArray(
      get(dto, 'warnings', 'Warnings')
    );

    $('securityDocsIdentity').innerHTML = [
      row(
        'Источник',
        `${get(identity, 'displayName', 'DisplayName') || sourceCode}`
        + ` (${sourceCode})`
      ),
      row('Статус', get(identity, 'status', 'Status')),
      row('Метод', get(identity, 'matchMethod', 'MatchMethod')),
      row(
        's_ObjectID',
        get(identity, 'docsUserObjectId', 'DocsUserObjectId')
      ),
      row(
        'Login',
        get(identity, 'docsLogin', 'DocsLogin')
      ),
      row(
        'ФИО',
        get(identity, 'docsFullName', 'DocsFullName')
      ),
      row(
        'Email',
        get(identity, 'docsEmail', 'DocsEmail')
      ),
      row(
        'Ошибка',
        get(identity, 'error', 'Error')
      )
    ].join('');

    $('securityHierarchy').innerHTML = hierarchy.length
      ? `
        <table class="security-table">
          <thead>
            <tr>
              <th>Depth</th>
              <th>ObjectID</th>
              <th>ChildID</th>
              <th>Основная</th>
              <th>Название</th>
              <th>Login</th>
            </tr>
          </thead>
          <tbody>
            ${hierarchy.map(node => `
              <tr>
                <td>${escapeHtml(get(node, 'depth', 'Depth'))}</td>
                <td>${escapeHtml(get(node, 'objectId', 'ObjectId'))}</td>
                <td>
                  ${escapeHtml(
                    get(node, 'childObjectId', 'ChildObjectId')
                    ?? '—'
                  )}
                </td>
                <td>
                  ${get(node, 'isFirstUse', 'IsFirstUse')
                    ? 'да'
                    : 'нет'}
                </td>
                <td>
                  ${escapeHtml(
                    get(node, 'fullName', 'FullName')
                    || (
                      get(node, 'isSelf', 'IsSelf')
                        ? 'Текущий пользователь'
                        : '—'
                    )
                  )}
                </td>
                <td>
                  ${escapeHtml(
                    get(node, 'login', 'Login')
                    || '—'
                  )}
                </td>
              </tr>`)
              .join('')}
          </tbody>
        </table>`
      : '<div class="security-empty">Иерархия не найдена.</div>';

    $('securityPolicy').innerHTML = [
      row(
        'Настроена',
        get(policy, 'isConfigured', 'IsConfigured')
          ? 'да'
          : 'нет'
      ),
      row(
        'В меню',
        get(policy, 'canSeeInMenu', 'CanSeeInMenu')
          ? 'да'
          : 'нет'
      ),
      row(
        'Поиск',
        get(policy, 'canSearch', 'CanSearch')
          ? 'да'
          : 'нет'
      ),
      row(
        'Карточка',
        get(policy, 'canOpenCard', 'CanOpenCard')
          ? 'да'
          : 'нет'
      ),
      row(
        'Экспорт',
        get(policy, 'canExport', 'CanExport')
          ? 'да'
          : 'нет'
      ),
      row(
        'Глубина объекта',
        get(policy, 'maxObjectDepth', 'MaxObjectDepth')
      ),
      row(
        'Глубина файлов',
        get(policy, 'maxFileTreeDepth', 'MaxFileTreeDepth')
      ),
      row(
        'Строк на страницу',
        get(policy, 'maxRowsPerPage', 'MaxRowsPerPage')
      ),
      row(
        'Связанных объектов',
        get(policy, 'maxRelatedObjects', 'MaxRelatedObjects')
      ),
      row(
        'Разрешенных связей',
        get(policy, 'allowedRelationCount', 'AllowedRelationCount')
      )
    ].join('');

    $('securityReference').innerHTML = [
      row(
        'Reference ID',
        get(reference, 'referenceId', 'ReferenceId')
      ),
      row(
        'TableName',
        get(reference, 'tableName', 'TableName')
      ),
      row(
        'Caption',
        get(reference, 'caption', 'Caption')
      ),
      row(
        'Principal Object IDs',
        asArray(
          get(dto, 'principalObjectIds', 'PrincipalObjectIds')
        ).join(', ') || '—'
      )
    ].join('');

    $('securityWarnings').innerHTML = warnings.length
      ? `
        <div class="security-warnings">
          ${warnings
            .map(message => `
              <div>⚠ ${escapeHtml(message)}</div>`)
            .join('')}
        </div>`
      : '';

    $('securityRightsMeta').textContent =
      `${get(dto, 'applicableRows', 'ApplicableRows') ?? 0}`
      + `/`
      + `${get(dto, 'totalRows', 'TotalRows') ?? rights.length}`
      + ` применимы к user/group chain`;

    $('securityRights').innerHTML = rights.length
      ? `
        <table class="security-table rights-table">
          <thead>
            <tr>
              <th>Прим.</th>
              <th>Type</th>
              <th>Scope</th>
              <th>AccessGroup</th>
              <th>UserID</th>
              <th>Reference</th>
              <th>Object</th>
              <th>Stage</th>
              <th>Direction</th>
              <th>Commands</th>
              <th>xReference/xObject</th>
            </tr>
          </thead>
          <tbody>
            ${rights.map(item => {
              const commands = asArray(
                get(item, 'commands', 'Commands')
              );

              const commandText = commands
                .map(command =>
                  `${get(command, 'commandId', 'CommandId')}:`
                  + `${get(command, 'enabled', 'Enabled') ? '+' : '−'}`
                )
                .join(' ');

              const applies = get(
                item,
                'appliesToCurrentPrincipal',
                'AppliesToCurrentPrincipal'
              );

              return `
                <tr class="${applies ? 'right-applies' : ''}">
                  <td>${applies ? '●' : '○'}</td>
                  <td>
                    ${escapeHtml(
                      get(item, 'accessTypeId', 'AccessTypeId')
                    )}
                  </td>
                  <td>
                    ${escapeHtml(
                      get(item, 'scope', 'Scope')
                    )}
                  </td>
                  <td>
                    ${escapeHtml(
                      get(item, 'accessGroupId', 'AccessGroupId')
                    )}
                    ·
                    ${escapeHtml(
                      get(item, 'accessGroupName', 'AccessGroupName')
                      || '—'
                    )}
                  </td>
                  <td>
                    ${escapeHtml(
                      get(item, 'userId', 'UserId')
                    )}
                  </td>
                  <td>
                    ${escapeHtml(
                      get(item, 'referenceId', 'ReferenceId')
                    )}
                  </td>
                  <td>
                    ${escapeHtml(
                      get(item, 'objectId', 'ObjectId')
                    )}
                  </td>
                  <td>
                    ${escapeHtml(
                      get(item, 'stageId', 'StageId')
                    )}
                  </td>
                  <td>
                    ${escapeHtml(
                      get(item, 'accessDirection', 'AccessDirection')
                    )}
                  </td>
                  <td class="commands-cell">
                    ${escapeHtml(commandText || '—')}
                  </td>
                  <td>
                    ${escapeHtml(
                      get(item, 'xReferenceId', 'XReferenceId')
                    )}
                    /
                    ${escapeHtml(
                      get(item, 'xObjectId', 'XObjectId')
                    )}
                  </td>
                </tr>`;
            }).join('')}
          </tbody>
        </table>`
      : `
        <div class="security-empty">
          Для выбранного reference/object строки не найдены.
        </div>`;
  }

  async function init() {
    await loadSources();
    await loadCurrent();

    $('securityRefresh')
      .addEventListener('click', refreshPreview);

    $('securitySource')
      .addEventListener('change', refreshPreview);

    $('securityReferenceId')
      .addEventListener('keydown', event => {
        if (event.key === 'Enter') {
          refreshPreview();
        }
      });

    $('securityObjectId')
      .addEventListener('keydown', event => {
        if (event.key === 'Enter') {
          refreshPreview();
        }
      });

    await refreshPreview();
  }

  init()
    .catch(error => setStatus(error.message, 'error'));
});
