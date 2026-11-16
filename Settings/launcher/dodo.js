// dodoMAKSharmony: русский перевод лаунчера Castle Story + панель «Моды» (включение функций).
// Ставится меню настроек dodoMAKSharmony; убирается снятием галочки в нём. Любая ошибка здесь не должна ломать лаунчер.
(function () {
  'use strict';
  var fs = require('fs');
  var path = require('path');
  var root = path.join(__dirname, '..', '..', '..', '..'); // папка игры (как в index.js)
  var cfgDir = path.join(root, 'CastleStoryPlus-Unity6', 'BepInEx', 'config');
  var FILES = [
    { file: 'castlestory.optimizer.cfg', title: 'dodoMAKSharmony (наш мод)' },
    { file: 'com.tricky12321.castlestoryplus.cfg', title: 'Castle Story Plus (мод Tricky12321)' }
  ];
  var FONT = 'Arial, "Segoe UI", sans-serif';

  // ---------- перевод ----------
  var RU = {
    'our latest update is': 'Последнее обновление —',
    'read more': 'Читать далее',
    'settings': 'Настройки',
    'resolution': 'Разрешение',
    'monitor': 'Монитор',
    'launch in fullscreen': 'Полноэкранный режим',
    'play': 'Играть',
    'credits': 'Создатели',
    'all rights reserved': 'все права защищены',
    'forum': 'Форум',
    'chat': 'Чат',
    'wiki': 'Вики',
    'support': 'Поддержка',
    'welcome,': 'Добро пожаловать,',
    'sign in / sign up': 'Войти / Регистрация',
    'profile': 'Профиль',
    'sign out': 'Выйти',
    'in partnership': 'В партнёрстве',
    'with': 'с',
    'version': 'версия'
  };

  function ruText(s) {
    var t = s.replace(/^\s+|\s+$/g, '').toLowerCase();
    if (RU[t]) return RU[t];
    var m = /^version\s+(.+)$/.exec(t);
    if (m) return 'версия ' + m[1];
    return null;
  }

  function translateNode(root) {
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null, false);
    var node, list = [];
    while ((node = walker.nextNode())) list.push(node);
    list.forEach(function (n) {
      var p = n.parentNode;
      if (!p || p.nodeName === 'SCRIPT' || p.nodeName === 'STYLE' || p.nodeName === 'OPTION') return;
      if (p.className && /(^|\s)fr(\s|$)/.test(p.className)) return;
      if (p.closest && p.closest('#dodo-mods')) return;
      var r = ruText(n.nodeValue);
      if (r !== null && r !== n.nodeValue.trim()) {
        n.nodeValue = n.nodeValue.replace(n.nodeValue.trim(), r);
        p.style.fontFamily = FONT;
      }
    });
  }

  function translateAll() { try { translateNode(document.body); } catch (e) { console.log('dodo translate', e); } }

  // ---------- чтение/запись конфигов BepInEx ----------
  var ruDesc = {};
  try {
    fs.readFileSync(path.join(__dirname, 'dodo_ru.txt'), 'utf8').split(/\r?\n/).forEach(function (line) {
      var i = line.indexOf('\t');
      if (i > 0) ruDesc[line.substring(0, i).trim()] = line.substring(i + 1).trim();
    });
  } catch (e) { /* перевода описаний нет — показываем как есть */ }

  function parseCfg(file) {
    var full = path.join(cfgDir, file);
    if (!fs.existsSync(full)) return null;
    var text = fs.readFileSync(full, 'utf8');
    var nl = text.indexOf('\r\n') >= 0 ? '\r\n' : '\n';
    var lines = text.split(/\r?\n/);
    var section = '', desc = [], type = '', entries = [];
    for (var i = 0; i < lines.length; i++) {
      var l = lines[i];
      var ms = /^\[(.+)\]\s*$/.exec(l);
      if (ms) { section = ms[1]; desc = []; type = ''; continue; }
      if (/^##/.test(l)) { desc.push(l.replace(/^##\s?/, '')); continue; }
      var mt = /^#\s*Setting type:\s*(.+)$/.exec(l);
      if (mt) { type = mt[1].trim(); continue; }
      if (/^#/.test(l)) continue;
      var me = /^([^=#]+?)\s*=\s*(.*)$/.exec(l);
      if (me) {
        if (type === 'Boolean') entries.push({ section: section, key: me[1], value: /^true$/i.test(me[2].trim()), line: i, desc: desc.join(' ') });
        desc = []; type = '';
      }
    }
    return { full: full, lines: lines, nl: nl, entries: entries };
  }

  function saveCfg(cfg) {
    var bak = cfg.full + '.bak-launcher';
    try { if (!fs.existsSync(bak)) fs.copyFileSync(cfg.full, bak); } catch (e) { }
    fs.writeFileSync(cfg.full, cfg.lines.join(cfg.nl), 'utf8');
  }

  // ---------- панель «Моды» ----------
  function el(tag, css, text) {
    var e = document.createElement(tag);
    if (css) e.style.cssText = css;
    if (text !== undefined) e.textContent = text;
    return e;
  }

  function openPanel() {
    var old = document.getElementById('dodo-mods');
    if (old) old.parentNode.removeChild(old);
    var wrap = el('div', 'position:fixed;left:0;top:0;right:0;bottom:0;z-index:99999;background:rgba(0,0,0,.72);display:flex;align-items:center;justify-content:center;font-family:' + FONT + ';');
    wrap.id = 'dodo-mods';
    var box = el('div', 'background:#fff;color:#222;border:6px solid #000;width:860px;max-width:94vw;height:82vh;display:flex;flex-direction:column;');
    var head = el('div', 'background:#ffc50a;padding:12px 16px;display:flex;align-items:center;border-bottom:4px solid #000;');
    head.appendChild(el('div', 'font-size:20px;font-weight:bold;flex:1;', 'МОДЫ — включение функций'));
    var close = el('button', 'font-size:16px;font-weight:bold;padding:4px 14px;cursor:pointer;', 'Закрыть');
    close.onclick = function () { wrap.parentNode.removeChild(wrap); };
    head.appendChild(close);
    var bar = el('div', 'padding:8px 16px;display:flex;align-items:center;border-bottom:2px solid #ddd;');
    var search = el('input', 'flex:1;font-size:15px;padding:5px 8px;');
    search.placeholder = 'Поиск по названию или описанию…';
    var counter = el('span', 'margin-left:12px;font-size:14px;color:#555;', '');
    bar.appendChild(search); bar.appendChild(counter);
    var presetBar = el('div', 'padding:8px 16px;display:flex;flex-wrap:wrap;gap:8px;border-bottom:2px solid #ddd;background:#fafafa;');
    var list = el('div', 'overflow-y:auto;flex:1;padding:6px 16px 16px;');
    var note = el('div', 'padding:6px 16px;font-size:12px;color:#777;border-top:2px solid #ddd;',
      'Изменения записываются сразу в конфиги модов и действуют со следующего запуска игры. Остальные настройки (числа, клавиши) — в программе dodoMAKSharmony-Settings.');

    var data = FILES.map(function (f) { var c = null; try { c = parseCfg(f.file); } catch (e) { console.log(e); } return { meta: f, cfg: c }; });

    function render() {
      var q = search.value.toLowerCase();
      list.innerHTML = '';
      var total = 0, on = 0;
      data.forEach(function (d) {
        list.appendChild(el('div', 'font-size:17px;font-weight:bold;margin:14px 0 4px;color:#b07a00;', d.meta.title));
        if (!d.cfg) {
          list.appendChild(el('div', 'color:#888;font-size:14px;', 'Файл настроек ещё не создан — один раз запусти игру, и здесь появятся функции.'));
          return;
        }
        var lastSection = null;
        d.cfg.entries.forEach(function (e) {
          var ru = ruDesc[e.desc] || e.desc;
          var hay = (e.section + ' ' + e.key + ' ' + ru + ' ' + e.desc).toLowerCase();
          if (q && hay.indexOf(q) < 0) return;
          total++; if (e.value) on++;
          if (e.section !== lastSection) {
            lastSection = e.section;
            list.appendChild(el('div', 'font-size:13px;font-weight:bold;color:#666;margin:10px 0 2px;text-transform:uppercase;', e.section));
          }
          var row = el('label', 'display:flex;align-items:flex-start;padding:3px 0;cursor:pointer;');
          var cb = document.createElement('input');
          cb.type = 'checkbox'; cb.checked = e.value; cb.style.cssText = 'margin:4px 10px 0 0;width:18px;height:18px;flex:none;';
          cb.onchange = function () {
            e.value = cb.checked;
            d.cfg.lines[e.line] = e.key + ' = ' + (e.value ? 'true' : 'false');
            try { saveCfg(d.cfg); } catch (err) { alert('Не удалось сохранить: ' + err.message); }
            updateCounter();
          };
          var txt = el('div', 'font-size:14px;line-height:1.3;');
          txt.appendChild(el('b', '', e.key));
          if (ru) txt.appendChild(el('span', 'color:#444;', ' — ' + ru));
          row.appendChild(cb); row.appendChild(txt);
          list.appendChild(row);
        });
      });
      counter.textContent = 'включено ' + on + ' из ' + total;
    }
    function updateCounter() {
      var total = 0, on = 0;
      data.forEach(function (d) { if (d.cfg) d.cfg.entries.forEach(function (e) { total++; if (e.value) on++; }); });
      counter.textContent = 'включено ' + on + ' из ' + total + ' (всего функций)';
    }
    // пресеты: файл dodo_presets.json выгружает программа настроек (те же правила, что у её кнопок)
    var presets = null;
    try { presets = JSON.parse(fs.readFileSync(path.join(__dirname, 'dodo_presets.json'), 'utf8')); } catch (e) { }
    function applyPreset(name) {
      var map = presets && presets[name];
      if (!map) { alert('Пресеты ещё не выгружены: один раз открой программу dodoMAKSharmony-Settings, и они появятся здесь.'); return; }
      data.forEach(function (d) {
        if (!d.cfg) return;
        var changed = false;
        d.cfg.entries.forEach(function (e) {
          var id = d.meta.file + '|' + e.section + '.' + e.key;
          if (Object.prototype.hasOwnProperty.call(map, id) && e.value !== map[id]) {
            e.value = map[id];
            d.cfg.lines[e.line] = e.key + ' = ' + (e.value ? 'true' : 'false');
            changed = true;
          }
        });
        if (changed) { try { saveCfg(d.cfg); } catch (err) { alert('Не удалось сохранить: ' + err.message); } }
      });
      render();
    }
    [
      ['vanilla', 'Как в оригинале', 'Выключить ВСЕ геймплейные изменения обоих модов. Интерфейс, ускорения и исправления остаются.'],
      ['max', 'Оптимизация максимум', 'Всё для скорости и стабильности: оптимизации, исправления, лёгкий ИИ; остальное выключается. Мультиплеер не трогается.'],
      ['optimization', 'Включить оптимизацию', 'Включить всё про скорость, остальное не трогать.'],
      ['recommended', 'Рекомендуемые', 'Значения по умолчанию, как задумали авторы.']
    ].forEach(function (p) {
      var b = el('button', 'font-size:14px;font-weight:bold;padding:6px 12px;cursor:pointer;background:#ffc50a;border:2px solid #000;', p[1]);
      b.title = p[2];
      b.onclick = function () { applyPreset(p[0]); };
      presetBar.appendChild(b);
    });

    search.oninput = render;
    box.appendChild(head); box.appendChild(presetBar); box.appendChild(bar); box.appendChild(list); box.appendChild(note);
    wrap.appendChild(box);
    document.body.appendChild(wrap);
    render();
    search.focus();
  }

  function addButton() {
    var play = document.querySelector('button.play');
    if (!play || document.getElementById('dodo-btn')) return;
    var b = document.createElement('button');
    b.id = 'dodo-btn';
    b.className = 'box';
    b.type = 'button';
    b.textContent = 'Моды';
    b.style.cssText = 'display:block;width:100%;margin-bottom:14px;padding:14px;font-size:22px;font-weight:bold;cursor:pointer;text-transform:uppercase;background:#ffc50a;border:5px solid #000;font-family:' + FONT + ';';
    b.onclick = function () { try { openPanel(); } catch (e) { alert('Ошибка панели модов: ' + e.message); } };
    play.parentNode.insertBefore(b, play);
  }

  // ---------- оформление: без левой панели «Последнее обновление», вместо логотипа — название мода ----------
  function applyLayout() {
    try {
      var st = document.createElement('style');
      st.textContent =
        '.left-col{display:none !important;}' +
        'header h1{background:none !important;text-indent:0 !important;width:auto !important;height:auto !important;overflow:visible !important;' +
        'top:4px !important;left:47px !important;font-family:"Arial Black",Arial,sans-serif !important;font-size:46px;line-height:1.1;font-weight:900;' +
        'color:#ffc50a;-webkit-text-stroke:2px #000;text-shadow:3px 4px 0 #000;letter-spacing:1px;white-space:nowrap;}';
      document.head.appendChild(st);
      var h = document.querySelector('header h1');
      if (h) h.textContent = 'dodoMAKSharmony';
      document.title = 'dodoMAKSharmony — Castle Story';
    } catch (e) { console.log('dodo layout', e); }
  }

  function start() {
    applyLayout();
    translateAll();
    addButton();
    try { new MutationObserver(function () { translateAll(); }).observe(document.body, { childList: true, subtree: true, characterData: true }); } catch (e) { }
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
