path "selfrun-apps/data/env/+/orgs/+/apps/+/services/+/+" {
  capabilities = ["create", "read", "update"]
}

path "selfrun-apps/data/env/+/orgs/+/providers/+/credentials/+" {
  capabilities = ["create", "read", "update"]
}

path "selfrun-apps/metadata/env/+/orgs/+/apps/+/services/+/+" {
  capabilities = ["delete"]
}

path "selfrun-apps/metadata/env/+/orgs/+/providers/+/credentials/+" {
  capabilities = ["delete"]
}

path "auth/token/renew-self" {
  capabilities = ["update"]
}

path "auth/token/lookup-self" {
  capabilities = ["read"]
}

path "selfrun-apps/data/env/+/orgs/+/integrations/+/credentials/+" {
  capabilities = ["create", "update", "read"]
}

path "selfrun-apps/metadata/env/+/orgs/+/integrations/+/credentials/+" {
  capabilities = ["delete"]
}
