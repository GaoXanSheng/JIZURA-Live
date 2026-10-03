// 通用确认弹窗（Promise 化）：askConfirm 发 bus 事件让 Dialogs.vue 弹窗，
// 用户作答（或 Esc/点背景=取消）后 resolve。导出前的「是否另存 WAV」这类
// 需要在用户手势现场问一句的场景用，避免到处写原生 confirm
import {bus} from './bus';

let resolver: ((v: boolean) => void) | null = null;

export function askConfirm(text: string, title = '确认'): Promise<boolean> {
  if (resolver) resolver(false);   // 上一个还没作答：按取消处理，不排队
  return new Promise(r => {
    resolver = r;
    bus.emit('confirm-open', {text, title});
  });
}

// 只有 Dialogs.vue 应该调用
export function answerConfirm(v: boolean) {
  const r = resolver;
  resolver = null;
  if (r) r(v);
}
