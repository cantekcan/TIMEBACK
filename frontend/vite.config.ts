/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  test: {
    environment: "node",
    include: ["src/**/*.test.ts", "src/**/*.test.tsx"],
    // "lcov" is what SonarQube Cloud's JS/TS analyzer (sonar.javascript.lcov.reportPaths) reads;
    // the rest are the same reporters Vitest already produced by default before this was set.
    coverage: { reporter: ["text", "html", "clover", "json", "lcov"] },
  },
});
