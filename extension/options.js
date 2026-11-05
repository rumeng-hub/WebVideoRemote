const radios = Array.prototype.slice.call(document.querySelectorAll('input[name="rate"]'));
const st = document.getElementById('st');

function mark(rate) {
  radios.forEach(function (el) { el.checked = (parseFloat(el.value) === parseFloat(rate)); });
}

chrome.storage.sync.get({ holdRate: 2 }, function (o) {
  mark(o && o.holdRate);
});

radios.forEach(function (el) {
  el.addEventListener('change', function () {
    if (!el.checked) return;
    chrome.storage.sync.set({ holdRate: parseFloat(el.value) }, function () {
      st.textContent = '已保存：长按 ' + el.value + 'x，松手回到 1x';
      setTimeout(function () { st.textContent = ''; }, 1800);
    });
  });
});
