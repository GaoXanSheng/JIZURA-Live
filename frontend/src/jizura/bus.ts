// 引擎（非响应式）向 Vue 组件发通知用。frame 这类高频事件只给确实需要的组件订阅，
// 不要把引擎状态整个做成响应式。handler 表挂 globalThis：vite dev 的 HMR 会产生
// 模块多实例，引擎和组件可能各拿一个副本，锚在同一张表上事件才不会失联。
type Handler = (...args: any[]) => void;

const map: Map<string, Set<Handler>> = (globalThis as any).__jl_bus ||= new Map();

export const bus = {
    on(ev: string, fn: Handler): () => void {
        let set = map.get(ev);
        if (!set) {
            set = new Set();
            map.set(ev, set);
        }
        set.add(fn);
        return () => set!.delete(fn);
    },
    emit(ev: string, ...args: any[]) {
        const set = map.get(ev);
        if (!set) return;
        for (const fn of [...set]) {
            try {
                fn(...args);
            } catch (e) {
                console.error(e);
            }
        }
    },
};
