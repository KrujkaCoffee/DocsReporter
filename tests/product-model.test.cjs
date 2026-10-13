const test = require('node:test');
const assert = require('node:assert/strict');
const M = require('../wwwroot/reporter/product-model.js');
test('разные источники не объединяют узлы', () => assert.notEqual(M.nodeKey('tdocs', 1, [2]), M.nodeKey('pkb', 1, [2])));
test('разные пути повторного вхождения сохраняются', () => assert.notEqual(M.nodeKey('tdocs', 1, [2, 3]), M.nodeKey('tdocs', 1, [4, 3])));
test('регистр source нормализован только в ключе', () => assert.equal(M.nodeKey('TDOCS', 1, []), M.nodeKey('tdocs', 1, [])));
test('UI не угадывает файл по точке в имени', () => assert.equal(M.kindLabel('folder'), 'Папка'));
test('неизвестный тип явно обозначен', () => assert.equal(M.kindLabel('bad'), 'Тип не подтверждён'));
test('пустой размер не превращён в нуль', () => assert.equal(M.size(null), '—'));
test('нуль байт показан как нуль', () => assert.equal(M.size(0), '0 КБ'));
test('отрицательный размер не форматируется как нормальный', () => assert.equal(M.size(-1), '—'));
test('адрес сохраняет источник, корень и путь вхождений', () => {
 const url = new URL(M.api('tdocs', 183, 23900, 'children', { path: [272674], asOf: '2026-10-12' }), 'https://example.test/');
 assert.equal(url.pathname, '/api/reporter/product/sources/tdocs/cards/183/roots/23900/children');
 assert.equal(url.searchParams.get('path'), '272674');
});
test('пустой path сериализуется как пустой корень', () => assert.equal(new URL(M.api('tdocs', 1, 2, 'children', { path: [] }), 'https://example.test/').searchParams.get('path'), ''));
test('опасные имена источников кодируются как сегмент', () => assert.ok(M.api('a/b?x', 1, 2, 'files', {}).includes('a%2Fb%3Fx')));
test('UI отбрасывает дробные, отрицательные и неточные id', () => {
 for (const bad of ['x', -1, 0, 1.5, Number.MAX_SAFE_INTEGER + 1]) assert.throws(() => M.id(bad));
});
