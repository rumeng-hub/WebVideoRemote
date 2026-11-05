// 仅当页面自身的 CSP 挡住了内容脚本访问本机服务时才会用到：
// 由后台 service worker 代取指令，再转发给内容脚本。
const BASE = 'http://127.0.0.1:17777';

async function pollOnce(site) {
  try {
    const r = await fetch(BASE + '/next?site=' + encodeURIComponent(site || 'any') + '&t=' + Date.now(), { cache: 'no-store' });
    if (r.status === 200) {
      const j = await r.json();
      return (j && j.cmd) || null;
    }
  } catch (e) { }
  return null;
}

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (!msg || msg.type !== 'poll') return;
  const site = msg.site || 'any';
  (async () => {
    for (let i = 0; i < 40; i++) {          // 约 20 秒，边轮询边保持 service worker 存活
      const cmd = await pollOnce(site);
      if (cmd) { reply({ cmd }); return; }
    }
    reply({ cmd: null });
  })();
  return true;                              // 异步回复
});
