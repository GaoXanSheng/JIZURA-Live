/* 极简事件总线：引擎运行时（非响应式）→ Vue 组件（响应式）的通知通道。
   高频事件（frame）只给需要的组件用，禁止把整个引擎状态响应式化。
   handler 表锚在 globalThis：vite dev 的模块多实例（HMR ?t 变体）下，
   引擎侧和组件侧即使拿到不同副本也共享同一张表，事件不会失联。 */
type Handler = (...args: any[]) => void;

const map: Map<string, Set<Handler>> = (globalThis as any).__jl_bus ||= new Map();

export const bus = {
  on(ev: string, fn: Handler): () => void {
    let set = map.get(ev);
    if (!set) { set = new Set(); map.set(ev, set); }
    set.add(fn);
    return () => set!.delete(fn);
  },
  emit(ev: string, ...args: any[]) {
    const set = map.get(ev);
    if (!set) return;
    for (const fn of [...set]) {
      try { fn(...args); } catch (e) { console.error(e); }
    }
  },
};
