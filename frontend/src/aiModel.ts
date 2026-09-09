/** Turns a raw AI model slug into a short, readable name for the player - the real model that
 *  answered (e.g. "gemini-3.5-flash-lite" or, on fallback, "gemini-3.1-flash-lite"), taken from
 *  the response body. Kept vendor-agnostic (the lookup table below covers more than just Gemini)
 *  since this same field carried OpenRouter model slugs before the AI provider changed. */
const VENDOR_NAME: Record<string, string> = {
  minimax: "MiniMax", gemma: "Gemma", google: "Google", gpt: "GPT", llama: "Llama",
  qwen: "Qwen", deepseek: "DeepSeek", mistral: "Mistral", phi: "Phi", claude: "Claude",
  grok: "Grok", command: "Command", yi: "Yi", glm: "GLM",
};

function titleToken(token: string): string {
  const lower = token.toLowerCase();
  if (VENDOR_NAME[lower]) return VENDOR_NAME[lower];
  if (/\d/.test(token)) return token.replace(/^[a-z]/i, (c) => c.toUpperCase()).replace(/b$/i, "B");
  return token.charAt(0).toUpperCase() + token.slice(1).toLowerCase();
}

/** "minimax/minimax-m2.7:free" -> "MiniMax M2.7", "google/gemma-4-26b-a4b-it:free" -> "Gemma 4 26B". */
export function aiModelDisplayName(slug: string | null | undefined): string | null {
  if (!slug) return null;
  const afterVendor = slug.includes("/") ? slug.split("/").slice(1).join("/") : slug;
  const withoutTag = afterVendor.split(":")[0];
  const tokens = withoutTag
    .split("-")
    .filter((t) => t.length > 0 && !/^a\d+b$/i.test(t) && t.toLowerCase() !== "it");
  if (tokens.length === 0) return withoutTag;
  return tokens.map(titleToken).join(" ");
}

/** The small, low-contrast disclosure line shown under the AI comment. */
export function aiDisclosureText(slug: string | null | undefined): string {
  const name = aiModelDisplayName(slug);
  return name ? `🤖 ${name} tarafından oluşturuldu` : "🤖 Yapay zekâ tarafından oluşturuldu";
}
