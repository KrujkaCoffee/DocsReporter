(function (root, factory) {
  const model = factory();
  if (typeof module === "object" && module.exports) module.exports = model;
  else root.ProductViewModel = model;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  "use strict";
  function id(value) {
    const n = Number(value);
    if (!Number.isSafeInteger(n) || n <= 0) throw new Error("Некорректный идентификатор объекта.");
    return n;
  }
  function nodeKey(source, rootId, path) {
    return [encodeURIComponent(source.toLowerCase()), id(rootId), path.map(id).join(",")].join(":");
  }
  function kindLabel(kind) {
    return ({ folder: "Папка", file: "Файл", unknown: "Тип не подтверждён" })[kind] || "Тип не подтверждён";
  }
  function size(value) {
    if (value === null || value === undefined) return "—";
    const n = Number(value);
    if (!Number.isFinite(n) || n < 0) return "—";
    return new Intl.NumberFormat("ru-RU", { maximumFractionDigits: 1 }).format(n / 1024) + " КБ";
  }
  function api(source, cardId, rootId, action, parameters) {
    let url = "api/reporter/product/sources/" + encodeURIComponent(source) + "/cards";
    if (cardId != null) url += "/" + id(cardId);
    if (rootId != null) url += "/roots/" + id(rootId);
    if (action) url += "/" + action;
    const search = new URLSearchParams();
    Object.entries(parameters || {}).forEach(([key, value]) => {
      if (value !== null && value !== undefined && value !== "") search.set(key, Array.isArray(value) ? value.map(id).join(",") : String(value));
    });
    return url + (search.size ? "?" + search.toString() : "");
  }
  return { id, nodeKey, kindLabel, size, api };
});
