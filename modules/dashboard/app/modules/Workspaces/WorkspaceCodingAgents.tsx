import type { DevelopmentWorkspace } from "@/models/controlPlane";

export function WorkspaceCodingAgents({ agents }: { agents?: DevelopmentWorkspace["agents"] }) {
  if (!agents?.length) return null;
  return <section aria-labelledby="agents-heading" className="space-y-2">
    <h2 id="agents-heading" className="text-lg font-semibold">Coding agents</h2>
    <ul className="flex flex-wrap gap-2 text-sm">
      {agents.map(agent => <li key={agent.id} className="rounded-md border px-3 py-1.5">
        <span className="font-medium">{agent.name}</span> <span className="text-muted-foreground">{agent.version}</span>
      </li>)}
    </ul>
    <p className="text-sm text-muted-foreground">Installed in this dev environment's image and available in T3. Sign in to each agent from T3 before first use.</p>
  </section>;
}
