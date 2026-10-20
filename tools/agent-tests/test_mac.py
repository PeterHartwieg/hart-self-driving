import importlib.machinery
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

loader = importlib.machinery.SourceFileLoader('mac_client', str(Path(__file__).with_name('mac-agent')))
spec = importlib.util.spec_from_loader(loader.name, loader)
client = importlib.util.module_from_spec(spec)
loader.exec_module(client)


class MacIsolationTests(unittest.TestCase):
    def test_wrong_bridge_or_profile_rejected(self):
        fields = dict(id='agentmac', port=28330, product=client.PRODUCT,
                      executable=str(client.EXE), data_path=str(client.PROFILE), test_only=True)
        client.identity({'agent_instance': fields})
        for key, value in [('id', 'agent1'), ('port', 8330), ('product', 'Big Ambitions'),
                           ('executable', '/Applications/Big Ambitions.app'),
                           ('data_path', '/normal/saves'), ('test_only', False)]:
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                client.identity({'agent_instance': {**fields, key: value}})

    def test_reused_pid_is_not_controlled(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(client, 'ROOT', Path(folder)):
            (Path(folder)/'run.json').write_text(json.dumps({'pid':123,'identity':'old process'}))
            responses = [subprocess.CompletedProcess([],0,'S different process')]
            with patch.object(client.subprocess, 'run', side_effect=responses), self.assertRaisesRegex(RuntimeError, 'PID identity changed'):
                client.process()

    def test_zombie_counts_as_stopped(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(client, 'ROOT', Path(folder)):
            (Path(folder)/'run.json').write_text(json.dumps({'pid':123,'identity':'old process'}))
            responses = [subprocess.CompletedProcess([],0,'ZN defunct process')]
            with patch.object(client.subprocess, 'run', side_effect=responses):
                self.assertIsNone(client.process())

    def test_graceful_stop_waits_out_exit_identity_transition(self):
        with patch.object(client, 'process', side_effect=[RuntimeError('changed comm'), None]), \
                patch.object(client.time, 'sleep'), patch.object(client.os, 'kill') as kill:
            self.assertTrue(client.wait_stopped(1))
            kill.assert_not_called()

    def test_mac_token_namespace_does_not_depend_on_pc_host(self):
        from ftlib import locks
        self.assertEqual(locks.instance_for_url(client.URL), 'agentmac')
        with tempfile.TemporaryDirectory() as folder, patch.dict('os.environ',
                {'BA_LOCK_OWNER':'mac-test','BA_LOCK_TOKEN_DIR':folder,'BA_LOCK_TOKEN':''}):
            with patch.object(locks, 'HOST', 'unreachable-pc'):
                first = locks.token('agentmac', create=True)
            with patch.object(locks, 'HOST', 'another-pc'):
                self.assertEqual(first, locks.token('agentmac'))


if __name__ == '__main__':
    unittest.main()
