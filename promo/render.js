// usage: node render.js snap 3.0 9.5 ...   |  node render.js all
const puppeteer = require('puppeteer-core');
const path = require('path'), fs = require('fs');
(async () => {
  const [mode, ...args] = process.argv.slice(2);
  const browser = await puppeteer.launch({
    executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe',
    headless: 'new', args: ['--force-device-scale-factor=1', '--hide-scrollbars'] });
  const page = await browser.newPage();
  await page.setViewport({ width: 1080, height: 1920, deviceScaleFactor: 1 });
  await page.goto(require('url').pathToFileURL(path.resolve('promo.html')).href, { waitUntil: 'networkidle0' });
  await page.evaluate(() => document.fonts.ready);
  await new Promise(r => setTimeout(r, 500));
  if (mode === 'events') {
    fs.writeFileSync('events.json', JSON.stringify(await page.evaluate(() => window.events())));
  } else if (mode === 'snap') {
    fs.mkdirSync('snaps', { recursive: true });
    // walk forward at 30fps so smoothing state is right
    const targets = args.map(Number).sort((a, b) => a - b);
    let f = 0;
    for (const tt of targets) {
      for (; f / 30 <= tt; f++) await page.evaluate(t => render(t), f / 30);
      await page.screenshot({ path: `snaps/t${tt.toFixed(2)}.png` });
    }
  } else {
    fs.mkdirSync('frames', { recursive: true });
    const n = 30 * 30;
    for (let f = 0; f < n; f++) {
      await page.evaluate(t => render(t), f / 30);
      await page.screenshot({ path: `frames/f${String(f).padStart(4, '0')}.jpg`, type: 'jpeg', quality: 95 });
      if (f % 150 === 0) console.log('frame', f);
    }
  }
  await browser.close();
})();
