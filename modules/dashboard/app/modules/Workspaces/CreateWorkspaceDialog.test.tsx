import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createDevelopmentWorkspace, mutateDevelopmentWorkspace } from "@/api/environmentsApi";
import { getWorkspaceSelections, workspaceOperation, type WorkspaceSelectionOption } from "@/api/workspacesApi";
import { ApiError, type DevelopmentWorkspace } from "@/models/controlPlane";
import { CreateWorkspaceDialog } from "./CreateWorkspaceDialog";

vi.mock("@/api/environmentsApi", () => ({ createDevelopmentWorkspace: vi.fn(), mutateDevelopmentWorkspace: vi.fn() }));
vi.mock("@/api/workspacesApi", () => ({ getWorkspaceSelections: vi.fn(), workspaceOperation: vi.fn() }));
const workspace = { id: "created-workspace", name: "API sandbox", desiredState: "stopped" } as DevelopmentWorkspace;
const props = { organizationId: "org-one", onCreated: vi.fn(), canManage: true };
const standard: WorkspaceSelectionOption = {
  imageId: "connect-one", imageName: "Muni T3 with Connect", profile: "standard", planId: "plan-one", planName: "Dev small",
  providerAccountName: "Team Hetzner", region: "fsn1", sku: "cx23", cpuCount: 2, memoryGiB: 8, computeHourlyRate: 0.05,
};
const large: WorkspaceSelectionOption = { ...standard, profile: "large", planId: "plan-two", planName: "Dev large", cpuCount: 4, memoryGiB: 16, computeHourlyRate: 0.1 };
const catalog = (options: WorkspaceSelectionOption[]) => ({ available: true, options, currency: "USD", volumeMonthlyRatePerGb: 0.05 });
const submitButton = () => screen.getByRole("button", { name: "Create environment" });

async function open() {
  fireEvent.click(screen.getByRole("button", { name: "Create dev environment" }));
  await waitFor(() => expect(screen.queryByText("Loading options…")).not.toBeInTheDocument());
}

describe("CreateWorkspaceDialog", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    sessionStorage.clear();
    vi.mocked(getWorkspaceSelections).mockResolvedValue(catalog([standard]));
    vi.mocked(createDevelopmentWorkspace).mockResolvedValue(workspace);
  });
  afterEach(() => { cleanup(); vi.restoreAllMocks(); });

  it("does not offer creation or request eligibility to read-only users", () => {
    render(<CreateWorkspaceDialog {...props} canManage={false} />);
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
    expect(getWorkspaceSelections).not.toHaveBeenCalled();
  });

  it("creates and starts an environment from a name and one submit with the only option and 20 GB, disclosing the hourly price first", async () => {
    render(<CreateWorkspaceDialog {...props} />);
    await open();
    expect(getWorkspaceSelections).toHaveBeenCalledWith("org-one", undefined, expect.any(AbortSignal));
    expect(screen.getByText("Its primary VM starts automatically after you create it.")).toBeInTheDocument();
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Refresh/ })).not.toBeInTheDocument();
    const summary = screen.getByRole("region", { name: "Summary" });
    expect(summary).toHaveTextContent("standard · 2 vCPU, 8 GB RAM");
    expect(summary).toHaveTextContent("20 GB");
    expect(summary).toHaveTextContent("Muni T3 with Connect");
    const estimate = within(summary).getByRole("group", { name: "Projected monthly cost" });
    expect(estimate).toHaveTextContent("VM rate$0.05/hour while running");
    expect(estimate).toHaveTextContent("Storage$1.00/month, even while the VM is stopped");
    expect(estimate).toHaveTextContent("Projected monthly total$37.00 if the VM runs 720 hours this month");
    expect(estimate).toHaveTextContent("never blocks creating or starting a VM");
    expect(within(summary).getByRole("note", { name: "Automatic start" }))
      .toHaveTextContent("Creating starts the primary VM automatically. It's charged $0.05/hour while it runs, until you stop it.");
    expect(summary).toHaveTextContent("doesn't link a T3 account");
    expect(createDevelopmentWorkspace).not.toHaveBeenCalled();
    expect(submitButton()).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    fireEvent.click(submitButton());
    await waitFor(() => expect(props.onCreated).toHaveBeenCalledWith(workspace));
    expect(createDevelopmentWorkspace).toHaveBeenCalledWith({ organizationId: "org-one", name: "API sandbox", imageId: "connect-one",
      profile: "standard", planId: "plan-one", volumeSizeGb: 20, startPrimaryVm: true, idempotencyKey: expect.any(String) });
    expect(createDevelopmentWorkspace).toHaveBeenCalledTimes(1);
    expect(mutateDevelopmentWorkspace).not.toHaveBeenCalled();
    expect(workspaceOperation).not.toHaveBeenCalled();
  });

  it("preselects the single configured default among several options", async () => {
    vi.mocked(getWorkspaceSelections).mockResolvedValue(catalog([standard, { ...large, isDefault: true }]));
    render(<CreateWorkspaceDialog {...props} />);
    await open();
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Summary" })).toHaveTextContent("large · 4 vCPU, 16 GB RAM");
  });

  it.each([
    { hourly: 0, storageRate: 0, storage: "$0.00", total: "$0.00" },
    { hourly: 1000, storageRate: 50, storage: "$1,000.00", total: "$721,000.00" },
  ])("keeps a $total monthly projection informational and allows automatic startup", async ({ hourly, storageRate, storage, total }) => {
    vi.mocked(getWorkspaceSelections).mockResolvedValue({
      ...catalog([{ ...standard, computeHourlyRate: hourly }]), volumeMonthlyRatePerGb: storageRate,
    });
    render(<CreateWorkspaceDialog {...props} />);
    await open();
    const estimate = screen.getByRole("group", { name: "Projected monthly cost" });
    expect(estimate).toHaveTextContent(`Storage${storage}/month`);
    expect(estimate).toHaveTextContent(`Projected monthly total${total} if the VM runs 720 hours this month`);
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    expect(submitButton()).toBeEnabled();
    fireEvent.click(submitButton());
    await waitFor(() => expect(props.onCreated).toHaveBeenCalledWith(workspace));
    expect(createDevelopmentWorkspace).toHaveBeenCalledWith(expect.objectContaining({ startPrimaryVm: true }));
  });

  it("asks for a choice inline when several options have no default, and allows customization", async () => {
    vi.mocked(getWorkspaceSelections).mockResolvedValue(catalog([standard, large]));
    render(<CreateWorkspaceDialog {...props} />);
    await open();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    expect(screen.getByText(/There's no default/)).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Summary" })).not.toBeInTheDocument();
    expect(submitButton()).toBeDisabled();
    const option = within(screen.getByLabelText("Size and location")).getByRole("option", { name: /large/ }) as HTMLOptionElement;
    fireEvent.change(screen.getByLabelText("Size and location"), { target: { value: option.value } });
    fireEvent.click(screen.getByRole("button", { name: "Customize" }));
    expect(screen.getByRole("button", { name: "Customize" })).toHaveAttribute("aria-expanded", "true");
    for (const size of ["", "9", "10001", "10.5"]) {
      fireEvent.change(screen.getByLabelText("Storage (GB)"), { target: { value: size } });
      expect(submitButton()).toBeDisabled();
    }
    fireEvent.change(screen.getByLabelText("Storage (GB)"), { target: { value: "40" } });
    const estimate = screen.getByRole("group", { name: "Projected monthly cost" });
    expect(estimate).toHaveTextContent("Storage$2.00/month");
    expect(estimate).toHaveTextContent("Projected monthly total$74.00 if the VM runs 720 hours this month");
    fireEvent.click(submitButton());
    await waitFor(() => expect(createDevelopmentWorkspace).toHaveBeenCalledWith(expect.objectContaining({ planId: "plan-two", volumeSizeGb: 40 })));
  });

  it("validates names and resets entries on cancel", async () => {
    render(<CreateWorkspaceDialog {...props} />);
    const trigger = screen.getByRole("button", { name: "Create dev environment" });
    await open();
    for (const name of ["   ", "a".repeat(129), "bad\u0007name"]) {
      fireEvent.change(screen.getByLabelText("Name"), { target: { value: name } });
      expect(submitButton()).toBeDisabled();
      fireEvent.submit(screen.getByRole("form", { name: "Create dev environment" }));
    }
    expect(createDevelopmentWorkspace).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    fireEvent.click(trigger);
    expect(screen.getByLabelText("Name")).toHaveValue("");
  });

  it("explains load failures, keeps the name, and retries", async () => {
    vi.mocked(getWorkspaceSelections).mockRejectedValueOnce(new Error("Network down."));
    render(<CreateWorkspaceDialog {...props} />);
    await open();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    expect(screen.getByRole("alert")).toHaveTextContent("Couldn't load the available options");
    expect(submitButton()).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    await screen.findByRole("region", { name: "Summary" });
    expect(screen.getByLabelText("Name")).toHaveValue("API sandbox");
    expect(submitButton()).toBeEnabled();
  });

  it("explains when creation is unavailable or nothing is approved", async () => {
    vi.mocked(getWorkspaceSelections).mockResolvedValueOnce({ available: false, unavailableReason: "Pricing is stale.", options: [] });
    render(<CreateWorkspaceDialog {...props} />);
    await open();
    expect(screen.getByText(/You can't create dev environments right now. Pricing is stale./)).toBeInTheDocument();
    cleanup();
    vi.mocked(getWorkspaceSelections).mockResolvedValueOnce(catalog([]));
    render(<CreateWorkspaceDialog {...props} />);
    await open();
    expect(screen.getByText(/No approved setup is available/)).toBeInTheDocument();
    expect(submitButton()).toBeDisabled();
  });

  it("keeps entries and the idempotency key after an ambiguous failure and prevents duplicate submission", async () => {
    let reject!: (reason: Error) => void;
    vi.mocked(createDevelopmentWorkspace).mockImplementationOnce(() => new Promise((_, fail) => { reject = fail; }));
    render(<CreateWorkspaceDialog {...props} />);
    await open();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    const form = screen.getByRole("form", { name: "Create dev environment" });
    fireEvent.submit(form);
    fireEvent.submit(form);
    expect(createDevelopmentWorkspace).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("button", { name: "Creating…" })).toBeDisabled();
    reject(new Error("Connection interrupted; result unknown."));
    expect(await screen.findByRole("alert")).toHaveTextContent("result unknown");
    expect(screen.getByLabelText("Name")).toHaveValue("API sandbox");
    fireEvent.submit(form);
    await waitFor(() => expect(props.onCreated).toHaveBeenCalledWith(workspace));
    const calls = vi.mocked(createDevelopmentWorkspace).mock.calls;
    expect(calls[0][0]).toEqual(calls[1][0]);
  });

  it("reuses an uncertain create after cancel, remount or refresh instead of creating and starting another environment", async () => {
    vi.mocked(createDevelopmentWorkspace).mockRejectedValueOnce(new Error("Connection interrupted; result unknown."));
    const first = render(<CreateWorkspaceDialog {...props} />);
    await open();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    fireEvent.click(submitButton());
    await screen.findByRole("alert");
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    first.unmount();

    render(<CreateWorkspaceDialog {...props} />);
    await open();
    expect(createDevelopmentWorkspace).toHaveBeenCalledTimes(1);
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    fireEvent.click(submitButton());
    await waitFor(() => expect(props.onCreated).toHaveBeenCalledWith(workspace));
    const calls = vi.mocked(createDevelopmentWorkspace).mock.calls;
    expect(calls[1][0]).toEqual(calls[0][0]);
  });

  it("keeps an in-flight create's key after unmount, even if its response arrives before the retry", async () => {
    let resolve!: (value: DevelopmentWorkspace) => void;
    vi.mocked(createDevelopmentWorkspace).mockImplementationOnce(() => new Promise(done => { resolve = done; }));
    const first = render(<CreateWorkspaceDialog {...props} />);
    await open();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    fireEvent.click(submitButton());
    first.unmount();
    resolve(workspace);
    await waitFor(() => expect(props.onCreated).not.toHaveBeenCalled());

    render(<CreateWorkspaceDialog {...props} />);
    await open();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    fireEvent.click(submitButton());
    await waitFor(() => expect(props.onCreated).toHaveBeenCalledWith(workspace));
    const calls = vi.mocked(createDevelopmentWorkspace).mock.calls;
    expect(calls[1][0]).toEqual(calls[0][0]);
  });

  it("does not send a create when browser storage cannot preserve a safe retry", async () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new DOMException("Quota exceeded", "QuotaExceededError"); });
    render(<CreateWorkspaceDialog {...props} />);
    await open();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    fireEvent.click(submitButton());
    expect(await screen.findByRole("alert")).toHaveTextContent("No create request was sent.");
    expect(createDevelopmentWorkspace).not.toHaveBeenCalled();
    expect(submitButton()).toBeEnabled();
  });

  it("separates changed requests while retaining uncertain retries and clears an acknowledged success", async () => {
    vi.mocked(createDevelopmentWorkspace)
      .mockRejectedValueOnce(new Error("Result unknown."))
      .mockRejectedValueOnce(new Error("Result unknown."));
    render(<CreateWorkspaceDialog {...props} />);
    await open();
    const name = screen.getByLabelText("Name");
    fireEvent.change(name, { target: { value: "API sandbox" } });
    fireEvent.click(submitButton());
    await screen.findByRole("alert");
    fireEvent.change(name, { target: { value: "Different environment" } });
    fireEvent.click(submitButton());
    await screen.findByRole("alert");
    fireEvent.change(name, { target: { value: "API sandbox" } });
    fireEvent.click(submitButton());
    await waitFor(() => expect(props.onCreated).toHaveBeenCalledTimes(1));

    await open();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    fireEvent.click(submitButton());
    await waitFor(() => expect(props.onCreated).toHaveBeenCalledTimes(2));
    const calls = vi.mocked(createDevelopmentWorkspace).mock.calls;
    expect(calls[0][0].idempotencyKey).not.toEqual(calls[1][0].idempotencyKey);
    expect(calls[2][0]).toEqual(calls[0][0]);
    expect(calls[3][0].idempotencyKey).not.toEqual(calls[0][0].idempotencyKey);
  });

  it("reloads options after a stale-selection rejection and asks only for the stale choice", async () => {
    vi.mocked(getWorkspaceSelections).mockResolvedValueOnce(catalog([standard])).mockResolvedValueOnce(catalog([large, { ...large, planId: "plan-three", planName: "Dev other" }]));
    vi.mocked(createDevelopmentWorkspace).mockRejectedValueOnce(new ApiError(400, { code: "workspace_plan_invalid", message: "Plan is no longer approved.", details: [] }));
    render(<CreateWorkspaceDialog {...props} />);
    await open();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API sandbox" } });
    fireEvent.click(submitButton());
    expect(await screen.findByText("Plan is no longer approved.")).toBeInTheDocument();
    expect(await screen.findByText(/no longer available. Choose another option/)).toBeInTheDocument();
    expect(getWorkspaceSelections).toHaveBeenCalledTimes(2);
    expect(screen.getByLabelText("Name")).toHaveValue("API sandbox");
    expect(screen.getByLabelText("Storage (GB)")).toHaveValue(20);
    expect(submitButton()).toBeDisabled();
  });
});
