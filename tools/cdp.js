// 通过 CDP 在页面里执行一段 JS 并打印结果，用来验证「按键到底有没有到网页里」。
// 用法： node cdp.js <url关键字> <表达式>     环境变量 CDP_PORT 可改调试端口（默认 9224）
const key = process.argv[2];
const expr = process.argv[3];
const PORT = process.env.CDP_PORT || '9224';

async function main() {
  const list = await (await fetch('http://127.0.0.1:' + PORT + '/json')).json();
  const pages = list.filter(t => t.type === 'page' && (t.url + t.title).toLowerCase().includes(key.toLowerCase()));
  if (!pages.length) { console.log('NO_TAB_FOUND: ' + key + ' (共 ' + list.length + ' 个目标)'); return; }
  const tab = pages[0];
  console.log('TAB: ' + tab.title);

  const ws = new WebSocket(tab.webSocketDebuggerUrl);
  let id = 0;
  const pending = new Map();
  const send = (method, params) => new Promise((res) => {
    const mid = ++id;
    pending.set(mid, res);
    ws.send(JSON.stringify({ id: mid, method, params }));
  });

  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
  };

  await new Promise((r) => { ws.onopen = r; });
  const isDispatch = expr.startsWith('DISPATCH:');
  const r = isDispatch
    ? await send('Input.dispatchKeyEvent', { type: 'keyDown', key: expr.slice(9), code: expr.slice(9), windowsVirtualKeyCode: 65, nativeVirtualKeyCode: 65 })
    : await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  const out = r.result && r.result.result ? r.result.result.value : r;
  console.log('RESULT: ' + JSON.stringify(out));
  ws.close();
  setTimeout(() => process.exit(0), 100);
}

main().catch(e => { console.log('ERR ' + e.message); process.exit(1); });
