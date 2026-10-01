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
    /* F0（Hz/帧，0=无声帧），帧率同 energyRate——K线主题用它驱动蜡烛涨跌 */
    pitch: Float32Array | null;
    pitchRate: number;
}

/* 11025 Hz 对节拍分析足够：onset 包络的帧率固定 50Hz（hop 随采样率自动缩放），
   BPM/相位/能量全部工作在 50Hz 帧上，波形峰也只取 1600 桶——
   采样率减半直接把 POST 体积（长曲 20MB+）和 C# 端 O(n) 循环都砍半 */
const ANALYZE_RATE = 11025;

export async function analyzeAudioOffThread(file: File): Promise<AnalyzedAudio> {
    // 两条并行，互不占主线程：
    // ① 播放用 AudioBuffer：decodeAudioData 原生异步解码；
    // ② 数值分析：原始文件（几 MB）直接 POST 给 C# /api/analyze/file，
    //    NAudio 解码 + 重采样 + 分析——不再走 OfflineAudioContext 和
    //    40MB 单声道 PCM 的主线程拷贝+上传（载入歌曲时的冻结源）。
    // ② 不可用（旧桌面版 / MediaFoundation 不认的格式）时回退旧 PCM 路径。
    const AC = window.AudioContext || (window as any).webkitAudioContext;
    const ac = new AC();

    const fileP = fetch('/api/analyze/file', {
        method: 'POST',
        headers: {'X-Filename': encodeURIComponent(file.name)},   // C# 按扩展名识别格式（保 ASCII）
        body: file,
    })
        .then(async r => {
            if (!r.ok) throw new Error('status ' + r.status);
            const j = await r.json();
            if (!j.ok) throw new Error(j.error || '分析失败');
            return j;
        })
        .catch(() => null);

    const decodeP = (async () => {
        // decodeAudioData 会 detach 传入的副本，须 slice
        const buf = await ac.decodeAudioData((await file.arrayBuffer()).slice(0));
        try {
            ac.close();
        } catch { /* 已关 */
        }
        return buf;
    })();

    const audioBuffer = await decodeP;
    let j = await fileP;
    if (!j) j = await analyzePcmFallback(audioBuffer);

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
        pitch: j.pitch ? Float32Array.from(j.pitch) : null,
        pitchRate: j.pitchRate || j.energyRate || 0,
    };
}

/* 旧路径：OfflineAudioContext 重采样 + 40MB PCM 上传（/api/analyze）。回退用 */
async function analyzePcmFallback(audioBuffer: AudioBuffer): Promise<any> {
    const offLen = Math.max(1, Math.ceil(audioBuffer.duration * ANALYZE_RATE));
    const off = new OfflineAudioContext(1, offLen, ANALYZE_RATE);
    const src = off.createBufferSource();
    src.buffer = audioBuffer;
    src.connect(off.destination);
    src.start(0);
    const mono = (await off.startRendering()).getChannelData(0);

    const payload = new ArrayBuffer(8 + mono.length * 4);
    new DataView(payload).setInt32(0, ANALYZE_RATE, true);
    new DataView(payload).setInt32(4, mono.length, true);
    new Float32Array(payload, 8).set(mono);
    const r = await fetch('/api/analyze', {method: 'POST', body: payload});
    if (!r.ok) throw new Error('本机分析服务不可用（' + r.status + '）');
    const j = await r.json();
    if (!j.ok) throw new Error(j.error || '分析失败');
    return j;
}
