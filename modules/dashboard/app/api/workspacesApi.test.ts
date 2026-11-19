import { afterEach, expect, it, vi } from "vitest";
import { setCsrfToken } from "./apiClient";
import { deleteWorkspace, getWorkspaceLaunchUrl, launchEphemeralVm, mutateWorkspaceVm, workspaceTask } from "./workspacesApi";

afterEach(() => vi.unstubAllGlobals());

it.each([[202, "deleting"], [200, "deleted"]])("parses HTTP %s permanent deletion response (%s)", async (status, phase) => {
  setCsrfToken("synthetic-csrf");
  const body = { id: "work/space", phase, volumeStatus: phase };
  const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } }));
  vi.stubGlobal("fetch", fetcher);

  await expect(deleteWorkspace("work/space", "org a")).resolves.toEqual(body);
  expect(fetcher).toHaveBeenCalledExactlyOnceWith(
    expect.stringContaining("/api/development-workspaces/work%2Fspace?organizationId=org%20a&confirmPermanentDeletion=true"),
    expect.objectContaining({ method: "DELETE", body: undefined, credentials: "include",
      headers: expect.objectContaining({ "X-Minicloud-CSRF": "synthetic-csrf" }) }),
  );
});

it("uses organization-scoped routes, escaped identities, generation fencing and explicit data policies", async () => {
  setCsrfToken("synthetic-csrf");
  const fetcher = vi.fn().mockImplementation(() => Promise.resolve(new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } })));
  vi.stubGlobal("fetch", fetcher);
  const request = { imageId: "image", profile: "Heavy", planId: "plan", repository: "https://git.example/repo", baseRef: "main",
    baseCommit: "a".repeat(40), pushAuthorized: true, acknowledgeIgnoredDataLoss: true, idempotencyKey: "launch" };
  await launchEphemeralVm("work space", "org a", request);
  await mutateWorkspaceVm("work space", "vm/1", "org a", "discard", { generation: 4, discardLocalData: true, acknowledgeIgnoredDataLoss: true });
  await workspaceTask("work space", "vm/1", "org a", "task/1", 4, false);
  await workspaceTask("work space", "vm/1", "org a", "task/1", 4, true, true);
  await getWorkspaceLaunchUrl("work space", "vm/1", "org a", 4, "preview");
  expect(fetcher.mock.calls.map(([url]) => url)).toEqual([
    expect.stringContaining("/work%20space/vms/ephemeral?organizationId=org%20a"),
    expect.stringContaining("/vms/vm%2F1/discard?organizationId=org%20a"),
    expect.stringContaining("/vms/vm%2F1/tasks/task%2F1/lease?organizationId=org%20a"),
    expect.stringContaining("/vms/vm%2F1/tasks/task%2F1/complete?organizationId=org%20a"),
    expect.stringContaining("/vms/vm%2F1/launch?organizationId=org%20a"),
  ]);
  expect(JSON.parse(fetcher.mock.calls[0][1].body)).toEqual(request);
  expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({ generation: 4, discardLocalData: true, acknowledgeIgnoredDataLoss: true });
  expect(fetcher.mock.calls[2][1].method).toBe("PUT");
  expect(JSON.parse(fetcher.mock.calls[3][1].body)).toEqual({ generation: 4, cancelled: true });
  expect(JSON.parse(fetcher.mock.calls[4][1].body)).toEqual({ generation: 4, service: "preview" });
  expect(fetcher.mock.calls[0][1]).toMatchObject({ credentials: "include", headers: { "X-Minicloud-CSRF": "synthetic-csrf" } });
});
