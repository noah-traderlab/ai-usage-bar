import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from aiusagebar import team


class SecureWrites(unittest.TestCase):
    def test_existing_temporary_file_is_not_overwritten(self):
        for writer in (team._write_private, team._write_atomic):
            with self.subTest(writer=writer.__name__), tempfile.TemporaryDirectory() as d:
                target = Path(d) / 'state.json'
                collision = Path(str(target) + '.tmp-fixed')
                collision.write_text('other file', encoding='utf-8')
                with patch.object(team.secrets, 'token_hex', return_value='fixed'):
                    writer(str(target), 'new state')
                self.assertTrue(collision.exists(), 'pre-existing file was consumed')
                self.assertEqual(collision.read_text(encoding='utf-8'), 'other file')
                self.assertEqual(target.read_text(encoding='utf-8'), 'new state')

    def test_failed_replace_cleans_temporary_secret(self):
        for writer in (team._write_private, team._write_atomic):
            with self.subTest(writer=writer.__name__), tempfile.TemporaryDirectory() as d:
                target = Path(d) / 'state.json'
                target.write_text('old state', encoding='utf-8')
                with patch.object(team.os, 'replace', side_effect=OSError('test failure')):
                    with self.assertRaises(OSError):
                        writer(str(target), 'secret state')
                self.assertEqual(target.read_text(encoding='utf-8'), 'old state')
                self.assertEqual(sorted(p.name for p in Path(d).iterdir()), ['state.json'])

    def test_private_unicode_roundtrip(self):
        with tempfile.TemporaryDirectory() as d:
            target = Path(d) / 'state.json'
            content = '秘密の状態' * 10000
            team._write_private(str(target), content)
            self.assertEqual(target.read_text(encoding='utf-8'), content)
            if os.name == 'posix':
                self.assertEqual(target.stat().st_mode & 0o777, 0o600)


if __name__ == '__main__':
    unittest.main()
