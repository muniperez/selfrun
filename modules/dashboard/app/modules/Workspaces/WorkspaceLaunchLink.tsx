import { useEffect, useState, type MouseEvent } from "react";

type Props = {
  url: string;
  deadline: number;
  label: string;
  onExpire: () => void;
};

export function WorkspaceLaunchLink({ url, deadline, label, onExpire }: Props) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    let timer: number | undefined;
    const update = () => {
      window.clearTimeout(timer);
      const time = Date.now();
      if (time >= deadline) { onExpire(); return; }
      setNow(time);
      timer = window.setTimeout(update, Math.min(1_000, deadline - time));
    };
    update();
    document.addEventListener("visibilitychange", update);
    window.addEventListener("focus", update);
    window.addEventListener("pageshow", update);
    return () => {
      window.clearTimeout(timer);
      document.removeEventListener("visibilitychange", update);
      window.removeEventListener("focus", update);
      window.removeEventListener("pageshow", update);
    };
  }, [deadline, onExpire]);

  const guard = (event: MouseEvent<HTMLAnchorElement>) => {
    if (Date.now() < deadline) return;
    event.preventDefault();
    onExpire();
  };
  const seconds = Math.ceil((deadline - Math.max(now, Date.now())) / 1_000);
  if (seconds <= 0) return null;
  const remaining = seconds >= 60 ? `${Math.floor(seconds / 60)}m` : `${seconds}s`;
  return <p className="break-words px-5 py-2 text-sm">
    <a className="text-primary underline underline-offset-4" href={url} target="_blank" rel="noopener noreferrer"
      onClick={guard} onAuxClick={guard} onContextMenu={guard}>{label}</a>
    <span className="ml-2 text-muted-foreground">Expires in {remaining}</span>
    <span className="block text-xs text-muted-foreground">The countdown is for opening this link, not for the session.</span>
  </p>;
}
