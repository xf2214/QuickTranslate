QuickTranslate Per-Monitor DPI 人工测试指南
环境要求：Windows 11/10 双屏或多屏，分辨率/缩放可独立配置（例如 屏1: 100%，屏2: 200%）。
TR-M6.2.1：
  1) 启动 QuickTranslate。
  2) 屏1(100%) Word/Block 各触发 5 次 → 框选文字与翻译弹窗肉眼对齐。
  3) 屏2(200%) 同 2)。
TR-M6.2.2：
  4) 鼠标在屏1 100% 放光标位置，快速移到屏2 200%，<500ms 内按 Alt+1 → 框位对齐。
  5) 反方向（屏2→屏1）再做 5 次。
通过：20 次中 ≥ 18 次无明显偏移。
截图保存为 docs/per-monitor-dpi-test-results/<timestamp>.png。
