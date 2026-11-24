import json
import pathlib
import re
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
BUNDLE = REPO_ROOT / "packaging/server/bundle"
SELF_HOSTING_DOC = REPO_ROOT / "docs/self-hosting.md"


class PasskeySelfHostingContractTests(unittest.TestCase):
    def test_identity_schemas_enable_passkey_display_name_and_preserve_password_and_recovery(self):
        base = json.loads((BUNDLE / "identity.schema.json").read_text())
        recovery = json.loads((BUNDLE / "identity.recovery.schema.json").read_text())

        for name, schema in (("identity.schema.json", base), ("identity.recovery.schema.json", recovery)):
            with self.subTest(schema=name):
                kratos = schema["properties"]["traits"]["properties"]["email"]["ory.sh/kratos"]
                self.assertEqual({"identifier": True}, kratos["credentials"]["password"])
                self.assertEqual({"display_name": True}, kratos["credentials"]["passkey"])

        self.assertNotIn("recovery", base["properties"]["traits"]["properties"]["email"]["ory.sh/kratos"])
        self.assertEqual(
            {"via": "email"},
            recovery["properties"]["traits"]["properties"]["email"]["ory.sh/kratos"]["recovery"],
        )

    def test_kratos_base_config_declares_passkey_webauthn_rp_and_privileged_settings(self):
        text = (BUNDLE / "kratos.yml").read_text()
        self.assertIn("password:\n      enabled: true", text)
        self.assertIn("passkey:\n      enabled: false", text)
        self.assertIn("display_name: Minicloud", text)
        self.assertIn("id: localhost", text)
        self.assertIn("- https://localhost", text)
        self.assertIn("privileged_session_max_age: 15m", text)

    def test_compose_wires_passkey_rp_id_origins_and_api_toggle_with_overrides(self):
        text = (BUNDLE / "compose.yml").read_text()
        expected_lines = [
            "Kratos__PasskeyEnabled: ${MINICLOUD_PASSKEY_ENABLED:-true}",
            "SELFSERVICE_METHODS_PASSKEY_ENABLED: ${MINICLOUD_PASSKEY_ENABLED:-true}",
            "SELFSERVICE_METHODS_PASSKEY_CONFIG_RP_DISPLAY_NAME: ${MINICLOUD_PASSKEY_RP_DISPLAY_NAME:-Minicloud}",
            "SELFSERVICE_METHODS_PASSKEY_CONFIG_RP_ID: ${MINICLOUD_PASSKEY_RP_ID:-${CONSOLE_HOSTNAME:?set CONSOLE_HOSTNAME}}",
            "SELFSERVICE_METHODS_PASSKEY_CONFIG_RP_ORIGINS: ${MINICLOUD_PASSKEY_RP_ORIGINS:-${CONSOLE_URL:?set CONSOLE_URL}}",
        ]
        for line in expected_lines:
            with self.subTest(line=line):
                self.assertIn(line, text)

    def test_self_hosting_docs_cover_https_rp_id_origins_hostname_change_and_fallback(self):
        doc = SELF_HOSTING_DOC.read_text()
        self.assertIn("### Passkey sign-in and WebAuthn configuration", doc)
        for token in (
            "HTTPS",
            "MINICLOUD_PASSKEY_ENABLED",
            "MINICLOUD_PASSKEY_RP_ID",
            "MINICLOUD_PASSKEY_RP_ORIGINS",
            "MINICLOUD_PASSKEY_RP_DISPLAY_NAME",
            "rp.id",
            "rp.origins",
            "CONSOLE_HOSTNAME",
            "CONSOLE_URL",
        ):
            with self.subTest(token=token):
                self.assertIn(token, doc)
        self.assertRegex(doc, re.compile(r"changing.*hostname|hostname.*chang", re.IGNORECASE | re.DOTALL))
        self.assertRegex(doc, re.compile(r"password.*fallback|fallback.*password", re.IGNORECASE | re.DOTALL))


if __name__ == "__main__":
    unittest.main()
