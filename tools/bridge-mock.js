// 模拟浏览器扩展的桥接客户端：长轮询取指令并打印，用于本机验证「exe → 浏览器脚本」这条通道。
// 用法： node bridge-mock.js [秒数] [网站 douyin|bilibili|any]
const BASE = 'http://127.0.0.1:17777';
const seconds = parseInt(process.argv[2] || '15', 10);
const site = process.argv[3] || 'any';
const deadline = Date.now() + seconds * 1000;
const tag = '[' + site + ']';

async function main() {
  console.log(tag + ' mock client start');
  let got = 0;
  while (Date.now() < deadline) {
    try {
      const r = await fetch(BASE + '/next?site=' + encodeURIComponent(site) + '&t=' + Date.now(), { cache: 'no-store' });
      if (r.status === 200) {
        const j = await r.json();
        got++;
        console.log(tag + ' 收到指令: ' + j.cmd);
        await fetch(BASE + '/state', {
          method: 'POST',
          body: JSON.stringify({ site: site, ack: j.cmd, result: 'mock', got: got }),
          headers: { 'Content-Type': 'text/plain' }
        });
      }
    } catch (e) {
      await new Promise(r => setTimeout(r, 1500));
    }
  }
  console.log(tag + ' exit, 共收到 ' + got + ' 条指令');
}

main();
