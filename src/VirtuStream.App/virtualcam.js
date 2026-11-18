// Niveau 1 : caméra interne au navigateur (n'a AUCUN effet hors de VirtuStream).
// Activée volontairement par l'utilisateur ; ne remplace pas un pilote Windows.
(() => {
  if (window.__vsInstalled) return; window.__vsInstalled = true;
  const md = navigator.mediaDevices; if (!md || !md.getUserMedia) return;
  const realGUM = md.getUserMedia.bind(md);
  let vid = null, actx = null, dest = null;
  async function ensure(cfg) {
    if (vid && vid.dataset.src === cfg.url) return vid;
    if (vid) { vid.pause(); vid.remove(); }
    vid = document.createElement('video');
    vid.crossOrigin = 'anonymous'; vid.loop = true; vid.playsInline = true; vid.dataset.src = cfg.url; vid.src = cfg.url;
    vid.style.cssText = 'position:fixed;width:1px;height:1px;opacity:0;pointer-events:none;';
    document.documentElement.appendChild(vid);
    await vid.play();
    return vid;
  }
  md.getUserMedia = async function (constraints) {
    const cfg = window.__VS_CFG;
    if (!cfg || !cfg.enabled || !constraints || !constraints.video) return realGUM(constraints);
    const v = await ensure(cfg);
    const out = new MediaStream();
    v.captureStream(cfg.fps || 30).getVideoTracks().forEach(t => {
      Object.defineProperty(t, 'label', { value: 'VirtuStream Virtual Camera (virtuelle)' });
      out.addTrack(t);
    });
    if (constraints.audio && cfg.audio) {
      if (!actx) { actx = new AudioContext(); }
      if (!dest) { const src = actx.createMediaElementSource(v); dest = actx.createMediaStreamDestination(); src.connect(dest); }
      dest.stream.getAudioTracks().forEach(t => out.addTrack(t));
    } else if (constraints.audio) {
      try { (await realGUM({ audio: constraints.audio })).getAudioTracks().forEach(t => out.addTrack(t)); } catch (e) {}
    }
    return out;
  };
})();
