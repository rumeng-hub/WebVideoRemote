// 网页视频遥控器 · 浏览器桥接脚本（内容脚本）
// 在抖音 / B站 页面里直接操作播放器，不依赖任何按键注入，也不抢焦点。
(function () {
  'use strict';

  var PORT = 17777;
  var BASE = 'http://127.0.0.1:' + PORT;
  var BRIDGE_VER = '1.10';  // 每次改脚本都要改这个；exe 会核对，版本不一致会直接提示「脚本过旧」
  var IS_DOUYIN = location.hostname.indexOf('douyin.com') >= 0;
  // 调试用：本机测试页可以用 ?wvrsite=douyin 冒充抖音页
  var SITE_OVERRIDE = '';
  try {
    var m = /[?&]wvrsite=([a-z]+)/.exec(location.search);
    if (m) SITE_OVERRIDE = m[1];
  } catch (e) {}
  var SITE = SITE_OVERRIDE || (IS_DOUYIN ? 'douyin' : 'bilibili');

  // 长按倍率：可在扩展「选项」页里改成 1.5 / 2 / 3（默认 2）
  var HOLD_RATE = 2;
  try {
    if (chrome && chrome.storage && chrome.storage.sync) {
      chrome.storage.sync.get({ holdRate: 2 }, function (o) {
        var r = parseFloat(o && o.holdRate);
        if (r >= 1 && r <= 3) HOLD_RATE = r;
      });
      chrome.storage.onChanged.addListener(function (ch) {
        if (ch && ch.holdRate) {
          var r2 = parseFloat(ch.holdRate.newValue);
          if (r2 >= 1 && r2 <= 3) HOLD_RATE = r2;
        }
      });
    }
  } catch (e) {}

  function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

  // ---------- 选「用户正在看的那一个」video ----------
  // 抖音信息流页面里通常同时挂着 4 个左右 <video>（当前 + 预加载的上下条），
  // 随便取第一个会操作到看不见的邻居上，所以这里综合几个信号来挑：
  //   1) 抖音自己的「当前视频」标记
  //   2) 在视口里的可见面积
  //   3) 是否没被静音（信息流里只有正在看的那条是出声的）
  //   4) 是否正在播放
  //   5) 上一次操作过的那个（保证「暂停→恢复」按的是同一条）
  var lastVideo = null;

  function visibleArea(v) {
    var r = v.getBoundingClientRect();
    var vw = window.innerWidth || document.documentElement.clientWidth;
    var vh = window.innerHeight || document.documentElement.clientHeight;
    var w = Math.min(r.right, vw) - Math.max(r.left, 0);
    var h = Math.min(r.bottom, vh) - Math.max(r.top, 0);
    return (w > 0 && h > 0) ? w * h : 0;
  }

  function pickVideo() {
    // ① 抖音标注的「当前正在播放的那一条」
    var sels = ['[data-e2e="feed-active-video"] video', '[data-e2e="video-detail"] video', '[data-e2e="feed-video"] video'];
    for (var s = 0; s < sels.length; s++) {
      try {
        var el = document.querySelector(sels[s]);
        if (el && el.isConnected) { lastVideo = el; return { v: el, how: '抖音标记' }; }
      } catch (e) {}
    }

    var vs = document.querySelectorAll('video');
    if (!vs.length) return { v: null, how: '页面里没有 video' };

    var best = null, bestScore = -1, bestHow = '';
    for (var i = 0; i < vs.length; i++) {
      var v = vs[i];
      var area = visibleArea(v);
      var score = area;
      var how = '可见';

      if (v === lastVideo && area > 0 && !v.paused) { score += 3e9; how = '上次那个'; }
      if (!v.muted && v.volume > 0) { score += 2e9; how += '+没静音'; }
      if (!v.paused && !v.ended) { score += 1e9; how += '+在播'; }
      if (v.currentTime > 0) score += 1e6;

      if (score > bestScore) { bestScore = score; best = v; bestHow = how; }
    }
    lastVideo = best;
    return { v: best, how: bestHow };
  }

  function vid() { return pickVideo().v; }

  // 回执里带上候选清单，出问题时一眼看出挑错在哪
  function candsInfo() {
    var vs = document.querySelectorAll('video');
    var out = [];
    for (var i = 0; i < vs.length && i < 6; i++) {
      var v = vs[i];
      out.push((v.paused ? '停' : '播') + (v.muted ? '静' : '响') + Math.round(visibleArea(v) / 1000) + 'k');
    }
    return out.join(' ');
  }

  function videoSig() { var v = vid(); return v ? (v.currentSrc || v.src || '') + '|' + (v.duration || 0) : ''; }

  // ---------- 模拟按键（让页面自己的快捷键来处理，等价于真人按键） ----------
  // 注意：只能派发一次。打在「当前焦点元素」上并让它向上冒泡，
  // 这样注册在元素/document/window 上的监听器各收到恰好一次 —— 和真实键盘一致。
  // （早先版本对 document 和 window 各派发一次，会让页面把一次按键当成两三次执行）
  function fireKey(type, key, code, keyCode) {
    var opt = {
      key: key, code: code, keyCode: keyCode, which: keyCode,
      bubbles: true, cancelable: true, composed: true, view: window
    };
    var target = document.activeElement;
    if (!target || target === document.documentElement) target = document.body || document;
    try {
      target.dispatchEvent(new KeyboardEvent(type, opt));
    } catch (e) {
      try { document.dispatchEvent(new KeyboardEvent(type, opt)); } catch (e2) {}
    }
  }
  function tapKey(key, code, keyCode) {
    fireKey('keydown', key, code, keyCode);
    setTimeout(function () { fireKey('keyup', key, code, keyCode); }, 70);
  }

  // ---------- 抖音：上一个 / 下一个视频 ----------
  function feedScroller() {
    var el = vid();
    while (el && el !== document.body) {
      var s = getComputedStyle(el);
      if (/(auto|scroll)/.test(s.overflowY) && el.scrollHeight > el.clientHeight + 40) return el;
      el = el.parentElement;
    }
    return null;
  }

  // 整个信息流的指纹：只要任何一个 video 换了源或改了播放状态，就算变了。
  // （抖音经常复用同一个 <video> 元素换流，只看单个元素会误判成「没换成功」）
  function feedSig() {
    var vs = document.querySelectorAll('video');
    var s = [];
    for (var i = 0; i < vs.length; i++) {
      s.push(((vs[i].currentSrc || vs[i].src || '').slice(-28)) + (vs[i].paused ? 'p' : 'P'));
    }
    return s.join('|');
  }

  async function switchVideo(dir) {
    var before = feedSig();

    // ① 模拟 ↑ / ↓（实测抖音认这个，等价于真人按键）
    if (dir > 0) tapKey('ArrowDown', 'ArrowDown', 40);
    else tapKey('ArrowUp', 'ArrowUp', 38);
    await sleep(450);
    if (feedSig() !== before) return 'key';

    // ② 滚动信息流
    var sc = feedScroller();
    if (sc) {
      try { sc.scrollBy({ top: dir * Math.max(240, sc.clientHeight * 0.9), behavior: 'smooth' }); } catch (e) {}
      await sleep(750);
      if (feedSig() !== before) return 'scroll';
    }

    // ③ 点页面自带的「上/下一个」按钮
    var words = dir > 0 ? ['下一个', '下一条', '下个', 'next'] : ['上一个', '上一条', '上个', 'previous', 'prev'];
    var cands = document.querySelectorAll('button,[role="button"],[aria-label],[title]');
    for (var i = 0; i < cands.length; i++) {
      var el = cands[i];
      var t = (el.getAttribute('aria-label') || el.getAttribute('title') || el.textContent || '').trim();
      if (!t || t.length > 12) continue;
      var hit = false;
      for (var j = 0; j < words.length; j++) if (t.toLowerCase().indexOf(words[j]) >= 0) { hit = true; break; }
      if (!hit) continue;
      try { el.click(); } catch (e) { continue; }
      await sleep(550);
      if (feedSig() !== before) return 'click';
    }
    return 'none';
  }

  // ---------- 长按 → 持续快进 ----------
  var holdTimer = null;
  var holdWatchdog = null;
  var holdTarget = null;
  var holdRate = 2;

  // ---------- 倍速回写 & 换视频后守住目标倍速 ----------
  // 抖音会记住「它以为的当前倍速」并贴到下一个视频上，而且回写时机不固定
  // （keyup 后、视频激活时都可能异步贴）。关键是要分清「页面自己贴的」
  // 和「用户主动改的」，否则 2x 会被永久锁死。
  // 抖音上把 1x 当作「正常速度」：页面自己贴的记忆倍速一律纠偏。
  // （不开软件时只有这个脚本在跑，之前把真实键盘手势也当成用户偏好，
  //  换视频/长按 → 后抖音贴的 2x 被采纳，再被纠偏器永久锁成 2x。）
  var desiredRate = (SITE === 'douyin') ? 1 : null;   // null = 还没产生过锁定目标，不干预
  var applying = 0;            // 自己回写倍速的计数，避免把自己的回写当成外部变化
  var holdQuietUntil = 0;      // 长按期间/松手后一小段，页面回写不跟随
  var lastSpeedMenuAt = 0;     // 最近一次在「倍速」菜单里的真实点击

  // 只有点在倍速菜单里才算用户主动改倍速；键盘（↑/↓ 换视频、长按 → 加速）
  // 全是 isTrusted 的真实按键，但都不是改倍速的意图，不能采纳页面随后贴的倍速。
  // 额外要求“短文本弹层”：播放器容器里也常出现「倍速」二字，
  // 若不算长度，点暂停/下一集也会被误判成菜单点击，2x 就又会被采纳。
  function inSpeedMenu(el) {
    for (var i = 0; el && i < 4; i++, el = el.parentElement) {
      try {
        var t = (el.textContent || '').replace(/\s+/g, '');
        if (t.length <= 60 && (t.indexOf('倍速') >= 0 || t.indexOf('播放速度') >= 0)) return true;
      } catch (e) {}
    }
    return false;
  }
  ['pointerdown', 'click'].forEach(function (ev) {
    document.addEventListener(ev, function (e) {
      if (e.isTrusted && inSpeedMenu(e.target)) lastSpeedMenuAt = Date.now();
    }, true);
  });

  // ---------- 抖音「右箭头」：短按=快进，长按=2x（不再先快进） ----------
  // 抖音原生逻辑是「按下立刻快进，按住不放才进 2x」，所以长按总会先跳一下。
  // 这里在捕获阶段截住真实 keydown：短按由我们补一次快进，长按直接进入 2x。
  // 遥控器（exe）走的是 HTTP 指令，不产生真实按键，不受影响。
  var ARROW_HOLD_MS = 280;
  var arrowDownAt = 0;         // 真实 ArrowRight 按下的时间；0 = 没按
  var arrowHolding = false;    // 已进入长按 2x
  var arrowHoldTimer = null;   // 判定长按的定时器
  var arrowDriveTimer = null;  // 长按期间维持 2x
  var arrowWatchdog = null;    // 松手事件丢失的兜底

  function arrowEditable() {
    var el = document.activeElement;
    if (!el) return false;
    return el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' ||
           el.tagName === 'SELECT' || el.isContentEditable;
  }

  function stopArrowDrive() {
    if (arrowDriveTimer) { clearInterval(arrowDriveTimer); arrowDriveTimer = null; }
  }

  function enterArrowHold() {
    arrowHoldTimer = null;
    if (!arrowDownAt) return;
    arrowHolding = true;
    desiredRate = HOLD_RATE;
    restoreRate(HOLD_RATE);
    stopArrowDrive();
    arrowDriveTimer = setInterval(function () {
      if (!arrowDownAt) { stopArrowDrive(); return; }
      restoreRate(HOLD_RATE);
    }, 200);
    if (arrowWatchdog) clearTimeout(arrowWatchdog);
    arrowWatchdog = setTimeout(function () { if (arrowDownAt) endArrowHold(); }, 20000);
  }

  function endArrowHold() {
    if (arrowHoldTimer) { clearTimeout(arrowHoldTimer); arrowHoldTimer = null; }
    if (arrowWatchdog) { clearTimeout(arrowWatchdog); arrowWatchdog = null; }
    stopArrowDrive();
    var wasHolding = arrowHolding;
    arrowHolding = false;
    arrowDownAt = 0;
    if (!wasHolding) return;
    desiredRate = 1;             // 松手 = 1x（原始需求）
    var press = function () { if (!holdTimer) restoreRate(1); };
    press();
    setTimeout(press, 60);
    setTimeout(press, 180);
    setTimeout(press, 450);
  }

  if (SITE === 'douyin') {
    // 挂在 window 的捕获阶段：路径上最早拿到事件，才能先于页面自己的处理拦掉快进
    window.addEventListener('keydown', function (e) {
      if (!e.isTrusted || e.key !== 'ArrowRight') return;
      if (e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;
      if (arrowDownAt) {                 // 键盘自动重复：一并拦掉
        e.preventDefault();
        e.stopImmediatePropagation();
        return;
      }
      if (arrowEditable()) return;       // 输入框里的 → 留给浏览器
      var v = vid();
      if (!v || v.paused) return;        // 暂停/无视频时不接管，保持原行为
      e.preventDefault();                // 关键：拦掉抖音「按下就快进」
      e.stopImmediatePropagation();
      arrowDownAt = Date.now();
      arrowHoldTimer = setTimeout(enterArrowHold, ARROW_HOLD_MS);
    }, true);

    window.addEventListener('keyup', function (e) {
      if (!e.isTrusted || e.key !== 'ArrowRight') return;
      if (!arrowDownAt) return;
      var wasHolding = arrowHolding;
      endArrowHold();
      if (!wasHolding) {
        // 短按：补回抖音原来的快进（5 秒）
        var v = vid();
        if (v) {
          var d = (isFinite(v.duration) && v.duration > 0) ? v.duration : 1e9;
          try { v.currentTime = Math.min(d - 0.5, v.currentTime + 5); } catch (err) {}
        }
      }
    }, true);
  } else {
    // 其它站点（B站）：只记录按住状态，不拦截原生按键
    document.addEventListener('keydown', function (e) {
      if (!e.isTrusted || e.key !== 'ArrowRight') return;
      if (!arrowDownAt) arrowDownAt = Date.now();
    }, true);
    document.addEventListener('keyup', function (e) {
      if (!e.isTrusted || e.key !== 'ArrowRight') return;
      if (arrowDownAt) endArrowHold();
    }, true);
  }

  // 松手事件丢了（切窗口/失焦）也不能让它一直停在 2x
  window.addEventListener('blur', function () {
    if (arrowDownAt) endArrowHold();
  }, true);

  function setRate(v, r) {
    if (!v) return;
    try {
      // defaultPlaybackRate 赋值不触发 ratechange，只在真正有偏差时写（稳态零写入）
      if (Math.abs(v.defaultPlaybackRate - r) > 0.001) v.defaultPlaybackRate = r;
    } catch (e) {}
    try {
      if (Math.abs(v.playbackRate - r) > 0.001) {
        applying++;
        try { v.playbackRate = r; } catch (e) {}
        setTimeout(function () { applying = Math.max(0, applying - 1); }, 150);
      }
    } catch (e) {}
  }

  // 把所有 video 都压到指定倍速（默认 1）。
  // 不能只恢复"当前选中的那个"：信息流里挂着好几个 video，
  // 长按期间一旦选中目标发生变化，就会有元素被改成 2 倍速却永远没人恢复它。
  function restoreRate(r) {
    var target = (r == null) ? 1 : r;
    var vs = document.querySelectorAll('video');
    for (var i = 0; i < vs.length; i++) setRate(vs[i], target);
  }

  // 低频纠偏器：无论页面什么时候把记忆的倍速贴回来（可能晚于任何固定窗口），
  // 500ms 内发现偏差就压回。稳态下所有 video 已等于目标值，零写入，不触发风控。
  setInterval(function () {
    if (arrowDownAt) return;               // 真人正按住 →：让页面临时加速
    if (desiredRate == null || holdTimer) return;
    restoreRate(desiredRate);
  }, 500);

  // 新视频加载 / 起播：立即把目标倍速贴上，后续万一被页面覆盖有纠偏器兜底
  function onVideoStart(e) {
    var v = e.target;
    if (!v || v.tagName !== 'VIDEO') return;
    if (desiredRate == null || holdTimer || arrowDownAt) return;
    setRate(v, desiredRate);
    var press = function () { if (!holdTimer) setRate(v, desiredRate); };
    setTimeout(press, 150);
    setTimeout(press, 400);
  }
  document.addEventListener('loadstart', onVideoStart, true);
  document.addEventListener('play', onVideoStart, true);

  document.addEventListener('ratechange', function (e) {
    var v = e.target;
    if (!v || v.tagName !== 'VIDEO') return;
    if (applying > 0) return;                  // 我们自己改的，忽略
    if (arrowDownAt) return;                    // 真人按住 →：倍速由我们接管，不跟随页面
    if (holdTimer || Date.now() < holdQuietUntil) return;  // 遥控长按中/刚松手：不跟随
    if (Date.now() - lastSpeedMenuAt < 1500) {
      // 刚在倍速菜单里点过：这是用户主动选的倍速，跟随
      desiredRate = v.playbackRate;
      return;
    }
    // 没有菜单点击：必是页面自己贴的记忆倍速，立刻压回锁定目标
    if (desiredRate != null) setRate(v, desiredRate);
  }, true);

  function startHold(rate) {
    stopHold(false);                 // 先彻底清一遍，避免上一轮残留
    holdRate = rate || 2;
    holdQuietUntil = Infinity;       // 长按期间页面的倍速回写都不算用户意图
    var pick = pickVideo();
    holdTarget = pick.v;
    lastPickHow = '长按倍速：' + pick.how;

    var apply = function () {
      var v = (holdTarget && holdTarget.isConnected) ? holdTarget : vid();
      holdTarget = v;
      if (v) setRate(v, holdRate);
    };
    apply();
    holdTimer = setInterval(apply, 200);
    // 同时也按一下页面自己的「按住 → 加速」逻辑（和 ↑/↓ 一样，有的站点认这个）
    fireKey('keydown', 'ArrowRight', 'ArrowRight', 39);

    // 兜底：万一「松开」那条指令没送到，20 秒后自己恢复原速并锁定 1x
    if (holdWatchdog) clearTimeout(holdWatchdog);
    holdWatchdog = setTimeout(function () {
      desiredRate = 1;
      restoreRate(1);
      holdTimer = null;
      holdWatchdog = null;
      holdTarget = null;
      holdQuietUntil = Date.now() + 2000;
    }, 20000);
  }

  // forceOne=true：松手后强制停在 1 倍速（长按松手用）。
  // 页面自己接到 keyup 会把倍速恢复到它记忆的值（比如 3x），
  // 所以得等它的处理生效后再补压几次，确保最后停在 1x。
  function stopHold(forceOne) {
    if (holdTimer) { clearInterval(holdTimer); holdTimer = null; }
    if (holdWatchdog) { clearTimeout(holdWatchdog); holdWatchdog = null; }
    holdTarget = null;
    restoreRate();
    fireKey('keyup', 'ArrowRight', 'ArrowRight', 39);
    holdQuietUntil = Date.now() + 2000;   // 松手后 2 秒内的回写都不算用户意图（真实 keyup 可能穿透到浏览器）
    if (forceOne) {
      desiredRate = 1;   // 松手 = 1 倍速，并且守住之后切换的新视频
      var press = function () { if (!holdTimer) restoreRate(); };
      setTimeout(press, 80);
      setTimeout(press, 220);
      setTimeout(press, 450);
    }
  }

  // ---------- 播放/暂停、跳转 ----------
  var lastPickHow = '';

  function toggleOne(v) {
    if (!v) return 'novideo';
    if (v.paused) {
      var p = v.play();
      if (p && p.catch) p.catch(function () {});
      return 'play';
    }
    v.pause();
    return 'pause';
  }

  // 挑中的那个如果不是用户正在看的（抖音信息流里挂着多个 video），
  // 状态不会真的变化，这时换下一个可见的再试
  function pausedSnapshot() {
    var vs = document.querySelectorAll('video');
    var s = [];
    for (var i = 0; i < vs.length; i++) s.push(vs[i].paused ? '1' : '0');
    return s.join('');
  }

  async function toggle() {
    // ① 首选：把「空格」交给页面自己的快捷键处理。
    //    这条和 ↑/↓ 用的是同一套机制，实测抖音/B站 网页都认，
    //    而且不会猜错是哪一个 <video>（信息流里同时挂着好几个）。
    var before = pausedSnapshot();
    tapKey(' ', 'Space', 32);
    await sleep(320);
    if (pausedSnapshot() !== before) { lastPickHow = '空格键（页面自己处理）'; return 'space'; }

    // ② 兜底：页面没响应空格时，直接操作元素，并在多个候选里依次试
    var pick = pickVideo();
    var v = pick.v;
    if (!v) { lastPickHow = '没找到 video'; return 'novideo'; }
    lastPickHow = '直接操作元素：' + pick.how;

    var wasPaused = v.paused;
    var res = toggleOne(v);
    await sleep(160);
    if (v.paused !== wasPaused) return res;

    var vs = document.querySelectorAll('video');
    for (var i = 0; i < vs.length; i++) {
      var o = vs[i];
      if (o === v || visibleArea(o) <= 0) continue;
      var b2 = o.paused;
      var r2 = toggleOne(o);
      await sleep(160);
      if (o.paused !== b2) { lastVideo = o; lastPickHow = '换用第' + (i + 1) + '个可见 video'; return r2; }
    }
    return res + '（没生效：可能是直播，或挑错了元素）';
  }

  function seek(delta) {
    var v = vid(); if (!v) return 'novideo';
    var d = (isFinite(v.duration) && v.duration > 0) ? v.duration : 1e9;
    v.currentTime = Math.min(d - 0.5, Math.max(0, v.currentTime + delta));
    return 'seek';
  }

  async function doCmd(cmd) {
    var res = 'unknown';
    try {
      if (cmd === 'next') res = await switchVideo(1);
      else if (cmd === 'prev') res = await switchVideo(-1);
      else if (cmd === 'toggle') res = await toggle();
      else if (cmd === 'holdStart') { startHold(HOLD_RATE); res = 'hold'; }
      else if (cmd === 'holdStop') { stopHold(false); res = 'release'; }
      else if (cmd === 'holdStop1x') { stopHold(true); res = 'release1x'; }
      else if (cmd.indexOf('seekf:') === 0) res = seek(parseFloat(cmd.slice(6)) || 5);
      else if (cmd.indexOf('seekb:') === 0) res = seek(-(parseFloat(cmd.slice(6)) || 5));
    } catch (e) { res = 'err'; }
    ack(cmd, res);   // 回执给 exe，方便在日志里看到「脚本到底做了什么」
    return res;
  }

  // ---------- 上报状态 ----------
  function post(obj) {
    try {
      fetch(BASE + '/state', {
        method: 'POST', body: JSON.stringify(obj), cache: 'no-store',
        headers: { 'Content-Type': 'text/plain' }
      }).catch(function () {});
    } catch (e) {}
  }

  function ack(cmd, result) {
    var v = vid();
    post({
      ver: BRIDGE_VER,
      ack: cmd, result: result, pick: lastPickHow,
      rate: v ? v.playbackRate : null,
      paused: v ? v.paused : null,
      videos: document.querySelectorAll('video').length,
      cands: candsInfo()
    });
  }

  function report() {
    var v = vid();
    post({
      ver: BRIDGE_VER,
      site: SITE,
      title: document.title,
      hasVideo: !!v,
      rate: v ? v.playbackRate : null,
      paused: v ? v.paused : null,
      videos: document.querySelectorAll('video').length,
      cands: candsInfo()
    });
  }

  // ---------- 主循环：长轮询取指令 ----------
  var running = false;
  var failStreak = 0;
  var useBackground = false;

  async function directLoop() {
    if (running) return;
    running = true;
    while (!useBackground) {
      try {
        var r = await fetch(BASE + '/next?site=' + SITE + '&t=' + Date.now(), { cache: 'no-store' });
        failStreak = 0;
        if (r.status === 200) {
          var j = await r.json();
          if (j && j.cmd) doCmd(j.cmd);
        }
      } catch (e) {
        failStreak++;
        if (failStreak >= 3) {           // 页面 CSP 可能挡住了本机请求，改由后台脚本代劳
          useBackground = true;
          running = false;
          backgroundLoop();
          return;
        }
        await sleep(1500);
      }
    }
    running = false;
  }

  function backgroundLoop() {
    if (!(window.chrome && chrome.runtime && chrome.runtime.sendMessage)) return;
    chrome.runtime.sendMessage({ type: 'poll', site: SITE }, function (res) {
      if (res && res.cmd) doCmd(res.cmd);
      setTimeout(backgroundLoop, 60);
    });
  }

  try {
    chrome.runtime.onMessage.addListener(function (msg) {
      if (msg && msg.type === 'cmd' && msg.cmd) doCmd(msg.cmd);
    });
  } catch (e) {}

  directLoop();
  setInterval(report, 5000);
  report();
  try { window.__wvrBridge = 'ready:' + SITE; } catch (e) {}
  console.log('[网页视频遥控器] 桥接脚本已就绪 · ' + (IS_DOUYIN ? '抖音' : 'B站'));
})();
