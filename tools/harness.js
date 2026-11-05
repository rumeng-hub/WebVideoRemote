// 本地测试台：静态页 + 事件收集。用真实 Edge 打开 /page 即可观测后台是否收到按键、媒体会话是否被 SMTC 控制。
// 用法： node harness.js [port]
const http = require('http');
const fs = require('fs');
const path = require('path');

const PORT = parseInt(process.argv[2] || '8799', 10);

// 生成一段 30 秒 440Hz 正弦 WAV（单声道 16bit 44.1k），供 <audio> 播放，从而产生真实的媒体会话
function makeWav(seconds, freq) {
  const rate = 44100, ch = 1, bps = 16;
  const n = rate * seconds;
  const dataSize = n * ch * bps / 8;
  const buf = Buffer.alloc(44 + dataSize);
  buf.write('RIFF', 0);
  buf.writeUInt32LE(36 + dataSize, 4);
  buf.write('WAVE', 8);
  buf.write('fmt ', 12);
  buf.writeUInt32LE(16, 16);
  buf.writeUInt16LE(1, 20);
  buf.writeUInt16LE(ch, 22);
  buf.writeUInt32LE(rate, 24);
  buf.writeUInt32LE(rate * ch * bps / 8, 28);
  buf.writeUInt16LE(ch * bps / 8, 32);
  buf.writeUInt16LE(bps, 34);
  buf.write('data', 36);
  buf.writeUInt32LE(dataSize, 40);
  for (let i = 0; i < n; i++) {
    // 音量压到 12%，避免测试时太吵
    const v = Math.round(Math.sin(2 * Math.PI * freq * i / rate) * 32767 * 0.12);
    buf.writeInt16LE(v, 44 + i * 2);
  }
  return buf;
}

const wav = makeWav(30, 440);
const events = [];

const server = http.createServer((req, res) => {
  const url = req.url.split('?')[0];

  if (req.method === 'POST' && url === '/ev') {
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', () => {
      try { events.push(JSON.parse(body)); } catch (e) {}
      if (events.length > 5000) events.splice(0, events.length - 5000);
      res.writeHead(204); res.end();
    });
    return;
  }
  if (url === '/log') {
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(events));
    return;
  }
  if (url === '/clear') {
    events.length = 0;
    res.writeHead(200); res.end('ok');
    return;
  }
  if (url === '/tone.wav') {
    res.writeHead(200, { 'Content-Type': 'audio/wav', 'Content-Length': wav.length });
    res.end(wav);
    return;
  }
  if (url === '/' || url === '/page') {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    res.end(fs.readFileSync(path.join(__dirname, 'page.html')));
    return;
  }
  res.writeHead(404); res.end('nope');
});

server.listen(PORT, '127.0.0.1', () => {
  console.log('harness listening: http://127.0.0.1:' + PORT + '/page');
  console.log('log endpoint     : http://127.0.0.1:' + PORT + '/log');
});
