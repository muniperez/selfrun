// The pinned T3 Settings page reads this same discovery RPC result.
export function sourceControlEvidence(discovery, versions) {
  if (!Array.isArray(discovery?.sourceControlProviders)) throw new Error("SOURCE_CONTROL_DISCOVERY_FAILED");
  for (const [kind, executable] of [["github", "gh"], ["gitlab", "glab"]]) {
    const providers = discovery.sourceControlProviders.filter((item) => item.kind === kind);
    const provider = providers[0];
    if (providers.length !== 1 || provider.executable !== executable || provider.status !== "available"
      || provider.auth?.status !== "unauthenticated" || provider.version?._tag !== "Some"
      || typeof provider.version.value !== "string" || typeof versions?.[executable] !== "string"
      || !provider.version.value.split(/[\s,]+/).some((part) =>
        part === versions[executable] || part === `v${versions[executable]}`)) {
      throw new Error("SOURCE_CONTROL_DISCOVERY_FAILED");
    }
  }
  // Never return account names, auth diagnostics, tokens, or CLI output to a report.
  return { githubAvailable: true, gitlabAvailable: true,
    githubAuthentication: "unauthenticated", gitlabAuthentication: "unauthenticated",
    pinnedT3SourceControlDiscoveryChecked: true };
}
