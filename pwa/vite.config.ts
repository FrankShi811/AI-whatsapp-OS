import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  base: "/AI-whatsapp-OS/",
  plugins: [react()],
  build: { sourcemap: true }
});
