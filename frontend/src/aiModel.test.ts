import { describe, expect, it } from "vitest";
import { aiDisclosureText, aiModelDisplayName } from "./aiModel";

describe("aiModelDisplayName", () => {
  it("turns a vendor/model:free slug into a short readable name", () => {
    expect(aiModelDisplayName("minimax/minimax-m2.7:free")).toBe("MiniMax M2.7");
    expect(aiModelDisplayName("google/gemma-4-26b-a4b-it:free")).toBe("Gemma 4 26B");
  });

  it("never returns the router's own name", () => {
    expect(aiModelDisplayName(null)).toBeNull();
    expect(aiModelDisplayName(undefined)).toBeNull();
  });
});

describe("aiDisclosureText", () => {
  it("names the real model when one was captured", () => {
    expect(aiDisclosureText("minimax/minimax-m2.7:free")).toBe("🤖 MiniMax M2.7 tarafından oluşturuldu");
  });

  it("falls back to a generic line when no model was captured (e.g. the local fallback ran)", () => {
    expect(aiDisclosureText(null)).toBe("🤖 Yapay zekâ tarafından oluşturuldu");
  });
});
