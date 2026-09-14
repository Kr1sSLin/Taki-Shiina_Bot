import { resolve } from 'node:path'
import { defineConfig, externalizeDepsPlugin } from 'electron-vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  main: {
    plugins: [externalizeDepsPlugin()],
    resolve: {
      alias: {
        '@shared': resolve('src/shared'),
        '@main': resolve('src/main')
      }
    },
    build: {
      rollupOptions: {
        input: {
          index: resolve('src/main/index.ts'),
          // 无界面集成自检入口（`npm run selftest`）：以真实 Electron 主进程运行
          // 但不创建窗口，用于无显示器 / 无 GPU / 受限沙箱环境下的验收与排障。
          selftest: resolve('src/main/selftest.ts')
        },
        /**
         * ⚠️ 必须把 chunk 输出到与入口**同一目录**，不能用 electron-vite 默认的 `chunks/` 子目录。
         *
         * 原因：主进程代码用 `__dirname` 定位预加载脚本与渲染页面
         * （`join(__dirname, '../preload/index.js')`、`join(__dirname, '../renderer/index.html')`）。
         * Rollup 在多入口下会把被共享的模块抽成独立 chunk；一旦 chunk 落在
         * `out/main/chunks/`，那些模块里的 `__dirname` 就变成 `out/main/chunks`，
         * 于是上述相对路径全部指错 →
         * **预加载脚本加载失败 + 渲染页面找不到 → 窗口一片空白**（此问题曾真实发生）。
         *
         * 让 chunk 与入口同层，`__dirname` 对二者一致，相对路径语义就与单文件构建完全相同。
         */
        output: {
          entryFileNames: '[name].js',
          chunkFileNames: '[name]-[hash].js'
        }
      }
    }
  },
  preload: {
    plugins: [externalizeDepsPlugin()],
    resolve: {
      alias: {
        '@shared': resolve('src/shared')
      }
    },
    build: {
      rollupOptions: {
        input: { index: resolve('src/preload/index.ts') }
      }
    }
  },
  renderer: {
    root: resolve('src/renderer'),
    resolve: {
      alias: {
        '@shared': resolve('src/shared'),
        '@renderer': resolve('src/renderer/src')
      }
    },
    plugins: [react()],
    build: {
      rollupOptions: {
        input: { index: resolve('src/renderer/index.html') }
      }
    }
  }
})
