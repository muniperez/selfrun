import json
import unittest

import test_server_installer as fixtures


installer = fixtures.installer


class PasskeysConfigTests(unittest.TestCase):
    setUp = fixtures.InstallerTests.setUp
    write = staticmethod(fixtures.InstallerTests.write)
    fixture_chain = fixtures.InstallerTests.fixture_chain
    subject = fixtures.InstallerTests.subject

    def configure(self, option):
        config = json.loads(self.config.read_text())
        if option is None:
            config.pop("passkeys", None)
        else:
            config["passkeys"] = option
        self.config.write_text(json.dumps(config))

    def test_defaults_enable_passkeys_scoped_to_console_hostname(self):
        self.configure(None)
        subject = self.subject()
        subject.state = {"installationId": "synthetic"}
        env = subject.environment()
        self.assertEqual("true", env["MINICLOUD_PASSKEY_ENABLED"])
        self.assertEqual("Minicloud", env["MINICLOUD_PASSKEY_RP_DISPLAY_NAME"])
        self.assertEqual("cloud.example.com", env["MINICLOUD_PASSKEY_RP_ID"])

    def test_parent_domain_rp_id_and_custom_display_name_are_accepted(self):
        self.configure({
            "enabled": True,
            "rpDisplayName": "Example Cloud",
            "rpId": "example.com",
        })
        subject = self.subject()
        subject.state = {"installationId": "synthetic"}
        env = subject.environment()
        self.assertEqual("true", env["MINICLOUD_PASSKEY_ENABLED"])
        self.assertEqual("Example Cloud", env["MINICLOUD_PASSKEY_RP_DISPLAY_NAME"])
        self.assertEqual("example.com", env["MINICLOUD_PASSKEY_RP_ID"])

    def test_explicit_disable_sets_passkey_flag_false(self):
        self.configure({"enabled": False})
        subject = self.subject()
        subject.state = {"installationId": "synthetic"}
        env = subject.environment()
        self.assertEqual("false", env["MINICLOUD_PASSKEY_ENABLED"])
        self.assertEqual("cloud.example.com", env["MINICLOUD_PASSKEY_RP_ID"])

    def test_invalid_rp_id_display_name_or_unknown_fields_fail_before_mutation(self):
        for option in (
            {"enabled": "true"},
            {"rpDisplayName": ""},
            {"rpDisplayName": "   "},
            {"rpDisplayName": "Leading space "},
            {"rpDisplayName": "Bad\nName"},
            {"rpDisplayName": "x" * 65},
            {"rpId": "com"},
            {"rpId": "other.example.com"},
            {"rpId": "evil.com"},
            {"rpId": "oud.example.com"},
            {"rpId": "https://cloud.example.com"},
            {"enabled": True, "origins": ["https://cloud.example.com"]},
        ):
            with self.subTest(option=option):
                self.configure(option)
                with self.assertRaises(installer.InstallError):
                    self.subject()
                self.assertFalse(self.args.root.exists())


if __name__ == "__main__":
    unittest.main()
