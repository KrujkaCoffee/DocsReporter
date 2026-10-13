(function () {
  "use strict";
  const M = window.ProductViewModel;
  const $ = id => document.getElementById(id);
  const base = new URL("../../", window.location.href);
  const initial = new URLSearchParams(window.location.search);
  const state = { source: "", query: "", card: null, root: null, roots: [], asOf: "", path: [], selected: null,
    category: "pdf", folderPath: [], searchCursor: null, fileCursor: null, cardEpoch: 0, searchEpoch: 0, fileEpoch: 0 };
  const requests = new Map();
  let selectedNodeButton = null;
  function el(tag, text, className) {
    const node = document.createElement(tag);
    if (text !== undefined && text !== null) node.textContent = String(text);
    if (className) node.className = className;
    return node;
  }
  function status(target, message, error = false) { $(target).textContent = message; $(target).classList.toggle("error", error); }
  function clear(node) { node.replaceChildren(); }
  async function get(url, channel) {
    requests.get(channel)?.abort();
    const controller = new AbortController(); requests.set(channel, controller);
    try {
      const response = await fetch(new URL(url, base), { credentials: "same-origin", cache: "no-store", signal: controller.signal });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(body.error || (response.status === 401 ? "Требуется вход через Windows." : "Не удалось загрузить данные."));
      return body;
    } finally { if (requests.get(channel) === controller) requests.delete(channel); }
  }
  function failed(error, target) { if (error.name !== "AbortError") status(target, error.message, true); }
  function resetCard() {
    state.cardEpoch++; state.fileEpoch++; state.card = null; state.root = null; state.selected = null;
    state.roots = []; state.path = []; state.folderPath = []; state.fileCursor = null;
    for (const [key, request] of requests) if (key !== "search") request.abort();
    clear($("tree")); clear($("files")); clear($("occurrence")); clear($("productRoot")); clear($("fileRoot"));
    $("cardCode").textContent = "Выберите карточку"; $("cardName").textContent = "";
    $("selectedCode").textContent = "—"; $("selectedName").textContent = ""; $("folderName").textContent = "";
    $("rootChooser").hidden = true; $("fileRoot").hidden = true; $("fileRootLabel").hidden = true;
    $("moreFiles").hidden = true; $("folderBack").disabled = true; $("rootFiles").disabled = true;
    $("contextSource").textContent = ""; $("asOf").textContent = "";
    status("fileStatus", "Выберите изделие."); status("cardStatus", "");
  }
  async function search(append = false) {
    const epoch = ++state.searchEpoch;
    if (!append) {
      resetCard(); clear($("cards")); state.searchCursor = null;
      state.query = $("query").value.trim();
    }
    if (!state.source || state.query.length < 2) return;
    $("moreCards").hidden = true; $("searchButton").disabled = true; status("searchStatus", "Поиск…");
    try {
      const data = await get(M.api(state.source, null, null, "", { query: state.query, after: append ? state.searchCursor : 0 }), "search");
      if (epoch !== state.searchEpoch) return;
      for (const card of data.items) {
        const button = el("button", null, "card-result"); button.type = "button";
        button.append(el("strong", card.code || "Без обозначения"), el("span", card.name || "Без наименования"));
        button.addEventListener("click", () => {
          document.querySelectorAll(".card-result").forEach(b => b.classList.remove("selected"));
          button.classList.add("selected"); openCard(card.objectId);
        });
        $("cards").append(button);
      }
      state.searchCursor = data.nextCursor; $("moreCards").hidden = !data.hasMore;
      status("searchStatus", $("cards").children.length ? "Выберите карточку из результатов." : "Доступные карточки не найдены в прочитанной части списка.");
    } catch (error) { if (epoch === state.searchEpoch) failed(error, "searchStatus"); }
    finally { if (epoch === state.searchEpoch) $("searchButton").disabled = false; }
  }
  async function openCard(cardId) {
    resetCard(); const epoch = state.cardEpoch; status("cardStatus", "Загрузка назначенного изделия…");
    try {
      const data = await get(M.api(state.source, cardId, null, "roots", {}), "card");
      if (epoch !== state.cardEpoch) return;
      state.card = data.card; state.roots = data.roots; state.asOf = data.asOf;
      $("cardCode").textContent = data.card.code || "Без обозначения"; $("cardName").textContent = data.card.name || "";
      $("contextSource").textContent = $("source").selectedOptions[0]?.textContent || state.source;
      $("asOf").textContent = "Применимость состава: " + data.asOf.split("-").reverse().join(".");
      const url = new URL(location.href); url.searchParams.set("source", state.source); url.searchParams.set("cardId", String(cardId));
      url.searchParams.delete("objectId"); history.replaceState(null, "", url);
      clear($("productRoot"));
      for (const root of data.roots) $("productRoot").append(new Option((root.denotation || "") + " — " + (root.name || ""), String(root.objectId)));
      $("rootChooser").hidden = data.roots.length < 2;
      if (!data.roots.length) { status("cardStatus", "Назначенное изделие не найдено среди доступных сохранённых объектов."); return; }
      status("cardStatus", ""); chooseRoot(data.roots[0]);
    } catch (error) { if (epoch === state.cardEpoch) failed(error, "cardStatus"); }
  }
  function chooseRoot(root) {
    state.cardEpoch++; state.root = root; state.path = []; clear($("tree"));
    $("rootFiles").disabled = false; const list = el("ul"); $("tree").append(list);
    const node = { object: root, path: [], amount: null, position: null, unit: null, bomSection: null, expansionState: "available" };
    const element = renderNode(node, list); selectNode(node, element.button); element.expand();
  }
  function renderNode(node, list) {
    const li = el("li"), row = el("div", null, "node-row"), toggle = el("button", "+", "toggle");
    const button = el("button", null, "node-select"); toggle.type = button.type = "button";
    toggle.setAttribute("aria-label", "Раскрыть " + (node.object.denotation || node.object.name || "элемент")); toggle.setAttribute("aria-expanded", "false");
    button.append(el("strong", node.object.denotation || "Без обозначения"), el("span", node.object.name || "Без наименования"));
    button.addEventListener("click", () => selectNode(node, button));
    row.append(toggle, button); li.append(row); list.append(li);
    const children = el("ul"); children.hidden = true; li.append(children);
    const message = el("p", "", "hint"); li.append(message);
    let loaded = false, busy = false, nextCursor = null;
    const epoch = state.cardEpoch;
    async function load(append) {
      if (busy || epoch !== state.cardEpoch) return;
      busy = true; toggle.disabled = true; message.textContent = "Загрузка состава…";
      try {
        const data = await get(M.api(state.source, state.card.objectId, state.root.objectId, "children", {
          path: node.path, asOf: state.asOf, after: append ? nextCursor : 0
        }), "tree:" + M.nodeKey(state.source, state.root.objectId, node.path));
        if (epoch !== state.cardEpoch) return;
        if (!append) clear(children);
        children.querySelector(":scope > .node-more")?.remove();
        for (const child of data.page.items) renderNode(child, children);
        nextCursor = data.page.nextCursor;
        if (data.page.hasMore) {
          const more = el("button", "Показать ещё", "node-more"); more.type = "button";
          more.addEventListener("click", () => load(true)); children.append(more);
        }
        loaded = true; children.hidden = false; toggle.textContent = "−"; toggle.setAttribute("aria-expanded", "true");
        message.textContent = children.children.length ? (data.page.scanLimited ? "Доступно продолжение чтения." : "") : "В этом представлении доступных дочерних элементов нет.";
      } catch (error) { if (epoch === state.cardEpoch && error.name !== "AbortError") message.textContent = error.message; }
      finally { busy = false; toggle.disabled = false; }
    }
    function expand() {
      if (node.expansionState !== "available") return;
      if (!loaded) { load(false); return; }
      children.hidden = !children.hidden; toggle.textContent = children.hidden ? "+" : "−";
      toggle.setAttribute("aria-expanded", String(!children.hidden));
    }
    toggle.addEventListener("click", expand);
    if (node.expansionState !== "available") {
      toggle.disabled = true; toggle.textContent = "·";
      message.textContent = ({ cycle: "Циклическое вхождение: дальнейшее раскрытие остановлено.",
        unsupported_structure: "Вставленная структура: требуется отдельная поддержка.", depth_limit: "Достигнут предел глубины." })[node.expansionState] || "Раскрытие недоступно.";
    }
    return { button, expand };
  }
  function selectNode(node, button) {
    selectedNodeButton?.classList.remove("selected"); button?.classList.add("selected"); selectedNodeButton = button;
    state.path = node.path.slice(); state.selected = node;
    $("selectedCode").textContent = node.object.denotation || "Без обозначения";
    $("selectedName").textContent = node.object.name || ""; clear($("occurrence"));
    const entries = [["Количество", node.amount], ["Позиция", node.position], ["Единица", node.unit], ["Раздел", node.bomSection]];
    for (const [label, value] of entries) if (value !== null && value !== undefined && value !== "")
      $("occurrence").append(el("dt", label), el("dd", typeof value === "number" ? new Intl.NumberFormat("ru-RU").format(value) : value));
    loadFileRoots();
  }
  async function loadFileRoots() {
    const epoch = ++state.fileEpoch;
    requests.get("files")?.abort(); clear($("files")); clear($("fileRoot"));
    state.folderPath = []; state.fileCursor = null; $("moreFiles").hidden = true;
    $("fileRoot").hidden = true; $("fileRootLabel").hidden = true; $("folderBack").disabled = true; $("folderName").textContent = "";
    if (!state.card || !state.root || !state.selected) return;
    status("fileStatus", "Загрузка назначения папок выбранного элемента…");
    try {
      const data = await get(M.api(state.source, state.card.objectId, state.root.objectId, "file-roots", {
        category: state.category, path: state.path, asOf: state.asOf
      }), "file-roots");
      if (epoch !== state.fileEpoch) return;
      if (!data.roots.length) {
        status("fileStatus", data.state === "restricted" ? "Просмотр назначенных папок недоступен." : "Для выбранного элемента папка не назначена."); return;
      }
      for (const root of data.roots) $("fileRoot").append(new Option(root.name || "Папка без названия", String(root.objectId)));
      $("fileRoot").hidden = data.roots.length < 2; $("fileRootLabel").hidden = data.roots.length < 2;
      state.folderPath = [data.roots[0].objectId]; await loadFiles(false, epoch);
    } catch (error) { if (epoch === state.fileEpoch) failed(error, "fileStatus"); }
  }
  async function loadFiles(append, expectedEpoch) {
    const epoch = expectedEpoch ?? state.fileEpoch;
    if (!state.folderPath.length || epoch !== state.fileEpoch) return;
    $("moreFiles").hidden = true; status("fileStatus", "Загрузка файлов…");
    if (!append) { clear($("files")); state.fileCursor = null; }
    try {
      const data = await get(M.api(state.source, state.card.objectId, state.root.objectId, "files", {
        category: state.category, path: state.path, asOf: state.asOf, folderPath: state.folderPath, after: append ? state.fileCursor : 0
      }), "files");
      if (epoch !== state.fileEpoch) return;
      $("folderName").textContent = data.folder.name || "Папка без названия";
      $("folderBack").disabled = state.folderPath.length < 2;
      for (const file of data.page.items) {
        const row = el("tr"), nameCell = el("td");
        if (file.kind === "folder") {
          const button = el("button", "▸ " + (file.name || "Без названия"), "text-button file-folder"); button.type = "button";
          button.addEventListener("click", () => { state.fileEpoch++; state.folderPath = [...state.folderPath, file.objectId]; loadFiles(false); });
          nameCell.append(button);
        } else nameCell.textContent = file.name || "Без названия";
        row.append(nameCell, el("td", M.kindLabel(file.kind)), el("td", file.kind === "file" ? file.version : "—"), el("td", file.kind === "file" ? M.size(file.size) : "—"));
        $("files").append(row);
      }
      state.fileCursor = data.page.nextCursor; $("moreFiles").hidden = !data.page.hasMore;
      status("fileStatus", $("files").children.length ? "Собственные файлы выбранного элемента. Имена и версии из базы." : data.page.hasMore ? "Доступно продолжение чтения." : "Папка назначена. Доступных файлов в этом представлении нет.");
    } catch (error) { if (epoch === state.fileEpoch) failed(error, "fileStatus"); }
  }
  async function start() {
    $("searchForm").addEventListener("submit", e => { e.preventDefault(); search(false); });
    $("moreCards").addEventListener("click", () => search(true));
    $("source").addEventListener("change", () => {
      state.source = $("source").value; state.searchEpoch++; requests.get("search")?.abort();
      clear($("cards")); $("moreCards").hidden = true; $("searchButton").disabled = false; resetCard(); status("searchStatus", "Введите запрос.");
    });
    $("productRoot").addEventListener("change", () => chooseRoot(state.roots.find(r => r.objectId === Number($("productRoot").value))));
    $("fileRoot").addEventListener("change", () => { state.fileEpoch++; state.folderPath = [M.id($("fileRoot").value)]; loadFiles(false); });
    $("moreFiles").addEventListener("click", () => loadFiles(true));
    $("folderBack").addEventListener("click", () => { if (state.folderPath.length > 1) { state.fileEpoch++; state.folderPath.pop(); loadFiles(false); } });
    $("rootFiles").addEventListener("click", () => { if (state.root) selectNode({ object: state.root, path: [] }, null); });
    document.querySelectorAll("[data-category]").forEach(button => button.addEventListener("click", () => {
      state.category = button.dataset.category;
      document.querySelectorAll("[data-category]").forEach(b => { const active = b === button; b.classList.toggle("active", active); b.setAttribute("aria-selected", String(active)); });
      loadFileRoots();
    }));
    try {
      const sources = await get("api/reporter/product/sources", "sources"); clear($("source"));
      for (const source of sources) $("source").append(new Option(source.displayName, source.code));
      if (!sources.length) throw new Error("Нет включённых проверенных источников. Проверьте конфигурацию изделия.");
      const requested = initial.get("source");
      if (requested && !sources.some(s => s.code.toLowerCase() === requested.toLowerCase())) { $("source").selectedIndex = -1; throw new Error("Источник из ссылки не разрешён для этого этапа. Выберите его вручную после настройки."); }
      state.source = requested ? sources.find(s => s.code.toLowerCase() === requested.toLowerCase()).code : sources[0].code;
      $("source").value = state.source; $("query").value = initial.get("q") || "";
      status("searchStatus", "Введите запрос.");
      const card = initial.get("cardId") || initial.get("objectId");
      if (card) await openCard(M.id(card));
    } catch (error) { failed(error, "searchStatus"); }
  }
  start();
})();
