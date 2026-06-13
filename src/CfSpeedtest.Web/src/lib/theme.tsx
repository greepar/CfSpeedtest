import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react";

type ThemePref = "light" | "dark" | "system";
type Resolved = "light" | "dark";

interface ThemeCtx {
  pref: ThemePref;
  resolved: Resolved;
  setPref: (p: ThemePref) => void;
  toggle: () => void;
}

const Ctx = createContext<ThemeCtx | null>(null);
const STORAGE_KEY = "cf-theme";

function readPref(): ThemePref {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    if (v === "light" || v === "dark") return v;
  } catch {
    /* ignore */
  }
  return "system";
}

function systemDark(): boolean {
  return window.matchMedia("(prefers-color-scheme: dark)").matches;
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [pref, setPrefState] = useState<ThemePref>(readPref);
  const [resolved, setResolved] = useState<Resolved>(() =>
    pref === "system" ? (systemDark() ? "dark" : "light") : pref,
  );

  const apply = useCallback((r: Resolved) => {
    const root = document.documentElement;
    root.classList.toggle("dark", r === "dark");
    setResolved(r);
  }, []);

  useEffect(() => {
    if (pref === "system") {
      apply(systemDark() ? "dark" : "light");
      const mq = window.matchMedia("(prefers-color-scheme: dark)");
      const handler = (e: MediaQueryListEvent) =>
        apply(e.matches ? "dark" : "light");
      mq.addEventListener("change", handler);
      return () => mq.removeEventListener("change", handler);
    }
    apply(pref);
  }, [pref, apply]);

  const setPref = useCallback((p: ThemePref) => {
    setPrefState(p);
    try {
      if (p === "system") localStorage.removeItem(STORAGE_KEY);
      else localStorage.setItem(STORAGE_KEY, p);
    } catch {
      /* ignore */
    }
  }, []);

  const toggle = useCallback(() => {
    setPref(resolved === "dark" ? "light" : "dark");
  }, [resolved, setPref]);

  return (
    <Ctx.Provider value={{ pref, resolved, setPref, toggle }}>
      {children}
    </Ctx.Provider>
  );
}

export function useTheme(): ThemeCtx {
  const c = useContext(Ctx);
  if (!c) throw new Error("useTheme must be used within ThemeProvider");
  return c;
}
