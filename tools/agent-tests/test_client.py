import importlib.machinery
import importlib.util
import pathlib
import unittest

loader = importlib.machinery.SourceFileLoader('client', str(pathlib.Path(__file__).with_name('agent')))
spec = importlib.util.spec_from_loader(loader.name, loader)
client = importlib.util.module_from_spec(spec)
loader.exec_module(client)

class InstanceIdentityTests(unittest.TestCase):
    def health(self):
        return {'agent_instance': {'id': 'agent1', 'test_only': True, 'port': 18330,
                'product': client.PRODUCT, 'executable': client.ROOT+'\\'+client.PRODUCT+'.exe'}}

    def test_dedicated_player(self):
        client.require_identity(self.health())

    def test_normal_bridge_rejected(self):
        with self.assertRaises(RuntimeError):
            client.require_identity({'ok': True, 'session': 'normal'})

    def test_wrong_target_fields_rejected(self):
        for field, value in [('id', 'interactive'), ('test_only', False), ('port', 8330),
                             ('product', 'Big Ambitions'), ('executable', r'C:\Program Files (x86)\Steam\steamapps\common\Big Ambitions\Big Ambitions.exe')]:
            with self.subTest(field=field):
                h=self.health(); h['agent_instance'][field]=value
                with self.assertRaises(RuntimeError): client.require_identity(h)

    def test_quote_keeps_powershell_text_literal(self):
        self.assertEqual(client.literal("test'$(Get-Process)"), "'test''$(Get-Process)'")

if __name__ == '__main__': unittest.main()
