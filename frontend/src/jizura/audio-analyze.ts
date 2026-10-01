/* 音频分析（C# 版）：
   decode 在主线程（WebAudio 限制，native 异步不阻塞 UI），
   单声道低采样 PCM POST 给本机服务 /api/analyze——数值计算全部在 C# 完成
   （同一套算法，比浏览器快一个量级），主线程零重活，载入歌曲不再卡顿。
   返回结构与引擎 J.analyzeAudio 完全一致。 */
export interface AnalyzedAudio {
  name: string;
  duration: number;
  sampleRate: number;
  buffer: AudioBuffer;
  bpm: number;
  beats: number[];
  energy: Float32Array;
  energyRate: number;
  peaks: Float32Array;
}

/* 11025 Hz 对节拍分析足够：onset 包络的帧率固定 50Hz（hop 随采样率自动缩放），
   BPM/相位/能量全部工作在 50Hz 帧上，波形峰也只取 1600 桶——
   采样率减半直接把 POST 体积（长曲 20MB+）和 C# 端 O(n) 循环都砍半 */
const ANALYZE_RATE = 11025;

export async function analyzeAudioOffThread(file: File): Promise<AnalyzedAudio> {
  // 1) 全质量 decode：预览播放用（decodeAudioData 会 detach 传入的副本，须 slice）
  const AC = window.AudioContext || (window as any).webkitAudioContext;
  const ac = new AC();
  let audioBuffer: AudioBuffer;
  try {
    audioBuffer = await ac.decodeAudioData((await file.arrayBuffer()).slice(0));
  } finally {
    try {
      ac.close();
    } catch {
    }
  }

  // 2) 分析用单声道低采样：从已解码的 audioBuffer 离线重采样渲染。
  //    不再对压缩文件做第二次 decodeAudioData——整文件再拷贝 + FLAC 二次解码
  //    正是载入瞬间卡顿的大头；渲染在音频线程跑，主线程只收小结果
  const offLen = Math.max(1, Math.ceil(audioBuffer.duration * ANALYZE_RATE));
  const off = new OfflineAudioContext(1, offLen, ANALYZE_RATE);
  const src = off.createBufferSource();
  src.buffer = audioBuffer;
  src.connect(off.destination);
  src.start(0);
  const mono = (await off.startRendering()).getChannelData(0);

  // 3) [int32 sampleRate][int32 count][float32×count] → C#
  const payload = new ArrayBuffer(8 + mono.length * 4);
  new DataView(payload).setInt32(0, ANALYZE_RATE, true);
  new DataView(payload).setInt32(4, mono.length, true);
  new Float32Array(payload, 8).set(mono);
  const r = await fetch('/api/analyze', { method: 'POST', body: payload });
  if (!r.ok) throw new Error('本机分析服务不可用（' + r.status + '）');
  const j = await r.json();
  if (!j.ok) throw new Error(j.error || '分析失败');

  return {
    name: file.name,
    duration: audioBuffer.duration,
    sampleRate: audioBuffer.sampleRate,
    buffer: audioBuffer,
    bpm: j.bpm,
    beats: j.beats,
    energy: Float32Array.from(j.energy),
    energyRate: j.energyRate,
    peaks: Float32Array.from(j.peaks),
  };
}
