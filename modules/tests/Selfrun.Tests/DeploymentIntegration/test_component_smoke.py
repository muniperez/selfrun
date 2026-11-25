#!/usr/bin/env python3
"""Docker-free regression for the packaged smoke's startup barrier."""
from contextlib import redirect_stdout
import io
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
import component_smoke as component


class ComponentReadinessTests(unittest.TestCase):
    def test_partial_migration_history_cannot_replace_api_readiness(self):
        for healthy in (False, True):
            with self.subTest(healthy=healthy):
                def result(code=0, output=""):
                    return SimpleNamespace(returncode=code, stdout=output, stderr="")

                def run(*args, **kwargs):
                    if args[:2] == ("docker", "run"):
                        return result(1, "Microsoft.NETCore.App")
                    if args[:3] == ("docker", "image", "inspect"):
                        return result(1)
                    return result(output="synthetic")

                def compose(*args, **kwargs):
                    if args[:3] == ("exec", "-T", "postgres"):
                        return result(output="3")
                    if args[:3] == ("exec", "-T", "api"):
                        return result(0 if healthy else 22)
                    return result()

                def wait(predicate, seconds, description):
                    self.assertEqual(seconds, 120)
                    self.assertEqual(predicate(), healthy)
                    raise TimeoutError("stop after readiness probe")

                with patch.object(component, "run", side_effect=run), \
                        patch.object(component, "compose", side_effect=compose), \
                        patch.object(component, "until", side_effect=wait), \
                        patch.object(component, "sql", side_effect=AssertionError("SQL before API readiness")) as sql, \
                        redirect_stdout(io.StringIO()), \
                        self.assertRaisesRegex(TimeoutError, "stop after readiness probe"):
                    component.main()
                sql.assert_not_called()


if __name__ == "__main__":
    unittest.main()
