import hashlib
import io
import json
import os
import tarfile
import unittest
from unittest import mock

import test_server_installer as fixtures


installer = fixtures.installer


class EmailRecoveryTests(unittest.TestCase):
    write = staticmethod(fixtures.InstallerTests.write)
    fixture_chain = fixtures.InstallerTests.fixture_chain
    subject = fixtures.InstallerTests.subject

    def setUp(self):
        fixtures.InstallerTests.setUp(self)
        for name in installer.EMAIL_RECOVERY_FILES:
            self.payload[name] = (fixtures.BUNDLE / name).read_bytes()
        archive = self.assets / self.manifest["bundle"]["name"]
        with tarfile.open(archive, "w:gz") as target:
            for name, data in self.payload.items():
                member = tarfile.TarInfo(name)
                member.size = len(data)
                target.addfile(member, io.BytesIO(data))
        self.manifest["bundle"]["sha256"] = hashlib.sha256(archive.read_bytes()).hexdigest()
        self.manifest["files"] = {name: hashlib.sha256(data).hexdigest() for name, data in self.payload.items()}
        (self.assets / "server-manifest.json").write_text(json.dumps(self.manifest))

    def configure(self, option):
        config = json.loads(self.config.read_text())
        config["emailRecovery"] = option
        self.config.write_text(json.dumps(config))

    def enable(self, *, plaintext=False):
        path = self.base / "smtp.json"
        values = {
            "connectionUri": ("smtp://mail-sink:1025/?disable_starttls=true" if plaintext else
                              "smtps://synthetic-user:synthetic%24%27%23%5Cpassword@smtp.example.invalid:465/"),
            "fromAddress": "recovery@example.invalid", "fromName": "MiniCloud fixture",
        }
        self.write(path, json.dumps(values).encode())
        option = {"enabled": True, "smtpFile": str(path)}
        if plaintext:
            option["allowInsecureSmtp"] = True
        self.configure(option)
        return path, values

    def test_disabled_default_preserves_legacy_config_credentials_and_compose_command(self):
        subject = self.subject()
        subject.execute()
        original = (subject.root / "secrets/kratos.env").read_bytes()
        command, env = subject.compose_invocation("up", "-d", "kratos")
        self.assertNotIn("MINICLOUD_EMAIL_RECOVERY_ENABLED", env)
        self.assertNotIn(str(subject.root / "bundle/compose.email-recovery.yml"), command)
        self.assertNotIn(b"COURIER_", original)
        resumed = self.subject()
        resumed.load_state(manifest=self.manifest)
        self.assertEqual(original, (subject.root / "secrets/kratos.env").read_bytes())
        self.assertNotIn("emailRecovery", resumed.state["configuration"])
        # Explicit false on a new installation needs no SMTP file or new credentials.
        self.configure({"enabled": False})
        disabled = self.subject()
        self.assertEqual({}, disabled.smtp_courier)
        with self.assertRaisesRegex(installer.InstallError, "original configuration"):
            disabled.load_state()
        self.assertEqual(original, (subject.root / "secrets/kratos.env").read_bytes())

    def test_enabled_first_install_uses_protected_smtp_and_signed_overlay_without_secret_arguments(self):
        path, values = self.enable()
        subject = self.subject()
        subject.execute()
        secret = subject.root / "secrets/kratos.env"
        original = secret.read_bytes()
        entries = dict(line.split("=", 1) for line in original.decode().splitlines())
        self.assertEqual(values["connectionUri"], entries["COURIER_SMTP_CONNECTION_URI"])
        self.assertEqual(values["fromAddress"], entries["COURIER_SMTP_FROM_ADDRESS"])
        self.assertEqual(values["fromName"], entries["COURIER_SMTP_FROM_NAME"])
        self.assertEqual(0o600, secret.stat().st_mode & 0o777)
        command, env = subject.compose_invocation("up", "-d", "kratos", "api")
        self.assertIn(str(subject.root / "bundle/compose.email-recovery.yml"), command)
        self.assertEqual("true", env["MINICLOUD_EMAIL_RECOVERY_ENABLED"])
        self.assertEqual("false", env["MINICLOUD_GOOGLE_OIDC_ENABLED"])
        self.assertNotIn(values["connectionUri"], json.dumps([command, env, subject.state]))
        self.assertEqual(str(path), subject.state["configuration"]["emailRecovery"]["smtpFile"])
        resumed = self.subject()
        resumed.execute()
        resumed.load_state(manifest=self.manifest)
        self.assertEqual(original, secret.read_bytes())
        self.assertEqual(subject.state["installationId"], resumed.state["installationId"])
        # The overlay is part of the verified inventory, not an operator-writable bypass.
        overlay = subject.root / "bundle/compose.email-recovery.yml"
        overlay.write_bytes(overlay.read_bytes() + b"\n# tampered\n")
        with self.assertRaisesRegex(installer.InstallError, "signed release"):
            resumed.compose_invocation("up", "-d", "kratos")

    def test_unknown_inactive_or_missing_inputs_fail_before_installation_mutation(self):
        for option in (
            {}, {"enabled": "true"}, {"enabled": True},
            {"enabled": False, "smtpFile": "/operator/smtp.json"},
            {"enabled": False, "allowInsecureSmtp": False},
            {"enabled": True, "smtpFile": "relative.json"},
            {"enabled": True, "smtpFile": "/operator/smtp.json", "allowInsecureSmtp": "true"},
            {"enabled": False, "qualified": True},
        ):
            with self.subTest(option=option):
                self.configure(option)
                with self.assertRaises(installer.InstallError):
                    self.subject()
                self.assertFalse(self.args.root.exists())
        self.enable()
        del self.manifest["files"]["compose.email-recovery.yml"]
        (self.assets / "server-manifest.json").write_text(json.dumps(self.manifest))
        with self.assertRaisesRegex(installer.InstallError, "does not support"):
            self.subject()
        self.assertFalse(self.args.root.exists())

    def test_smtp_input_rejects_injection_tls_bypass_and_unsafe_paths_without_echoing_values(self):
        path, values = self.enable()
        invalid = [
            "{", json.dumps(values)[:-1] + ',"fromName":"duplicate"}',
            json.dumps(values | {"recipient": "third-party@example.com"}),
            json.dumps(values | {"fromName": "synthetic\nINJECTED=true"}),
            json.dumps(values | {"fromName": "synthetic${INTERPOLATED}"}),
            json.dumps(values | {"fromAddress": "synthetic@example.invalid\nBcc: other@example.com"}),
        ]
        invalid.extend(json.dumps(values | {"connectionUri": uri}) for uri in (
            "https://synthetic-secret@smtp.example.invalid:443/",
            "smtps://synthetic-secret@smtp.example.invalid:465/?skip_ssl_verify=true",
            "smtp://smtp.example.invalid:25/?disable_starttls=true",
            "smtp://smtp.example.invalid:25/?disable_starttls=false&disable_starttls=true",
            "smtps://smtp.example.invalid:465/?disable_starttls=false",
            "smtp://smtp.example.invalid:25/?unknown=true",
            "smtp://smtp.example.invalid:25/path",
            "smtp://smtp.example.invalid:70000/",
            "smtp://user-only@smtp.example.invalid:25/",
            "smtp://user:secret$EXPANSION@smtp.example.invalid:25/",
            "smtp://user:secret#fragment@smtp.example.invalid:25/",
            "smtp://user:secret%0A@smtp.example.invalid:25/",
            "smtp://user:secret%ZZ@smtp.example.invalid:25/",
        ))
        for data in invalid:
            with self.subTest(data=data):
                path.write_text(data)
                with self.assertRaises(installer.InstallError) as caught:
                    self.subject()
                self.assertNotIn("synthetic-secret", str(caught.exception))
        path.write_text(json.dumps(values))
        os.chmod(path, 0o644)
        with self.assertRaises(installer.InstallError):
            self.subject()
        os.chmod(path, 0o600)
        link = self.base / "smtp-link.json"
        link.symlink_to(path)
        for target in (link, self.args.root / "smtp.json", self.recovery / "smtp.json", self.assets / "smtp.json"):
            self.configure({"enabled": True, "smtpFile": str(target)})
            with self.assertRaises(installer.InstallError):
                self.subject()
        self.assertFalse(self.args.root.exists())

    def test_isolated_capture_sink_is_explicit_and_cannot_transmit_smtp_credentials_in_plaintext(self):
        path, values = self.enable(plaintext=True)
        subject = self.subject()
        self.assertEqual(values["connectionUri"], subject.smtp_courier["COURIER_SMTP_CONNECTION_URI"])
        path.write_text(json.dumps(values | {
            "connectionUri": "smtp://synthetic-user:synthetic-password@mail-sink:1025/?disable_starttls=true"}))
        with self.assertRaises(installer.InstallError):
            self.subject()
        # Ordinary SMTP enforces StartTLS without an insecure override.
        self.configure({"enabled": True, "smtpFile": str(path)})
        path.write_text(json.dumps(values | {"connectionUri": "smtp://smtp.example.invalid:587/"}))
        self.assertTrue(self.subject().smtp_courier)

    def test_resume_and_upgrade_validation_reject_rotation_toggle_and_missing_secrets(self):
        path, values = self.enable()
        subject = self.subject()
        subject.execute()
        secret = subject.root / "secrets/kratos.env"
        original = secret.read_bytes()
        path.write_text(json.dumps(values | {"connectionUri": "smtps://changed.example.invalid:465/"}))
        for manifest in (None, self.manifest):
            with self.subTest(manifest=manifest):
                with self.assertRaisesRegex(installer.InstallError, "Kratos credentials"):
                    self.subject().load_state(manifest=manifest)
                self.assertEqual(original, secret.read_bytes())
        path.write_text(json.dumps(values))
        secret.write_bytes(original + b"COURIER_SMTP_CONNECTION_URI=smtp://injected:25/\n")
        with self.assertRaisesRegex(installer.InstallError, "Duplicate"):
            self.subject().load_state()
        secret.write_bytes(original)
        self.configure({"enabled": False})
        with self.assertRaisesRegex(installer.InstallError, "original configuration"):
            self.subject().load_state()
        self.configure({"enabled": True, "smtpFile": str(path)})
        secret.unlink()
        with self.assertRaisesRegex(installer.InstallError, "Established credential missing"):
            self.subject().execute()
        self.assertFalse(secret.exists())

    def test_opted_in_identity_schema_changes_only_recoverable_email_metadata(self):
        original = json.loads((fixtures.BUNDLE / "identity.schema.json").read_text())
        recovery = json.loads((fixtures.BUNDLE / "identity.recovery.schema.json").read_text())
        email = recovery["properties"]["traits"]["properties"]["email"]["ory.sh/kratos"]
        self.assertEqual({"via": "email"}, email.pop("recovery"))
        self.assertEqual(original, recovery)
        kratos = (fixtures.BUNDLE / "kratos.yml").read_text()
        self.assertIn("version: v1.3.1", kratos)
        self.assertIn("recovery:\n      enabled: false", kratos)
        overlay = (fixtures.BUNDLE / "compose.email-recovery.yml").read_text()
        for contract in (
            "SELFSERVICE_FLOWS_RECOVERY_USE: code", "SELFSERVICE_METHODS_CODE_ENABLED: \"true\"",
            "SELFSERVICE_METHODS_CODE_PASSWORDLESS_ENABLED: \"false\"",
            "SELFSERVICE_METHODS_CODE_MFA_ENABLED: \"false\"", "COURIER_DELIVERY_STRATEGY: smtp",
            "--watch-courier", "Kratos__EmailRecoveryEnabled:",
        ):
            self.assertIn(contract, overlay)
        self.assertNotIn("--dev", overlay)
        self.assertNotIn("ports:", overlay)
        self.assertNotIn("image:", overlay)
        self.assertEqual(2, overlay.count("./identity.recovery.schema.json:/etc/config/identity.schema.json:ro"))

    def test_opted_in_https_readiness_requires_deployed_api_recovery_capability(self):
        self.enable()
        subject = self.subject()
        subject.initialize_state()
        subject.readiness = installer.Installer.readiness.__get__(subject)
        subject.service_readiness = mock.Mock()
        subject.bootstrap_state = "ready"
        response = mock.MagicMock()
        response.__enter__.return_value = response
        response.status = 200
        response.read.return_value = b"Healthy"
        response.headers.get_content_type.return_value = "text/html"
        for enabled in (False, True):
            with self.subTest(enabled=enabled), \
                 mock.patch.object(installer.urllib.request, "build_opener") as opener, \
                 mock.patch.object(installer, "https_json", side_effect=[
                     {"serverVersion": "0.1.0", "sourceRevision": "a" * 40}, {"state": "ready"},
                     {"apiBaseUrl": "https://api.cloud.example.com", "appDomain": "apps.example.com",
                      "auth": {"origin": "https://api.cloud.example.com",
                               "capabilitiesUrl": "https://api.cloud.example.com/api/account/capabilities"}},
                     {"emailRecovery": enabled, "password": True},
                 ]) as read:
                opener.return_value.open.return_value = response
                if enabled:
                    self.assertEqual("ready", subject.readiness()["state"])
                else:
                    with self.assertRaisesRegex(installer.InstallError, "capability"):
                        subject.readiness()
                self.assertEqual(mock.call(subject.config["apiUrl"] + "/api/account/capabilities", no_store=True),
                                 read.call_args)


if __name__ == "__main__":
    unittest.main()
