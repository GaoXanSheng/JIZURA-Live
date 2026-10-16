// 启动冒烟：拉起主程序，存活 15 秒视为通过；提前退出输出退出码与 stderr 并失败。
const { spawn } = require("child_process");
const path = require("path");
const exe = path.join(__dirname, "..", "bin", "Debug", "net10.0-windows", "JizuraLivePV.exe");
const p = spawn(exe, [], { cwd: path.dirname(exe) });
let err = "";
p.stderr.on("data", (d) => (err += d));
let exited = false;
p.on("exit", (code) => {
  exited = true;
  console.log(`FAIL: 程序在 15 秒内退出，退出码 ${code}\n${err.slice(-3000)}`);
  process.exit(1);
});
setTimeout(() => {
  if (exited) return;
  p.kill();
  console.log("PASS: 程序存活 15 秒");
  process.exit(0);
}, 15000);
